using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Integration.Outbox;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Workers;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class Cx01SimulatorDatabaseTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Simulator_poll_is_accepted_through_the_transactional_inbox()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await fixture.NewTenantAsync("cx01");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        await settings.SetAsync(connection, new(simulator.BaseUri, "FICTIONAL_DEMO", "10.0 FP 2308"),
            CancellationToken.None);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("integration-user", "not-logged"),
            IntegrationContracts.Json);
        var credential = await protector.EncryptAsync(tenant, connection, Cx01Client.CredentialPurpose, plaintext,
            null, fixture.Clock.UtcNow.AddDays(30), CancellationToken.None);
        await store.SaveAsync(credential, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<ConnectionThrottleProfiles>().OnboardAsync(
            new(tenant, connection), Cx01Capabilities.For("10.0 FP 2308", false).Throttle, CancellationToken.None);

        var result = await scope.ServiceProvider.GetRequiredService<Cx01Adapter>().PollAsync(
            new(tenant, connection, "deliveries", null, TimeSpan.FromMinutes(2), 1), CancellationToken.None);
        var accepted = await scope.ServiceProvider.GetRequiredService<InboundMessageStore>()
            .Accept(result.Events.Single(), CancellationToken.None);

        Assert.Equal(InboundAcceptanceResult.Accepted, accepted.Outcome);
        var row = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().InboundMessages
            .AsNoTracking().SingleAsync(x => x.TenantId == tenant && x.InboundMessageId == accepted.InboundMessageId);
        Assert.Equal(nameof(DeliveryPosted), row.MessageType);
        Assert.DoesNotContain("not-logged", row.RawPayload, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Webhook_subscription_and_failed_delivery_watchdog_use_the_simulator()
    {
        await using var simulator = new Cx01Simulator { FailedNotificationAvailable = true };
        var tenant = await fixture.NewTenantAsync("cxhook");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        await settings.SetAsync(connection, new(simulator.BaseUri, "FICTIONAL_DEMO", "10.0 FP 2602",
            WebhooksAvailable: true), CancellationToken.None);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        var login = JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("integration-user", "not-logged"),
            IntegrationContracts.Json);
        await store.SaveAsync(await protector.EncryptAsync(tenant, connection, Cx01Client.CredentialPurpose,
            login, null, fixture.Clock.UtcNow.AddDays(30), CancellationToken.None), CancellationToken.None);
        await store.SaveAsync(await protector.EncryptAsync(tenant, connection,
            Cx01WebhookEndpoints.SigningCredentialPurpose, "simulator-signing-secret"u8.ToArray(), null,
            fixture.Clock.UtcNow.AddDays(30), CancellationToken.None), CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<ConnectionThrottleProfiles>().OnboardAsync(
            new(tenant, connection), Cx01Capabilities.For("10.0 FP 2602", true).Throttle, CancellationToken.None);
        var maintenance = scope.ServiceProvider.GetRequiredService<Cx01WebhookMaintenance>();

        await maintenance.SubscribeAsync(connection, new Uri("https://connector.invalid/notifications"),
            CancellationToken.None);
        var accepted = await maintenance.WatchdogConnectionAsync(connection, CancellationToken.None);

        Assert.True(simulator.WebhookSubscriptionCreated);
        Assert.Equal(1, accepted);
    }

    [DatabaseFact]
    public async Task Reference_only_outbound_updates_and_finds_the_configured_reference_field()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await fixture.NewTenantAsync("cxout");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        await settings.SetAsync(connection, new(simulator.BaseUri, "FICTIONAL_DEMO", "10.0 FP 2308",
            RmaReferenceField: "U_YojanaRma"), CancellationToken.None);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        var login = JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("integration-user", "not-logged"),
            IntegrationContracts.Json);
        await store.SaveAsync(await protector.EncryptAsync(tenant, connection, Cx01Client.CredentialPurpose,
            login, null, fixture.Clock.UtcNow.AddDays(30), CancellationToken.None), CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<ConnectionThrottleProfiles>().OnboardAsync(
            new(tenant, connection), Cx01Capabilities.For("10.0 FP 2308", false).Throttle,
            CancellationToken.None);
        var adapter = scope.ServiceProvider.GetRequiredService<Cx01Adapter>();
        var reference = "RMA-FICTIONAL-1001";

        var created = await adapter.CreateAsync(new(connection, Guid.CreateVersion7(),
            nameof(CreateServiceOrder), reference, "{\"target_record_ref\":\"return:5101\"}", null),
            CancellationToken.None);
        var found = await adapter.FindByReferenceAsync(new(connection, nameof(CreateServiceOrder), reference),
            CancellationToken.None);

        Assert.Equal(OutboundConnectorOutcome.Created, created.Outcome);
        Assert.Equal("YOJANA:" + reference, simulator.LastReferenceValue);
        Assert.Equal("5101", found?.ErpExternalId);
    }
}
