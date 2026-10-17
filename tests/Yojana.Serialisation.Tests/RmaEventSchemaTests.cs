using System.Text.Json;
using Xunit;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Tests;

public sealed class RmaEventSchemaTests
{
    [Theory]
    [InlineData(RmaAuthorised.Schema, RmaAuthorised.Version)]
    [InlineData(RmaReceived.Schema, RmaReceived.Version)]
    [InlineData(RmaDispositioned.Schema, RmaDispositioned.Version)]
    public void Published_rma_contract_has_an_embedded_1_0_schema(string schema, string version)
    {
        using var document = JsonDocument.Parse(PublishedEventSchemas.Read(schema, version));
        Assert.Equal("1.0", version);
        Assert.Equal(schema, document.RootElement.GetProperty("title").GetString());
    }
}
