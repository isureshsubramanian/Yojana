using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Outbox;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class OutboxRelayTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Lost_create_reply_is_adopted_by_reference_without_a_second_create()
    {
        fixture.Clock.UtcNow = new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc);
        fixture.Connector.Configure(FakeOutboundBehaviour.TimeoutAfterCreate);
        var tenant = await fixture.NewTenantAsync("lost-reply");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(),
            IntegrationHostFixture.FakeConnectorCode);
        var commandId = await AddCommandAsync(tenant, connection);

        using var scope = fixture.ScopeFor(tenant);
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        var first = await relay.RelayConnectionAsync(connection, CancellationToken.None);
        Assert.Equal(1, first.Retrying);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var retry = await db.OutboundCommands.SingleAsync(x => x.OutboundCommandId == commandId);
        Assert.Equal(OutboundCommandStatus.Retry, retry.Status);
        Assert.NotNull(fixture.Connector.LastIdempotencyKeyFor(connection));

        fixture.Clock.UtcNow = retry.NextAttemptAt!.Value.AddSeconds(1);
        var second = await relay.RelayConnectionAsync(connection, CancellationToken.None);
        Assert.Equal(1, second.Completed);
        await db.Entry(retry).ReloadAsync();
        Assert.Equal(OutboundCommandStatus.Done, retry.Status);
        Assert.Equal(OutboundConnectorOutcome.AlreadyExisted, retry.Outcome);
        Assert.Equal(1, fixture.Connector.CreateCallsFor(connection));
        Assert.Equal(2, fixture.Connector.FindCallsFor(connection));
        var mapping = await db.ExternalIdMaps.SingleAsync(x => x.ExternalId == retry.ErpExternalId);
        Assert.Equal(Guid.Parse(retry.ModuleDocumentRef[(retry.ModuleDocumentRef.LastIndexOf('/') + 1)..]),
            mapping.ModuleId);
    }

    [DatabaseFact]
    public async Task A_retry_that_falls_due_within_the_same_pass_waits_for_the_next_pass()
    {
        fixture.Clock.UtcNow = new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc);
        fixture.Connector.Configure(FakeOutboundBehaviour.Fail);
        var tenant = await fixture.NewTenantAsync("same-pass");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(),
            IntegrationHostFixture.FakeConnectorCode);
        var commandId = await AddCommandAsync(tenant, connection);

        using var scope = fixture.ScopeFor(tenant);
        // A zero backoff makes the retry due at once; the pass must still not attempt it a second time.
        var relay = ActivatorUtilities.CreateInstance<OutboundRelay>(scope.ServiceProvider, new ZeroJitter());
        var result = await relay.RelayConnectionAsync(connection, CancellationToken.None);
        Assert.Equal(new OutboundRelayResult(0, 1, 0), result);
        Assert.Equal(1, fixture.Connector.CreateCallsFor(connection));
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var command = await db.OutboundCommands.AsNoTracking().SingleAsync(x => x.OutboundCommandId == commandId);
        Assert.Equal(OutboundCommandStatus.Retry, command.Status);
        Assert.Equal(1, command.AttemptCount);
    }

    private sealed class ZeroJitter : IConnectionRetryJitter
    {
        public TimeSpan Backoff(int retryNumber, TimeSpan ceiling) => TimeSpan.Zero;
    }

    [DatabaseFact]
    public async Task Connector_idempotency_key_is_omitted_when_not_declared()
    {
        fixture.Clock.UtcNow = new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc);
        fixture.Connector.Configure(FakeOutboundBehaviour.Succeed, acceptsIdempotencyKey: false);
        var tenant = await fixture.NewTenantAsync("no-key");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(),
            IntegrationHostFixture.FakeConnectorCode);
        await AddCommandAsync(tenant, connection);
        using var scope = fixture.ScopeFor(tenant);
        var result = await scope.ServiceProvider.GetRequiredService<OutboundRelay>()
            .RelayConnectionAsync(connection, CancellationToken.None);
        Assert.Equal(1, result.Completed);
        Assert.Null(fixture.Connector.LastIdempotencyKeyFor(connection));
    }

    private async Task<Guid> AddCommandAsync(TenantId tenant, Guid connection)
    {
        var id = TimeOrderedId.New(fixture.Clock.UtcNow);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.OutboundCommands.Add(new OutboundCommandRow
        {
            TenantId = tenant,
            OutboundCommandId = id,
            ConnectionId = connection,
            CommandType = "CreateServiceOrder",
            IdempotencyKey = Guid.NewGuid(),
            ModuleDocumentRef = $"service/{Guid.NewGuid():N}",
            ErpReferenceText = $"YOJ-{Guid.NewGuid():N}",
            Payload = "{\"customer_ref\":\"FIC-CUSTOMER\",\"problem_description\":\"Fictional repair\"}",
            Status = OutboundCommandStatus.Pending,
            CreatedAt = fixture.Clock.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();
        return id;
    }
}
