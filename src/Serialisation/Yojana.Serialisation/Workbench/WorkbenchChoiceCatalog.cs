using System.Text.Json;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Workbench;

/// <summary>Tenant-backed choice lookup; command dispatch remains behind <see cref="WorkbenchCommandDispatcher"/>.</summary>
public interface IWorkbenchChoiceCatalog
{
    Task<IReadOnlyList<WorkbenchChoice>> GetChoicesAsync(CancellationToken cancellationToken);
    Task<WorkbenchChoice> GetAsync(string choice, CancellationToken cancellationToken);
    Task<WorkbenchChoiceOption> RouteAsync(string choice, string command, CancellationToken cancellationToken);
}

/// <summary>The ADR table is the seed and missing-setting default, not runtime global state.</summary>
public static class StandardWorkbenchChoices
{
    private static readonly IReadOnlyList<string> Initiators = CorrectionApprovalRules.DefaultPolicy.WorkbenchInitiatorRoles;

    public static readonly IReadOnlyList<WorkbenchChoice> All =
    [
        Choice(CorrectionWorkbenchChoice.WrongDocumentSerials, "Wrong serials recorded on a shipment, transfer or receipt", "Documents and orders",
            "Correct the serial side of the posted document without changing its quantities.", CorrectionRootCause.DocumentSerials,
            [YojanaRole.ShippingSupervisor], 10, ["shipment", "transfer", "receipt", "invoice", "wrong serial"],
            Option("correct", "Correct the document's serials", CorrectionCommand.CorrectDocumentSerials, CorrectDocumentSerialsReason.ShippedWrongUnits)),
        Choice(CorrectionWorkbenchChoice.MistypedOrCrossed, "The serial is mistyped, or two units are crossed", "Serial and label",
            "Choose whether one value is wrong or two units carry one another's value.", CorrectionRootCause.TypoOrCrossedPair,
            [YojanaRole.SerialAdministrator], 20, ["mistyped", "crossed", "label", "serial"],
            Option("mistyped", "One serial is mistyped", CorrectionCommand.CorrectIdentifier, IdentifierCorrectionReason.Typo),
            Option("crossed", "Two units carry each other's serial", CorrectionCommand.SwapIdentifiers, SwapIdentifiersReason.CrossRecordedAtBuild)),
        Choice(CorrectionWorkbenchChoice.WrongItem, "The serial is on the wrong item", "Item, build and allocation",
            "Correct the unit's effective-dated item assignment.", CorrectionRootCause.WrongItemOrOrder,
            [YojanaRole.InventoryController, YojanaRole.ServiceSupervisor], 30, ["item", "sku", "model", "document mismatch"],
            Option("correct", "Correct the item", CorrectionCommand.CorrectItem, CorrectItemReason.WrongItemAtBuild)),
        Choice(CorrectionWorkbenchChoice.WrongOrder, "The unit was released against the wrong order", "Documents and orders",
            "Correct the opening source document and re-derive dependent cover.", CorrectionRootCause.WrongItemOrOrder,
            [YojanaRole.OrderDeskSupervisor], 40, ["order", "delivery", "released"],
            Option("correct", "Correct the source document", CorrectionCommand.CorrectSourceDocument, CorrectSourceDocumentReason.ReleasedUnderWrongOrder)),
        Choice(CorrectionWorkbenchChoice.WrongAllocation, "Serials were allocated to the wrong or a cancelled work order", "Item, build and allocation",
            "Choose whether the reservation moves before commissioning or an unfinished unit is voided.", CorrectionRootCause.AllocationBeforeBirth,
            [YojanaRole.ProductionSupervisor], 50, ["allocation", "work order", "cancelled", "reservation"],
            Option("move", "Move or exchange uncommissioned allocations", CorrectionCommand.ReassignAllocation, ReassignAllocationReason.AllocationError),
            Option("void", "The allocated unit was never completed", CorrectionCommand.VoidUnit, VoidUnitReason.WorkOrderQuantityReduced)),
        Choice(CorrectionWorkbenchChoice.DuplicateUnit, "Two records are the same physical unit", "Item, build and allocation",
            "Merge the duplicate record into a surviving unit while retaining the alias.", CorrectionRootCause.TypoOrCrossedPair,
            [YojanaRole.SerialAdministrator], 60, ["duplicate", "same unit", "two records"],
            Option("merge", "Merge the duplicate records", CorrectionCommand.MergeUnits, MergeUnitsReason.DuplicateRecord)),
        Choice(CorrectionWorkbenchChoice.WrongComponents, "Component serials are missing or wrong", "Item, build and allocation",
            "Choose whether to correct the tree, introduce late serialisation, or apply a vendor list.", CorrectionRootCause.WrongItemOrOrder,
            [YojanaRole.ProductionSupervisor, YojanaRole.Quality, YojanaRole.ItemMasterOwner], 70, ["component", "as built", "vendor list", "missing serial"],
            Option("as_built", "Correct captured components", CorrectionCommand.CorrectAsBuilt, CorrectAsBuiltReason.ComponentNotCaptured),
            Option("late_serialisation", "The item was not serialised when it should have been", CorrectionCommand.LateSerialization, LateSerializationReason.CodingErrorAtItemSetup),
            Option("vendor_list", "Apply a vendor serial list", CorrectionCommand.AssignIdentifiers, AssignIdentifiersReason.VendorList)),
        Choice(CorrectionWorkbenchChoice.ReplacedOrExchanged, "This unit was replaced or exchanged", "Returns and refurbishment",
            "Start a missing replacement shipment or correct the unit recorded on an existing replacement.", CorrectionRootCause.ReplacementOrExchange,
            [YojanaRole.ServiceManager], 75, ["replacement", "exchange", "wrong unit", "shipment"],
            Option("ship", "The replacement has not been shipped in the system", WorkbenchRouteCommand.ShipWarrantyReplacement, ReplacementReason.WarrantyDefect),
            Option("correct_link", "The replacement is recorded against the wrong unit", CorrectionCommand.CorrectReplacementLink, CorrectReplacementLinkReason.WrongUnitRecorded)),
        Choice(CorrectionWorkbenchChoice.RefurbishedAndReserialised, "This unit was refurbished and re-serialised", "Returns and refurbishment",
            "Release completed refurbishment under the approved policy or correct a relabelled identifier.", CorrectionRootCause.RefurbishmentReserialisation,
            [YojanaRole.ProductionSupervisor, YojanaRole.SerialAdministrator], 76, ["refurbished", "re-serialised", "release", "relabel"],
            Option("release", "Release completed refurbishment", WorkbenchRouteCommand.ReleaseRefurbishment, IdentifierCorrectionReason.Relabel),
            Option("relabel", "Only the identifier was recorded incorrectly", CorrectionCommand.CorrectIdentifier, IdentifierCorrectionReason.Relabel)),
        Choice(CorrectionWorkbenchChoice.WrongWarranty, "The warranty is wrong", "Warranty",
            "Choose the existing entitlement operation that describes the correction.", CorrectionRootCause.Warranty,
            [YojanaRole.ServiceDesk, YojanaRole.ServiceManager], 80, ["warranty", "coverage", "term", "expiry"],
            Option("amend", "Amend dates, term or coverage", "entitlement.amend", AmendmentReason.Correction),
            Option("supersede", "Change the coverage level", "entitlement.supersede", AmendmentReason.Correction),
            Option("attach", "Attach an extended plan or add-on", "entitlement.attach_extended", AmendmentReason.Purchase),
            Option("close", "Close cover included in error", "entitlement.close", EntitlementClosedReason.IncludedInError),
            Option("backfill", "Create missing historical cover", "entitlement.backfill", BackfillReason.HistoricalGap)),
        new(WorkbenchChoiceId.Fallback, "None of these, or not sure", "Other",
            "Describe what is wrong. A serial administrator will re-file the request under a real choice or escalate it.",
            [], [], "unclassified", 0, ["not sure", "none", "other"], int.MaxValue, Initiators,
            [YojanaRole.SerialAdministrator], WorkbenchChoiceStatus.Shown, Standard: true, Fallback: true),
    ];

    public static WorkbenchChoiceConfiguration Default => new(0, All, 10m, "system", DateTime.UnixEpoch);

    private static WorkbenchChoiceOption Option(string value, string label, string command, string reason) =>
        new(value, label, command, reason, Root(command));

    private static WorkbenchChoice Choice(string choice, string label, string area, string help, int rootCause,
        IReadOnlyList<string> commitRoles, int order, IReadOnlyList<string> searchWords, params WorkbenchChoiceOption[] options)
    {
        var question = options.Length == 1
            ? Array.Empty<WorkbenchNarrowingQuestion>()
            : [new WorkbenchNarrowingQuestion("route", "Which situation applies?", options)];
        return new(choice, label, area, help, options.Select(o => o.Command).ToList(), question, options[0].DefaultReason,
            rootCause, searchWords, order, Initiators, commitRoles);
    }

    private static int Root(string command) => command switch
    {
        CorrectionCommand.CorrectDocumentSerials => CorrectionRootCause.DocumentSerials,
        CorrectionCommand.CorrectIdentifier or CorrectionCommand.SwapIdentifiers or CorrectionCommand.MergeUnits => CorrectionRootCause.TypoOrCrossedPair,
        CorrectionCommand.CorrectItem or CorrectionCommand.CorrectSourceDocument or CorrectionCommand.CorrectAsBuilt => CorrectionRootCause.WrongItemOrOrder,
        CorrectionCommand.ReassignAllocation or CorrectionCommand.VoidUnit or CorrectionCommand.LateSerialization or CorrectionCommand.AssignIdentifiers => CorrectionRootCause.AllocationBeforeBirth,
        WorkbenchRouteCommand.ShipWarrantyReplacement or CorrectionCommand.CorrectReplacementLink => CorrectionRootCause.ReplacementOrExchange,
        WorkbenchRouteCommand.ReleaseRefurbishment => CorrectionRootCause.RefurbishmentReserialisation,
        _ when command.StartsWith("entitlement.", StringComparison.Ordinal) => CorrectionRootCause.Warranty,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "The workbench route has no KPI root cause."),
    };
}

public sealed class WorkbenchChoiceSettings(
    ITenantSettings settings,
    CorrectionApprovalSettings approvals,
    IClock clock) : IWorkbenchChoiceCatalog
{
    public const string Key = "corrections.workbench_choices";

    // Coupled to the factory registration in SerialisationModule: ITenantSettings owns PlatformDbContext. Keeping it
    // behind this boundary prevents Wolverine from seeing a second DbContext through workbench command handlers.
    public async Task<WorkbenchChoiceConfiguration> GetConfigurationAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } text
            ? JsonSerializer.Deserialize<WorkbenchChoiceConfiguration>(text, LedgerJson.Options) ?? StandardWorkbenchChoices.Default
            : StandardWorkbenchChoices.Default;

    public async Task<WorkbenchChoiceSettingsView> GetViewAsync(CancellationToken cancellationToken)
    {
        var current = await GetConfigurationAsync(cancellationToken);
        var history = new List<WorkbenchChoiceConfigurationVersion>();
        foreach (var version in await settings.HistoryAsync(Key, cancellationToken))
        {
            if (JsonSerializer.Deserialize<WorkbenchChoiceConfiguration>(version.Value, LedgerJson.Options) is { } configuration)
                history.Add(new(configuration, version.ValidFrom, version.ValidTo));
        }
        return new(current, history);
    }

    public async Task<WorkbenchChoiceConfiguration> SetAsync(UpdateWorkbenchChoices update, string actorRef, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorRef)) throw new CommandRefusedException("audit_actor_required", "Workbench choice settings need an audit actor.");
        var current = await GetConfigurationAsync(cancellationToken);
        var policy = await approvals.GetAsync(cancellationToken);
        if (WorkbenchChoiceRules.Validate(update, current, policy) is { Count: > 0 } problems)
            throw new CommandRefusedException(problems[0].Rule, problems[0].Message, problems[0].UnblockingStep);
        var saved = new WorkbenchChoiceConfiguration(current.Version + 1, WorkbenchChoiceRules.Normalise(update.Choices),
            update.UnclassifiedAlertPercent, actorRef, clock.UtcNow);
        await settings.SetAsync(Key, JsonSerializer.Serialize(saved, LedgerJson.Options), cancellationToken);
        return saved;
    }

    public async Task<IReadOnlyList<WorkbenchChoice>> GetChoicesAsync(CancellationToken cancellationToken) =>
        (await GetConfigurationAsync(cancellationToken)).Choices
            .Where(c => c.Status == WorkbenchChoiceStatus.Shown)
            .OrderBy(c => c.Fallback ? int.MaxValue : c.Order).ThenBy(c => c.Label, StringComparer.Ordinal).ToList();

    public async Task<WorkbenchChoice> GetAsync(string choice, CancellationToken cancellationToken) =>
        (await GetConfigurationAsync(cancellationToken)).Choices.FirstOrDefault(c => c.Choice == choice)
        ?? throw new CommandRefusedException("workbench_choice_unknown", $"Workbench choice '{choice}' is not available.");

    public async Task<WorkbenchChoiceOption> RouteAsync(string choice, string command, CancellationToken cancellationToken)
    {
        var selected = await GetAsync(choice, cancellationToken);
        if (selected.Fallback)
            throw new CommandRefusedException("workbench_fallback_has_no_command", "The fallback files an unclassified request and does not run a command.");
        return WorkbenchChoiceRules.Options(selected).FirstOrDefault(o => o.Command == command)
            ?? throw new CommandRefusedException("workbench_route_invalid", $"Command '{command}' is not a route from workbench choice '{choice}'.", "choose one of the choice's narrowing answers");
    }
}
