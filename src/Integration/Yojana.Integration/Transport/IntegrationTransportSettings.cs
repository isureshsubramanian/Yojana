using System.Text.Json;
using Yojana.Integration.Contracts;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Transport;

public sealed record IntegrationTransportSettingsView(IntegrationTransportRoute Route,
    IReadOnlyList<IntegrationTransportRoute> DrainingRoutes, string Version);
public sealed record UpdateIntegrationTransportSettings(string Mode, Guid? CredentialReference, string ExpectedVersion);

public sealed class IntegrationTransportSettings(ITenantSettings settings, ITenantContext tenant,
    IIntegrationTransport transport, MessageQueueCredentials credentials, IClock clock)
    : IIntegrationRouteResolver
{
    public const string SettingKey = "integration.transport";
    private sealed record Stored(IntegrationTransportRoute Route, IReadOnlyList<IntegrationTransportRoute> DrainingRoutes, string ChangedBy, DateTime ChangedAt);

    public async Task<IntegrationTransportSettingsView> ReadAsync(CancellationToken cancellationToken)
    {
        var setting = await settings.GetVersionedAsync(SettingKey, cancellationToken);
        if (setting is null) return new(IntegrationTransportRoute.Local, [], ITenantSettings.MissingVersion);
        var stored = JsonSerializer.Deserialize<Stored>(setting.Value)
            ?? throw new InvalidOperationException("The integration transport setting is invalid.");
        Validate(stored.Route);
        foreach (var route in stored.DrainingRoutes) Validate(route);
        return new(stored.Route, stored.DrainingRoutes, setting.Version);
    }

    public async Task<IntegrationTransportRoute> ResolveAsync(CancellationToken cancellationToken) =>
        (await ReadAsync(cancellationToken)).Route;

    public async Task<IntegrationTransportSettingsView> UpdateAsync(UpdateIntegrationTransportSettings request, string actor,
        CancellationToken cancellationToken)
    {
        var route = new IntegrationTransportRoute(request.Mode, request.CredentialReference);
        Validate(route);
        var current = await ReadAsync(cancellationToken);
        if (current.Version != request.ExpectedVersion)
            throw new TenantSettingVersionConflictException(SettingKey, request.ExpectedVersion, current.Version);
        if (route.Mode == IntegrationTransportRoute.MessageQueue)
        {
            if (!await credentials.ExistsAsync(route.CredentialReference!.Value, cancellationToken))
                throw new ArgumentException("The message queue credential reference is not configured for this tenant.");
            // Establish the new receiver before making its route visible. Failure leaves the old setting effective.
            await transport.EnsureReceivingAsync(tenant.TenantId, route, cancellationToken);
        }
        var draining = current.DrainingRoutes.Where(x => x != route).ToList();
        if (current.Route.Mode == IntegrationTransportRoute.MessageQueue && current.Route != route
            && !draining.Contains(current.Route)) draining.Add(current.Route);
        if (draining.Count > 16)
            throw new ArgumentException("At most sixteen previous message queue routes may be retained. Complete the drain runbook first.");
        var saved = await settings.SetAsync(SettingKey, JsonSerializer.Serialize(new Stored(route, draining, actor, clock.UtcNow)),
            request.ExpectedVersion, cancellationToken);
        return new(route, draining, saved.Version);
    }

    private static void Validate(IntegrationTransportRoute route)
    {
        if (route.Mode is not (IntegrationTransportRoute.InProcess or IntegrationTransportRoute.MessageQueue)
            || (route.Mode == IntegrationTransportRoute.InProcess && route.CredentialReference is not null)
            || (route.Mode == IntegrationTransportRoute.MessageQueue
                && (route.CredentialReference is null || route.CredentialReference == Guid.Empty)))
            throw new ArgumentException("Transport must be in_process without a credential or message_queue with a credential reference.");
    }
}

public sealed class TenantIntegrationTransportHealth(ITenantContext tenant, IClock clock,
    IIntegrationRouteResolver routes, IIntegrationTransport transport) : IIntegrationTransportHealth
{
    public async Task<IntegrationTransportHealth> ReadAsync(CancellationToken cancellationToken)
    {
        var route = await routes.ResolveAsync(cancellationToken);
        var state = route.Mode == IntegrationTransportRoute.InProcess ? "connected"
            : await transport.ProbeAsync(tenant.TenantId, route, cancellationToken);
        return new(route.Mode, state, clock.UtcNow);
    }
}
