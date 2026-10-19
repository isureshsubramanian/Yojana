using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Inbox;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class InboxTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Duplicate_is_a_no_op_and_only_the_newest_change_is_translatable()
    {
        var tenant = await fixture.NewTenantAsync("inbox");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var first = Envelope(tenant.Value, connection, "001");
        using var scope = fixture.ScopeFor(tenant);
        var store = scope.ServiceProvider.GetRequiredService<InboundMessageStore>();
        var accepted = await store.Accept(Acceptance(first), CancellationToken.None);
        var duplicate = await store.Accept(Acceptance(first), CancellationToken.None);
        var older = await store.Accept(Acceptance(Envelope(tenant.Value, connection, "000")), CancellationToken.None);
        var newer = await store.Accept(Acceptance(Envelope(tenant.Value, connection, "002")), CancellationToken.None);

        Assert.Equal(InboundAcceptanceResult.Accepted, accepted.Outcome);
        Assert.Equal(InboundAcceptanceResult.AlreadyProcessed, duplicate.Outcome);
        Assert.Equal(InboundAcceptanceResult.Superseded, older.Outcome);
        Assert.Equal(InboundAcceptanceResult.Accepted, newer.Outcome);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(3, await db.InboundMessages.CountAsync());
        Assert.Equal(3, await db.IdempotencyIndex.CountAsync());
        Assert.Equal(newer.InboundMessageId, await db.InboundMessages.Where(x => x.IsLatest).Select(x => x.InboundMessageId).SingleAsync());
    }

    private static CanonicalEnvelope Envelope(Guid tenant, Guid connection, string marker) => new("1.0", Guid.CreateVersion7(), tenant,
        connection, "CX01", "item", "ITEM-1", new(marker, OpenEnum.Known("sequence")),
        new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 1, 8, 0, 1, TimeSpan.Zero),
        OpenEnum.Known("poll"), OpenEnum.Known("complete"), "raw/ITEM-1");

    private static InboundAcceptance Acceptance(CanonicalEnvelope envelope)
    {
        var message = new CanonicalMessage<ItemUpserted>(envelope, nameof(ItemUpserted),
            new("ITEM-1", "Fictional item", OpenEnum.Known("serial"), "EA", false));
        var json = JsonSerializer.Serialize(message, IntegrationContracts.Json);
        return new(envelope, nameof(ItemUpserted), json, "{\"source\":\"unchanged\"}");
    }
}
