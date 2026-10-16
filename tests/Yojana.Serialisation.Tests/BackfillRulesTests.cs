using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.Tests;

public sealed class BackfillRulesTests
{
    private static BackfillEntitlements Command(string reason = BackfillReason.HistoricalGap, IReadOnlyList<string>? roles = null, IReadOnlyList<string>? evidence = null, IReadOnlyList<Guid>? units = null) =>
        new(Guid.NewGuid(), units ?? [Guid.NewGuid()], reason, evidence ?? ["delivery DN-42"], new DocumentRef("correction", Guid.NewGuid()), Guid.NewGuid(), DateTime.UtcNow,
            "manager", new AmendmentCommitter("manager", roles ?? [YojanaRole.ServiceManager]));

    [Theory]
    [InlineData(BackfillReason.HistoricalGap)]
    [InlineData(BackfillReason.DropShipGap)]
    [InlineData(BackfillReason.MigrationGap)]
    public void The_three_reasons_are_accepted(string reason) => Assert.Null(BackfillRules.CommandProblem(Command(reason)));

    [Fact]
    public void A_manager_distinct_units_and_evidence_are_required()
    {
        var unit = Guid.NewGuid();
        Assert.Equal("approval_required", BackfillRules.CommandProblem(Command(roles: [YojanaRole.ServiceDesk])));
        Assert.Equal("evidence_required", BackfillRules.CommandProblem(Command(evidence: [])));
        Assert.Equal("units_required", BackfillRules.CommandProblem(Command(units: [])));
        Assert.Equal("units_repeated", BackfillRules.CommandProblem(Command(units: [unit, unit])));
        Assert.Equal("reason_unknown", BackfillRules.CommandProblem(Command("other")));
    }

    [Fact]
    public void Only_an_assurance_backfill_changes_the_reserve()
    {
        Assert.True(BackfillRules.FinanceReviewRequired(AssuranceOrService.Assurance));
        Assert.False(BackfillRules.FinanceReviewRequired(AssuranceOrService.Service));
    }
}
