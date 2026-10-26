using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connectors;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.Notifications;
using Yojana.Integration.Reconciliation;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Contracts.Paging;

namespace Yojana.Integration.Connections;

public sealed class ConnectionQueries(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    ConnectionHealthSettings healthSettings, ReconciliationSettings reconciliationSettings,
    ReconciliationBusinessDay reconciliationDay,
    ConnectorOnboardingChecklistGenerator checklist,
    IConnectionThrottleProfileStore throttles, IUserDirectory people, IServiceProvider services,
    Coexistence.WarrantyContractMaintenanceSettings warrantyContracts)
{
    public async Task<RegistrationConnectorPage> RegistrationConnectorsAsync(string? sort, string? direction,
        string? cursor, int? take, CancellationToken ct)
    {
        var query = db.ConnectorCatalogue.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.Enabled);
        var paging = ConnectionEndpoints.ConnectorPages.Parse(sort, direction, take, cursor, defaultTake: 12);
        var rows = await paging.Apply(query).Take(paging.Take + 1).ToListAsync(ct);
        var pageRows = rows.Take(paging.Take).ToList();
        var items = pageRows.Select(x => (Entry: x, Form: services.GetKeyedService<IConnectionLifecycleAdapter>(x.ConnectorCode)?.RegistrationForm))
            .Where(x => x.Form is not null).Select(x => new RegistrationConnector(x.Entry.ConnectorCode, x.Entry.DisplayName, x.Form!)).ToList();
        return new(items, rows.Count > paging.Take ? paging.CursorFor(pageRows[^1]) : null);
    }

    public Task<RegistrationConnectorPage> RegistrationConnectorsAsync(string? cursor, int take, CancellationToken ct) =>
        RegistrationConnectorsAsync(null, null, cursor, take, ct);

    public async Task<ConnectionPage> ListAsync(string? sort, string? direction, string? cursor, int? take,
        bool archived, CancellationToken ct)
    {
        var query = db.Connections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && (archived ? x.Status == ConnectionStatus.Archived : x.Status != ConnectionStatus.Archived));
        var paging = ConnectionEndpoints.ConnectionPages.Parse(sort, direction, take, cursor, defaultTake: 50);
        var rows = await paging.Apply(query).Take(paging.Take + 1).ToListAsync(ct);
        var pageRows = rows.Take(paging.Take).ToList();
        var ids = pageRows.Select(x => x.ConnectionId).ToList();
        var health = await db.ConnectionHealth.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && ids.Contains(x.ConnectionId)).ToDictionaryAsync(x => x.ConnectionId, ct);
        var gateway = await db.ConnectionGatewaySettings.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && ids.Contains(x.ConnectionId)).ToDictionaryAsync(x => x.ConnectionId, ct);
        var codes = rows.Select(x => x.ConnectorCode).Distinct().ToList();
        var accepted = await db.ConnectorAcceptanceRuns.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && ids.Contains(x.ConnectionId)
                && x.Status == ConnectorAcceptanceRunStatus.Passed)
            .Select(x => x.ConnectionId).Distinct().ToListAsync(ct);
        var catalogue = await db.ConnectorCatalogue.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && codes.Contains(x.ConnectorCode)).ToDictionaryAsync(x => x.ConnectorCode, x => x.DisplayName, ct);
        var sync = await SyncStatesAsync(pageRows, health.ToDictionary(x => x.Key,
            x => x.Value.OutboxDepth), ct);
        var views = pageRows.Select(connection =>
        {
            var state = health.GetValueOrDefault(connection.ConnectionId) ?? Missing(connection);
            var configured = gateway.GetValueOrDefault(connection.ConnectionId);
            return new ConnectionSummary(connection.ConnectionId, connection.DisplayLabel, connection.ConnectorCode,
                catalogue.GetValueOrDefault(connection.ConnectorCode) ?? connection.ConnectorCode,
                connection.Status == ConnectionStatus.Paused ? "paused" : state.Status,
                connection.Status == ConnectionStatus.Active && state.IsLive,
                configured?.OutboundMode ?? "none", configured?.ReadOnly ?? true,
                View(state), accepted.Contains(connection.ConnectionId), sync[connection.ConnectionId]);
        }).ToList();
        return new(views, rows.Count > paging.Take ? paging.CursorFor(pageRows[^1]) : null, await KpisAsync(ct));
    }

    public Task<ConnectionPage> ListAsync(string? cursor, int take, bool archived, CancellationToken ct) =>
        ListAsync(null, null, cursor, take, archived, ct);

    public async Task<TenantStatusPage> StatusAsync(string? sort, string? direction, string? cursor, int? take,
        CancellationToken ct)
    {
        var page = await ListAsync(sort, direction, cursor, take, false, ct);
        return new(page.Items.Select(row => new TenantConnectionStatus(row.ConnectionId, row.ConnectionName,
            row.Status, row.IsLive, row.Health.CheckedUpTo, row.Health.WaitingToSend, row.Health.OpenDeadLetters,
            row.Health.OpenManualTasks, row.Status is "down" or "degraded" ? 1 : 0, row.Health.UpdatedAt,
            row.Sync)).ToList(), page.NextCursor, page.Kpis);
    }

    public Task<TenantStatusPage> StatusAsync(string? cursor, int take, CancellationToken ct) =>
        StatusAsync(null, null, cursor, take, ct);

    public async Task<ConnectionDetail> DetailAsync(Guid id, CancellationToken ct)
    {
        var connection = await ConnectionAsync(id, ct);
        var health = await db.ConnectionHealth.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == id, ct) ?? Missing(connection);
        var gateway = await db.ConnectionGatewaySettings.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == id, ct);
        var name = await db.ConnectorCatalogue.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectorCode == connection.ConnectorCode).Select(x => x.DisplayName).SingleOrDefaultAsync(ct)
            ?? connection.ConnectorCode;
        var capabilities = await db.ConnectionCapabilities.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == id).OrderBy(x => x.CanonicalMessage)
            .Select(x => new CapabilityView(x.CanonicalMessage, x.SupportLevel)).ToListAsync(ct);
        var secrets = await db.ConnectionCredentials.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == id && x.RotatedAt == null).OrderBy(x => x.Purpose)
            .Select(x => new CredentialState(x.Purpose, x.RetiredAt == null, x.CreatedAt, x.ExpiresAt,
                x.RotationDueAt, x.RetiredAt)).ToListAsync(ct);
        var rules = await db.ConnectionDegradationSettings.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == id).OrderBy(x => x.RuleNumber)
            .Select(x => new DegradationRuleSetting(x.RuleNumber, x.Enabled, x.SettingsJson)).ToListAsync(ct);
        var adapter = services.GetKeyedService<IConnectionLifecycleAdapter>(connection.ConnectorCode);
        JsonElement? connectorSettings = null;
        if (adapter is not null)
        {
            try { connectorSettings = await adapter.ReadSettingsAsync(id, ct); }
            catch (InvalidOperationException) when (!connection.RegistrationCompleted) { }
            foreach (var purpose in adapter.CredentialPurposes.Where(purpose => !secrets.Any(secret => secret.Purpose == purpose)))
                secrets.Add(new(purpose, false, null, null, null, null));
        }
        var acceptancePassed = await db.ConnectorAcceptanceRuns.AsNoTracking().AnyAsync(x =>
            x.TenantId == tenant.TenantId && x.ConnectionId == id
            && x.Status == ConnectorAcceptanceRunStatus.Passed, ct);
        var sync = (await SyncStatesAsync([connection], new Dictionary<Guid, int>
        {
            [id] = health.OutboxDepth,
        }, ct))[id];
        return new(new(id, connection.DisplayLabel, connection.ConnectorCode, name,
                connection.Status == ConnectionStatus.Paused ? "paused" : health.Status,
                connection.Status == ConnectionStatus.Active && health.IsLive,
                gateway.OutboundMode, gateway.ReadOnly, View(health), acceptancePassed, sync), connection.CreatedAt,
            connection.ArchivedAt,
            capabilities, new(gateway.OutboundMode, gateway.ReadOnly, gateway.NativeWarrantyObjectMode,
                (await warrantyContracts.GetAsync(ct)).MaintainedIn,
                gateway.OrderingWaitMinutes, await healthSettings.GetAsync(id, ct),
                (await throttles.GetAsync(new(tenant.TenantId, id), ct))?.Profile, connectorSettings, rules,
                gateway.SyncStartMode is null ? null : new ConnectionSyncStart(gateway.SyncStartMode,
                    gateway.SyncStartDate, gateway.MasterDataFull), gateway.ServiceCertificateSha256), secrets,
            await checklist.GenerateAsync(id, ct), connection.Status,
            await reconciliationSettings.WindowDaysAsync(id, ct), await reconciliationDay.TodayAsync(id, ct),
            adapter is null ? null : await adapter.AuthorisationAsync(id, ct),
            adapter?.Features ?? ConnectionFeatures.None, sync);
    }

    public async Task<ConnectionHealthDetail> HealthAsync(Guid id, CancellationToken ct)
    {
        var connection = await ConnectionAsync(id, ct);
        var row = await db.ConnectionHealth.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == id, ct) ?? Missing(connection);
        if (connection.Status == ConnectionStatus.Paused)
        {
            row.Status = "paused";
            row.IsLive = false;
        }
        // A stopped connection may be waiting on the final migration load's hand-over (OD-60); a running one is not.
        var notes = services.GetService(typeof(IConnectionHandOverNotes)) as IConnectionHandOverNotes;
        var handOver = connection.Status == ConnectionStatus.Active || notes is null ? null
            : await notes.ForAsync(id, ct);
        var sync = (await SyncStatesAsync([connection], new Dictionary<Guid, int>
        {
            [id] = row.OutboxDepth,
        }, ct))[id];
        return new(id, connection.DisplayLabel, View(row),
            JsonSerializer.Deserialize<List<RecordTypeHealth>>(row.RecordTypesJson, IntegrationContracts.Json) ?? [],
            handOver, sync);
    }

    public async Task<ConnectionEventPage> EventsAsync(Guid id, string? sort, string? direction, string? cursor,
        int? take, CancellationToken ct)
    {
        _ = await ConnectionAsync(id, ct);
        var query = db.ConnectionEvents.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == id);
        var paging = ConnectionEndpoints.EventPages.Parse(sort, direction, take, cursor, defaultTake: 50);
        var rows = await paging.Apply(query).Take(paging.Take + 1).ToListAsync(ct);
        var pageRows = rows.Take(paging.Take).ToList();
        var names = await people.DisplayNamesAsync(pageRows.Select(x => x.ActorRef).Distinct().ToList(), ct);
        return new(pageRows.Select(x => new ConnectionEventView(x.SequenceNo, x.EventType, x.OccurredAt,
                names.GetValueOrDefault(x.ActorRef) ?? (x.ActorRef.StartsWith("system:", StringComparison.Ordinal)
                    ? "System" : "Unknown user"), EventSummary(x.EventType))).ToList(),
            rows.Count > paging.Take ? paging.CursorFor(pageRows[^1]) : null);
    }

    public Task<ConnectionEventPage> EventsAsync(Guid id, string? cursor, int take, CancellationToken ct) =>
        EventsAsync(id, null, null, cursor, take, ct);

    // Hidden coupling: the web client's connection schemas (web/src/integration/adminTypes.ts) treat Sync as always
    // present, because every list, status and detail view is built from this method, which returns an entry for each
    // connection. The contracts declare Sync nullable only for other constructors; a view sent with Sync null would
    // show "unexpected response" on the Integration screens.
    private async Task<IReadOnlyDictionary<Guid, ConnectionSyncState>> SyncStatesAsync(
        List<Connection> connections, IReadOnlyDictionary<Guid, int> waiting, CancellationToken ct)
    {
        if (connections.Count == 0) return new Dictionary<Guid, ConnectionSyncState>();
        var ids = connections.Select(x => x.ConnectionId).ToList();
        var watermarks = await db.ConnectionPollWatermarks.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && ids.Contains(x.ConnectionId))
            .Select(x => new { x.ConnectionId, x.Watermark }).ToListAsync(ct);
        var actorRefs = connections.Where(x => !string.IsNullOrWhiteSpace(x.SyncStoppedBy))
            .Select(x => x.SyncStoppedBy!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var names = actorRefs.Count == 0
            ? new Dictionary<string, string>()
            : await people.DisplayNamesAsync(actorRefs, ct);
        return connections.ToDictionary(connection => connection.ConnectionId, connection =>
        {
            var resumedAt = connection.SyncResumedAt;
            var rows = watermarks.Where(x => x.ConnectionId == connection.ConnectionId).ToList();
            var seed = services.GetKeyedService<IConnectorWatermarkSeed>(connection.ConnectorCode);
            var catchingUp = connection.Status == ConnectionStatus.Active && resumedAt is not null
                && rows.Count > 0 && (seed is null || rows.Any(row => row.Watermark is not { Length: > 0 } value
                    || seed.InstantOf(value) is not { } instant || instant <= resumedAt));
            string? stoppedByName = null;
            if (connection.SyncStoppedBy is { Length: > 0 } actor)
                stoppedByName = names.GetValueOrDefault(actor) ?? (actor.StartsWith(IUserDirectory.AutomaticActorPrefix,
                    StringComparison.Ordinal) ? "System" : "Unknown user");
            return new ConnectionSyncState(connection.Status == ConnectionStatus.Paused ? "stopped" : "running",
                connection.SyncStoppedAt, stoppedByName, connection.SyncStopReason, resumedAt, catchingUp,
                connection.NotificationControlState, connection.NotificationControlFailureCode,
                waiting.GetValueOrDefault(connection.ConnectionId));
        });
    }

    private async Task<ConnectionKpis> KpisAsync(CancellationToken ct)
    {
        var active = db.Connections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.Status != ConnectionStatus.Archived);
        var count = await active.CountAsync(ct);
        var summary = await db.ConnectionHealth.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && active.Any(connection => connection.ConnectionId == x.ConnectionId))
            .GroupBy(x => x.TenantId).Select(group => new
            {
                RelayConnected = group.Count(x => x.RelayConnected),
                Live = group.Count(x => x.IsLive), Paused = group.Count(x => x.Status == "paused"
                    || x.Status == "reconsent_required" || x.Status == "disabled"),
                Today = group.Sum(x => x.ReceivedToday), Month = group.Sum(x => x.ReceivedLast30Days),
                Waiting = group.Sum(x => x.OutboxDepth), Oldest = group.Min(x => x.OldestWaitingAt),
                DeadLetters = group.Sum(x => x.OpenDeadLetters), Tasks = group.Sum(x => x.OpenManualTasks),
                Attention = group.Count(x => x.Status == "down" || x.Status == "degraded"
                    || x.Status == "reconsent_required"),
            }).SingleOrDefaultAsync(ct);
        return new(count, summary?.Live ?? 0, summary?.Paused ?? 0, summary?.Today ?? 0, summary?.Month ?? 0,
            summary?.Waiting ?? 0, summary?.Oldest, summary?.DeadLetters ?? 0, summary?.Tasks ?? 0, summary?.Attention ?? 0, summary?.RelayConnected ?? 0);
    }

    private static string EventSummary(string kind) => kind switch
    {
        "ConnectionRegistered" => "Connection registered",
        "ConnectionArchived" => "Connection archived",
        "ConnectionPaused" => "Connection paused",
        "StreamStopped" => "Remote notifications stopped",
        "StreamResumedByWatchdog" => "Remote notifications resumed by the watchdog",
        "CallRateSlowedOnBusy" => "Calls delayed after the remote reported busy",
        "OutOfHoursReplayBooked" => "Notification replay booked outside business hours",
        "PasswordDue" => "Integration credential due for replacement",
        "HealthStatusChanged" => "Connection health changed",
        "CredentialSet" => "Connection credential set",
        "CredentialRotated" => "Connection credential replaced",
        "OnboardingItemConfirmed" => "Onboarding checklist item confirmed",
        "WarrantyContractMaintenanceChanged" => "Warranty contract maintenance changed",
        "ConsentGranted" => "Connection consent granted",
        "ConsentDisabled" => "Connection disabled and tokens deleted",
        "ConsentRequired" => "Fresh connection consent required",
        "ConsentExpiry14Days" => "Connection consent expires within 14 days",
        "ConsentExpiry3Days" => "Connection consent expires within 3 days",
        "ConsentExpiryReminder" => "Connection consent expiry reminder sent",
        "ConsentPauseNotified" => "Connection sync pause notification sent",
        "ConsentReminderSettingsChanged" => "Connection consent reminder settings changed",
        "ConsentRenewed" => "Connection access renewed",
        "ExtensionChecked" => "Tenant-side extension checked",
        "ConnectionSyncBaselined" => "Connection reading re-baselined",
        "ConnectionCertificatePinned" => "Server certificate pin changed",
        "SyncStopped" => "Sync stopped",
        "SyncResumed" => "Sync resumed",
        _ => "Connection activity recorded",
    };

    private async Task<Connection> ConnectionAsync(Guid id, CancellationToken ct) =>
        await db.Connections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == id, ct)
        ?? throw new ConnectionRuleException("connection_not_found", "The connection was not found.");
    private ConnectionHealthView View(ConnectionHealth row) => new(row.Status, row.IsLive, row.LastInboundAt,
        row.CheckedUpTo, row.NotificationState, row.ReceivedToday, row.ReceivedLast30Days, row.OutboxDepth,
        row.OldestWaitingAt, row.OldestWaitingAt is { } at ? Math.Max(0, (clock.UtcNow - at).TotalSeconds) : null,
        row.MaxOutboxAgeSeconds, row.OpenDeadLetters, row.OpenManualTasks, row.OldestDeadLetterAt,
        row.LastDailyCheckResult, row.LastDailyCheckAt, row.DailyCheckDifferences, row.CallsUsed, row.CallsCap,
        row.CallAlertPercent, row.CredentialReplaceBy, row.CredentialWarnDays, row.LastErrorCode,
        row.LastErrorMessage, row.UpdatedAt, row.RelayConnected);
    private static ConnectionHealth Missing(Connection connection) => new()
    {
        ConnectionId = connection.ConnectionId, TenantId = connection.TenantId,
        Status = connection.Status == ConnectionStatus.Archived ? "archived"
            : connection.Status is ConnectionStatus.Paused or ConnectionStatus.Registering ? "paused" : "degraded",
    };
}
