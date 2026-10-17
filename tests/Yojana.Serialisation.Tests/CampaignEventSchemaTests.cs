using System.Reflection;
using System.Text.Json;
using Xunit;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Tests;

public sealed class CampaignEventSchemaTests
{
    [Fact]
    public void Units_selected_has_a_matching_embedded_summary_schema()
    {
        using var document = JsonDocument.Parse(PublishedEventSchemas.Read(CampaignUnitsSelected.Schema, CampaignUnitsSelected.Version));
        Assert.Equal(CampaignUnitsSelected.Schema, document.RootElement.GetProperty("title").GetString());
        Assert.True(typeof(IPublishedSummaryEvent).IsAssignableFrom(typeof(CampaignUnitsSelected)));
        var schemaProperties = document.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var recordProperties = typeof(CampaignUnitsSelected).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => SnakeCase(p.Name)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(recordProperties.Order(), schemaProperties.Order());
    }

    private static string SnakeCase(string value) => string.Concat(value.Select((character, index) =>
        index > 0 && char.IsUpper(character) ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
}
