using System.Reflection;
using System.Text.Json;
using Xunit;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Tests;

public sealed class ManufacturingContractSchemaTests
{
    [Theory]
    [InlineData(ManufacturingProductionOrderQuantityChangeRequested.Schema, ManufacturingProductionOrderQuantityChangeRequested.Version, typeof(ManufacturingProductionOrderQuantityChangeRequested))]
    [InlineData(ManufacturingComponentReturned.Schema, ManufacturingComponentReturned.Version, typeof(ManufacturingComponentReturned))]
    [InlineData(UnitProductionOrderReconciled.Schema, UnitProductionOrderReconciled.Version, typeof(UnitProductionOrderReconciled))]
    public void Manufacturing_contract_has_an_embedded_schema_matching_its_record(string schema, string version, Type type)
    {
        using var document = JsonDocument.Parse(PublishedEventSchemas.Read(schema, version));
        Assert.Equal(schema, document.RootElement.GetProperty("title").GetString());
        var schemaProperties = document.RootElement.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var recordProperties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property => SnakeCase(property.Name)).ToHashSet(StringComparer.Ordinal);
        Assert.True(recordProperties.SetEquals(schemaProperties), $"{schema}: record and schema properties differ: {string.Join(", ", recordProperties.SymmetricExcept(schemaProperties))}");
    }

    [Fact]
    public void Integration_is_a_valid_correction_origin_in_code_and_on_the_wire()
    {
        Assert.Contains(CorrectionInitiatedVia.Integration, CorrectionInitiatedVia.All);
        using var schema = JsonDocument.Parse(PublishedEventSchemas.Read(UnitVoided.Schema, UnitVoided.Version));
        var values = schema.RootElement.GetProperty("$defs").GetProperty("correction").GetProperty("properties")
            .GetProperty("initiated_via").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToList();
        Assert.Contains(CorrectionInitiatedVia.Integration, values);
    }

    private static string SnakeCase(string value) => string.Concat(value.Select((character, index) =>
        index > 0 && char.IsUpper(character) ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
}

internal static class SetTestExtensions
{
    public static IReadOnlyList<T> SymmetricExcept<T>(this IReadOnlySet<T> first, IReadOnlySet<T> second)
    {
        var difference = first.ToHashSet();
        difference.SymmetricExceptWith(second);
        return difference.ToList();
    }
}
