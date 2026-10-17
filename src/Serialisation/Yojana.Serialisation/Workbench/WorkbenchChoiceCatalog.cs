using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Workbench;

/// <summary>P3-T16b replaces this source without changing the workbench routing or request workflow.</summary>
public interface IWorkbenchChoiceCatalog
{
    IReadOnlyList<WorkbenchChoice> GetChoices();
    WorkbenchChoice Get(string choice);
    WorkbenchChoiceOption Route(string choice, string command);
}

/// <summary>The ADR table as code until P3-T16b moves the same narrow contract to tenant data.</summary>
public sealed class BuiltInWorkbenchChoiceCatalog : IWorkbenchChoiceCatalog
{
    // Declared before Choices: the Choices initialiser fills it, and static initialisers run in source order.
    private static readonly Dictionary<(string Choice, string Command), WorkbenchChoiceOption> Routes = [];

    private static readonly IReadOnlyList<WorkbenchChoice> Choices =
    [
        Choice(CorrectionWorkbenchChoice.WrongDocumentSerials, "Wrong serials recorded on a shipment, transfer or receipt", "Documents and orders",
            "Correct the serial side of the posted document without changing its quantities.", CorrectionRootCause.DocumentSerials,
            Option("correct", "Correct the document's serials", CorrectionCommand.CorrectDocumentSerials, CorrectDocumentSerialsReason.ShippedWrongUnits)),
        Choice(CorrectionWorkbenchChoice.MistypedOrCrossed, "The serial is mistyped, or two units are crossed", "Serial and label",
            "Choose whether one value is wrong or two units carry one another's value.", CorrectionRootCause.TypoOrCrossedPair,
            Option("mistyped", "One serial is mistyped", CorrectionCommand.CorrectIdentifier, IdentifierCorrectionReason.Typo),
            Option("crossed", "Two units carry each other's serial", CorrectionCommand.SwapIdentifiers, SwapIdentifiersReason.CrossRecordedAtBuild)),
        Choice(CorrectionWorkbenchChoice.WrongItem, "The serial is on the wrong item", "Item, build and allocation",
            "Correct the unit's effective-dated item assignment.", CorrectionRootCause.WrongItemOrOrder,
            Option("correct", "Correct the item", CorrectionCommand.CorrectItem, CorrectItemReason.WrongItemAtBuild)),
        Choice(CorrectionWorkbenchChoice.WrongOrder, "The unit was released against the wrong order", "Documents and orders",
            "Correct the opening source document and re-derive dependent cover.", CorrectionRootCause.WrongItemOrOrder,
            Option("correct", "Correct the source document", CorrectionCommand.CorrectSourceDocument, CorrectSourceDocumentReason.ReleasedUnderWrongOrder)),
        Choice(CorrectionWorkbenchChoice.WrongAllocation, "Serials were allocated to the wrong or a cancelled work order", "Item, build and allocation",
            "Choose whether the reservation moves before commissioning or an unfinished unit is voided.", CorrectionRootCause.AllocationBeforeBirth,
            Option("move", "Move or exchange uncommissioned allocations", CorrectionCommand.ReassignAllocation, ReassignAllocationReason.AllocationError),
            Option("void", "The allocated unit was never completed", CorrectionCommand.VoidUnit, VoidUnitReason.WorkOrderQuantityReduced)),
        Choice(CorrectionWorkbenchChoice.DuplicateUnit, "Two records are the same physical unit", "Item, build and allocation",
            "Merge the duplicate record into a surviving unit while retaining the alias.", CorrectionRootCause.TypoOrCrossedPair,
            Option("merge", "Merge the duplicate records", CorrectionCommand.MergeUnits, MergeUnitsReason.DuplicateRecord)),
        Choice(CorrectionWorkbenchChoice.WrongComponents, "Component serials are missing or wrong", "Item, build and allocation",
            "Choose whether to correct the tree, introduce late serialisation, or apply a vendor list.", CorrectionRootCause.WrongItemOrOrder,
            Option("as_built", "Correct captured components", CorrectionCommand.CorrectAsBuilt, CorrectAsBuiltReason.ComponentNotCaptured),
            Option("late_serialisation", "The item was not serialised when it should have been", CorrectionCommand.LateSerialization, LateSerializationReason.CodingErrorAtItemSetup),
            Option("vendor_list", "Apply a vendor serial list", CorrectionCommand.AssignIdentifiers, AssignIdentifiersReason.VendorList)),
        Choice(CorrectionWorkbenchChoice.WrongWarranty, "The warranty is wrong", "Warranty",
            "Choose the existing entitlement operation that describes the correction.", CorrectionRootCause.Warranty,
            Option("amend", "Amend dates, term or coverage", "entitlement.amend", AmendmentReason.Correction),
            Option("supersede", "Change the coverage level", "entitlement.supersede", AmendmentReason.Correction),
            Option("attach", "Attach an extended plan or add-on", "entitlement.attach_extended", AmendmentReason.Purchase),
            Option("close", "Close cover included in error", "entitlement.close", EntitlementClosedReason.IncludedInError),
            Option("backfill", "Create missing historical cover", "entitlement.backfill", BackfillReason.HistoricalGap)),
    ];

    public IReadOnlyList<WorkbenchChoice> GetChoices() => Choices;

    public WorkbenchChoice Get(string choice) => Choices.FirstOrDefault(c => c.Choice == choice)
        ?? throw new CommandRefusedException("workbench_choice_unknown", $"Workbench choice '{choice}' is not available.");

    public WorkbenchChoiceOption Route(string choice, string command)
    {
        var selected = Get(choice);
        // A single-route choice asks no narrowing question, so its one option lives only in this table.
        return selected.Questions.SelectMany(q => q.Options).FirstOrDefault(o => o.Command == command)
            ?? Routes.GetValueOrDefault((choice, command))
            ?? throw new CommandRefusedException("workbench_route_invalid", $"Command '{command}' is not a route from workbench choice '{choice}'.", "choose one of the choice's narrowing answers");
    }

    private static WorkbenchChoiceOption Option(string value, string label, string command, string reason) =>
        new(value, label, command, reason, Root(command));

    private static WorkbenchChoice Choice(string choice, string label, string area, string help, int rootCause, params WorkbenchChoiceOption[] options)
    {
        foreach (var option in options) Routes[(choice, option.Command)] = option;
        var question = options.Length == 1
            ? Array.Empty<WorkbenchNarrowingQuestion>()
            : [new WorkbenchNarrowingQuestion("route", "Which situation applies?", options)];
        return new(choice, label, area, help, options.Select(o => o.Command).ToList(), question, options[0].DefaultReason, rootCause);
    }

    private static int Root(string command) => command switch
    {
        CorrectionCommand.CorrectDocumentSerials => CorrectionRootCause.DocumentSerials,
        CorrectionCommand.CorrectIdentifier or CorrectionCommand.SwapIdentifiers or CorrectionCommand.MergeUnits => CorrectionRootCause.TypoOrCrossedPair,
        CorrectionCommand.CorrectItem or CorrectionCommand.CorrectSourceDocument or CorrectionCommand.CorrectAsBuilt => CorrectionRootCause.WrongItemOrOrder,
        CorrectionCommand.ReassignAllocation or CorrectionCommand.VoidUnit or CorrectionCommand.LateSerialization or CorrectionCommand.AssignIdentifiers => CorrectionRootCause.AllocationBeforeBirth,
        _ when command.StartsWith("entitlement.", StringComparison.Ordinal) => CorrectionRootCause.Warranty,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "The workbench route has no KPI root cause."),
    };
}
