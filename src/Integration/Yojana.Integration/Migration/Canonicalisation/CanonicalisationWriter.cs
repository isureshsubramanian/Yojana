using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Canonicalisation;

/// <summary>Writes the high-volume candidate graph with set-based SQL; review anomalies are handled separately.</summary>
public sealed class CanonicalisationWriter(IntegrationDbContext db, ITenantContext tenant)
{
    public async Task WriteAsync(MigrationCanonRun run, string sourceSystem,
        IReadOnlyList<CanonicalFamilyRule> rules, CancellationToken cancellationToken)
    {
        var previousTimeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        try
        {
            await db.Database.ExecuteSqlRawAsync(CandidateSql(RuleValues(rules.Count)),
                Parameters(run, sourceSystem, rules), cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT [migration].[migration_unit_candidate_source]
                (tenant_id, candidate_source_id, run_id, candidate_id, matched_serial_source_key,
                 source_kind, source_key, role)
            SELECT {{tenant.TenantId.Value}}, NEWID(), {{run.RunId}}, candidate.candidate_id,
                   candidate.primary_source_key, N'serial_master', candidate.primary_source_key, N'kept'
              FROM [migration].[migration_unit_candidate] AS candidate
             WHERE candidate.tenant_id = {{tenant.TenantId.Value}} AND candidate.run_id = {{run.RunId}};
            """, cancellationToken);
            await WriteEventsAsync(run, sourceSystem, cancellationToken);
            await db.Database.ExecuteSqlRawAsync(CardSql(RuleValues(rules.Count)),
                Parameters(run, sourceSystem, rules), cancellationToken);
            await db.Database.ExecuteSqlRawAsync(ContractSql(RuleValues(rules.Count)),
                Parameters(run, sourceSystem, rules), cancellationToken);
        }
        finally
        {
            db.Database.SetCommandTimeout(previousTimeout);
        }
    }

    private async Task WriteEventsAsync(MigrationCanonRun run, string sourceSystem,
        CancellationToken cancellationToken)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection) await db.Database.OpenConnectionAsync(cancellationToken);

        var last = "";
        try
        {
            while (true)
            {
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 600;
                command.CommandText = """
                DECLARE @event_batch_keys TABLE (source_key nvarchar(1000) NOT NULL);
                INSERT [migration].[migration_event_candidate]
                    (tenant_id, event_candidate_id, run_id, candidate_id, serial_master_source_key,
                     transaction_source_key, match_rule, provenance)
                OUTPUT inserted.transaction_source_key INTO @event_batch_keys(source_key)
                SELECT TOP (50000) @tenant, NEWID(), @run, candidate.candidate_id,
                       master.source_pk, line.source_pk, candidate.match_rule, N'source'
                  FROM [migration].[stg_src_itl1] AS line
                  JOIN [migration].[stg_src_osrn] AS master
                    ON master.tenant_id = line.tenant_id AND master.batch_id = line.batch_id
                   AND master.source_system = line.source_system AND master.absent_since_batch_id IS NULL
                   AND master.item_code = line.item_code AND master.system_number = line.system_number
                  JOIN [migration].[migration_unit_candidate] AS candidate
                    ON candidate.tenant_id = master.tenant_id AND candidate.run_id = @run
                   AND candidate.primary_source_key = master.source_pk
                 WHERE line.tenant_id = @tenant AND line.batch_id = @batch
                   AND line.source_system = @source AND line.absent_since_batch_id IS NULL
                   AND line.source_pk > @last
                 ORDER BY line.source_pk;
                SELECT COALESCE(MAX(source_key), N'') FROM @event_batch_keys;
                """;
                command.Parameters.Add(new SqlParameter("@tenant", tenant.TenantId.Value));
                command.Parameters.Add(new SqlParameter("@run", run.RunId));
                command.Parameters.Add(new SqlParameter("@batch", run.BatchId));
                command.Parameters.Add(new SqlParameter("@source", sourceSystem));
                command.Parameters.Add(new SqlParameter("@last", last));

                var next = (string?)await command.ExecuteScalarAsync(cancellationToken) ?? "";
                if (next.Length == 0) return;
                last = next;
            }
        }
        finally
        {
            if (closeConnection) await db.Database.CloseConnectionAsync();
        }
    }

    private object[] Parameters(MigrationCanonRun run, string sourceSystem,
        IReadOnlyList<CanonicalFamilyRule> rules)
    {
        var parameters = new List<object>
        {
            new SqlParameter("@tenant", tenant.TenantId.Value),
            new SqlParameter("@run", run.RunId),
            new SqlParameter("@batch", run.BatchId),
            new SqlParameter("@source", sourceSystem),
        };
        for (var index = 0; index < rules.Count; index++)
        {
            parameters.Add(new SqlParameter($"@prefix{index}", rules[index].ItemCodePrefix));
            parameters.Add(new SqlParameter($"@hyphens{index}", rules[index].StripHyphens));
            parameters.Add(new SqlParameter($"@spaces{index}", rules[index].StripSpaces));
            parameters.Add(new SqlParameter($"@zeros{index}", rules[index].StripLeadingZeros));
        }
        return parameters.ToArray();
    }

    private static string RuleValues(int count) => string.Join(",", Enumerable.Range(0, count).Select(index =>
        $"(@prefix{index},@hyphens{index},@spaces{index},@zeros{index})"));

    private static string FamilyCtes(string values) => $$"""
        family_rules(prefix, strip_hyphens, strip_spaces, strip_leading_zeros) AS
        (
            SELECT prefix, strip_hyphens, strip_spaces, strip_leading_zeros
              FROM (VALUES {{values}}) rules(prefix, strip_hyphens, strip_spaces, strip_leading_zeros)
        )
        """;

    private static string CandidateSql(string values) => $$"""
        WITH {{FamilyCtes(values)}},
        normalised AS
        (
            SELECT master.source_pk, master.item_code, item.item_name, master.serial_number,
                   CASE WHEN family_rule.prefix = N'' THEN
                       CASE WHEN CHARINDEX(N'-', master.item_code) > 0
                            THEN LEFT(master.item_code, CHARINDEX(N'-', master.item_code) - 1)
                            ELSE master.item_code END ELSE family_rule.prefix END AS family,
                   CASE WHEN NULLIF(TRIM(master.serial_number), N'') IS NULL
                        THEN CONCAT(N'SYSTEM-', CONVERT(nvarchar(40), master.system_number))
                        ELSE master.serial_number END COLLATE Latin1_General_100_BIN2 AS exact_key,
                   CASE WHEN NULLIF(master.normalised_serial_number, N'') IS NULL
                        THEN UPPER(CONCAT(N'SYSTEM-', CONVERT(nvarchar(40), master.system_number)))
                        ELSE master.normalised_serial_number END COLLATE Latin1_General_100_BIN2 AS trimmed_key,
                   [migration].[canonical_serial](CASE WHEN NULLIF(TRIM(master.serial_number), N'') IS NULL
                        THEN CONCAT(N'SYSTEM-', CONVERT(nvarchar(40), master.system_number))
                        ELSE master.serial_number END, family_rule.strip_hyphens, family_rule.strip_spaces,
                        family_rule.strip_leading_zeros) COLLATE Latin1_General_100_BIN2 AS family_key,
                   CASE WHEN NULLIF(TRIM(master.serial_number), N'') IS NULL THEN 1 ELSE 0 END AS placeholder
              FROM [migration].[stg_src_osrn] AS master
              OUTER APPLY
              (
                  SELECT TOP (1) family_rules.* FROM family_rules
                   WHERE UPPER(master.item_code) LIKE family_rules.prefix + N'%'
                   ORDER BY LEN(family_rules.prefix) DESC, family_rules.prefix
              ) AS family_rule
              OUTER APPLY
              (
                  SELECT TOP (1) staged.item_name FROM [migration].[stg_src_oitm] AS staged
                   WHERE staged.tenant_id = master.tenant_id AND staged.batch_id = master.batch_id
                     AND staged.source_system = master.source_system AND staged.absent_since_batch_id IS NULL
                     AND staged.item_code = master.item_code
              ) AS item
             WHERE master.tenant_id = @tenant AND master.batch_id = @batch
               AND master.source_system = @source AND master.absent_since_batch_id IS NULL
               AND master.item_code IS NOT NULL
        ), counted AS
        (
            SELECT normalised.*,
                   COUNT_BIG(1) OVER (PARTITION BY item_code, exact_key) AS exact_count,
                   COUNT_BIG(1) OVER (PARTITION BY item_code, trimmed_key) AS trimmed_count,
                   COUNT_BIG(1) OVER (PARTITION BY item_code, family_key) AS family_count
              FROM normalised
        ), resolved AS
        (
            SELECT *, CASE WHEN exact_count > 1 THEN N'exact'
                           WHEN trimmed_count > 1 THEN N'trimmed_upper'
                           WHEN family_count > 1 THEN N'family_normalised'
                           ELSE N'exact' END AS match_rule
              FROM counted
        )
        INSERT [migration].[migration_unit_candidate]
            (tenant_id, candidate_id, run_id, primary_source_key, item_code, item_name, family,
             canonical_serial, stored_serial, trimmed_serial, family_normalised_serial,
             match_rule, provenance, disposition, note)
        SELECT @tenant, NEWID(), @run, source_pk, item_code, item_name, family,
               CASE match_rule WHEN N'trimmed_upper' THEN trimmed_key
                    WHEN N'family_normalised' THEN family_key ELSE exact_key END,
               serial_number, trimmed_key, family_key, match_rule, N'source', N'active',
               CASE WHEN placeholder = 1 THEN N'System-assigned placeholder; the system number is the canonical key.' END
          FROM resolved;
        """;

    private static string CardSql(string values) => $$"""
        WITH {{FamilyCtes(values)}},
        matches AS
        (
            SELECT card.source_pk AS card_source_key, candidate.candidate_id,
                   candidate.primary_source_key,
                   CASE WHEN card.internal_serial COLLATE Latin1_General_100_BIN2 = candidate.stored_serial COLLATE Latin1_General_100_BIN2
                             OR (card.manufacturer_serial IS NOT NULL AND master.manufacturer_serial IS NOT NULL
                                 AND card.manufacturer_serial COLLATE Latin1_General_100_BIN2 = master.manufacturer_serial COLLATE Latin1_General_100_BIN2) THEN 1
                        WHEN card.normalised_internal_serial COLLATE Latin1_General_100_BIN2 = candidate.trimmed_serial COLLATE Latin1_General_100_BIN2 THEN 2
                        WHEN [migration].[canonical_serial](card.internal_serial, family_rule.strip_hyphens,
                             family_rule.strip_spaces, family_rule.strip_leading_zeros) COLLATE Latin1_General_100_BIN2 =
                             candidate.family_normalised_serial COLLATE Latin1_General_100_BIN2 THEN 3 END AS priority,
                   card.warranty_start, card.warranty_end
              FROM [migration].[stg_src_oins] AS card
              JOIN [migration].[migration_unit_candidate] AS candidate
                ON candidate.tenant_id = card.tenant_id AND candidate.run_id = @run
               AND candidate.item_code = card.item_code
              JOIN [migration].[stg_src_osrn] AS master
                ON master.tenant_id = candidate.tenant_id AND master.batch_id = @batch
               AND master.source_system = @source AND master.absent_since_batch_id IS NULL
               AND master.source_pk = candidate.primary_source_key
              OUTER APPLY
              (
                  SELECT TOP (1) family_rules.* FROM family_rules
                   WHERE UPPER(card.item_code) LIKE family_rules.prefix + N'%'
                   ORDER BY LEN(family_rules.prefix) DESC, family_rules.prefix
              ) AS family_rule
             WHERE card.tenant_id = @tenant AND card.batch_id = @batch
               AND card.source_system = @source AND card.absent_since_batch_id IS NULL
        ), ranked AS
        (
            SELECT *, MIN(priority) OVER (PARTITION BY card_source_key) AS best_priority
              FROM matches WHERE priority IS NOT NULL
        ), chosen AS
        (
            SELECT *, COUNT_BIG(1) OVER (PARTITION BY card_source_key, priority) AS best_count
              FROM ranked WHERE priority = best_priority
        )
        INSERT [migration].[migration_unit_candidate_source]
            (tenant_id, candidate_source_id, run_id, candidate_id, matched_serial_source_key,
             source_kind, source_key, role)
        SELECT @tenant, NEWID(), @run, candidate_id, primary_source_key,
               N'equipment_card', card_source_key, N'kept'
          FROM chosen WHERE best_count = 1;

        WITH linked AS
        (
            SELECT source.candidate_id, source.matched_serial_source_key, source.source_key,
                   card.warranty_start, card.warranty_end, candidate.match_rule
              FROM [migration].[migration_unit_candidate_source] AS source
              JOIN [migration].[stg_src_oins] AS card
                ON card.tenant_id = source.tenant_id AND card.batch_id = @batch
               AND card.source_system = @source AND card.absent_since_batch_id IS NULL
               AND card.source_pk = source.source_key
              JOIN [migration].[migration_unit_candidate] AS candidate
                ON candidate.tenant_id = source.tenant_id AND candidate.candidate_id = source.candidate_id
             WHERE source.tenant_id = @tenant AND source.run_id = @run
               AND source.source_kind = N'equipment_card'
        )
        INSERT [migration].[migration_entitlement_candidate]
            (tenant_id, entitlement_candidate_id, run_id, candidate_id, serial_master_source_key,
             source_kind, source_key, start_date, end_date, match_rule, provenance)
        SELECT @tenant, NEWID(), @run, candidate_id, matched_serial_source_key,
               N'equipment_card', source_key, warranty_start, warranty_end, match_rule, N'source'
          FROM linked WHERE warranty_start IS NOT NULL OR warranty_end IS NOT NULL;
        """;

    private static string ContractSql(string values) => $$"""
        WITH {{FamilyCtes(values)}},
        matches AS
        (
            SELECT line.source_pk AS line_source_key, line.contract_id, candidate.candidate_id,
                   candidate.primary_source_key,
                   CASE WHEN line.internal_serial COLLATE Latin1_General_100_BIN2 = candidate.stored_serial COLLATE Latin1_General_100_BIN2
                             OR (line.manufacturer_serial IS NOT NULL AND master.manufacturer_serial IS NOT NULL
                                 AND line.manufacturer_serial COLLATE Latin1_General_100_BIN2 = master.manufacturer_serial COLLATE Latin1_General_100_BIN2) THEN 1
                        WHEN UPPER(TRIM(line.internal_serial)) COLLATE Latin1_General_100_BIN2 = candidate.trimmed_serial COLLATE Latin1_General_100_BIN2 THEN 2
                        WHEN [migration].[canonical_serial](line.internal_serial, family_rule.strip_hyphens,
                             family_rule.strip_spaces, family_rule.strip_leading_zeros) COLLATE Latin1_General_100_BIN2 =
                             candidate.family_normalised_serial COLLATE Latin1_General_100_BIN2 THEN 3 END AS priority,
                   line.start_date, line.end_date
              FROM [migration].[stg_src_ctr1] AS line
              JOIN [migration].[migration_unit_candidate] AS candidate
                ON candidate.tenant_id = line.tenant_id AND candidate.run_id = @run
               AND candidate.item_code = line.item_code
              JOIN [migration].[stg_src_osrn] AS master
                ON master.tenant_id = candidate.tenant_id AND master.batch_id = @batch
               AND master.source_system = @source AND master.absent_since_batch_id IS NULL
               AND master.source_pk = candidate.primary_source_key
              OUTER APPLY
              (
                  SELECT TOP (1) family_rules.* FROM family_rules
                   WHERE UPPER(line.item_code) LIKE family_rules.prefix + N'%'
                   ORDER BY LEN(family_rules.prefix) DESC, family_rules.prefix
              ) AS family_rule
             WHERE line.tenant_id = @tenant AND line.batch_id = @batch
               AND line.source_system = @source AND line.absent_since_batch_id IS NULL
        ), ranked AS
        (
            SELECT *, MIN(priority) OVER (PARTITION BY line_source_key) AS best_priority
              FROM matches WHERE priority IS NOT NULL
        ), chosen AS
        (
            SELECT *, COUNT_BIG(1) OVER (PARTITION BY line_source_key, priority) AS best_count
              FROM ranked WHERE priority = best_priority
        )
        INSERT [migration].[migration_unit_candidate_source]
            (tenant_id, candidate_source_id, run_id, candidate_id, matched_serial_source_key,
             source_kind, source_key, role)
        SELECT @tenant, NEWID(), @run, candidate_id, primary_source_key,
               N'contract_line', line_source_key, N'kept'
          FROM chosen WHERE best_count = 1;

        INSERT [migration].[migration_unit_candidate_source]
            (tenant_id, candidate_source_id, run_id, candidate_id, matched_serial_source_key,
             source_kind, source_key, role)
        SELECT @tenant, NEWID(), @run, linked.candidate_id, linked.matched_serial_source_key,
               N'contract', linked.source_key, N'kept'
          FROM
          (
              SELECT DISTINCT source.candidate_id, source.matched_serial_source_key,
                     contract.source_pk AS source_key
                FROM [migration].[migration_unit_candidate_source] AS source
                JOIN [migration].[stg_src_ctr1] AS line
                  ON line.tenant_id = source.tenant_id AND line.batch_id = @batch
                 AND line.source_system = @source AND line.source_pk = source.source_key
                JOIN [migration].[stg_src_octr] AS contract
                  ON contract.tenant_id = line.tenant_id AND contract.batch_id = line.batch_id
                 AND contract.source_system = line.source_system AND contract.absent_since_batch_id IS NULL
                 AND contract.contract_id = line.contract_id
               WHERE source.tenant_id = @tenant AND source.run_id = @run
                 AND source.source_kind = N'contract_line'
          ) AS linked;

        INSERT [migration].[migration_entitlement_candidate]
            (tenant_id, entitlement_candidate_id, run_id, candidate_id, serial_master_source_key,
             source_kind, source_key, start_date, end_date, match_rule, provenance)
        SELECT @tenant, NEWID(), @run, source.candidate_id, source.matched_serial_source_key,
               source.source_kind, source.source_key,
               CASE source.source_kind WHEN N'contract_line' THEN line.start_date ELSE contract.start_date END,
               CASE source.source_kind WHEN N'contract_line' THEN line.end_date ELSE contract.end_date END,
               candidate.match_rule, N'source'
          FROM [migration].[migration_unit_candidate_source] AS source
          JOIN [migration].[migration_unit_candidate] AS candidate
            ON candidate.tenant_id = source.tenant_id AND candidate.candidate_id = source.candidate_id
          LEFT JOIN [migration].[stg_src_ctr1] AS line
            ON source.source_kind = N'contract_line' AND line.tenant_id = source.tenant_id
           AND line.batch_id = @batch AND line.source_system = @source AND line.source_pk = source.source_key
          LEFT JOIN [migration].[stg_src_octr] AS contract
            ON source.source_kind = N'contract' AND contract.tenant_id = source.tenant_id
           AND contract.batch_id = @batch AND contract.source_system = @source AND contract.source_pk = source.source_key
         WHERE source.tenant_id = @tenant AND source.run_id = @run
           AND source.source_kind IN (N'contract', N'contract_line')
           AND (line.start_date IS NOT NULL OR line.end_date IS NOT NULL
             OR contract.start_date IS NOT NULL OR contract.end_date IS NOT NULL);
        """;
}
