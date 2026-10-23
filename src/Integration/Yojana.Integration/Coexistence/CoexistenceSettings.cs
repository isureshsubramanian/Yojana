using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Integration.Connections;

namespace Yojana.Integration.Coexistence;

/// <summary>
/// Reads and changes a connection's coexistence choices. The OD-14 mode lives on the gateway setting row beside
/// the outbound mode; the drift-scan choices are tenant settings keyed by connection.
/// </summary>
public sealed class CoexistenceSettings(IntegrationDbContext db, ITenantContext tenant, ITenantSettings settings,
    IServiceProvider services, IClock clock, IConnectionAuditSink audit,
    WarrantyContractMaintenanceSettings contractMaintenance, NativeWarrantyPushService nativeWarranty)
{
    private static readonly JsonSerializerOptions Json = new(IntegrationContracts.Json) { DictionaryKeyPolicy = null };

    public static string Key(Guid connectionId) => $"integration.coexistence.connection.{connectionId:N}";

    public async Task<CoexistenceConnectionSettings> GetAsync(Guid connectionId, CancellationToken ct)
    {
        var value = await settings.GetAsync(Key(connectionId), ct);
        return value is null ? new() : (JsonSerializer.Deserialize<CoexistenceConnectionSettings>(value, Json) ?? new()).Validate();
    }

    public ICoexistenceDriftReader? Reader(string connectorCode) =>
        services.GetKeyedService<ICoexistenceDriftReader>(connectorCode);

    public static IReadOnlyList<string> Watched(CoexistenceConnectionSettings value, ICoexistenceDriftReader reader,
        string contractsMaintainedIn = WarrantyContractsMaintainedIn.IncumbentUntilCutover) =>
        (value.WatchedFields ?? reader.DefaultWatchedFields)
        .Concat(contractsMaintainedIn == WarrantyContractsMaintainedIn.Module ? reader.WarrantyContractFields : [])
        .Distinct(StringComparer.Ordinal).ToList();

    public static IReadOnlyList<string> Explaining(CoexistenceConnectionSettings value, ICoexistenceDriftReader reader,
        string contractsMaintainedIn = WarrantyContractsMaintainedIn.IncumbentUntilCutover) =>
        (value.ExplainingFields ?? reader.DefaultExplainingFields)
        .Except(Watched(value, reader, contractsMaintainedIn), StringComparer.Ordinal).ToList();

    public async Task<CoexistenceView> ViewAsync(Guid connectionId, CancellationToken ct)
    {
        var connection = await ConnectionAsync(connectionId, ct);
        var gateway = await db.ConnectionGatewaySettings.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.ConnectionId == connectionId, ct);
        var configured = await GetAsync(connectionId, ct);
        var reader = Reader(connection.ConnectorCode);
        var state = reader is null ? null : await db.CoexistenceScanStates.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.ConnectionId == connectionId && x.RecordType == reader.RecordType, ct);
        var open = await db.CoexistenceConflicts.CountAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.Status != CoexistenceConflictStatus.Closed, ct);
        var maintenance = await contractMaintenance.GetAsync(ct);
        var pushedAvailable = await db.ConnectionCapabilities.AsNoTracking().AnyAsync(x =>
            x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
            && x.CanonicalMessage == nameof(PushNativeWarrantyObject)
            && x.SupportLevel == CapabilitySupport.Native, ct);
        return new(connectionId, connection.DisplayLabel, SyncState(connection.Status),
            gateway?.NativeWarrantyObjectMode ?? NativeWarrantyObjectModes.Frozen, maintenance.MaintainedIn,
            maintenance.MaintainedIn == WarrantyContractsMaintainedIn.Module, pushedAvailable,
            reader is not null, reader?.RecordType, configured.DriftScanEnabled,
            reader is null ? configured.WatchedFields ?? [] : Watched(configured, reader, maintenance.MaintainedIn),
            reader is null ? configured.ExplainingFields ?? [] : Explaining(configured, reader, maintenance.MaintainedIn),
            reader?.DefaultWatchedFields ?? [], reader?.DefaultExplainingFields ?? [],
            configured.PageSize, configured.MaxPagesPerRun, state?.LastCompletedAt, state?.LastPassRecords ?? 0,
            state?.Cursor is not null, open);
    }

    public async Task<CoexistenceView> UpdateAsync(Guid connectionId, UpdateCoexistenceSettings request, string actor,
        CancellationToken ct)
    {
        CoexistenceGuard.Actor(actor);
        if (request.NativeWarrantyObjectMode is not (NativeWarrantyObjectModes.Frozen or NativeWarrantyObjectModes.Pushed))
            throw new CoexistenceRuleException("native_mode_invalid", "Choose frozen or pushed.");
        var connection = await ConnectionAsync(connectionId, ct);
        if (connection.Status == ConnectionStatus.Archived)
            throw new CoexistenceRuleException("connection_archived", "An archived connection cannot be changed.");
        var pushedAvailable = await db.ConnectionCapabilities.AsNoTracking().AnyAsync(x =>
            x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
            && x.CanonicalMessage == nameof(PushNativeWarrantyObject)
            && x.SupportLevel == CapabilitySupport.Native, ct);
        if (request.NativeWarrantyObjectMode == NativeWarrantyObjectModes.Pushed && !pushedAvailable)
            throw new CoexistenceRuleException("pushed_mode_unavailable",
                "This connector does not support pushing values to its native warranty object.");
        var value = new CoexistenceConnectionSettings(request.WatchedFields, request.ExplainingFields,
            request.DriftScanEnabled, request.PageSize, request.MaxPagesPerRun).Validate();
        await using var activity = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId,
            connectionId, ct);
        var gateway = await db.ConnectionGatewaySettings.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId, ct)
            ?? throw new CoexistenceRuleException("connection_not_found", "The connection has no gateway settings.");
        var before = new { gateway.NativeWarrantyObjectMode, Settings = await GetAsync(connectionId, ct) };
        var modeChanged = gateway.NativeWarrantyObjectMode != request.NativeWarrantyObjectMode;
        gateway.NativeWarrantyObjectMode = request.NativeWarrantyObjectMode;
        await db.SaveChangesAsync(ct);
        var dropped = 0;
        if (modeChanged && request.NativeWarrantyObjectMode == NativeWarrantyObjectModes.Pushed)
            await nativeWarranty.StartOrResumeBackfillAsync(connectionId, actor, ct);
        else if (modeChanged)
            dropped = await nativeWarranty.FreezeAsync(connectionId, actor, ct);
        await settings.SetAsync(Key(connectionId), JsonSerializer.Serialize(value, Json), ct);
        await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow), connectionId,
            ConnectionAuditEventType.CoexistenceSettingsChanged, actor, clock.UtcNow,
            JsonSerializer.Serialize(new { Before = before,
                After = new { request.NativeWarrantyObjectMode, Settings = value }, DroppedQueuedPushes = dropped },
                Json)), ct);
        return await ViewAsync(connectionId, ct);
    }

    internal static string SyncState(string status) => status switch
    {
        ConnectionStatus.Active => "running",
        ConnectionStatus.Paused => "stopped",
        _ => status,
    };

    private async Task<Connection> ConnectionAsync(Guid connectionId, CancellationToken ct) =>
        await db.Connections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId, ct)
        ?? throw new CoexistenceRuleException("connection_not_found", "The connection was not found.");
}

internal static class CoexistenceGuard
{
    public static void Actor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new CoexistenceRuleException("audit_actor_required", "An authenticated actor is required.");
    }

    public static string Text(string? value, int max, string rule, string message)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > max) throw new CoexistenceRuleException(rule, message);
        return trimmed;
    }
}
