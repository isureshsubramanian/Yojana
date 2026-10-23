using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connectors;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Connections;

public sealed class ExtensionStatusService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    IUserDirectory people,
    ConnectionHealthSettings healthSettings,
    IConnectionAuditSink audit,
    IServiceProvider services)
{
    private const string CheckEvent = nameof(ConnectionAuditEventType.ExtensionChecked);

    public async Task<ConnectionExtensionStatusView> ReadAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var (connection, lifecycle, adapter) = await ResolveAsync(connectionId, cancellationToken);
        var declaration = adapter.Declaration;
        var snapshot = await adapter.SnapshotAsync(connectionId, cancellationToken);
        var gateway = await db.ConnectionGatewaySettings.AsNoTracking().SingleAsync(row =>
            row.TenantId == tenant.TenantId && row.ConnectionId == connectionId, cancellationToken);
        var last = await db.ConnectionEvents.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == connectionId && row.EventType == CheckEvent)
            .OrderByDescending(row => row.SequenceNo)
            .Select(row => new { row.OccurredAt, row.ActorRef, row.DetailsJson })
            .FirstOrDefaultAsync(cancellationToken);
        var checkedResult = last is null ? null
            : JsonSerializer.Deserialize<ConnectionExtensionCheckAudit>(last.DetailsJson,
                IntegrationContracts.Json);
        var checkedPages = checkedResult?.Pages.ToDictionary(page => page.PageCode, StringComparer.Ordinal)
            ?? new Dictionary<string, ConnectionExtensionPageProbe>(StringComparer.Ordinal);

        var inboundEntities = declaration.Pages.Select(page => page.InboundEntity).OfType<string>()
            .Distinct(StringComparer.Ordinal).ToList();
        var today = clock.UtcNow.Date;
        var tomorrow = today.AddDays(1);
        var inbound = await db.InboundMessages.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == connectionId && inboundEntities.Contains(row.ErpEntity))
            .GroupBy(row => row.ErpEntity).Select(group => new
            {
                Entity = group.Key,
                Today = group.LongCount(row => row.AcceptedAt >= today && row.AcceptedAt < tomorrow),
                LastSeen = group.Max(row => (DateTime?)row.AcceptedAt),
            }).ToListAsync(cancellationToken);
        var inboundByEntity = inbound.ToDictionary(row => row.Entity, StringComparer.Ordinal);
        var polls = await db.ConnectionPollWatermarks.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == connectionId)
            .Select(row => new { row.EntityType, row.IntervalSeconds }).ToListAsync(cancellationToken);
        var pollByEntity = polls.ToDictionary(row => row.EntityType, StringComparer.Ordinal);
        var notifications = snapshot.Notifications.GroupBy(item => item.PageCode, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(item => item.ExpiresAt), StringComparer.Ordinal);
        var now = clock.UtcNow;
        var pages = declaration.Pages.Select(definition =>
        {
            var pageCheck = checkedPages.GetValueOrDefault(definition.Code);
            var outboundOff = definition.OutboundOnly && (gateway.ReadOnly || gateway.OutboundMode == "none");
            var status = outboundOff ? ConnectionExtensionPageStatus.NotUsedYet
                : pageCheck?.Status ?? ConnectionExtensionPageStatus.NotReachable;
            var reason = outboundOff ? "Outbound is off until cutover."
                : pageCheck?.Reason ?? "No extension check has been recorded yet.";
            var pageInbound = definition.InboundEntity is null
                ? null : inboundByEntity.GetValueOrDefault(definition.InboundEntity);
            DateTime? expiry = notifications.TryGetValue(definition.Code, out var recordedExpiry)
                ? recordedExpiry : null;
            return new ConnectionExtensionPageView(definition.Code, definition.Label,
                declaration.ContractVersion, status, reason,
                definition.InboundEntity is null ? null : pageInbound?.Today ?? 0,
                definition.InboundEntity is null ? null : pageInbound?.LastSeen,
                expiry is null ? "not_subscribed" : expiry > now ? "live" : "expired",
                expiry, definition.PollEntity is null ? null
                    : pollByEntity.GetValueOrDefault(definition.PollEntity)?.IntervalSeconds);
        }).ToList();

        var credentials = await db.ConnectionCredentials.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ConnectionId == connectionId
                && row.RotatedAt == null && row.RetiredAt == null)
            .Select(row => new { row.ExpiresAt, ReplaceBy = (DateTime?)row.RotationDueAt })
            .ToListAsync(cancellationToken);
        var expiryAt = credentials.Select(row => row.ExpiresAt).Where(value => value != null).Min();
        var replaceBy = credentials.Select(row => row.ReplaceBy).Where(value => value != null).Min();
        var warnDays = (await healthSettings.GetAsync(connectionId, cancellationToken)).CredentialWarnDays;
        var actorName = last is null ? null : await ActorNameAsync(last.ActorRef, cancellationToken);
        var liveNotifications = snapshot.Notifications.Where(item => item.ExpiresAt > now).ToList();
        DateTime? nextRenewal = liveNotifications.Count == 0 ? null : liveNotifications.Min(item =>
        {
            var due = item.ExpiresAt.AddHours(-snapshot.RenewSubscriptionsBeforeHours);
            return due < now ? now : due;
        });
        var access = new ConnectionExtensionAccessView(
            ApplicationRegistrationPerTenant: credentials.Count == 1,
            PermissionSetWithoutFullAccessRole: declaration.PermissionSetWithoutFullAccessRole,
            FullAccessRoleAssigned: null,
            CredentialExpiresAt: expiryAt,
            CredentialReminderAt: expiryAt?.AddDays(-warnDays),
            CredentialReplaceBy: replaceBy);
        return new(connectionId, connection.DisplayLabel, lifecycle.Features, snapshot.InstalledVersion,
            declaration.ShippedVersion, IsNewerThanOnePointZero(snapshot.InstalledVersion),
            declaration.ContractVersion, last?.OccurredAt, actorName,
            pages.Count(page => page.Status == ConnectionExtensionPageStatus.Reachable),
            pages.Count(page => page.Status != ConnectionExtensionPageStatus.NotUsedYet), access,
            new(liveNotifications.Count, nextRenewal), pages,
            $"/api/integration/connections/{connectionId:D}/extension/installation-guide");
    }

    public async Task<ConnectionExtensionStatusView> CheckAsync(Guid connectionId, string actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw Rule("audit_actor_required", "The authenticated user has no actor id.");
        var (connection, _, adapter) = await ResolveAsync(connectionId, cancellationToken);
        var gateway = await db.ConnectionGatewaySettings.AsNoTracking().SingleAsync(row =>
            row.TenantId == tenant.TenantId && row.ConnectionId == connectionId, cancellationToken);
        var snapshot = await adapter.SnapshotAsync(connectionId, cancellationToken);
        var pageResults = await adapter.ProbeAsync(connectionId,
            !gateway.ReadOnly && gateway.OutboundMode != "none", cancellationToken);
        var at = clock.UtcNow;
        var details = JsonSerializer.Serialize(new ConnectionExtensionCheckAudit(snapshot.InstalledVersion,
            adapter.Declaration.ContractVersion, pageResults), IntegrationContracts.Json);
        await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(at), connection.ConnectionId,
            ConnectionAuditEventType.ExtensionChecked, actor, at, details), cancellationToken);
        return await ReadAsync(connectionId, cancellationToken);
    }

    public async Task<string> InstallationGuideAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var (_, _, adapter) = await ResolveAsync(connectionId, cancellationToken);
        return adapter.InstallationGuide();
    }

    public static bool IsNewerThanOnePointZero(string value)
    {
        var candidate = value.Trim().TrimStart('v', 'V');
        return Version.TryParse(candidate, out var version)
            && (version.Major > 1 || version.Major == 1 && version.Minor > 0);
    }

    private async Task<(Connection Connection, IConnectionLifecycleAdapter Lifecycle,
        IConnectionExtensionAdapter Adapter)> ResolveAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.ConnectionId == connectionId, cancellationToken)
            ?? throw Rule("connection_not_found", "The connection was not found.");
        var lifecycle = services.GetKeyedService<IConnectionLifecycleAdapter>(connection.ConnectorCode);
        var adapter = services.GetKeyedService<IConnectionExtensionAdapter>(connection.ConnectorCode);
        if (lifecycle?.Features.TenantSideExtension != true || adapter is null)
            throw Rule("connection_extension_not_found",
                "This connector does not have a tenant-side extension.");
        return (connection, lifecycle, adapter);
    }

    private async Task<string> ActorNameAsync(string actor, CancellationToken cancellationToken)
    {
        if (actor.StartsWith("system:", StringComparison.Ordinal)) return "System";
        var names = await people.DisplayNamesAsync([actor], cancellationToken);
        return names.GetValueOrDefault(actor) ?? "Former or unknown user";
    }

    private static ExtensionStatusRuleException Rule(string rule, string message) => new(rule, message);
}

public sealed class ExtensionStatusRuleException(string rule, string message)
    : CommandRefusedException(rule, message);
