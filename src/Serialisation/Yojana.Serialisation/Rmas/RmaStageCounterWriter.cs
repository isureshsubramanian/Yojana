using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Rmas;

/// <summary>Atomic write boundary for the tenant's six-by-sixteen striped RMA pipeline counters.</summary>
public sealed class RmaStageCounterWriter(SerialisationDbContext db, ITenantContext tenant)
{
    public async Task AddRequestedAsync(Guid rmaId, CancellationToken cancellationToken)
    {
        RequireTransaction();
        var slot = RmaStageCounterSlot.For(rmaId);
        var changed = await IncrementRequestedAsync(slot, cancellationToken);
        if (changed == 1) return;

        // Existing tenants take only the atomic UPDATE above. The guarded bootstrap is reserved for a tenant's first
        // RMA so its range locks cannot contend with every later create/transition at the shared counter boundary.
        await EnsureRowsAsync(cancellationToken);
        changed = await IncrementRequestedAsync(slot, cancellationToken);
        if (changed != 1) throw new InvalidOperationException("The requested RMA stage counter was not available.");
    }

    private Task<int> IncrementRequestedAsync(byte slot, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            -- P6-T22 RMA create: requested counter
            UPDATE serialisation.rma_stage_counter
            SET document_count = document_count + 1
            WHERE tenant_id = {tenant.TenantId.Value} AND stage = {Contracts.RmaStage.Requested} AND slot = {slot};
            """, cancellationToken);

    public async Task MoveAsync(Guid rmaId, string from, string to, bool fromHasException, bool toHasException,
        CancellationToken cancellationToken)
    {
        RequireTransaction();
        var slot = RmaStageCounterSlot.For(rmaId);
        var fromException = fromHasException ? 1 : 0;
        var toException = toHasException ? 1 : 0;
        // Both rows share one slot. The clustered key is (tenant, stage, slot), so this set-based update reaches them in
        // the same stage-key order for every transition; two commands cannot take the pair in opposing lock order.
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE serialisation.rma_stage_counter WITH (UPDLOCK)
            SET document_count = document_count + CASE WHEN stage = {from} THEN -1 ELSE 1 END,
                exception_count = exception_count + CASE WHEN stage = {from} THEN -{fromException} ELSE {toException} END
            WHERE tenant_id = {tenant.TenantId.Value} AND stage IN ({from}, {to}) AND slot = {slot};
            """, cancellationToken);
        if (changed != 2)
            throw new InvalidOperationException("RMA stage counters are incomplete; run the administrator rebuild before changing stage.");
    }

    public async Task<bool> RebuildAsync(Guid commandId, string actorRef, DateTime rebuiltAt,
        CancellationToken cancellationToken)
    {
        RequireTransaction();
        if (await db.RmaStageCounters.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId
                && row.LastRebuildCommandId == commandId, cancellationToken))
            return true;

        await EnsureRowsAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH source AS (
                SELECT r.stage, hashed.slot,
                       COUNT(*) AS document_count,
                       SUM(CASE WHEN r.has_exception = CAST(1 AS bit) THEN CONVERT(bigint, 1) ELSE CONVERT(bigint, 0) END) AS exception_count
                FROM serialisation.rma r
                CROSS APPLY (SELECT CONVERT(tinyint, SUBSTRING(HASHBYTES('SHA2_256',
                    CONVERT(varchar(32), LOWER(REPLACE(CONVERT(char(36), r.rma_id), '-', '')))), 1, 1)) % {RmaStageCounterSlot.Count} AS slot) hashed
                WHERE r.tenant_id = {tenant.TenantId.Value}
                GROUP BY r.stage, hashed.slot
            )
            UPDATE counters WITH (UPDLOCK)
            SET document_count = COALESCE(source.document_count, 0),
                exception_count = COALESCE(source.exception_count, 0),
                last_rebuild_command_id = {commandId},
                last_rebuilt_at = {rebuiltAt},
                last_rebuilt_by = {actorRef}
            FROM serialisation.rma_stage_counter counters
            LEFT JOIN source ON source.stage = counters.stage AND source.slot = counters.slot
            WHERE counters.tenant_id = {tenant.TenantId.Value};
            """, cancellationToken);
        return false;
    }

    private Task<int> EnsureRowsAsync(CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            MERGE serialisation.rma_stage_counter WITH (HOLDLOCK) AS target
            USING (
                SELECT {tenant.TenantId.Value} AS tenant_id, stages.stage, slots.slot
                FROM (VALUES ('requested'), ('authorised'), ('in_transit'), ('received'), ('dispositioned'), ('closed')) stages(stage)
                CROSS JOIN (VALUES (0), (1), (2), (3), (4), (5), (6), (7),
                                   (8), (9), (10), (11), (12), (13), (14), (15)) slots(slot)
            ) AS source
            ON target.tenant_id = source.tenant_id AND target.stage = source.stage AND target.slot = source.slot
            WHEN NOT MATCHED THEN
                INSERT (tenant_id, stage, slot, document_count, exception_count)
                VALUES (source.tenant_id, source.stage, source.slot, 0, 0);
            """, cancellationToken);

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("RMA counters must be changed inside the RMA command transaction.");
    }
}

/// <summary>Stable, process-independent stripe assignment shared by every write for one RMA.</summary>
public static class RmaStageCounterSlot
{
    public const int Count = 16;

    public static byte For(Guid rmaId)
    {
        // Hash the canonical lower-case UUID text so migration/rebuild SQL can reproduce the exact byte stream.
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(rmaId.ToString("N")));
        return (byte)(hash[0] % Count);
    }
}

public static class RebuildRmaStageCountersHandler
{
    public static async Task<Contracts.RebuildRmaStageCountersResult> Handle(
        Contracts.RebuildRmaStageCounters command,
        SerialisationDbContext db,
        RmaStageCounterWriter counters,
        RmaQueries queries,
        Platform.Contracts.Time.IClock clock,
        CancellationToken cancellationToken)
    {
        _ = db; // Keeps Wolverine's transaction discovery explicit for the projection repair command.
        if (command.CommandId == Guid.Empty)
            throw new Contracts.CommandRefusedException("command_id_required", "A command id is required.");
        var replayed = await counters.RebuildAsync(command.CommandId, command.ActorRef, clock.UtcNow, cancellationToken);
        return new(command.CommandId, await queries.SummaryAsync(clock.UtcNow, cancellationToken), replayed);
    }
}
