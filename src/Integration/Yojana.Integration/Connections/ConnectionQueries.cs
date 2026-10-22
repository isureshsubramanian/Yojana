using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connectors;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.Reconciliation;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Connections;

public sealed class ConnectionQueries(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    ConnectionHealthSettings healthSettings, ReconciliationSettings reconciliationSettings,
    ReconciliationBusinessDay reconciliationDay,
    ConnectorOnboardingChecklistGenerator checklist,
    IConnectionThrottleProfileStore throttles, IUserDirectory people, IServiceProvider services,
    Coexistence.WarrantyContractMaintenanceSettings warrantyContracts)
{
    public async Task<RegistrationConnectorPage> RegistrationConnectorsAsync(string? cursor, int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 50);
        var query = db.ConnectorCatalogue.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.Enabled);
        if (cursor is not null) query = query.Where(x => x.ConnectorCode.CompareTo(cursor) > 0);
        var rows = await query.OrderBy(x => x.ConnectorCode).Take(take + 1).ToListAsync(ct);
        var items = rows.Take(take).Select(x => (Entry: x, Form: services.GetKeyedService<IConnectionLifecycleAdapter>(x.ConnectorCode)?.RegistrationForm))
            .Where(x => x.Form is not null).Select(x => new RegistrationConnector(x.Entry.ConnectorCode, x.Entry.DisplayName, x.Form!)).ToList();
        return new(items, rows.Count > take ? rows[take - 1].ConnectorCode : null);
    }

    public async Task<ConnectionPage> ListAsync(string? cursor, int take, bool archived, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 200);
        var query = db.Connections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && (archived ? x.Status == ConnectionStatus.Archived : x.Status != ConnectionStatus.Archived));
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!Guid.TryParse(cursor, out var id)) throw new ConnectionRuleException("cursor_invalid", "The connection cursor is invalid.");
            query = query.Where(x => x.ConnectionId.CompareTo(id) > 0);
        }
        var rows = await query.OrderBy(x => x.ConnectionId).Take(take + 1).ToListAsync(ct);
        var ids = rows.Take(take).Select(x => x.ConnectionId).ToList();
        var health = await db.ConnectionHealth.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && ids.Contains(x.ConnectionId)).ToDictionaryAsync(x => x.ConnectionId, ct);
        var gateway = await db.ConnectionGatewaySettings.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && ids.Contains(x.ConnectionId)).ToDictionaryAsync(x => x.ConnectionId, ct);
        var codes = rows.Select(x => x.ConnectorCode).Distinct().ToList();
        var catalogue = await db.ConnectorCatalogue.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && codes.Contains(x.ConnectorCode)).ToDictionaryAsync(x => x.ConnectorCode, x => x.DisplayName, ct);
        var views = rows.Take(take).Select(connection =>
        {
            var state = health.GetValueOrDefault(connection.ConnectionId) ?? Missing(connection);
            var configured = gateway.GetValueOrDefault(connection.ConnectionId);
            return new ConnectionSummary(connection.ConnectionId, connection.DisplayLabel, connection.ConnectorCode,
                catalogue.GetValueOrDefault(connection.ConnectorCode) ?? connection.ConnectorCode,
                state.Status, state.IsLive, configured?.OutboundMode ?? "none", configured?.ReadOnly ?? true, View(state));
        }).ToList();
        return new(views, rows.Count > take ? views[^1].ConnectionId.ToString("D") : null, await KpisAsync(ct));
    }

    public async Task<TenantStatusPage> StatusAsync(string? cursor, int take, CancellationToken ct)
    {
        var page = await ListAsync(cursor, take, false, ct);
        return new(page.Items.Select(row => new TenantConnectionStatus(row.ConnectionId, row.ConnectionName,
            row.Status, row.IsLive, row.Health.CheckedUpTo, row.Health.WaitingToSend, row.Health.OpenDeadLetters,
            row.Health.OpenManualTasks, row.Status is "down" or "degraded" ? 1 : 0, row.Health.UpdatedAt)).ToList(), page.NextCursor, page.Kpis);
    }

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
        return new(new(id, connection.DisplayLabel, connection.ConnectorCode, name, health.Status, health.IsLive,
                gateway.OutboundMode, gateway.ReadOnly, View(health)), connection.CreatedAt, connection.ArchivedAt,
            capabilities, new(gateway.OutboundMode, gateway.ReadOnly, gateway.NativeWarrantyObjectMode,
                (await warrantyContracts.GetAsync(ct)).MaintainedIn,
                gateway.OrderingWaitMinutes, await healthSettings.GetAsync(id, ct),
                (await throttles.GetAsync(new(tenant.TenantId, id), ct))?.Profile, connectorSettings, rules), secrets,
            await checklist.GenerateAsync(id, ct), connection.Status,
            await reconciliationSettings.WindowDaysAsync(id, ct), await reconciliationDay.TodayAsync(id, ct));
    }

    public async Task<ConnectionHealthDetail> HealthAsync(Guid id, CancellationToken ct)
    {
        var connection = await ConnectionAsync(id, ct);
        var row = await db.ConnectionHealth.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == id, ct) ?? Missing(connection);
        // A stopped connection may be waiting on the final migration load's hand-over (OD-60); a running one is not.
        var notes = services.GetService(typeof(IConnectionHandOverNotes)) as IConnectionHandOverNotes;
        var handOver = connection.Status == ConnectionStatus.Active || notes is null ? null
            : await notes.ForAsync(id, ct);
        return new(id, connection.DisplayLabel, View(row),
            JsonSerializer.Deserialize<List<RecordTypeHealth>>(row.RecordTypesJson, IntegrationContracts.Json) ?? [],
            handOver);
    }

    public async Task<ConnectionEventPage> EventsAsync(Guid id, string? cursor, int take, CancellationToken ct)
    {
        _ = await ConnectionAsync(id, ct);
        take = Math.Clamp(take, 1, 200);
        var query = db.ConnectionEvents.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == id);
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence < 1)
                throw new ConnectionRuleException("cursor_invalid", "The event cursor is invalid.");
            query = query.Where(x => x.SequenceNo < sequence);
        }
        var rows = await query.OrderByDescending(x => x.SequenceNo).Take(take + 1).ToListAsync(ct);
        var names = await people.DisplayNamesAsync(rows.Take(take).Select(x => x.ActorRef).Distinct().ToList(), ct);
        return new(rows.Take(take).Select(x => new ConnectionEventView(x.SequenceNo, x.EventType, x.OccurredAt,
                names.GetValueOrDefault(x.ActorRef) ?? (x.ActorRef.StartsWith("system:", StringComparison.Ordinal)
                    ? "System" : "Unknown user"), EventSummary(x.EventType))).ToList(), rows.Count > take ? rows[take - 1].SequenceNo.ToString(CultureInfo.InvariantCulture) : null);
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
                Live = group.Count(x => x.IsLive), Paused = group.Count(x => x.Status == "paused"),
                Today = group.Sum(x => x.ReceivedToday), Month = group.Sum(x => x.ReceivedLast30Days),
                Waiting = group.Sum(x => x.OutboxDepth), Oldest = group.Min(x => x.OldestWaitingAt),
                DeadLetters = group.Sum(x => x.OpenDeadLetters), Tasks = group.Sum(x => x.OpenManualTasks),
                Attention = group.Count(x => x.Status == "down" || x.Status == "degraded"),
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
