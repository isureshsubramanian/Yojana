using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ports;
using Yojana.Web;

namespace Yojana.Platform.DatabaseTests;

public sealed class DevReferenceCatalogueTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Development_loads_every_label_from_the_shipped_catalogue()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "DevData", "reference-labels.json");
        var expected = JsonSerializer.Deserialize<DevReferenceCatalogue.DevReferenceCatalogueFile>(
            await File.ReadAllTextAsync(path), JsonOptions)!;
        var itemPort = new InMemoryItemNamePort(new InMemoryItemPorts());
        var partyPort = new InMemoryPartyNamePort();
        var documents = new InMemoryDocumentNumberRegistry();

        var counts = DevReferenceCatalogue.Load(new TestEnvironment(Environments.Development), path, itemPort, partyPort, documents);

        Assert.Equal(new DevReferenceCatalogueCounts(expected.Items.Count, expected.Parties.Count, expected.Documents.Count), counts);
        var itemLabels = await itemPort.NamesAsync(expected.Items.Select(row => row.Id).ToList(), CancellationToken.None);
        var partyLabels = await partyPort.NamesAsync(expected.Parties.Select(row => row.Id).ToList(), CancellationToken.None);
        Assert.All(expected.Items, row => Assert.Equal((row.Code, row.Name), (itemLabels[row.Id].Code, itemLabels[row.Id].Name)));
        Assert.All(expected.Parties, row => Assert.Equal((row.Code, row.Name, row.PartyType),
            (partyLabels[row.Id].Code, partyLabels[row.Id].Name, partyLabels[row.Id].PartyType)));
        Assert.All(expected.Documents, row =>
        {
            Assert.True(documents.TryGet(new DocumentKey(row.Type, row.Id), out var label));
            Assert.Equal((row.Number, row.Status, row.ItemId), (label.Number, label.Status, label.ItemId));
        });
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Non_development_environments_never_open_or_register_the_catalogue(string environmentName)
    {
        var unknown = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var itemPort = new InMemoryItemNamePort(new InMemoryItemPorts());
        var partyPort = new InMemoryPartyNamePort();
        var documents = new InMemoryDocumentNumberRegistry();

        var counts = DevReferenceCatalogue.Load(new TestEnvironment(environmentName), "file-must-not-be-opened.json", itemPort, partyPort, documents);

        Assert.Equal(new DevReferenceCatalogueCounts(0, 0, 0), counts);
        Assert.Null((await itemPort.NamesAsync([unknown], CancellationToken.None))[unknown].Name);
        Assert.Null((await partyPort.NamesAsync([unknown], CancellationToken.None))[unknown].Name);
        Assert.False(documents.TryGet(new DocumentKey("invoice", unknown), out _));
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Yojana.Web.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
