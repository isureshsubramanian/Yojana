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
        var itemProfiles = new InMemoryItemPorts();
        var itemPort = new InMemoryItemNamePort(itemProfiles);
        var familyPort = new InMemoryItemFamilyNamePort();
        var warranties = new InMemoryWarrantyPorts();
        var partyPort = new InMemoryPartyNamePort();
        var contactPort = new InMemoryPartyContactPort();
        var documents = new InMemoryDocumentNumberRegistry();
        var locations = new InMemoryLocationNameRegistry();

        var counts = DevReferenceCatalogue.Load(new TestEnvironment(Environments.Development), path, itemProfiles, itemPort, familyPort, warranties, partyPort, contactPort, documents, locations);

        Assert.Equal(new DevReferenceCatalogueCounts(expected.Items.Count, expected.Parties.Count, expected.Documents.Count, expected.Locations.Count), counts);
        var torrenfold = Assert.Single(await contactPort.ContactsAsync(Guid.Parse("20000000-0000-0000-0000-000000000010"), CancellationToken.None));
        Assert.Equal(("Jonas Weber", "j.weber@torrenfold.example", "de"), (torrenfold.Name, torrenfold.Email, torrenfold.Language));
        var itemLabels = await itemPort.NamesAsync(expected.Items.Select(row => row.Id).ToList(), CancellationToken.None);
        var partyLabels = await partyPort.NamesAsync(expected.Parties.Select(row => row.Id).ToList(), CancellationToken.None);
        Assert.All(expected.Items, row => Assert.Equal((row.Code, row.Name), (itemLabels[row.Id].Code, itemLabels[row.Id].Name)));
        Assert.Contains(await familyPort.ListAsync(CancellationToken.None), row => row.Code == "server_node" && row.Name == "Server node");
        foreach (var row in expected.Items.Where(row => row.Serialisation is not null))
        {
            var profile = await itemProfiles.GetAsync(row.Id, CancellationToken.None);
            Assert.Equal((row.Code, row.Serialisation!.SerialisationPoint), (profile!.ItemCode, profile.SerialisationPoint));
            var registeredPolicies = await warranties.PoliciesForAsync(row.Id, CancellationToken.None);
            Assert.Equal((row.WarrantyPolicies ?? []).Select(policy => policy.PolicyCode).Order(), registeredPolicies.Select(policy => policy.PolicyCode).Order());
        }
        Assert.All(expected.Parties, row => Assert.Equal((row.Code, row.Name, row.PartyType),
            (partyLabels[row.Id].Code, partyLabels[row.Id].Name, partyLabels[row.Id].PartyType)));
        Assert.All(expected.Documents, row =>
        {
            Assert.True(documents.TryGet(new DocumentKey(row.Type, row.Id), out var label));
            Assert.Equal((row.Number, row.Status, row.ItemId), (label.Number, label.Status, label.ItemId));
        });
        Assert.All(expected.Locations, row =>
        {
            Assert.True(locations.TryGet(row.Id, out var label));
            Assert.Equal((row.Code, row.Name, row.Kind), (label.Code, label.Name, label.Kind));
        });
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Non_development_environments_never_open_or_register_the_catalogue(string environmentName)
    {
        var unknown = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var itemProfiles = new InMemoryItemPorts();
        var itemPort = new InMemoryItemNamePort(itemProfiles);
        var familyPort = new InMemoryItemFamilyNamePort();
        var warranties = new InMemoryWarrantyPorts();
        var partyPort = new InMemoryPartyNamePort();
        var contactPort = new InMemoryPartyContactPort();
        var documents = new InMemoryDocumentNumberRegistry();
        var locations = new InMemoryLocationNameRegistry();

        var counts = DevReferenceCatalogue.Load(new TestEnvironment(environmentName), "file-must-not-be-opened.json", itemProfiles, itemPort, familyPort, warranties, partyPort, contactPort, documents, locations);

        Assert.Equal(new DevReferenceCatalogueCounts(0, 0, 0, 0), counts);
        Assert.Null((await itemPort.NamesAsync([unknown], CancellationToken.None))[unknown].Name);
        Assert.Null((await partyPort.NamesAsync([unknown], CancellationToken.None))[unknown].Name);
        Assert.False(documents.TryGet(new DocumentKey("invoice", unknown), out _));
        Assert.False(locations.TryGet(unknown, out _));
        Assert.Empty(await familyPort.ListAsync(CancellationToken.None));
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Yojana.Web.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
