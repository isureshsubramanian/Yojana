using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Yojana.Integration.Data;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Contracts.Security;
using Yojana.Integration.LegacyMigration.Profiling;

namespace Yojana.Integration.LegacyMigration;

public sealed class MigrationBatchService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    Staging.SourceExtractor extractor,
    IUserDirectory people,
    IMigrationProfileQueue profiles)
{
    public async Task<StartMigrationBatchResponse> StartAsync(StartMigrationBatchRequest request, string actor,
        CancellationToken cancellationToken)
    {
        Actor(actor);
        var source = await db.MigrationSources.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.ConnectionId == request.SourceId, cancellationToken)
            ?? throw new MigrationRuleException("migration_source_not_found", "The migration source was not found.");
        var failure = source.LicencePosition != MigrationLicencePosition.Confirmed
            ? "migration_extraction_licence_unconfirmed"
            : source.Runtime != MigrationSourceRuntime.SqlServer
                ? "migration_source_runtime_unsupported" : null;
        var now = clock.UtcNow;
        var batch = new MigrationBatch
        {
            TenantId = tenant.TenantId,
            BatchId = Guid.CreateVersion7(now),
            SourceSystem = source.SourceSystem,
            ConnectionId = source.ConnectionId,
            Kind = "full",
            RequestedBy = actor,
            RequestedAt = now,
            FinishedAt = failure is null ? null : now,
            Status = failure is null ? MigrationBatchStatus.Queued : MigrationBatchStatus.Refused,
            FailureText = failure,
        };
        db.MigrationBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);
        return new(batch.BatchId, batch.Status, batch.FailureText);
    }

    public async Task<MigrationBatchPage> ListAsync(string? cursor, int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 200);
        var query = db.MigrationBatches.AsNoTracking().Where(x => x.TenantId == tenant.TenantId);
        if (Decode(cursor) is { } after)
            query = query.Where(x => x.RequestedAt < after.At ||
                (x.RequestedAt == after.At && x.BatchId.CompareTo(after.Id) < 0));
        var rows = await query.OrderByDescending(x => x.RequestedAt).ThenByDescending(x => x.BatchId)
            .Take(take + 1).ToListAsync(cancellationToken);
        var pageRows = rows.Take(take).ToList();
        var names = await people.DisplayNamesAsync(pageRows.Select(x => x.RequestedBy).Distinct().ToList(),
            cancellationToken);
        var items = pageRows.Select(row => View(row, names.GetValueOrDefault(row.RequestedBy)
            ?? row.RequestedBy)).ToList();
        var next = rows.Count > take && items.Count != 0
            ? Encode(new(items[^1].RequestedAt, items[^1].BatchId)) : null;
        return new(items, next);
    }

    public async Task<MigrationBatchView?> DetailAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await db.MigrationBatches.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.BatchId == batchId, cancellationToken);
        if (batch is null) return null;
        var tables = await db.MigrationBatchTables.AsNoTracking().Where(x =>
                x.TenantId == tenant.TenantId && x.BatchId == batchId)
            .OrderBy(x => x.SourceTable).Select(x => new MigrationBatchTableView(x.SourceTable, x.RowsRead,
                x.Inserted, x.Updated, x.Unchanged, x.Absent, x.DurationMilliseconds))
            .ToListAsync(cancellationToken);
        var names = await people.DisplayNamesAsync([batch.RequestedBy], cancellationToken);
        return View(batch, names.GetValueOrDefault(batch.RequestedBy) ?? batch.RequestedBy, tables);
    }

    public async Task<int> RunPendingAsync(CancellationToken cancellationToken)
    {
        var completed = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await ClaimAsync(cancellationToken);
            if (batch is null) return completed;
            try
            {
                var source = await db.MigrationSources.AsNoTracking().SingleAsync(x =>
                    x.TenantId == tenant.TenantId && x.ConnectionId == batch.ConnectionId, cancellationToken);
                if (source.LicencePosition != MigrationLicencePosition.Confirmed)
                    throw new MigrationRuleException("migration_extraction_licence_unconfirmed",
                        "The source licence position is no longer confirmed.");
                if (source.Runtime != MigrationSourceRuntime.SqlServer)
                    throw new MigrationRuleException("migration_source_runtime_unsupported",
                        "Only sql_server migration sources are extractable in this build.");
                var results = await extractor.ExtractFullAsync(source, batch.BatchId, clock.UtcNow,
                    cancellationToken);
                db.MigrationBatchTables.AddRange(results.Select(result => new MigrationBatchTable
                {
                    TenantId = tenant.TenantId,
                    BatchId = batch.BatchId,
                    SourceTable = result.SourceTable,
                    RowsRead = result.RowsRead,
                    Inserted = result.Inserted,
                    Updated = result.Updated,
                    Unchanged = result.Unchanged,
                    Absent = result.Absent,
                    DurationMilliseconds = result.DurationMilliseconds,
                }));
                batch.Status = MigrationBatchStatus.Succeeded;
                batch.FinishedAt = clock.UtcNow;
                // TIGHT COUPLING (successful extraction/profile hand-off): this queue operation shares the success
                // SaveChanges so no succeeded extraction can exist without its mandatory automatic profile run.
                // MigrationProfileService documents the matching half; separating the saves can strand a batch.
                profiles.EnqueueAutomatic(batch);
                await db.SaveChangesAsync(cancellationToken);
                completed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                profiles.DiscardAutomatic(batch.BatchId);
                batch.Status = MigrationBatchStatus.Failed;
                batch.FailureText = "migration_extraction_cancelled";
                batch.FinishedAt = clock.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                profiles.DiscardAutomatic(batch.BatchId);
                batch.Status = ex is MigrationRuleException rule &&
                    rule.Rule is "migration_extraction_licence_unconfirmed" or "migration_source_runtime_unsupported"
                    ? MigrationBatchStatus.Refused : MigrationBatchStatus.Failed;
                // Driver text may include the server, database, login or connection details. Only local codes persist.
                batch.FailureText = ex is MigrationRuleException local ? local.Rule : "migration_extraction_failed";
                batch.FinishedAt = clock.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        return completed;
    }

    private async Task<MigrationBatch?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var busySources = db.MigrationBatches.Where(x => x.TenantId == tenant.TenantId &&
            x.Status == MigrationBatchStatus.Running).Select(x => x.SourceSystem);
        var batch = await db.MigrationBatches.Where(x => x.TenantId == tenant.TenantId &&
                x.Status == MigrationBatchStatus.Queued && !busySources.Contains(x.SourceSystem))
            .OrderBy(x => x.RequestedAt).ThenBy(x => x.BatchId).FirstOrDefaultAsync(cancellationToken);
        if (batch is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        // TIGHT COUPLING (single running extraction): this transition and MigrationModel's filtered unique index
        // jointly enforce one running batch per tenant/source. Removing the index makes concurrent workers race;
        // changing the running state here without changing its filter makes every claim fail open.
        batch.Status = MigrationBatchStatus.Running;
        batch.StartedAt = clock.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return batch;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return null;
        }
    }

    private static MigrationBatchView View(MigrationBatch row, string requestedByName,
        IReadOnlyList<MigrationBatchTableView>? tables = null) => new(row.BatchId, row.ConnectionId,
        row.SourceSystem, row.Kind, requestedByName, row.RequestedAt, row.StartedAt, row.FinishedAt, row.Status,
        row.FailureText, tables);

    private static void Actor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 320)
            throw new MigrationRuleException("audit_actor_required", "The authenticated user has no actor id.");
    }

    private static string Encode(Cursor cursor) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(cursor)));
    private static Cursor? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try { return JsonSerializer.Deserialize<Cursor>(Encoding.UTF8.GetString(Convert.FromBase64String(cursor))); }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new MigrationRuleException("migration_cursor_invalid", "The page cursor is invalid.");
        }
    }

    private sealed record Cursor(DateTime At, Guid Id);
}
