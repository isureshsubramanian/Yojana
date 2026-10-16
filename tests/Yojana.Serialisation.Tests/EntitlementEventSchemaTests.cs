using System.Reflection;
using System.Text.Json;
using Xunit;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Tests;

/// <summary>P2-T18: every entitlement finance event and the Finance reply has an embedded schema that matches the record, nullability included.</summary>
public sealed class EntitlementEventSchemaTests
{
    private static readonly (string Schema, string Version, Type Type, bool Published)[] Contracts =
    [
        (EntitlementCreated.Schema, EntitlementCreated.Version, typeof(EntitlementCreated), true),
        (EntitlementExtendedSold.Schema, EntitlementExtendedSold.Version, typeof(EntitlementExtendedSold), true),
        (EntitlementTransferred.Schema, EntitlementTransferred.Version, typeof(EntitlementTransferred), true),
        (EntitlementRebased.Schema, EntitlementRebased.Version, typeof(EntitlementRebased), true),
        (EntitlementAmended.Schema, EntitlementAmended.Version, typeof(EntitlementAmended), true),
        (EntitlementSuperseded.Schema, EntitlementSuperseded.Version, typeof(EntitlementSuperseded), true),
        (EntitlementBackfilled.Schema, EntitlementBackfilled.Version, typeof(EntitlementBackfilled), true),
        (EntitlementClosed.Schema, EntitlementClosed.Version, typeof(EntitlementClosed), true),
        (EntitlementCoverageChanged.Schema, EntitlementCoverageChanged.Version, typeof(EntitlementCoverageChanged), true),
        (FinanceEntitlementPostingRecorded.Schema, FinanceEntitlementPostingRecorded.Version, typeof(FinanceEntitlementPostingRecorded), false)
    ];

    [Fact]
    public void Every_entitlement_event_has_a_matching_embedded_schema()
    {
        var nullability = new NullabilityInfoContext();

        foreach (var (schema, version, type, _) in Contracts)
        {
            using var document = JsonDocument.Parse(PublishedEventSchemas.Read(schema, version));
            Assert.Equal(schema, document.RootElement.GetProperty("title").GetString());

            var schemaProperties = document.RootElement.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            var required = document.RootElement.GetProperty("required").EnumerateArray().Select(element => element.GetString()!).ToHashSet(StringComparer.Ordinal);
            var recordProperties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).ToDictionary(property => SnakeCase(property.Name), StringComparer.Ordinal);

            Assert.True(recordProperties.Keys.ToHashSet(StringComparer.Ordinal).IsSubsetOf(schemaProperties), $"{schema}: record properties missing from schema: {string.Join(", ", recordProperties.Keys.Except(schemaProperties))}");
            Assert.True(required.IsSubsetOf(recordProperties.Keys), $"{schema}: required schema entries missing from record: {string.Join(", ", required.Except(recordProperties.Keys))}");

            foreach (var (name, property) in recordProperties)
            {
                var isNullable = nullability.Create(property).ReadState == NullabilityState.Nullable;
                Assert.Equal(!isNullable, required.Contains(name));
            }
        }
    }

    [Fact]
    public void Only_outbound_entitlement_events_are_published_unit_events()
    {
        foreach (var (_, _, type, published) in Contracts)
        {
            Assert.Equal(published, typeof(IPublishedUnitEvent).IsAssignableFrom(type));
        }
    }

    private static string SnakeCase(string value) => string.Concat(value.Select((character, index) => index > 0 && char.IsUpper(character) ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
}
