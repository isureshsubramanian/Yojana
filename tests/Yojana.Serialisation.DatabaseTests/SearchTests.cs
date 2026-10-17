using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Search;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P1-T23: the global search index is maintained through the outbox, ranks with reasons, opens exact identifier hits directly, and the hover metadata follows the tenant's profile.</summary>
[Collection("serialisation host")]
public sealed class SearchTests(SerialisationHostFixture fixture, ITestOutputHelper output)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<SearchResponse> SearchAsync(TenantId tenant, string q, string? scope = null, string tier = SearchDisclosureTier.BackOffice)
    {
        using var s = fixture.ScopeFor(tenant);
        return await s.ServiceProvider.GetRequiredService<SearchQueries>().SearchAsync(q, scope, tier, CancellationToken.None);
    }

    /// <summary>The index lags the ledger by the outbox delay: wait until the expected number of entries exists.</summary>
    private async Task WaitForEntriesAsync(TenantId tenant, string entityType, int count, Func<Data.Entities.SearchEntry, bool>? predicate = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = fixture.ScopeFor(tenant);
            var rows = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().SearchEntries.AsNoTracking().Where(e => e.EntityType == entityType).ToListAsync();
            if (rows.Count(r => predicate?.Invoke(r) ?? true) >= count) return;
            await Task.Delay(150);
        }

        Assert.Fail($"the search index did not reach {count} {entityType} entries within 20 s");
    }

    [DatabaseFact]
    public async Task Exact_prefix_superseded_document_and_near_hits_rank_with_reasons_and_the_profile_shapes_the_hover_card()
    {
        var tenant = await fixture.NewTenantAsync("sr");
        var p = "SR" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(item) with { ItemCode = "VX-220" });
        var leeds = Guid.NewGuid();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
        [
            new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{p}-493445"), new IdentifierInput(IdentifierType.Imei, "356938035643809")]),
            new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{p}-493446")]),
            new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{p}-493456")]),
        ], At, "t", Entity, LocationId: leeds, LocationKind: LocationKind.Warehouse));
        var u1 = commissioned.Lines[0].UnitId!.Value;
        var u2 = commissioned.Lines[1].UnitId!.Value;
        await InvokeAsync<CorrectIdentifierResult>(tenant, new CorrectIdentifier(Guid.NewGuid(), u2, IdentifierType.InternalSerial, $"{p}-493447", IdentifierCorrectionReason.Typo, At.AddHours(1), "stores", Entity));
        var delivery = new DocumentRef("delivery", Guid.NewGuid());
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), u1, LocationKind.CustomerSite, EpisodeReason.Sale, At.AddDays(1), "shipping", Entity, LocationId: Guid.NewGuid(), PartyRef: Guid.NewGuid(), PartyType: PartyType.Customer, Document: delivery with { Line = 4 }));
        await WaitForEntriesAsync(tenant, SearchEntityType.Unit, 3);
        await WaitForEntriesAsync(tenant, SearchEntityType.Document, 1);
        await WaitForEntriesAsync(tenant, SearchEntityType.Unit, 1, e => e.EntityId == u1 && e.Subtitle!.Contains("customer site"));
        await WaitForEntriesAsync(tenant, SearchEntityType.Unit, 1, e => e.EntityId == u2 && e.Title == $"{p}-493447");

        // exact identifier: relevance 100, reason names the type, opens directly; the document that lists it is in the Documents scope
        var exact = await SearchAsync(tenant, $"{p}-493445");
        Assert.Equal($"/units/{u1}", exact.OpenDirectly);
        var top = exact.Groups.First(g => g.Label == "Units").Results[0];
        Assert.Equal(100, top.Relevance);
        Assert.Equal("exact match · internal serial", top.Reason);
        Assert.Equal([new MatchSpan(0, p.Length + 7)], top.Matches);
        Assert.Contains(exact.Scopes, s => s.Id == "documents" && s.Count == 1);
        var doc = exact.Groups.First(g => g.Label == "Documents").Results[0];
        Assert.StartsWith("document lists the serial", doc.Reason);
        Assert.Contains("line 4", doc.Reason);
        Assert.StartsWith("/units?document=delivery:", doc.Href);
        Assert.True(doc.Relevance < 100);

        // the normalised form (no hyphen) and the IMEI also hit exactly
        Assert.Equal($"/units/{u1}", (await SearchAsync(tenant, $"{p}493445")).OpenDirectly);
        Assert.Equal($"/units/{u1}", (await SearchAsync(tenant, "356938035643809")).OpenDirectly);
        Assert.Equal("exact match · imei", (await SearchAsync(tenant, "356938035643809")).Groups[0].Results[0].Reason);

        // prefix: all three units, none opens directly, ordered by coverage
        var prefix = await SearchAsync(tenant, $"{p}-4934");
        Assert.Null(prefix.OpenDirectly);
        var units = prefix.Groups.First(g => g.Label == "Units").Results;
        Assert.Equal(3, units.Count);
        Assert.All(units, u => Assert.StartsWith("prefix of internal serial", u.Reason));
        Assert.All(units, u => Assert.InRange(u.Relevance, 60, 99));

        // superseded: the old value still finds the unit, says so, and the new value is exact
        var old = await SearchAsync(tenant, $"{p}-493446");
        Assert.Null(old.OpenDirectly);
        var oldHit = old.Groups[0].Results[0];
        Assert.Equal($"/units/{u2}", oldHit.Href);
        Assert.StartsWith("exact match on a superseded internal serial", oldHit.Reason);
        Assert.Equal($"/units/{u2}", (await SearchAsync(tenant, $"{p}-493447")).OpenDirectly);

        // near: one substitution, no exact hit → the near match carries its own reason (493356 is one edit from 493456 and two from every other serial here)
        var near = await SearchAsync(tenant, $"{p}-493356");
        Assert.Null(near.OpenDirectly);
        var nearHit = near.Groups[0].Results[0];
        Assert.StartsWith("near match · 1 edit", nearHit.Reason);
        Assert.Equal($"{p}-493456", nearHit.Title);

        // scope narrows the groups; the hover card follows the default profile, then the tenant's own, then the portal tier
        var scoped = await SearchAsync(tenant, $"{p}-493445", "documents");
        Assert.Single(scoped.Groups);
        Assert.Equal("Documents", scoped.Groups[0].Label);
        Assert.Equal(2, scoped.Scopes.Count);
        var card = top.Metadata!;
        Assert.Equal("Unit · VX-220", card.Eyebrow);
        Assert.Contains(card.Fields, f => f.Label == "Item" && f.Value == "VX-220");
        Assert.Contains(card.Fields, f => f.Label == "Custody" && f.Value == "customer site");
        Assert.Contains(card.Fields, f => f.Label == "Last event" && f.Value == UnitEventType.CustodyChanged);
        Assert.Contains(card.Actions, a => a.Primary && a.Href == $"/units/{u1}");

        using (var scope = fixture.ScopeFor(tenant))
        {
            await scope.ServiceProvider.GetRequiredService<ITenantSettings>().SetAsync(SearchMetadataProfiles.KeyPrefix + SearchEntityType.Unit,
                SearchMetadataProfiles.Serialise([new SearchProfileField("serial", "Serial", true, SearchDisclosureTier.Portal), new SearchProfileField("owner_since", "Customer since"), new SearchProfileField("expiry_date", "Expiry")]), CancellationToken.None);
        }

        var custom = (await SearchAsync(tenant, $"{p}-493445")).Groups[0].Results[0].Metadata!;
        Assert.Equal(["Serial", "Customer since", "Expiry"], custom.Fields.Select(f => f.Label));
        Assert.True(custom.Fields[0].Mono);
        Assert.Null(custom.Fields[2].Value);
        var portal = (await SearchAsync(tenant, $"{p}-493445", tier: SearchDisclosureTier.Portal)).Groups[0].Results[0].Metadata!;
        Assert.Equal(["Serial"], portal.Fields.Select(f => f.Label));
        Assert.Equal("prefix", exact.Engine);
    }

    [DatabaseFact]
    public async Task Bulk_commissioned_units_are_indexed_through_the_reindex_request_and_relevance_holds_over_two_thousand_serials()
    {
        var tenant = await fixture.NewTenantAsync("sb");
        var p = "SB" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        const int count = 2000;
        var lines = Enumerable.Range(1, count).Select(i => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{p}-{i:000000}")])).ToList();
        CommissionUnitsResult? result = null;
        async Task InvokeBulkAsync(IMessageContext _)
        {
            result = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item,
                UnitOrigin.Purchased, lines, At, "t", Entity, LocationId: Guid.NewGuid(),
                LocationKind: LocationKind.Warehouse, Document: new DocumentRef("receipt", Guid.NewGuid(), 1)));
        }
        await fixture.Host.TrackActivity(TimeSpan.FromMinutes(2))
            .WaitForExecutionOf<SearchReindexRequested>(2)
            .ExecuteAndWaitAsync(InvokeBulkAsync);
        Assert.NotNull(result);
        Assert.Equal(count, result.Commissioned);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(count, await db.SearchEntries.CountAsync(e => e.EntityType == SearchEntityType.Unit));
            Assert.Single(await db.SearchEntries.Where(e => e.EntityType == SearchEntityType.Document).ToListAsync());
        }

        // Relevance on migrated-shaped serial data (feeds OD-13): exact, 8-character prefix and one-character typos over a sample.
        var random = new Random(20261015);
        var sample = Enumerable.Range(0, 40).Select(_ => random.Next(1, count + 1)).Distinct().ToList();
        int exactTop = 0, prefixFound = 0, typoTop = 0;
        long totalMs = 0;
        foreach (var n in sample)
        {
            var serial = $"{p}-{n:000000}";
            var exact = await SearchAsync(tenant, serial);
            totalMs += exact.TookMs;
            if (exact.OpenDirectly is not null && exact.Groups[0].Results[0].Title == serial && exact.Groups[0].Results[0].Relevance == 100) exactTop++;
            var prefix = await SearchAsync(tenant, serial[..^1], "units"); // ten units share the prefix; the scoped group lists up to fifty
            if (prefix.Groups.Count > 0 && prefix.Groups[0].Results.Any(r => r.Title == serial)) prefixFound++;
            var pos = serial.Length - 1 - random.Next(0, 3);
            var typo = serial[..pos] + (serial[pos] == '9' ? '8' : (char)(serial[pos] + 1)) + serial[(pos + 1)..];
            var near = await SearchAsync(tenant, typo);
            if (near.Groups.Count > 0 && near.Groups[0].Results[0].Title == serial && near.Groups[0].Results[0].Reason.StartsWith("near match")) typoTop++;
            else if (near.Groups.Count > 0 && near.Groups[0].Results[0].Relevance == 100) typoTop++; // the typo happened to be another real serial: an exact hit is the right answer
        }

        output.WriteLine($"relevance over {count} serials, {sample.Count} samples: exact top-1 {exactTop}/{sample.Count}, prefix found {prefixFound}/{sample.Count}, typo top-1 {typoTop}/{sample.Count}, mean exact query {totalMs / sample.Count} ms");
        Assert.Equal(sample.Count, exactTop);
        Assert.Equal(sample.Count, prefixFound);
        Assert.True(typoTop >= sample.Count * 9 / 10, $"typo top-1 {typoTop}/{sample.Count}");
    }
}
