using System.Text.Json;
using Xunit;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Tests;

public sealed class ClaimEventSchemaTests
{
    [Theory]
    [InlineData(ClaimAdjudicated.Schema, ClaimAdjudicated.Version)]
    [InlineData(RecoveryClaimRaised.Schema, RecoveryClaimRaised.Version)]
    [InlineData(RecoveryClaimApproved.Schema, RecoveryClaimApproved.Version)]
    [InlineData(ChargeableQuoteRequested.Schema, ChargeableQuoteRequested.Version)]
    public void Published_claim_contract_has_an_embedded_1_0_schema(string schema, string version)
    {
        using var document = JsonDocument.Parse(PublishedEventSchemas.Read(schema, version));
        Assert.Equal("1.0", version);
        Assert.Equal(schema, document.RootElement.GetProperty("title").GetString());
    }
}
