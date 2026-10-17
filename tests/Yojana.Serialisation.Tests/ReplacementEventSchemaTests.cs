using System.Reflection;
using System.Text.Json;
using Xunit;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Tests;

public sealed class ReplacementEventSchemaTests
{
    [Fact]
    public void Replacement_events_have_matching_versioned_schemas()
    {
        var contracts = new[]
        {
            (ReplacementShipped.Schema, ReplacementShipped.Version, typeof(ReplacementShipped)),
            (ReplacementAssetTagPrintRequested.Schema, ReplacementAssetTagPrintRequested.Version, typeof(ReplacementAssetTagPrintRequested)),
            (ReplacementLinkCorrected.Schema, ReplacementLinkCorrected.Version, typeof(ReplacementLinkCorrected)),
            (CertificateReissueRequested.Schema, CertificateReissueRequested.Version, typeof(CertificateReissueRequested)),
        };
        var nullability = new NullabilityInfoContext();
        foreach (var (schema, version, type) in contracts)
        {
            using var document = JsonDocument.Parse(PublishedEventSchemas.Read(schema, version));
            Assert.Equal(schema, document.RootElement.GetProperty("title").GetString());
            var schemaProperties = document.RootElement.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            var required = document.RootElement.GetProperty("required").EnumerateArray().Select(element => element.GetString()!).ToHashSet(StringComparer.Ordinal);
            var recordProperties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).ToDictionary(property => SnakeCase(property.Name), StringComparer.Ordinal);
            Assert.True(recordProperties.Keys.ToHashSet(StringComparer.Ordinal).IsSubsetOf(schemaProperties));
            foreach (var (name, property) in recordProperties)
                Assert.Equal(nullability.Create(property).ReadState != NullabilityState.Nullable, required.Contains(name));
        }
    }

    private static string SnakeCase(string value) => string.Concat(value.Select((character, index) =>
        index > 0 && char.IsUpper(character) ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
}
