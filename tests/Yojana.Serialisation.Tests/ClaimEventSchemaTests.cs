using System.Text.Json;
using Xunit;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Tests;

public sealed class ClaimEventSchemaTests
{
    [Fact]
    public void Claim_adjudicated_has_an_embedded_1_1_schema()
    {
        using var document = JsonDocument.Parse(PublishedEventSchemas.Read(ClaimAdjudicated.Schema, ClaimAdjudicated.Version));
        Assert.Equal("1.1", ClaimAdjudicated.Version);
        Assert.Equal(ClaimAdjudicated.Schema, document.RootElement.GetProperty("title").GetString());
        var required = document.RootElement.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToList();
        Assert.Contains("claimant_party_ref", required);
        Assert.Contains("settlement_party_ref", required);
    }

    [Theory]
    [InlineData(RecoveryClaimRaised.Schema, RecoveryClaimRaised.Version)]
    [InlineData(RecoveryClaimApproved.Schema, RecoveryClaimApproved.Version)]
    [InlineData(ChargeableQuoteRequested.Schema, ChargeableQuoteRequested.Version)]
    public void Published_claim_contract_has_an_embedded_1_0_schema(string schema, string version)
    {
        using var document = JsonDocument.Parse(PublishedEventSchemas.Read(schema, version));
        Assert.Equal("1.0", version);
        Assert.Equal(schema, document.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void Claim_adjudicated_keeps_successor_item_optional_in_schema_1_0()
    {
        using var document = JsonDocument.Parse(PublishedEventSchemas.Read(ClaimAdjudicated.Schema, ClaimAdjudicated.Version));
        var root = document.RootElement;

        Assert.True(root.GetProperty("properties").TryGetProperty("successor_item_id", out _));
        Assert.DoesNotContain("successor_item_id", root.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
    }
}
