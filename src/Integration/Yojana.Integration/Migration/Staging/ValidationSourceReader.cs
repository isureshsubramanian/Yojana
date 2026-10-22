using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Staging;

public sealed class SignedQuantityMismatchRow
{
    public required string Item { get; init; }
    public string? Serial { get; init; }
    [Precision(28, 8)]
    public decimal SourceQuantity { get; init; }
    [Precision(28, 8)]
    public decimal MovementQuantity { get; init; }
}

/// <summary>
/// Adapts source stock and transaction totals for validation. Physical source-shaped table names remain confined to
/// the staging boundary; validation receives only the source-agnostic comparison rows.
/// </summary>
public sealed class ValidationSourceReader(IntegrationDbContext db, ITenantContext tenant)
{
    public async Task<IReadOnlyList<SignedQuantityMismatchRow>> SignedQuantityMismatchesAsync(Guid batchId,
        Guid mapRunId, string sourceSystem, CancellationToken cancellationToken) => await db.Database
        .SqlQuery<SignedQuantityMismatchRow>(
        $$"""
        WITH quantities AS
        (
            SELECT [item_code], [system_number], SUM(COALESCE([quantity], 0)) AS [quantity]
              FROM [migration].[stg_src_osrq]
             WHERE [tenant_id] = {{tenant.TenantId.Value}} AND [batch_id] = {{batchId}}
               AND [source_system] = {{sourceSystem}} AND [absent_since_batch_id] IS NULL
               AND [item_code] IS NOT NULL AND [system_number] IS NOT NULL
             GROUP BY [item_code], [system_number]
        ), movements AS
        (
            SELECT line.[item_code], line.[system_number], SUM(COALESCE(line.[quantity], 0)) AS [quantity]
              FROM [migration].[stg_src_itl1] AS line
              JOIN [migration].[stg_src_oitl] AS header
                ON header.[tenant_id] = line.[tenant_id]
               AND header.[batch_id] = line.[batch_id]
               AND header.[source_system] = line.[source_system]
               AND header.[log_entry] = line.[log_entry]
             WHERE line.[tenant_id] = {{tenant.TenantId.Value}} AND line.[batch_id] = {{batchId}}
               AND line.[source_system] = {{sourceSystem}} AND line.[absent_since_batch_id] IS NULL
               AND header.[absent_since_batch_id] IS NULL
               AND line.[item_code] IS NOT NULL AND line.[system_number] IS NOT NULL
               AND header.[stock_effect] = 1
             GROUP BY line.[item_code], line.[system_number]
        ), planned AS
        (
            SELECT master.[item_code], master.[system_number], master.[serial_number]
              FROM [migration].[migration_plan_unit] AS plan_unit
              JOIN [migration].[migration_unit_candidate] AS candidate
                ON candidate.[tenant_id] = plan_unit.[tenant_id]
               AND candidate.[candidate_id] = plan_unit.[candidate_id]
              JOIN [migration].[stg_src_osrn] AS master
                ON master.[tenant_id] = candidate.[tenant_id]
               AND master.[batch_id] = {{batchId}}
               AND master.[source_system] = {{sourceSystem}}
               AND master.[absent_since_batch_id] IS NULL
               AND master.[source_pk] = candidate.[primary_source_key]
             WHERE plan_unit.[tenant_id] = {{tenant.TenantId.Value}}
               AND plan_unit.[map_run_id] = {{mapRunId}}
               AND master.[item_code] IS NOT NULL AND master.[system_number] IS NOT NULL
        )
        SELECT planned.[item_code] AS [Item], planned.[serial_number] AS [Serial],
               CONVERT(decimal(28,8), COALESCE(quantities.[quantity], 0)) AS [SourceQuantity],
               CONVERT(decimal(28,8), COALESCE(movements.[quantity], 0)) AS [MovementQuantity]
          FROM planned
          LEFT JOIN quantities ON quantities.[item_code] = planned.[item_code]
                              AND quantities.[system_number] = planned.[system_number]
          LEFT JOIN movements ON movements.[item_code] = planned.[item_code]
                             AND movements.[system_number] = planned.[system_number]
         WHERE COALESCE(quantities.[quantity], 0) <> COALESCE(movements.[quantity], 0)
        """).ToListAsync(cancellationToken);
}
