using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Yojana.Integration.Connections;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Coexistence;

/// <summary>
/// Compares module-owned fields on remote records with their accepted baseline (ADR-0001 "Coexistence design",
/// conflict rules). The remote wins on stock and documents, the module on owner, condition, entitlement, RMA and
/// certificate; so a watched field that changed WITHOUT a document explaining it is parked as a conflict for a person.
/// Nothing here writes to the remote or to the module's own records, and no conflict is ever resolved automatically:
/// the only closure the scan makes is when the remote value returns to the baseline, i.e. the edit no longer exists.
/// </summary>
public sealed class CoexistenceDriftService(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    CoexistenceSettings coexistence, WarrantyContractMaintenanceSettings contractMaintenance)
{
    public const string SourceObserved = "observed";
    public const string SourceDocument = "document";
    public const string SourceDecision = "decision";
    public const string SourcePushed = "pushed";

    public async Task<int> ScanScheduledAsync(CancellationToken ct)
    {
        var connections = await db.Connections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.Status == ConnectionStatus.Active).OrderBy(x => x.ConnectionId)
            .Select(x => new { x.ConnectionId, x.ConnectorCode }).ToListAsync(ct);
        var scanned = 0;
        foreach (var connection in connections)
        {
            if (coexistence.Reader(connection.ConnectorCode) is null) continue;
            if (!(await coexistence.GetAsync(connection.ConnectionId, ct)).DriftScanEnabled) continue;
            await ScanAsync(connection.ConnectionId, ct);
            scanned++;
        }
        return scanned;
    }

    public async Task<DriftScanResult> ScanAsync(Guid connectionId, CancellationToken ct)
    {
        await using var operation = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId, connectionId, ct);
        var connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId, ct)
            ?? throw new CoexistenceRuleException("connection_not_found", "The connection was not found.");
        if (connection.Status != ConnectionStatus.Active)
            throw new CoexistenceRuleException("connection_inactive", "Only a running connection can be scanned.");
        var reader = coexistence.Reader(connection.ConnectorCode);
        if (reader is null) return new(connectionId, false, 0, 0, 0, 0, 0, 0, false);
        var configured = await coexistence.GetAsync(connectionId, ct);
        var maintainedIn = (await contractMaintenance.GetAsync(ct)).MaintainedIn;
        var watched = CoexistenceSettings.Watched(configured, reader, maintainedIn);
        var explaining = CoexistenceSettings.Explaining(configured, reader, maintainedIn);
        var fields = watched.Concat(explaining).ToList();

        var state = await db.CoexistenceScanStates.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.RecordType == reader.RecordType, ct);
        if (state is null)
        {
            state = new() { TenantId = tenant.TenantId, ConnectionId = connectionId, RecordType = reader.RecordType };
            db.CoexistenceScanStates.Add(state);
        }
        var counts = new Counts();
        var passCompleted = false;
        for (var page = 0; page < configured.MaxPagesPerRun; page++)
        {
            if (state.Cursor is null)
            {
                state.PassStartedAt = clock.UtcNow;
                state.LastPassRecords = 0;
            }
            var read = await reader.ReadPageAsync(connectionId, fields, state.Cursor, configured.PageSize, ct);
            counts.Pages++;
            await ApplyAsync(connectionId, reader.RecordType, read.Records, watched, explaining,
                reader.WarrantyContractFields, maintainedIn, counts, ct);
            state.LastPassRecords += read.Records.Count;
            state.Cursor = read.NextCursor;
            state.UpdatedAt = clock.UtcNow;
            if (read.NextCursor is null)
            {
                state.LastCompletedAt = clock.UtcNow;
                passCompleted = true;
            }
            await db.SaveChangesAsync(ct);
            if (passCompleted) break;
        }
        return new(connectionId, true, counts.Pages, counts.Records, counts.NewBaselines, counts.Explained,
            counts.Raised, counts.Closed, passCompleted);
    }

    private async Task ApplyAsync(Guid connectionId, string recordType, IReadOnlyList<CoexistenceRemoteRecord> records,
        IReadOnlyList<string> watched, IReadOnlyList<string> explaining, IReadOnlyList<string> contractFields,
        string maintainedIn, Counts counts, CancellationToken ct)
    {
        if (records.Count == 0) return;
        var keys = records.Select(x => x.RecordKey).Distinct(StringComparer.Ordinal).ToList();
        var baselines = await db.CoexistenceBaselines.Where(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId && x.RecordType == recordType && keys.Contains(x.RecordKey))
            .ToListAsync(ct);
        var conflicts = await db.CoexistenceConflicts.Where(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId && x.RecordType == recordType && keys.Contains(x.RecordKey)
                && x.Status != CoexistenceConflictStatus.Closed)
            .ToListAsync(ct);
        var now = clock.UtcNow;
        foreach (var record in records)
        {
            counts.Records++;
            var hash = ExplainingHash(record, explaining);
            foreach (var field in watched)
            {
                var remote = Value(record, field);
                var baseline = baselines.FirstOrDefault(x => x.RecordKey == record.RecordKey && x.FieldName == field);
                var conflict = conflicts.FirstOrDefault(x => x.RecordKey == record.RecordKey && x.FieldName == field);
                if (conflict is not null)
                {
                    if (remote == conflict.BaselineValue)
                    {
                        // The edit no longer exists remotely (reverted there, before or after a decision).
                        await CloseAsync(conflict, now, ct);
                        counts.Closed++;
                        if (baseline is not null) { baseline.ExplainingHash = hash; baseline.UpdatedAt = now; }
                    }
                    else
                    {
                        if (maintainedIn == WarrantyContractsMaintainedIn.Module && contractFields.Contains(field))
                            conflict.Kind = CoexistenceConflictKind.ContractChangedInIncumbent;
                        conflict.RemoteValue = remote;
                        conflict.LastSeenAt = now;
                        conflict.IncumbentChangedAt = record.ChangedAt ?? conflict.IncumbentChangedAt;
                    }
                    continue;
                }
                if (baseline is null)
                {
                    baseline = new()
                    {
                        TenantId = tenant.TenantId, ConnectionId = connectionId, RecordType = recordType,
                        RecordKey = record.RecordKey, FieldName = field, Value = remote, ExplainingHash = hash,
                        Source = SourceObserved, UpdatedAt = now,
                    };
                    db.CoexistenceBaselines.Add(baseline);
                    baselines.Add(baseline);
                    counts.NewBaselines++;
                    continue;
                }
                if (remote == baseline.Value)
                {
                    if (baseline.ExplainingHash != hash) { baseline.ExplainingHash = hash; baseline.UpdatedAt = now; }
                    continue;
                }
                if (baseline.ExplainingHash != hash)
                {
                    // A posted document moved the record (a new delivery re-points the owner, for example).
                    baseline.Value = remote;
                    baseline.ExplainingHash = hash;
                    baseline.Source = SourceDocument;
                    baseline.UpdatedAt = now;
                    counts.Explained++;
                    continue;
                }
                conflict = new()
                {
                    TenantId = tenant.TenantId, ConflictId = Guid.CreateVersion7(now), ConnectionId = connectionId,
                    RecordType = recordType, RecordKey = record.RecordKey, RecordLabel = Truncate(record.Label, 300)!,
                    Kind = maintainedIn == WarrantyContractsMaintainedIn.Module && contractFields.Contains(field)
                        ? CoexistenceConflictKind.ContractChangedInIncumbent
                        : CoexistenceConflictKind.FieldChangedInIncumbent,
                    FieldName = field, BaselineValue = baseline.Value, RemoteValue = remote,
                    Status = CoexistenceConflictStatus.Open, DetectedAt = now, LastSeenAt = now,
                    IncumbentChangedAt = record.ChangedAt,
                };
                db.CoexistenceConflicts.Add(conflict);
                conflicts.Add(conflict);
                counts.Raised++;
            }
        }
    }

    private async Task CloseAsync(CoexistenceConflict conflict, DateTime now, CancellationToken ct)
    {
        conflict.Status = CoexistenceConflictStatus.Closed;
        conflict.ClosedAt = now;
        conflict.LastSeenAt = now;
        if (conflict.Decision is null)
        {
            conflict.Decision = "reverted_remotely";
            conflict.DecisionNote = "The remote value returned to the baseline before anyone decided.";
        }
        if (conflict.ManualTaskId is { } taskId)
        {
            var task = await db.ManualOutboundTasks.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
                && x.ManualOutboundTaskId == taskId && x.Status == ManualTaskStatus.Open, ct);
            if (task is not null)
            {
                task.Status = ManualTaskStatus.Completed;
                task.CompletedBy = "coexistence-drift-scan";
                task.CompletedAt = now;
                task.Note = "The drift scan saw the remote value back at the baseline.";
            }
        }
    }

    internal static string ExplainingHash(CoexistenceRemoteRecord record, IReadOnlyList<string> explaining)
    {
        var text = string.Join('\u001f', explaining.Select(field => field + '\u001e' + (Value(record, field) ?? "\u0000")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string? Value(CoexistenceRemoteRecord record, string field) =>
        Truncate(record.Values.TryGetValue(field, out var value) ? value : null, 1000);

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    private sealed class Counts
    {
        public int Pages, Records, NewBaselines, Explained, Raised, Closed;
    }
}

[DisallowConcurrentExecution]
public sealed class CoexistenceDriftJob(ITenantContextSetter setter, CoexistenceDriftService service) : TenantJob(setter)
{
    public const string JobName = "integration-coexistence-drift";
    // After the nightly reconciliation (01:00), so the two never compete for one connection's call budget.
    public const string Cron = "0 30 2 * * ?";

    protected override async Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await service.ScanScheduledAsync(cancellationToken);
}
