using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ports;

namespace Yojana.Web;

/// <summary>
/// Development labels for opaque item, party and document references. A real item/party module and the document-owning
/// modules replace these in-memory ports; this catalogue is only composition-root data for the development host.
/// </summary>
public static class DevReferenceCatalogue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static DevReferenceCatalogueCounts Load(
        IHostEnvironment environment,
        string path,
        InMemoryItemNamePort items,
        InMemoryPartyNamePort parties,
        InMemoryDocumentNumberRegistry documents)
    {
        if (!environment.IsDevelopment()) return new(0, 0, 0);

        using var stream = File.OpenRead(path);
        var catalogue = JsonSerializer.Deserialize<DevReferenceCatalogueFile>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Development reference catalogue '{path}' is empty.");
        EnsureUnique(catalogue.Items.Select(row => row.Id), "item");
        EnsureUnique(catalogue.Parties.Select(row => row.Id), "party");
        EnsureUnique(catalogue.Documents.Select(row => (row.Type, row.Id)), "document");

        foreach (var item in catalogue.Items)
        {
            Require(item.Code, "item code");
            items.Register(new ItemLabel(item.Id, item.Code, item.Name));
        }

        foreach (var party in catalogue.Parties)
        {
            Require(party.Code, "party code");
            Require(party.PartyType, "party type");
            parties.Register(new PartyLabel(party.Id, party.Code, party.Name, party.PartyType));
        }

        foreach (var document in catalogue.Documents)
        {
            Require(document.Type, "document type");
            Require(document.Number, "document number");
            documents.Register(new DocumentLabel(new DocumentRef(document.Type, document.Id), document.Number, document.Status, document.ItemId));
        }

        return new(catalogue.Items.Count, catalogue.Parties.Count, catalogue.Documents.Count);
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

    public sealed record DevReferenceCatalogueFile(
        IReadOnlyList<DevItemLabel> Items,
        IReadOnlyList<DevPartyLabel> Parties,
        IReadOnlyList<DevDocumentLabel> Documents);

    public sealed record DevItemLabel(Guid Id, string Code, string? Name);

    public sealed record DevPartyLabel(Guid Id, string Code, string? Name, string PartyType);

    public sealed record DevDocumentLabel(Guid Id, string Type, string Number, string? Status = null, Guid? ItemId = null);
}

public sealed record DevReferenceCatalogueCounts(int Items, int Parties, int Documents);
