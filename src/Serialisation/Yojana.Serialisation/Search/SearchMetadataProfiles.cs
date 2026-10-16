using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Search;

/// <summary>
/// The hover card's field list per entity type: tenant data under <c>search.metadata.&lt;entity_type&gt;</c> (a JSON
/// array of <see cref="SearchProfileField"/>), with the ADR defaults when the tenant has not edited it. Fields are
/// filtered by the viewer's disclosure tier, so the portal sees fewer than the back office.
/// </summary>
public sealed class SearchMetadataProfiles(ITenantSettings settings)
{
    public const string KeyPrefix = "search.metadata.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly IReadOnlyDictionary<string, SearchMetadataProfile> Defaults = new Dictionary<string, SearchMetadataProfile>
    {
        [SearchEntityType.Unit] = new(SearchEntityType.Unit,
        [
            new("item_code", "Item", Tier: SearchDisclosureTier.Portal), new("status", "Status", Tier: SearchDisclosureTier.Portal), new("owner", "Owner"), new("owner_since", "Owner since", Tier: SearchDisclosureTier.Portal),
            new("custody", "Custody"), new("warranty_end", "Warranty end", Tier: SearchDisclosureTier.Portal), new("last_event", "Last event"), new("last_event_at", "Last event at"), new("flags", "Flags"),
        ]),
        [SearchEntityType.Document] = new(SearchEntityType.Document,
        [
            new("document_type", "Document"), new("serials", "Serials"), new("lines", "Lines"), new("first_event", "First event"), new("last_event", "Last event"), new("event_types", "Events"),
        ]),
    };

    public async Task<SearchMetadataProfile> GetAsync(string entityType, CancellationToken cancellationToken)
    {
        var text = await settings.GetAsync(KeyPrefix + entityType, cancellationToken);
        if (text is not null)
        {
            try
            {
                var fields = JsonSerializer.Deserialize<List<SearchProfileField>>(text, Json);
                if (fields is { Count: > 0 }) return new SearchMetadataProfile(entityType, fields);
            }
            catch (JsonException)
            {
                // a malformed profile falls back to the default rather than hiding every card
            }
        }

        return Defaults.TryGetValue(entityType, out var d) ? d : new SearchMetadataProfile(entityType, []);
    }

    public static string Serialise(IReadOnlyList<SearchProfileField> fields) => JsonSerializer.Serialize(fields, Json);
}
