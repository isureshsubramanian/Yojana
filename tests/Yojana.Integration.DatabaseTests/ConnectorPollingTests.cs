using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class ConnectorPollingTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Modified_since_poll_passes_the_persisted_watermark_and_overlap_to_the_connector()
    {
        var tenant = await fixture.NewTenantAsync("poll");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var item = "ITEM-POLL-" + Guid.NewGuid().ToString("N")[..6];
        var envelope = new CanonicalEnvelope("1.0", Guid.CreateVersion7(), tenant.Value, connection, "CX01",
            "item", item, new("2026-09-01T08:01:00Z", OpenEnum.Known("timestamp")),
            new(2026, 9, 1, 8, 1, 0, TimeSpan.Zero), new(2026, 9, 1, 8, 1, 1, TimeSpan.Zero),
            OpenEnum.Known("poll"), OpenEnum.Known("complete"), "raw/poll");
        var message = new CanonicalMessage<ItemUpserted>(envelope, nameof(ItemUpserted),
            new(item, "Fictional polled item", OpenEnum.Known("serial"), "EA", false));
        fixture.Poller.NextResult = new([new InboundAcceptance(envelope, nameof(ItemUpserted),
            JsonSerializer.Serialize(message, IntegrationContracts.Json), "{}")], "2026-09-01T08:01:00Z");
        fixture.Poller.Requests.Clear();
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.ConnectionPollWatermarks.Add(new ConnectionPollWatermark
        {
            TenantId = tenant, ConnectionId = connection, EntityType = "item", Watermark = "2026-09-01T08:00:00Z",
            OverlapSeconds = 120, IntervalSeconds = 60, NextPollAt = fixture.Clock.UtcNow.AddMinutes(-1),
        });
        await db.SaveChangesAsync();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ConnectionPollingService>()
            .PollDueAsync(CancellationToken.None));
        var request = Assert.Single(fixture.Poller.Requests);
        Assert.Equal(TimeSpan.FromMinutes(2), request.OverlapWindow);
        Assert.Equal("2026-09-01T08:00:00Z", request.Watermark);
        Assert.Equal("2026-09-01T08:01:00Z", (await db.ConnectionPollWatermarks.SingleAsync()).Watermark);
    }
}
