using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Workbench;

namespace Yojana.Serialisation.Tests;

public sealed class WorkbenchTests
{
    [Fact]
    public void Built_in_catalog_maps_every_phase_three_choice_to_existing_commands()
    {
        var catalog = new BuiltInWorkbenchChoiceCatalog();
        var choices = catalog.GetChoices();

        Assert.Equal(8, choices.Count);
        Assert.DoesNotContain(choices, c => c.Choice == CorrectionWorkbenchChoice.ReplacedOrExchanged);
        Assert.DoesNotContain(choices, c => c.Choice == CorrectionWorkbenchChoice.RefurbishedAndReserialised);
        Assert.All(choices, choice =>
        {
            Assert.NotEmpty(choice.Commands);
            Assert.All(choice.Commands, command =>
            {
                var route = catalog.Route(choice.Choice, command);
                Assert.False(string.IsNullOrWhiteSpace(route.DefaultReason));
                Assert.InRange(route.RootCause, CorrectionRootCause.DocumentSerials, CorrectionRootCause.Warranty);
            });
        });
        Assert.Equal([CorrectionCommand.CorrectIdentifier, CorrectionCommand.SwapIdentifiers],
            catalog.Get(CorrectionWorkbenchChoice.MistypedOrCrossed).Commands);
        Assert.Equal([CorrectionCommand.ReassignAllocation, CorrectionCommand.VoidUnit],
            catalog.Get(CorrectionWorkbenchChoice.WrongAllocation).Commands);
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
}
