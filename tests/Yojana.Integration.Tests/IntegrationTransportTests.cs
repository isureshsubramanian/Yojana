using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Yojana.Integration.Contracts;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Transport;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class IntegrationTransportTests
{
    private const string Secret = "Endpoint=sb://fictional-relay.servicebus.windows.net/;SharedAccessKeyName=tenant;SharedAccessKey=ZmFrZQ==";
    private static readonly DateTime Now = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Missing_setting_routes_locally_without_resolving_any_cloud_credential()
    {
        var (settings, _, transport, _, _) = Setup();
        Assert.Equal(IntegrationTransportRoute.Local, await settings.ResolveAsync(default));
        Assert.Equal(ITenantSettings.MissingVersion, (await settings.ReadAsync(default)).Version);
        Assert.Equal(0, transport.Starts);
    }

    [Fact]
    public async Task Switching_is_immediate_and_retains_old_queue_routes_for_drain()
    {
        var (settings, credentials, transport, _, _) = Setup();
        var first = await credentials.StoreAsync(new(Secret, Now.AddDays(90)), default);
        var queue = await settings.UpdateAsync(new("message_queue", first.CredentialReference, "missing"), "actor:test", default);
        Assert.Equal(queue.Route, await settings.ResolveAsync(default));
        var local = await settings.UpdateAsync(new("in_process", null, queue.Version), "actor:test", default);
        Assert.Equal(IntegrationTransportRoute.Local, await settings.ResolveAsync(default));
        Assert.Equal([queue.Route], local.DrainingRoutes);
        var back = await settings.UpdateAsync(new("message_queue", first.CredentialReference, local.Version), "actor:test", default);
        Assert.Empty(back.DrainingRoutes);
        Assert.Equal(2, transport.Starts);
    }

    [Fact]
    public async Task Stale_update_and_failed_receiver_leave_the_previous_route_effective()
    {
        var (settings, credentials, transport, _, _) = Setup();
        var local = await settings.UpdateAsync(new("in_process", null, "missing"), "actor:test", default);
        await Assert.ThrowsAsync<TenantSettingVersionConflictException>(() =>
            settings.UpdateAsync(new("in_process", null, "missing"), "actor:test", default));
        var credential = await credentials.StoreAsync(new(Secret, Now.AddDays(90)), default);
        transport.FailStart = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.UpdateAsync(
            new("message_queue", credential.CredentialReference, local.Version), "actor:test", default));
        var unchanged = await settings.ReadAsync(default);
        Assert.Equal(local.Route, unchanged.Route);
        Assert.Equal(local.Version, unchanged.Version);
        Assert.Empty(unchanged.DrainingRoutes);
    }

    [Fact]
    public async Task A_failed_drain_route_does_not_block_restoring_the_current_receiver()
    {
        var (settings, credentials, transport, _, tenant) = Setup();
        var oldCredential = await credentials.StoreAsync(new(Secret, Now.AddDays(90)), default);
        var old = await settings.UpdateAsync(new("message_queue", oldCredential.CredentialReference, "missing"), "actor:test", default);
        var nextCredential = await credentials.StoreAsync(new(Secret, Now.AddDays(90)), default);
        var current = await settings.UpdateAsync(new("message_queue", nextCredential.CredentialReference, old.Version), "actor:test", default);
        transport.FailReference = oldCredential.CredentialReference;
        transport.Restored.Clear();
        var job = new TransportReceiverJob(tenant, tenant, settings, transport, NullLogger<TransportReceiverJob>.Instance);
        await job.RestoreAsync(default);
        Assert.Equal([current.Route], transport.Restored);
    }

    [Theory]
    [InlineData("unknown", null)]
    [InlineData("message_queue", null)]
    [InlineData("in_process", "10000000-0000-0000-0000-000000000001")]
    public async Task Invalid_route_is_rejected(string mode, string? reference)
    {
        var (settings, _, _, _, _) = Setup();
        await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateAsync(
            new(mode, reference is null ? null : Guid.Parse(reference), "missing"), "actor:test", default));
    }

    [Fact]
    public async Task Namespace_credentials_are_encrypted_and_immutable_and_never_serialised_in_settings()
    {
        var (settings, credentials, _, store, tenant) = Setup();
        var request = new StoreMessageQueueCredential(Secret, Now.AddDays(90));
        Assert.DoesNotContain(Secret, request.ToString());
        var first = await credentials.StoreAsync(request, default);
        var second = await credentials.StoreAsync(request, default);
        Assert.NotEqual(first, second);
        var encrypted = await store.GetCurrentAsync(tenant.TenantId, first.CredentialReference, MessageQueueCredentials.Purpose, default);
        Assert.NotNull(encrypted);
        Assert.DoesNotContain(Secret, System.Text.Encoding.UTF8.GetString(encrypted.Ciphertext));
        await settings.UpdateAsync(new("message_queue", first.CredentialReference, "missing"), "actor:test", default);
        Assert.DoesNotContain(Secret, System.Text.Json.JsonSerializer.Serialize(await settings.ReadAsync(default)));
        Assert.Equal(Secret, await credentials.ReadSecretAsync(first.CredentialReference, default));
        Assert.Null(await store.GetCurrentAsync(new TenantId(Guid.NewGuid()), first.CredentialReference,
            MessageQueueCredentials.Purpose, default));
    }

    [Fact]
    public async Task Invalid_credential_errors_do_not_echo_input()
    {
        var (_, credentials, _, _, _) = Setup();
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            credentials.StoreAsync(new("sensitive-invalid-input", Now.AddDays(90)), default));
        Assert.DoesNotContain("sensitive-invalid-input", error.ToString());
    }

    [Fact]
    public void Queue_names_are_stable_distinct_and_contain_no_raw_tenant_or_reference()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var route = new IntegrationTransportRoute("message_queue", Guid.NewGuid());
        var name = route.AddressPrefix(tenant);
        Assert.Equal(name, route.AddressPrefix(tenant));
        Assert.DoesNotContain(tenant.Value.ToString("N"), name);
        Assert.DoesNotContain(route.CredentialReference!.Value.ToString("N"), name);
        Assert.NotEqual(name, route.AddressPrefix(new(Guid.NewGuid())));
        Assert.NotEqual(name, (route with { CredentialReference = Guid.NewGuid() }).AddressPrefix(tenant));
    }

    [Fact]
    public async Task Health_is_a_tenant_scoped_read_port_and_local_health_does_not_probe_cloud()
    {
        var (settings, _, transport, _, tenant) = Setup();
        var health = new TenantIntegrationTransportHealth(tenant, new TestClock(Now), settings, transport);
        Assert.Equal(new("in_process", "connected", Now), await health.ReadAsync(default));
        Assert.Equal(0, transport.Probes);
    }

    [Fact]
    public void Receiver_rejects_cross_tenant_and_ambiguous_messages_before_opening_a_scope()
    {
        var tenant = new TenantId(Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => IntegrationTransportReceiver.Validate(tenant,
            new(Guid.NewGuid(), Guid.NewGuid(), RelayConnectionId: Guid.NewGuid())));
        Assert.Throws<InvalidOperationException>(() => IntegrationTransportReceiver.Validate(tenant,
            new(tenant.Value, Guid.NewGuid())));
    }

    [Fact]
    public void Settings_endpoints_require_integration_access_and_writes_require_administrator()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<IntegrationTransportSettings>();
        builder.Services.AddScoped<MessageQueueCredentials>();
        builder.Services.AddScoped<IIntegrationTransportHealth, TenantIntegrationTransportHealth>();
        using var app = builder.Build();
        app.MapIntegrationTransportEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>().ToList();
        Assert.Equal(4, endpoints.Count);
        Assert.All(endpoints, endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            item => item.Policy == YojanaPolicy.IntegrationOperations));
        foreach (var endpoint in endpoints.Where(x => x.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!
                     .HttpMethods.Any(method => method is "PUT" or "POST")))
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), x => x.Policy == YojanaPolicy.TenantAdministrator);
        Assert.DoesNotContain(typeof(UpdateIntegrationTransportSettings).GetProperties(), x => x.Name.Contains("Actor"));
    }

    private static (IntegrationTransportSettings Settings, MessageQueueCredentials Credentials, TestCarrier Carrier,
        InMemoryConnectionCredentialStore Store, TenantContext Tenant) Setup()
    {
        var clock = new TestClock(Now);
        var tenant = new TenantContext();
        tenant.Set(new TenantId(Guid.NewGuid()));
        var store = new InMemoryConnectionCredentialStore();
        var keys = new LocalKeyEncryptionKeyProvider(new LocalKeyEncryptionOptions
        { CurrentKey = new("test", "1", new byte[32]) });
        var protector = new ConnectionCredentialProtector(new(new InMemoryTenantDataKeyStore(), keys, clock), clock);
        var credentials = new MessageQueueCredentials(tenant, store, protector, clock);
        var carrier = new TestCarrier();
        return (new(new VersionedSettings(), tenant, carrier, credentials, clock), credentials, carrier, store, tenant);
    }

    private sealed class TestCarrier : IIntegrationTransport
    {
        public int Starts { get; private set; }
        public int Probes { get; private set; }
        public bool FailStart { get; set; }
        public Guid? FailReference { get; set; }
        public List<IntegrationTransportRoute> Restored { get; } = [];
        public Task SendAsync(IntegrationTransportRoute route, IntegrationTransportMessage message, CancellationToken ct) => Task.CompletedTask;
        public Task EnsureReceivingAsync(TenantId tenant, IntegrationTransportRoute route, CancellationToken ct)
        {
            if (route.CredentialReference == FailReference && FailReference is not null)
                throw new IntegrationTransportUnavailableException("receiver unavailable");
            if (FailStart) throw new InvalidOperationException("receiver unavailable");
            Restored.Add(route);
            Starts++;
            return Task.CompletedTask;
        }
        public Task<string> ProbeAsync(TenantId tenant, IntegrationTransportRoute route, CancellationToken ct)
        { Probes++; return Task.FromResult("connected"); }
    }

    private sealed class VersionedSettings : ITenantSettings
    {
        private VersionedTenantSetting? current;
        private int version;
        public Task<string?> GetAsync(string key, CancellationToken ct) => Task.FromResult(current?.Value);
        public Task<VersionedTenantSetting?> GetVersionedAsync(string key, CancellationToken ct) => Task.FromResult(current);
        public Task SetAsync(string key, string value, CancellationToken ct) => throw new NotSupportedException();
        public Task<VersionedTenantSetting> SetAsync(string key, string value, string expectedVersion, CancellationToken ct)
        {
            if ((current?.Version ?? "missing") != expectedVersion)
                throw new TenantSettingVersionConflictException(key, expectedVersion, current?.Version ?? "missing");
            current = new(value, (++version).ToString(), Now);
            return Task.FromResult(current);
        }
    }
}
