using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ports;

namespace Yojana.Demo;

public sealed class DemoCatalogue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private DemoCatalogue(CatalogueFile source)
    {
        Items = source.Items.ToDictionary(row => row.Code, StringComparer.Ordinal);
        Parties = source.Parties.ToDictionary(row => row.Code, StringComparer.Ordinal);
        Documents = source.Documents.ToDictionary(row => row.Number, StringComparer.Ordinal);
        Locations = source.Locations?.ToDictionary(row => row.Code, StringComparer.Ordinal)
            ?? new Dictionary<string, CatalogueLocation>(StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, CatalogueItem> Items { get; }
    public IReadOnlyDictionary<string, CatalogueParty> Parties { get; }
    public IReadOnlyDictionary<string, CatalogueDocument> Documents { get; }
    public IReadOnlyDictionary<string, CatalogueLocation> Locations { get; }

    public static DemoCatalogue Load(string path)
    {
        using var stream = File.OpenRead(path);
        var source = JsonSerializer.Deserialize<CatalogueFile>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Development catalogue '{path}' is empty.");
        return new(source);
    }

    public void Register(IServiceProvider services)
    {
        var profiles = services.GetRequiredService<InMemoryItemPorts>();
        var itemNames = services.GetRequiredService<InMemoryItemNamePort>();
        var warranties = services.GetRequiredService<InMemoryWarrantyPorts>();
        foreach (var item in Items.Values)
        {
            itemNames.Register(new ItemLabel(item.Id, item.Code, item.Name));
            if (item.Serialisation is { } profile)
            {
                profiles.Register(new ItemSerialisationProfile(item.Id, item.Code, profile.TrackingMode,
                    profile.SerialisationPoint, profile.RetentionClass, profile.SerialReusePolicy,
                    profile.AsBuiltCompleteness, profile.SerialScheme, profile.ExpiryRequired, profile.ItemClass,
                    RequiresSanitisation: profile.RequiresSanitisation));
            }

            foreach (var policy in item.WarrantyPolicies ?? [])
                warranties.Register(new ItemWarrantyPolicy(item.Id, policy.CoverageFamily, policy.PolicyCode, policy.EntitlementRequiredOnSale));
        }

        var partyNames = services.GetRequiredService<InMemoryPartyNamePort>();
        foreach (var party in Parties.Values)
            partyNames.Register(new PartyLabel(party.Id, party.Code, party.Name, party.PartyType));

        var documents = services.GetRequiredService<InMemoryDocumentNumberRegistry>();
        foreach (var document in Documents.Values)
            documents.Register(new DocumentLabel(new DocumentRef(document.Type, document.Id), document.Number, document.Status, document.ItemId));
    }

    // Shared JSON contract: the web host's DevReferenceCatalogue reads this same development-only source. Keeping the
    // input narrow avoids runtime coupling between the tool and web composition root; change both readers together.
    public sealed record CatalogueFile(
        IReadOnlyList<CatalogueItem> Items,
        IReadOnlyList<CatalogueParty> Parties,
        IReadOnlyList<CatalogueDocument> Documents,
        IReadOnlyList<CatalogueLocation>? Locations = null);

    public sealed record CatalogueItem(
        Guid Id,
        string Code,
        string? Name,
        ItemSerialisation? Serialisation = null,
        IReadOnlyList<ItemWarranty>? WarrantyPolicies = null);

    public sealed record ItemSerialisation(
        string TrackingMode,
        string SerialisationPoint,
        string RetentionClass,
        string SerialReusePolicy,
        string AsBuiltCompleteness,
        string SerialScheme,
        bool ExpiryRequired = false,
        string? ItemClass = null,
        bool RequiresSanitisation = false);

    public sealed record ItemWarranty(string CoverageFamily, string PolicyCode, bool EntitlementRequiredOnSale = true);
    public sealed record CatalogueParty(Guid Id, string Code, string? Name, string PartyType);
    public sealed record CatalogueDocument(Guid Id, string Type, string Number, string? Status = null, Guid? ItemId = null);
    public sealed record CatalogueLocation(Guid Id, string Code, string Name, string Kind);
}
