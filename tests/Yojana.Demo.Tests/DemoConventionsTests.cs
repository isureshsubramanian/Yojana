using Xunit;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ports;

namespace Yojana.Demo.Tests;

public sealed class DemoConventionsTests
{
    [Fact]
    public void Manual_task_draft_keeps_customer_separate_from_the_refusal_summary()
    {
        var story = new DemoLoader.DemoManualTask("replacement", "CreateReplacementDelivery", "warehouse",
            "Replacement delivery · RMA-2026-01877", "ERP refused: period 2026-09 closed",
            "Ashvane Cooperative Bank", "The ERP refused the automatic entry because the posting period is closed.",
            "YJ-RMA-2026-01877-DN1", 2, 10, 0, 4850m);

        using var draft = JsonDocument.Parse(DemoLoader.BuildManualTaskDraft(story, new(2026, 10, 19)));
        var fields = draft.RootElement.GetProperty("fields");

        Assert.Equal("Ashvane Cooperative Bank", fields.GetProperty("customer").GetString());
        Assert.Equal("ERP refused: period 2026-09 closed", draft.RootElement.GetProperty("subtitle").GetString());
        Assert.NotEqual(draft.RootElement.GetProperty("subtitle").GetString(),
            fields.GetProperty("customer").GetString());
    }

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

        Assert.Equal(7, catalogue.Locations.Count);
        Assert.Equal("warehouse", catalogue.Locations["LDS-WA-01"].Kind);
        Assert.Equal("quarantine", catalogue.Locations["LDS-Q1"].Kind);
        Assert.Equal("quarantine", catalogue.Locations["LDS-Q2"].Kind);
        Assert.Equal("in_repair", catalogue.Locations["LDS-RB-01"].Kind);
        Assert.All(catalogue.Locations.Values, location =>
            Assert.StartsWith("40000000-0000-0000-0000-0000000000", location.Id.ToString("D")));
        Assert.Equal(7, catalogue.Locations.Values.Select(location => location.Id).Distinct().Count());
    }

    [Fact]
    public void Demo_catalogue_registers_facility_display_names_for_certificate_snapshots()
    {
        var catalogue = DemoCatalogue.Load(Path.Combine(AppContext.BaseDirectory, "DevData", "reference-labels.json"));
        var services = new ServiceCollection();
        services.AddSingleton<InMemoryItemPorts>();
        services.AddSingleton<IItemQueryPort>(provider => provider.GetRequiredService<InMemoryItemPorts>());
        services.AddSingleton<InMemoryItemNamePort>();
        services.AddSingleton<InMemoryItemFamilyNamePort>();
        services.AddSingleton<InMemoryWarrantyPorts>();
        services.AddSingleton<InMemoryPartyNamePort>();
        services.AddSingleton<InMemoryDocumentNumberRegistry>();
        services.AddSingleton<InMemoryLocationNameRegistry>();
        using var provider = services.BuildServiceProvider();

        catalogue.Register(provider);

        var expected = catalogue.Locations["LDS-WA-01"];
        Assert.True(provider.GetRequiredService<InMemoryLocationNameRegistry>().TryGet(expected.Id, out var registered));
        Assert.Equal((expected.Code, expected.Name, expected.Kind), (registered.Code, registered.Name, registered.Kind));
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

    [Fact]
    public async Task Failed_step_stops_the_host_and_returns_the_step_name_with_a_non_zero_exit_code()
    {
        var lifetime = new RecordingHostedService();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IHostedService>(lifetime);
        using var host = builder.Build();
        using var error = new StringWriter();

        var exitCode = await DemoProcess.RunAsync(host,
            _ => throw new InvalidOperationException("post supplier receipts: fictional failure"),
            error, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.True(lifetime.Stopped);
        Assert.Contains("post supplier receipts", error.ToString());
    }

    [Fact]
    public async Task Successful_run_stops_the_host_and_returns_zero()
    {
        var lifetime = new RecordingHostedService();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IHostedService>(lifetime);
        using var host = builder.Build();

        var exitCode = await DemoProcess.RunAsync(host, _ => Task.CompletedTask, TextWriter.Null, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(lifetime.Stopped);
    }

    private sealed class NoRecordedDocuments : IRecordedDocumentReferenceQuery
    {
        public Task<IReadOnlyList<DocumentRef>> CandidatesAsync(string type, string idPrefix, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DocumentRef>>([]);
    }

    private sealed class RecordingHostedService : IHostedService
    {
        public bool Stopped { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) { Stopped = true; return Task.CompletedTask; }
    }
}
