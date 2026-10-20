using System.Text.Json;
using Xunit;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Tests;

public sealed class RmaEventSchemaTests
{
    [Theory]
    [InlineData(RmaAuthorised.Schema, RmaAuthorised.Version, "1.1")]
    [InlineData(RmaReceived.Schema, RmaReceived.Version, "1.1")]
    [InlineData(RmaDispositioned.Schema, RmaDispositioned.Version, "1.2")]
    public void Published_rma_contract_has_its_embedded_schema(string schema, string version, string expected)
    {
        using var document = JsonDocument.Parse(PublishedEventSchemas.Read(schema, version));
        Assert.Equal(expected, version);
        Assert.Equal(schema, document.RootElement.GetProperty("title").GetString());
    }
}
