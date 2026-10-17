using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Workbench;

namespace Yojana.Serialisation.Tests;

public sealed class WorkbenchTests
{
    [Fact]
    public void Standard_catalog_maps_every_enabled_choice_to_an_inline_command_or_screen_handoff()
    {
        var choices = StandardWorkbenchChoices.All.Where(c => !c.Fallback).ToList();

        Assert.Equal(10, choices.Count);
        Assert.Contains(choices, c => c.Choice == CorrectionWorkbenchChoice.ReplacedOrExchanged);
        Assert.Contains(choices, c => c.Choice == CorrectionWorkbenchChoice.RefurbishedAndReserialised);
        Assert.All(choices, choice =>
        {
            Assert.NotEmpty(choice.Commands);
            Assert.All(choice.Commands, command =>
            {
                var route = WorkbenchChoiceRules.Options(choice).Single(o => o.Command == command);
                Assert.False(string.IsNullOrWhiteSpace(route.DefaultReason));
                Assert.InRange(route.RootCause, CorrectionRootCause.DocumentSerials, CorrectionRootCause.Warranty);
            });
        });
        Assert.Equal([CorrectionCommand.CorrectIdentifier, CorrectionCommand.SwapIdentifiers],
            choices.Single(c => c.Choice == CorrectionWorkbenchChoice.MistypedOrCrossed).Commands);
        Assert.Equal([CorrectionCommand.ReassignAllocation, CorrectionCommand.VoidUnit],
            choices.Single(c => c.Choice == CorrectionWorkbenchChoice.WrongAllocation).Commands);
        Assert.True(StandardWorkbenchChoices.All[^1].Fallback);
        Assert.Empty(Validate(StandardWorkbenchChoices.All));
    }

    [Fact]
    public void Choice_cannot_weaken_the_commands_approval_roles()
    {
        var added = Added("loose", CorrectionCommand.CorrectIdentifier) with { CommitRoles = [YojanaRole.CustomerService] };
        var problems = Validate([.. StandardWorkbenchChoices.All, added]);

        Assert.Contains(problems, p => p.Rule == "workbench_approval_weaker_than_command");
    }

    [Fact]
    public void Choice_refuses_a_command_without_a_dispatch_adapter()
    {
        var problems = Validate([.. StandardWorkbenchChoices.All, Added("unknown", "change_anything")]);

        Assert.Contains(problems, p => p.Rule == "workbench_command_unknown");
    }

    [Fact]
    public void Standard_choice_cannot_be_retired()
    {
        var choices = StandardWorkbenchChoices.All.Select(c => c.Choice == CorrectionWorkbenchChoice.WrongItem
            ? c with { Status = WorkbenchChoiceStatus.Retired } : c).ToList();

        Assert.Contains(Validate(choices), p => p.Rule == "standard_choice_cannot_retire");
    }

    [Fact]
    public void Fallback_is_always_present()
    {
        var choices = StandardWorkbenchChoices.All.Where(c => !c.Fallback).ToList();

        Assert.Contains(Validate(choices), p => p.Rule == "workbench_fallback_required");
    }

    [Fact]
    public void Suggestion_rules_are_ordered_distinct_and_limited_to_three()
    {
        var suggestions = WorkbenchSuggestionRules.Suggest(new(true, true, true, true));

        Assert.Equal(3, suggestions.Count);
        Assert.Equal(CorrectionWorkbenchChoice.WrongItem, suggestions[0].Choice);
        Assert.Equal(CorrectionWorkbenchChoice.WrongComponents, suggestions[1].Choice);
        Assert.Equal(CorrectionWorkbenchChoice.DuplicateUnit, suggestions[2].Choice);
        Assert.Equal(3, WorkbenchSuggestionRules.Suggest(new(false, false, false, false)).Count);
    }

    [Fact]
    public void Preview_hash_is_stable_and_covers_the_visible_preview()
    {
        var preview = Preview("before");

        Assert.Equal(WorkbenchPreviewHasher.Hash(preview), WorkbenchPreviewHasher.Hash(preview with { Units = preview.Units.ToList() }));
        Assert.NotEqual(WorkbenchPreviewHasher.Hash(preview), WorkbenchPreviewHasher.Hash(Preview("after")));
    }

    private static CorrectionPreview Preview(string detail) => new(
        SelectionId: null,
        Command: CorrectionCommand.VoidUnit,
        UnitCount: 1,
        Change: 1,
        Skip: 0,
        Block: 0,
        Units: [new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "2609-000001", UnitOutcome.Outcomes.Change, null, detail)],
        UnitEvents: [new(Guid.Parse("11111111-1111-1111-1111-111111111111"), UnitEventType.Voided, new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc))],
        EntitlementChanges: [],
        CertificateVersions: [],
        PrintJobs: []);

    private static WorkbenchChoice Added(string id, string command) => new(id, id, "Added", "Added choice",
        [command], [], IdentifierCorrectionReason.Typo, CorrectionRootCause.TypoOrCrossedPair, [id], 90,
        [YojanaRole.CustomerService], [YojanaRole.SerialAdministrator], Standard: false);

    private static IReadOnlyList<CommandRefusal> Validate(IReadOnlyList<WorkbenchChoice> choices) =>
        WorkbenchChoiceRules.Validate(new(choices, 10m), StandardWorkbenchChoices.Default, CorrectionApprovalRules.DefaultPolicy);
}
