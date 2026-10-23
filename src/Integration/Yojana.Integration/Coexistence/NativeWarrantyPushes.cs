using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Backfills;
using Yojana.Integration.Connections;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Coexistence;

public interface INativeWarrantyBackfillProcessor
{
    Task ProcessAsync(BackfillCursor cursor, int? maximumPages, CancellationToken cancellationToken);
}

public sealed record NativeWarrantyRemoteValues(string Status, DateOnly? WarrantyEndDate);

public static class NativeWarrantyCardStatusDerivation
{
    private static readonly HashSet<string> TerminalLifecycleStatuses =
    [
        LifecycleStatus.Void, LifecycleStatus.ScrappedPreRelease, LifecycleStatus.Scrapped,
        LifecycleStatus.Decommissioned, LifecycleStatus.Merged,
    ];

    public static string Derive(NativeWarrantyPushSource source)
    {
        // Business precedence is defined once here: Terminated > InRepair > Returned > Loaned > Active.
        // All connectors receive only this neutral result and remain free to translate it to their own codes.
        if (TerminalLifecycleStatuses.Contains(source.LifecycleStatus))
            return NativeWarrantyCardStatus.Terminated;
        if (source.LocationKind == LocationKind.InRepair)
            return NativeWarrantyCardStatus.InRepair;
        if (source.Disposition == UnitDisposition.Returned)
            return NativeWarrantyCardStatus.Returned;
        if (source.CustodyReason == EpisodeReason.Loan)
            return NativeWarrantyCardStatus.Loaned;
        return NativeWarrantyCardStatus.Active;
    }
}

/// <summary>
/// Coalesces module changes into the normal outbound queue and drives the persisted mapped-unit backfill. It knows
/// only the canonical command and the installed-base mapping contract; connector field names stay in the adapter.
/// </summary>
public sealed class NativeWarrantyPushService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    INativeWarrantyPushSourcePort sources,
    WarrantyContractMaintenanceSettings contractMaintenance) : INativeWarrantyBackfillProcessor
{
    public const string MappingEntityType = "installed_base_record";
    public const string BackfillEntity = "native_warranty_push";
    public const string DroppedFrozenOutcome = "dropped_frozen";

    public async Task<int> QueueUnitAsync(Guid unitId, Guid triggerId, CancellationToken cancellationToken)
        => await QueueUnitAsync(unitId, triggerId, statusChangeOnly: false, cancellationToken);

    public async Task<int> QueueStatusChangeAsync(Guid unitId, Guid triggerId,
        CancellationToken cancellationToken)
        => await QueueUnitAsync(unitId, triggerId, statusChangeOnly: true, cancellationToken);

    private async Task<int> QueueUnitAsync(Guid unitId, Guid triggerId, bool statusChangeOnly,
        CancellationToken cancellationToken)
    {
        var source = await sources.ReadAsync(unitId, cancellationToken);
        if (source is null) return 0;
        var cardStatus = NativeWarrantyCardStatusDerivation.Derive(source);
        var maintenance = await contractMaintenance.GetAsync(cancellationToken);
        var connections = await (from connection in db.Connections.AsNoTracking()
                                 join gateway in db.ConnectionGatewaySettings.AsNoTracking()
                                     on new { connection.TenantId, connection.ConnectionId }
                                     equals new { gateway.TenantId, gateway.ConnectionId }
                                 join capability in db.ConnectionCapabilities.AsNoTracking()
                                     on new { connection.TenantId, connection.ConnectionId }
                                     equals new { capability.TenantId, capability.ConnectionId }
                                 join map in db.ExternalIdMaps.AsNoTracking()
                                     on new { connection.TenantId, connection.ConnectionId }
                                     equals new { map.TenantId, map.ConnectionId }
                                 where connection.TenantId == tenant.TenantId
                                     && connection.Status == ConnectionStatus.Active
                                     && gateway.NativeWarrantyObjectMode == NativeWarrantyObjectModes.Pushed
                                     && !gateway.ReadOnly && gateway.OutboundMode != "none"
                                     && capability.CanonicalMessage == nameof(PushNativeWarrantyObject)
                                     && capability.SupportLevel == CapabilitySupport.Native
                                     && map.EntityType == MappingEntityType && map.ModuleId == unitId
                                     && map.ValidTo == null
                                 select new { connection.ConnectionId, connection.ConnectorCode,
                                     RemoteRecordKey = map.ExternalId }).Distinct().ToListAsync(cancellationToken);
        var queued = 0;
        foreach (var connection in connections)
            if (await QueueConnectionAsync(connection.ConnectionId, connection.ConnectorCode,
                    connection.RemoteRecordKey, unitId, source, cardStatus, maintenance.MaintainedIn, triggerId,
                    statusChangeOnly, cancellationToken))
                queued++;
        return queued;
    }

    private async Task<bool> QueueConnectionAsync(Guid connectionId, string connectorCode, string remoteRecordKey,
        Guid unitId, NativeWarrantyPushSource source, string cardStatus, string maintainedIn, Guid triggerId,
        bool statusChangeOnly,
        CancellationToken cancellationToken)
    {
        await using var activity = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId,
            connectionId, cancellationToken);
        var enabled = await db.ConnectionGatewaySettings.AsNoTracking().AnyAsync(row =>
            row.TenantId == tenant.TenantId && row.ConnectionId == connectionId
            && row.NativeWarrantyObjectMode == NativeWarrantyObjectModes.Pushed && !row.ReadOnly
            && row.OutboundMode != "none", cancellationToken);
        if (!enabled) return false;
        var now = clock.UtcNow;
        var includeContract = maintainedIn == WarrantyContractsMaintainedIn.Module;
        var state = await db.NativeWarrantyPushStates.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == connectionId && row.UnitId == unitId, cancellationToken);
        if (statusChangeOnly && state is not null
            && StringComparer.Ordinal.Equals(state.DesiredModuleStatus, cardStatus))
            return false;
        if (state is null)
        {
            state = new NativeWarrantyPushState
            {
                TenantId = tenant.TenantId, ConnectionId = connectionId, UnitId = unitId,
                RemoteRecordKey = remoteRecordKey, DesiredModuleStatus = cardStatus,
                DesiredWarrantyEndDate = includeContract ? source.WarrantyEndDate : null,
                IncludesWarrantyContractFields = includeContract, UpdatedAt = now,
            };
            db.NativeWarrantyPushStates.Add(state);
        }
        else
        {
            state.RemoteRecordKey = remoteRecordKey;
            state.DesiredModuleStatus = cardStatus;
            state.DesiredWarrantyEndDate = includeContract ? source.WarrantyEndDate : null;
            state.IncludesWarrantyContractFields = includeContract;
            state.UpdatedAt = now;
        }

        var queued = state.OutboundCommandId is { } existingId
            ? await db.OutboundCommands.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
                && row.OutboundCommandId == existingId, cancellationToken) : null;
        var idempotency = DeterministicId.Create(triggerId,
            nameof(PushNativeWarrantyObject) + ":" + connectionId.ToString("N") + ":" + unitId.ToString("N"));
        var command = new PushNativeWarrantyObject(idempotency, $"unit/{unitId:N}",
            $"YOJ-WARRANTY-{triggerId:N}", new(unitId, source.Serial ?? ""), source.LifecycleStatus,
            includeContract ? source.WarrantyEndDate : null, includeContract, cardStatus);
        var payload = JsonSerializer.Serialize(command, IntegrationContracts.Json);
        if (queued is not null && queued.Status is OutboundCommandStatus.Pending or OutboundCommandStatus.Retry)
        {
            queued.Payload = payload;
            queued.IdempotencyKey = idempotency;
            queued.ErpReferenceText = command.ErpReferenceText;
            queued.NextAttemptAt = null;
            queued.LastFailure = null;
        }
        else if (queued is not null && queued.Status == OutboundCommandStatus.Parked
            && queued.Outcome == OutboundConnectorOutcome.CoexistenceConflict)
        {
            // Keep the parked row current. The operator resolves one conflict and releases the latest values, not a
            // stale command followed by another write.
            queued.Payload = payload;
            queued.IdempotencyKey = idempotency;
            queued.ErpReferenceText = command.ErpReferenceText;
        }
        else
        {
            queued = new OutboundCommandRow
            {
                TenantId = tenant.TenantId, OutboundCommandId = TimeOrderedId.New(now),
                ConnectionId = connectionId, CommandType = nameof(PushNativeWarrantyObject),
                IdempotencyKey = idempotency, ModuleDocumentRef = command.ModuleDocumentRef,
                ErpReferenceText = command.ErpReferenceText, Payload = payload,
                Status = OutboundCommandStatus.Pending, CreatedAt = now,
            };
            db.OutboundCommands.Add(queued);
            state.OutboundCommandId = queued.OutboundCommandId;
        }
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task StartOrResumeBackfillAsync(Guid connectionId, string actor,
        CancellationToken cancellationToken)
    {
        var existing = await db.BackfillCursors.Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == connectionId && row.Entity == BackfillEntity
                && row.Status != BackfillStatus.Completed && row.Status != BackfillStatus.Cancelled)
            .OrderByDescending(row => row.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            existing.Status = BackfillStatus.Queued;
            existing.LastChangedBy = actor;
            existing.LastFailure = null;
        }
        else
        {
            var now = clock.UtcNow;
            db.BackfillCursors.Add(new BackfillCursor
            {
                TenantId = tenant.TenantId, BackfillCursorId = TimeOrderedId.New(now),
                ConnectionId = connectionId, Entity = BackfillEntity,
                RangeStart = DateOnly.FromDateTime(now), RangeEnd = DateOnly.FromDateTime(now),
                PageSize = 100, Status = BackfillStatus.Queued, RequestedBy = actor,
                LastChangedBy = actor, CreatedAt = now,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> FreezeAsync(Guid connectionId, string actor, CancellationToken cancellationToken)
    {
        var backfills = await db.BackfillCursors.Where(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == connectionId && row.Entity == BackfillEntity
            && (row.Status == BackfillStatus.Queued || row.Status == BackfillStatus.Running))
            .ToListAsync(cancellationToken);
        foreach (var backfill in backfills)
        {
            backfill.Status = BackfillStatus.Paused;
            backfill.LastChangedBy = actor;
        }
        var commands = await db.OutboundCommands.Where(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == connectionId && row.CommandType == nameof(PushNativeWarrantyObject)
            && (row.Status == OutboundCommandStatus.Pending || row.Status == OutboundCommandStatus.Retry
                || row.Status == OutboundCommandStatus.Parked)).ToListAsync(cancellationToken);
        foreach (var command in commands)
        {
            command.Status = OutboundCommandStatus.Done;
            command.Outcome = DroppedFrozenOutcome;
            command.LastFailure = "Dropped because native warranty object mode was switched to frozen.";
            command.CompletedAt = clock.UtcNow;
            command.NextAttemptAt = null;
            command.LockedUntil = null;
        }
        var ids = commands.Select(row => row.OutboundCommandId).ToList();
        var states = await db.NativeWarrantyPushStates.Where(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == connectionId && row.OutboundCommandId != null
            && ids.Contains(row.OutboundCommandId.Value)).ToListAsync(cancellationToken);
        foreach (var state in states) state.OutboundCommandId = null;
        await db.SaveChangesAsync(cancellationToken);
        return commands.Count;
    }

    public async Task ProcessAsync(BackfillCursor cursor, int? maximumPages,
        CancellationToken cancellationToken)
    {
        var pages = 0;
        cursor.Status = BackfillStatus.Running;
        cursor.StartedAt ??= clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        while (maximumPages is null || pages < maximumPages.Value)
        {
            await db.Entry(cursor).ReloadAsync(cancellationToken);
            if (cursor.Status is BackfillStatus.Paused or BackfillStatus.Cancelled) return;
            var pushed = await db.ConnectionGatewaySettings.AsNoTracking().AnyAsync(row =>
                row.TenantId == tenant.TenantId && row.ConnectionId == cursor.ConnectionId
                && row.NativeWarrantyObjectMode == NativeWarrantyObjectModes.Pushed, cancellationToken);
            if (!pushed)
            {
                cursor.Status = BackfillStatus.Paused;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            Guid? after = Guid.TryParse(cursor.Cursor, out var parsed) ? parsed : null;
            var query = db.ExternalIdMaps.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == cursor.ConnectionId && row.EntityType == MappingEntityType
                && row.ValidTo == null);
            if (after is { } value) query = query.Where(row => row.ModuleId.CompareTo(value) > 0);
            var unitIds = await query.OrderBy(row => row.ModuleId).Select(row => row.ModuleId)
                .Distinct().Take(cursor.PageSize).ToListAsync(cancellationToken);
            foreach (var unitId in unitIds)
                await QueueUnitAsync(unitId, DeterministicId.Create(cursor.BackfillCursorId,
                    unitId.ToString("N", CultureInfo.InvariantCulture)), cancellationToken);
            cursor.ProcessedMessages += unitIds.Count;
            cursor.ProcessedPages++;
            cursor.LastPageAt = clock.UtcNow;
            pages++;
            if (unitIds.Count < cursor.PageSize)
            {
                cursor.Cursor = null;
                cursor.Status = BackfillStatus.Completed;
                cursor.CompletedAt = clock.UtcNow;
                cursor.TotalMessages = cursor.ProcessedMessages;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            cursor.Cursor = unitIds[^1].ToString("N", CultureInfo.InvariantCulture);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task ApplyConflictDecisionAsync(CoexistenceConflict conflict, string decision, DateTime now,
        CancellationToken cancellationToken)
    {
        var command = await db.OutboundCommands.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == conflict.ConnectionId && row.PendingRemoteKey == conflict.ConflictId.ToString("N")
            && row.CommandType == nameof(PushNativeWarrantyObject), cancellationToken);
        var state = command is null ? null : await db.NativeWarrantyPushStates.SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.ConnectionId == conflict.ConnectionId
            && row.OutboundCommandId == command.OutboundCommandId, cancellationToken);
        if (command is null || state is null)
            throw new CoexistenceRuleException("conflict_push_missing",
                "The parked warranty push behind this conflict is no longer available.");
        if (decision == CoexistenceConflictDecision.KeepModule)
        {
            state.AllowRemoteOverwriteOnce = true;
            command.Status = OutboundCommandStatus.Pending;
            command.Outcome = null;
            command.PendingRemoteKey = null;
            command.LastFailure = null;
            command.CompletedAt = null;
            command.NextAttemptAt = null;
        }
        else
        {
            var accepted = JsonSerializer.Deserialize<NativeWarrantyRemoteValues>(conflict.RemoteValue ?? "{}",
                IntegrationContracts.Json) ?? throw new CoexistenceRuleException("conflict_remote_value_invalid",
                    "The remote warranty values on this conflict are invalid.");
            state.LastPushedRemoteStatus = accepted.Status;
            state.LastPushedWarrantyEndDate = accepted.WarrantyEndDate;
            state.LastPushedAt = now;
            command.Status = OutboundCommandStatus.Done;
            command.Outcome = "remote_value_accepted";
            command.PendingRemoteKey = null;
            command.LastFailure = null;
            command.CompletedAt = now;
        }
        conflict.Status = CoexistenceConflictStatus.Closed;
        conflict.ClosedAt = now;
    }
}

/// <summary>Wolverine consumers for warranty-relevant unit facts.</summary>
public static class NativeWarrantyPushHandler
{
    public static Task<int> Handle(EntitlementCoverageChanged message, NativeWarrantyPushService pushes,
        CancellationToken cancellationToken) =>
        pushes.QueueUnitAsync(message.Header.UnitId, message.Header.EventId, cancellationToken);

    public static Task<int> Handle(UnitLifecycleChanged message, NativeWarrantyPushService pushes,
        CancellationToken cancellationToken) =>
        pushes.QueueStatusChangeAsync(message.Header.UnitId, message.Header.EventId, cancellationToken);

    public static Task<int> Handle(CustodyChanged message, NativeWarrantyPushService pushes,
        CancellationToken cancellationToken) =>
        pushes.QueueStatusChangeAsync(message.Header.UnitId, message.Header.EventId, cancellationToken);

    public static Task<int> Handle(UnitDispositionChanged message, NativeWarrantyPushService pushes,
        CancellationToken cancellationToken) =>
        pushes.QueueStatusChangeAsync(message.Header.UnitId, message.Header.EventId, cancellationToken);
}
