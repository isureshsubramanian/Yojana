using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ports;

namespace Yojana.Demo.Tests;

public sealed class DemoConventionsTests
{
    [Fact]
    public void Stable_names_produce_repeatable_version_five_style_ids()
    {
        var first = DemoIds.FromName("claim:twin-dimm");

        Assert.Equal(first, DemoIds.FromName("claim:twin-dimm"));
        Assert.NotEqual(first, DemoIds.FromName("claim:storage-nvme"));
        Assert.Equal('5', first.ToString("D")[14]);
        Assert.Contains(first.ToString("D")[19], "89ab");
    }

    [Fact]
    public void Date_spreading_keeps_claims_inside_the_requested_window()
    {
        var today = new DateOnly(2026, 10, 17);

        var dates = DemoDates.Spread(today, 25, 24, 1);

        Assert.Equal(25, dates.Count);
        Assert.Equal(today.AddDays(-24), dates[0]);
        Assert.Equal(today.AddDays(-1), dates[^1]);
        Assert.True(dates.Zip(dates.Skip(1)).All(pair => pair.First <= pair.Second));
        Assert.All(dates, date => Assert.InRange(today.DayNumber - date.DayNumber, 1, 24));
    }

    [Fact]
    public async Task Demo_lot_labels_use_stable_loader_ids_and_are_searchable_through_the_document_port()
    {
        var catalogue = DemoCatalogue.Load(Path.Combine(AppContext.BaseDirectory, "DevData", "reference-labels.json"));
        var expected = new[] { "KPL-2511", "KPL-2601", "KPL-2410" };
        var registry = new InMemoryDocumentNumberRegistry();
        foreach (var document in catalogue.Documents.Values)
            registry.Register(new DocumentLabel(new DocumentRef(document.Type, document.Id), document.Number, document.Status, document.ItemId));
        var port = new DefaultDocumentNumberPort(registry, new NoRecordedDocuments());

        foreach (var number in expected)
        {
            var lot = catalogue.Documents[number];
            Assert.Equal("lot", lot.Type);
            Assert.Equal(DemoIds.FromName("lot:" + number), lot.Id);
            Assert.Equal(Guid.Parse("10000000-0000-0000-0000-000000000016"), lot.ItemId);
            var found = Assert.Single(await port.FindAsync("lot", number, 10, CancellationToken.None));
            Assert.Equal((lot.Id, lot.ItemId), (found.Document.Id, found.ItemId));
        }
    }

    [Fact]
    public void Phase_five_locations_are_distinct_catalogue_records()
    {
        var catalogue = DemoCatalogue.Load(Path.Combine(AppContext.BaseDirectory, "DevData", "reference-labels.json"));

        Assert.Equal(4, catalogue.Locations.Count);
        Assert.Equal("warehouse", catalogue.Locations["AST-WH-01"].Kind);
        Assert.Equal("quarantine", catalogue.Locations["AST-Q-01"].Kind);
        Assert.Equal("quarantine", catalogue.Locations["AST-Q-02"].Kind);
        Assert.Equal("in_repair", catalogue.Locations["AST-RPR-01"].Kind);
        Assert.All(catalogue.Locations.Values, location =>
            Assert.StartsWith("40000000-0000-0000-0000-0000000000", location.Id.ToString("D")));
        Assert.Equal(4, catalogue.Locations.Values.Select(location => location.Id).Distinct().Count());
    }

    [Fact]
    public void Phase_five_sanitisation_flags_follow_the_item_catalogue()
    {
        var catalogue = DemoCatalogue.Load(Path.Combine(AppContext.BaseDirectory, "DevData", "reference-labels.json"));

        Assert.True(Assert.IsType<DemoCatalogue.ItemSerialisation>(catalogue.Items["RS-1100"].Serialisation).RequiresSanitisation);
        Assert.True(Assert.IsType<DemoCatalogue.ItemSerialisation>(catalogue.Items["SS-1110"].Serialisation).RequiresSanitisation);
        Assert.True(Assert.IsType<DemoCatalogue.ItemSerialisation>(catalogue.Items["SS-1112"].Serialisation).RequiresSanitisation);
        Assert.False(Assert.IsType<DemoCatalogue.ItemSerialisation>(catalogue.Items["PF-400"].Serialisation).RequiresSanitisation);
    }

    private sealed class NoRecordedDocuments : IRecordedDocumentReferenceQuery
    {
        public Task<IReadOnlyList<DocumentRef>> CandidatesAsync(string type, string idPrefix, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DocumentRef>>([]);
    }
}
