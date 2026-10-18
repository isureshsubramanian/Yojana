using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.Tests;

public sealed class ReferencePortTests
{
    private static readonly Guid ItemId = Guid.Parse("1a2b3c4d-1111-2222-3333-444444444444");
    private static readonly Guid PartyRef = Guid.Parse("5e6f7788-1111-2222-3333-444444444444");
    private static readonly Guid DocumentId = Guid.Parse("90abcdef-1111-2222-3333-444444444444");
    private static readonly Guid LocationId = Guid.Parse("7a8b9c0d-1111-2222-3333-444444444444");

    [Fact]
    public async Task Defaults_use_item_facts_and_one_shared_fallback_format()
    {
        var items = new InMemoryItemNamePort(new ItemFacts(new ItemSerialisationProfile(ItemId, "FG-100", "serial",
            "production_release", "standard_10y", "never", "block", "internal")));
        var parties = new InMemoryPartyNamePort();
        var documents = new DefaultDocumentNumberPort(new InMemoryDocumentNumberRegistry(), new RecordedDocuments([]));

        var item = (await items.NamesAsync([ItemId], CancellationToken.None))[ItemId];
        var party = (await parties.NamesAsync([PartyRef], CancellationToken.None))[PartyRef];
        var document = (await documents.NumbersAsync([new DocumentRef("production_order", DocumentId)], CancellationToken.None))
            [new DocumentKey("production_order", DocumentId)];

        Assert.Equal("FG-100", item.Code);
        Assert.Null(item.Name);
        Assert.Equal("BP-5E6F7788", party.Code);
        Assert.Null(party.Name);
        Assert.Equal("MO-90ABCDEF", document.Number);
    }

    [Theory]
    [InlineData("receipt", "GR-")]
    [InlineData("shipment", "DN-")]
    [InlineData("invoice", "IN-")]
    [InlineData("transfer", "TR-")]
    [InlineData("return", "RT-")]
    [InlineData("lot", "LOT-")]
    [InlineData("other", "DOC-")]
    public void Document_fallback_prefixes_are_centralised(string type, string prefix)
    {
        Assert.Equal(prefix + "90ABCDEF", ReferenceFallback.DocumentNumber(new DocumentRef(type, DocumentId)));
    }

    [Fact]
    public async Task Registered_labels_win_and_search_ranks_exact_codes_and_numbers_first()
    {
        var items = new InMemoryItemNamePort(new ItemFacts(null));
        var parties = new InMemoryPartyNamePort();
        var registry = new InMemoryDocumentNumberRegistry();
        var documents = new DefaultDocumentNumberPort(registry,
            new RecordedDocuments([new DocumentRef("production_order", DocumentId)]));
        items.Register(new ItemLabel(ItemId, "FG-100", "Field gateway"));
        parties.Register(new PartyLabel(PartyRef, "C-200", "Northwind Plant", "customer"));
        registry.Register(new DocumentLabel(new DocumentRef("production_order", DocumentId), "MO-2026-731", "released", ItemId));

        Assert.Equal("Field gateway", (await items.NamesAsync([ItemId], CancellationToken.None))[ItemId].Name);
        Assert.Equal("Northwind Plant", (await parties.NamesAsync([PartyRef], CancellationToken.None))[PartyRef].Name);
        Assert.Equal(ItemId, (await documents.NumbersAsync([new DocumentRef("production_order", DocumentId)], CancellationToken.None))
            [new DocumentKey("production_order", DocumentId)].ItemId);
        Assert.Equal(ItemId, Assert.Single(await items.SearchAsync("FG-100", 10, CancellationToken.None)).ItemId);
        Assert.Equal(PartyRef, Assert.Single(await parties.SearchAsync("North", 10, CancellationToken.None)).PartyRef);
        Assert.Equal("MO-2026-731", Assert.Single(await documents.FindAsync("production_order", "MO-2026-731", 10, CancellationToken.None)).Number);
    }

    [Fact]
    public async Task Recorded_reference_resolves_a_fallback_number_without_inverting_its_hex_prefix()
    {
        var reference = new DocumentRef("production_order", DocumentId);
        var documents = new DefaultDocumentNumberPort(new InMemoryDocumentNumberRegistry(), new RecordedDocuments([reference]));

        var result = Assert.Single(await documents.FindAsync("production_order", "MO-90AB", 10, CancellationToken.None));

        Assert.Equal(reference, result.Document);
        Assert.Equal("MO-90ABCDEF", result.Number);
    }

    [Fact]
    public async Task Registered_lot_search_preserves_the_item_needed_for_its_label()
    {
        var lotId = Guid.Parse("7061be89-a2b0-5cda-b70a-0b2616e5b9c4");
        var registry = new InMemoryDocumentNumberRegistry();
        registry.Register(new DocumentLabel(new DocumentRef("lot", lotId), "KPL-2511", ItemId: ItemId));
        var documents = new DefaultDocumentNumberPort(registry, new RecordedDocuments([]));

        var result = Assert.Single(await documents.FindAsync("lot", "KPL-25", 10, CancellationToken.None));

        Assert.Equal(new DocumentRef("lot", lotId), result.Document);
        Assert.Equal("KPL-2511", result.Number);
        Assert.Equal(ItemId, result.ItemId);
    }

    [Fact]
    public async Task Default_location_port_names_recorded_custody_locations_with_a_short_fallback()
    {
        var registry = new InMemoryLocationNameRegistry();
        var locations = new DefaultLocationNamePort(registry,
            new RecordedLocations([new RecordedLocationReference(LocationId, "quarantine")]));

        var result = (await locations.NamesAsync([LocationId], CancellationToken.None))[LocationId];

        Assert.Equal("LOC-7A8B9C0D", result.Code);
        Assert.Null(result.Name);
        Assert.Equal("quarantine", result.Kind);
        Assert.Equal(LocationId, Assert.Single(await locations.SearchAsync(
            "LOC-7A8B", 10, "quarantine", CancellationToken.None)).LocationId);
    }

    private sealed class ItemFacts(ItemSerialisationProfile? profile) : IItemQueryPort
    {
        public Task<ItemSerialisationProfile?> GetAsync(Guid itemId, CancellationToken cancellationToken) => Task.FromResult(profile);

        public Task<IReadOnlyDictionary<Guid, ItemSerialisationProfile?>> GetManyAsync(
            IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ItemSerialisationProfile?>>(itemIds.Distinct().ToDictionary(
                itemId => itemId, _ => profile));
    }

    private sealed class RecordedDocuments(IReadOnlyList<DocumentRef> documents) : IRecordedDocumentReferenceQuery
    {
        public Task<IReadOnlyList<DocumentRef>> CandidatesAsync(string type, string idPrefix, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DocumentRef>>(documents.Where(document => document.Type == type
                && document.Id.ToString("N").StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase)).Take(take).ToList());
    }

    private sealed class RecordedLocations(IReadOnlyList<RecordedLocationReference> locations) : IRecordedLocationReferenceQuery
    {
        public Task<IReadOnlyDictionary<Guid, string?>> KindsAsync(
            IReadOnlyCollection<Guid> locationIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string?>>(locations
                .Where(location => locationIds.Contains(location.LocationId))
                .ToDictionary(location => location.LocationId, location => location.Kind));

        public Task<IReadOnlyList<RecordedLocationReference>> CandidatesAsync(
            string idPrefix, string? kind, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecordedLocationReference>>(locations
                .Where(location => location.LocationId.ToString("N").StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase)
                    && (kind is null || location.Kind == kind))
                .Take(take).ToList());
    }
}
