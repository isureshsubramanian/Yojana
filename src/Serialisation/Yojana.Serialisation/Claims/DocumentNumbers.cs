using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Claims;

/// <summary>
/// Issues human document numbers (CLM-2026-00412, RCV-2026-0031) from the tenant's <c>number_series</c> row for the
/// series and year. The increment runs inside the caller's transaction, so a refused command gives its number back and
/// numbers stay gap-free per committed document; concurrent callers queue on the series row's update lock.
/// </summary>
public sealed class DocumentNumbers(SerialisationDbContext db, ITenantContext tenant)
{
    public const string Claim = "CLM";
    public const string Recovery = "RCV";
    public const string WarrantyReplacement = "WRP";

    public async Task<string> NextAsync(string series, DateTime at, CancellationToken cancellationToken)
    {
        var year = at.Year;
        var tenantId = tenant.TenantId.Value;
        // Through db.Database so the tenant session-context interceptor runs; a raw connection would meet RLS with no tenant.
        var issued = await db.Database.SqlQueryRaw<int>("""
            MERGE [serialisation].[number_series] WITH (HOLDLOCK) AS target
            USING (SELECT {0} AS tenant_id, {1} AS series, {2} AS [year]) AS source
            ON target.tenant_id = source.tenant_id AND target.series = source.series AND target.[year] = source.[year]
            WHEN MATCHED THEN UPDATE SET last_value = target.last_value + 1
            WHEN NOT MATCHED THEN INSERT (tenant_id, series, [year], last_value) VALUES (source.tenant_id, source.series, source.[year], 1)
            OUTPUT inserted.last_value AS [Value];
            """, tenantId, series, year).ToListAsync(cancellationToken);

        return Format(series, year, issued.Single());
    }

    public static string Format(string series, int year, int value) => series switch
    {
        Claim => $"{Claim}-{year}-{value:D5}",
        Recovery => $"{Recovery}-{year}-{value:D4}",
        WarrantyReplacement => $"{WarrantyReplacement}-{year}-{value:D5}",
        _ => $"{series}-{year}-{value:D5}",
    };
}
