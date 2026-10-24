using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ports;

namespace Yojana.Web;

/// <summary>
/// Development labels for opaque item, party, document and location references. Their owning modules replace these
/// in-memory ports; this catalogue is only composition-root data for the development host.
/// </summary>
public static class DevReferenceCatalogue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static DevReferenceCatalogueCounts Load(
        IHostEnvironment environment,
        string path,
        InMemoryItemPorts itemProfiles,
        InMemoryItemNamePort items,
        InMemoryItemFamilyNamePort itemFamilies,
        InMemoryWarrantyPorts warranties,
        InMemoryPartyNamePort parties,
        InMemoryPartyContactPort contacts,
        InMemoryDocumentNumberRegistry documents,
        InMemoryLocationNameRegistry locations)
    {
        if (!environment.IsDevelopment()) return new(0, 0, 0, 0);

        using var stream = File.OpenRead(path);
        var catalogue = JsonSerializer.Deserialize<DevReferenceCatalogueFile>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Development reference catalogue '{path}' is empty.");
        EnsureUnique(catalogue.Items.Select(row => row.Id), "item");
        EnsureUnique(catalogue.Parties.Select(row => row.Id), "party");
        EnsureUnique(catalogue.Documents.Select(row => (row.Type, row.Id)), "document");
        EnsureUnique(catalogue.Locations.Select(row => row.Id), "location");

        foreach (var item in catalogue.Items)
        {
            Require(item.Code, "item code");
            items.Register(new ItemLabel(item.Id, item.Code, item.Name));
            if (item.Serialisation is { } profile)
            {
                if (!string.IsNullOrWhiteSpace(profile.ItemClass))
                    itemFamilies.Register(new ItemFamilyLabel(profile.ItemClass, FamilyName(profile.ItemClass)));
                itemProfiles.Register(new ItemSerialisationProfile(item.Id, item.Code, profile.TrackingMode,
                    profile.SerialisationPoint, profile.RetentionClass, profile.SerialReusePolicy,
                    profile.AsBuiltCompleteness, profile.SerialScheme, profile.ExpiryRequired, profile.ItemClass,
                    RequiresSanitisation: profile.RequiresSanitisation, ModelNumbers: profile.ModelNumbers));
            }

            foreach (var policy in item.WarrantyPolicies ?? [])
            {
                Require(policy.CoverageFamily, "item warranty coverage family");
                Require(policy.PolicyCode, "item warranty policy code");
                warranties.Register(new ItemWarrantyPolicy(item.Id, policy.CoverageFamily, policy.PolicyCode,
                    policy.EntitlementRequiredOnSale));
            }
        }

        foreach (var party in catalogue.Parties)
        {
            Require(party.Code, "party code");
            Require(party.PartyType, "party type");
            parties.Register(new PartyLabel(party.Id, party.Code, party.Name, party.PartyType));
            foreach (var contact in party.Contacts ?? [])
            {
                Require(contact.Email, "contact e-mail");
                contacts.Register(new PartyContactLabel(party.Id, contact.Name, contact.Email, contact.Role, contact.Language));
            }
        }

        foreach (var document in catalogue.Documents)
        {
            Require(document.Type, "document type");
            Require(document.Number, "document number");
            documents.Register(new DocumentLabel(new DocumentRef(document.Type, document.Id), document.Number, document.Status, document.ItemId));
        }

        foreach (var location in catalogue.Locations)
        {
            Require(location.Code, "location code");
            Require(location.Kind, "location kind");
            locations.Register(new LocationLabel(location.Id, location.Code, location.Name, location.Kind));
        }

        return new(catalogue.Items.Count, catalogue.Parties.Count, catalogue.Documents.Count, catalogue.Locations.Count);
    }

    private static void EnsureUnique<T>(IEnumerable<T> values, string kind) where T : notnull
    {
        if (values.GroupBy(value => value).Any(group => group.Count() > 1))
            throw new InvalidDataException($"Development reference catalogue contains a duplicate {kind} reference.");
    }

    private static void Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"Development reference catalogue has a blank {field}.");
    }

    private static string FamilyName(string code)
    {
        var text = code.Replace('_', ' ').Trim();
        return text.Length == 0 ? code : char.ToUpperInvariant(text[0]) + text[1..];
    }

    // Shared JSON contract: tools/Yojana.Demo/DemoCatalogue.cs reads the same development-only file so the loader and
    // the restarted host see identical labels, item profiles and product-policy mappings. Change both readers together.
    public sealed record DevReferenceCatalogueFile(
        IReadOnlyList<DevItemLabel> Items,
        IReadOnlyList<DevPartyLabel> Parties,
        IReadOnlyList<DevDocumentLabel> Documents,
        IReadOnlyList<DevLocationLabel> Locations);

    public sealed record DevItemLabel(
        Guid Id,
        string Code,
        string? Name,
        DevItemSerialisation? Serialisation = null,
        IReadOnlyList<DevItemWarrantyPolicy>? WarrantyPolicies = null);

    public sealed record DevItemSerialisation(
        string TrackingMode,
        string SerialisationPoint,
        string RetentionClass,
        string SerialReusePolicy,
        string AsBuiltCompleteness,
        string SerialScheme,
        bool ExpiryRequired = false,
        string? ItemClass = null,
        bool RequiresSanitisation = false,
        IReadOnlyList<string>? ModelNumbers = null);

    public sealed record DevItemWarrantyPolicy(
        string CoverageFamily,
        string PolicyCode,
        bool EntitlementRequiredOnSale = true);

    /// <param name="Contacts">Customer contacts for customer e-mail (P9-T20); only the host reads them, the demo loader ignores them.</param>
    public sealed record DevPartyLabel(Guid Id, string Code, string? Name, string PartyType, IReadOnlyList<DevPartyContact>? Contacts = null);

    public sealed record DevPartyContact(string Name, string Email, string? Role = null, string? Language = null);

    public sealed record DevDocumentLabel(Guid Id, string Type, string Number, string? Status = null, Guid? ItemId = null);

    public sealed record DevLocationLabel(Guid Id, string Code, string? Name, string Kind);
}

public sealed record DevReferenceCatalogueCounts(int Items, int Parties, int Documents, int Locations);
