using System.Diagnostics;

namespace Yojana.Bench;

/// <summary>
/// Loads entitlements for every loaded unit so the warranty check (P2, p99 ≤ 20 ms) is measured against a populated
/// serialisation.warranty_current, not an empty view. Set-based on the server, per tenant, in keyset batches of units:
/// 92 % of units carry live base cover (12, 24 or 36 months, starts spread over the last three years, so part of it has
/// run out by today), 30 % also carry extended cover starting the day after base ends, and every 13th unit has an older
/// closed base row (superseded) that the view must skip. unit_current.live_entitlement_ids and warranty_end_max are
/// rewritten from the live rows, the way the entitlement commands maintain them.
///
/// Coupled to the shape of serialisation.entitlement (required columns, the closed_reason and term checks, the
/// one-live-row-per-family filtered index) and to the warranty_current view's join through live_entitlement_ids: a
/// migration that adds a required column or changes how the view finds live rows must change this loader too, or the
/// load fails (column) or the benchmark silently measures a view that finds nothing (join).
/// </summary>
public sealed class EntitlementLoader(BenchOptions options, BenchReport report)
{
    private const int BatchUnits = 100_000;
    private static readonly Guid Obligor = Guid.ParseExact("e9b7c777-b155-4b08-98d7-5744baf4cc84", "D");
    private static readonly Guid BasePolicy = Guid.ParseExact("b0000000-0000-4000-8000-0000000000b1", "D");
    private static readonly Guid ExtendedPolicy = Guid.ParseExact("b0000000-0000-4000-8000-0000000000e1", "D");

    public async Task RunAsync()
    {
        var watch = Stopwatch.StartNew();
        long units = 0, rows = 0;
        for (var index = 1; index <= options.Tenants; index++)
        {
            var tenant = BenchOptions.TenantId(index);
            await using var connection = await Sql.OpenAsync(options.ConnectionString, tenant);
            if (await Sql.ScalarAsync<int>(connection, "SELECT CASE WHEN EXISTS (SELECT 1 FROM serialisation.entitlement WHERE tenant_id = @t) THEN 1 ELSE 0 END", 600, ("@t", tenant)) == 1)
            {
                report.Log($"entitlements: tenant {index} already has entitlements, skipped");
                continue;
            }

            var last = Guid.Empty;
            while (true)
            {
                var batch = await Sql.RowsAsync(connection, BatchSql, 3600, ("@t", tenant), ("@last", last), ("@batch", BatchUnits),
                    ("@obligor", Obligor), ("@basePolicy", BasePolicy), ("@extendedPolicy", ExtendedPolicy));
                var done = Convert.ToInt32(batch[0]["units"]);
                if (done == 0) break;
                units += done;
                rows += Convert.ToInt64(batch[0]["rows"]);
                last = (Guid)batch[0]["last"]!;
                report.Log($"entitlements: tenant {index} · {units:N0} units · {rows:N0} entitlements · {watch.Elapsed.TotalSeconds:N0} s");
                if (done < BatchUnits) break;
            }

            await Sql.ExecAsync(connection, "UPDATE STATISTICS serialisation.entitlement; UPDATE STATISTICS serialisation.unit_current;", 3600);
        }

        report.Measure("Entitlement load for the warranty check", "informative", $"{rows:N0} entitlements for {units:N0} units in {BenchReport.Ms(watch.Elapsed.TotalMilliseconds)}", "INFO",
            "92 % base (12/24/36 months, starts over three years), 30 % extended, every 13th unit with a closed superseded base row");
    }

    private const string BatchSql = """
        SET NOCOUNT ON; SET XACT_ABORT ON;
        DECLARE @today date = CAST(SYSUTCDATETIME() AS date), @now datetime2 = SYSUTCDATETIME();
        SELECT TOP (@batch) c.unit_id, ABS(CAST(CHECKSUM(c.unit_id) AS bigint)) AS h
        INTO #u FROM serialisation.unit_current AS c WHERE c.tenant_id = @t AND c.unit_id > @last ORDER BY c.unit_id;

        CREATE TABLE #e (entitlement_id uniqueidentifier NOT NULL, unit_id uniqueidentifier NOT NULL, family nvarchar(40) NOT NULL, type nvarchar(30) NOT NULL,
            policy uniqueidentifier NOT NULL, months int NOT NULL, start_date date NOT NULL, end_date date NOT NULL, status nvarchar(20) NOT NULL, closed_reason nvarchar(40) NULL,
            assurance nvarchar(20) NOT NULL, created_at datetime2 NOT NULL);

        -- Base cover: 92 % of units, 12/24/36 months, started up to three years ago.
        INSERT #e SELECT NEWID(), u.unit_id, N'base_warranty', N'manufacturer', @basePolicy, b.months, b.start_date, DATEADD(day, -1, DATEADD(month, b.months, b.start_date)),
            N'active', NULL, N'assurance', @now
        FROM #u AS u CROSS APPLY (SELECT 12 * (1 + u.h % 3) AS months, DATEADD(day, -CAST(u.h % 1095 AS int), @today) AS start_date) AS b
        WHERE u.h % 100 < 92;

        -- Extended cover: 30 % of units, 24 months from the day after base ends.
        INSERT #e SELECT NEWID(), e.unit_id, N'extended_warranty', N'extended', @extendedPolicy, 24, DATEADD(day, 1, e.end_date), DATEADD(day, -1, DATEADD(month, 24, DATEADD(day, 1, e.end_date))),
            N'active', NULL, N'service', @now
        FROM #e AS e JOIN #u AS u ON u.unit_id = e.unit_id WHERE e.family = N'base_warranty' AND u.h % 100 < 30;

        -- History: every 13th unit had an earlier base row, closed when it was superseded; the view must not return it.
        INSERT #e SELECT NEWID(), u.unit_id, N'base_warranty', N'manufacturer', @basePolicy, 12, DATEADD(day, -1200, @today), DATEADD(day, -836, @today),
            N'closed', N'superseded', N'assurance', DATEADD(day, -1200, @now)
        FROM #u AS u WHERE u.h % 13 = 0;

        INSERT serialisation.entitlement (tenant_id, entitlement_id, unit_id, scope, type, coverage_family, direction, obligor_entity_id, policy_version_id,
            start_trigger, start_basis, start_date, start_time_zone, end_date, computed_by_rule_version, duration_basis, duration_unit, duration_value,
            coverage, region_scope, transferable, assurance_or_service, status, closed_reason, closed_effective, closed_at, confidence, statutory_extension_applied,
            provenance, created_event_id, created_at)
        SELECT @t, e.entitlement_id, e.unit_id, N'unit', e.type, e.family, N'outbound', @obligor, e.policy,
            N'deliver', N'deliver', e.start_date, N'UTC', e.end_date, N'v1', N'time', N'months', e.months,
            N'{"parts_pct":100,"labour_pct":100,"service_mode":"on_site"}', N'["EU","UK"]', N'yes', e.assurance, e.status, e.closed_reason,
            CASE WHEN e.status = N'closed' THEN e.end_date END, CASE WHEN e.status = N'closed' THEN @now END, N'high', 0,
            N'backfilled', NEWID(), e.created_at
        FROM #e AS e;

        UPDATE c SET live_entitlement_ids = j.ids, warranty_end_max = j.last_end
        FROM serialisation.unit_current AS c
        JOIN (SELECT unit_id, N'[' + STRING_AGG(CAST(N'"' + CONVERT(nvarchar(36), entitlement_id) + N'"' AS nvarchar(max)), N',') + N']' AS ids, MAX(end_date) AS last_end
              FROM #e WHERE status <> N'closed' GROUP BY unit_id) AS j ON j.unit_id = c.unit_id
        WHERE c.tenant_id = @t;

        SELECT (SELECT COUNT(*) FROM #u) AS units, (SELECT COUNT(*) FROM #e) AS rows, (SELECT TOP (1) unit_id FROM #u ORDER BY unit_id DESC) AS last;
        """;
}
