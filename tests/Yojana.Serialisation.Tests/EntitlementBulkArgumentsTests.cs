using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;
using Xunit;

namespace Yojana.Serialisation.Tests;

public sealed class EntitlementBulkArgumentsTests
{
    [Fact]
    public void Parses_operation_specific_values_once()
    {
        var policy = Guid.NewGuid();
        var parsed = EntitlementBulkArguments.Parse(new BulkOperationArguments(DateTime.UtcNow, "actor", Guid.NewGuid(), new("sales_order", Guid.NewGuid()), Extra:
            new Dictionary<string, string>
            {
                ["family"] = "service_level", ["policy_version_id"] = policy.ToString(), ["effective_date"] = "2026-11-01",
                ["new_term_value"] = "36", ["new_term_unit"] = DurationRule.Months, ["entered_value"] = "1250.50",
                ["evidence_refs"] = "invoice:1,approval:2",
            }));

        Assert.Equal("service_level", parsed.Family);
        Assert.Equal(policy, parsed.PolicyVersionId);
        Assert.Equal(new DateOnly(2026, 11, 1), parsed.EffectiveDate);
        Assert.Equal(36, parsed.NewTermValue);
        Assert.Equal(1250.50m, parsed.EnteredValue);
        Assert.Equal(["invoice:1", "approval:2"], parsed.EvidenceRefs);
    }

    [Theory]
    [InlineData("effective_date", "01/11/2026")]
    [InlineData("policy_version_id", "not-a-guid")]
    [InlineData("entered_value", "twelve")]
    public void Refuses_malformed_values(string key, string value)
    {
        var error = Assert.Throws<CommandRefusedException>(() => EntitlementBulkArguments.Parse(new BulkOperationArguments(DateTime.UtcNow, "actor", Guid.NewGuid(),
            new("order", Guid.NewGuid()), Extra: new Dictionary<string, string> { ["family"] = "base", [key] = value })));
        Assert.Equal("bulk_arguments_invalid", error.Rule);
    }
}
