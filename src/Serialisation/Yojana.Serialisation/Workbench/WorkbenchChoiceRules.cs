using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;

namespace Yojana.Serialisation.Workbench;

public static class WorkbenchChoiceRules
{
    // TIGHT COUPLING: these ids mirror WorkbenchCommandDispatcher's explicit inline adapters. The dispatcher deliberately
    // has no generic reflection route, so settings validation cannot discover them. Adding or removing an inline adapter
    // on either side without changing the other makes a valid command unavailable or saves a dead-end inline route.
    public static readonly IReadOnlyList<string> InlineCommands =
    [
        CorrectionCommand.CorrectIdentifier, CorrectionCommand.SwapIdentifiers, CorrectionCommand.VoidUnit,
        CorrectionCommand.CorrectItem, CorrectionCommand.CorrectAsBuilt, CorrectionCommand.LateSerialization,
        CorrectionCommand.MergeUnits, CorrectionCommand.AssignIdentifiers, CorrectionCommand.ReassignAllocation,
        CorrectionCommand.CorrectDocumentSerials, CorrectionCommand.CorrectSourceDocument, CorrectionCommand.CorrectReplacementLink,
        "entitlement.amend", "entitlement.supersede", "entitlement.attach_extended", "entitlement.close", "entitlement.backfill",
    ];

    // Dedicated routes hand control to their owning screen; no command is dispatched from the workbench boundary.
    public static readonly IReadOnlyList<string> HandoffCommands =
        [WorkbenchRouteCommand.ShipWarrantyReplacement, WorkbenchRouteCommand.ReleaseRefurbishment];

    public static readonly IReadOnlyList<string> AvailableCommands = [.. InlineCommands, .. HandoffCommands];

    public static IReadOnlyList<CommandRefusal> Validate(UpdateWorkbenchChoices update, WorkbenchChoiceConfiguration current,
        CorrectionApprovalPolicy approvalPolicy)
    {
        var problems = new List<CommandRefusal>();
        if (update.UnclassifiedAlertPercent is < 0 or > 100)
            problems.Add(new("unclassified_alert_invalid", "The unclassified-request alert must be between 0 and 100 percent."));
        if (update.Choices.GroupBy(c => c.Choice, StringComparer.Ordinal).Any(g => g.Count() > 1))
            problems.Add(new("workbench_choice_duplicate", "A workbench choice id is repeated."));
        if (update.Choices.GroupBy(c => c.Order).Any(g => g.Key != int.MaxValue && g.Count() > 1))
            problems.Add(new("workbench_choice_order_duplicate", "Two shown positions use the same order."));

        var fallback = update.Choices.Where(c => c.Choice == WorkbenchChoiceId.Fallback).ToList();
        if (fallback.Count != 1 || !fallback[0].Fallback || !fallback[0].Standard
            || fallback[0].Status != WorkbenchChoiceStatus.Shown || fallback[0].Label != "None of these, or not sure")
            problems.Add(new("workbench_fallback_required", "'None of these, or not sure' must be present and shown."));

        foreach (var standard in StandardWorkbenchChoices.All)
        {
            var candidate = update.Choices.FirstOrDefault(c => c.Choice == standard.Choice);
            if (candidate is null)
            {
                problems.Add(new("standard_choice_required", $"Standard choice '{standard.Choice}' cannot be deleted."));
                continue;
            }
            if (candidate.Status == WorkbenchChoiceStatus.Retired)
                problems.Add(new("standard_choice_cannot_retire", $"Standard choice '{standard.Choice}' cannot be retired."));
            if (!SameRoutes(candidate, standard))
                problems.Add(new("standard_choice_route_fixed", $"Standard choice '{standard.Choice}' must retain its shipped command routes."));
        }

        foreach (var existing in current.Choices.Where(c => !c.Standard))
        {
            var changed = update.Choices.FirstOrDefault(c => c.Choice == existing.Choice);
            if (changed is null)
                problems.Add(new("added_choice_cannot_delete", $"Added choice '{existing.Choice}' must be retired, not deleted."));
            else if (!SameRoutes(changed, existing))
                problems.Add(new("added_choice_route_fixed", $"Added choice '{existing.Choice}' must retain its audited command route; retire it and add a new choice instead."));
        }

        foreach (var choice in update.Choices)
        {
            if (choice.Standard && !StandardWorkbenchChoices.All.Any(c => c.Choice == choice.Choice))
                problems.Add(new("added_choice_must_not_be_standard", $"Added choice '{choice.Choice}' cannot be marked standard."));
            if (string.IsNullOrWhiteSpace(choice.Choice) || string.IsNullOrWhiteSpace(choice.Label) || string.IsNullOrWhiteSpace(choice.Area) || string.IsNullOrWhiteSpace(choice.HelpText))
                problems.Add(new("workbench_choice_text_required", $"Choice '{choice.Choice}' needs an id, label, area and help text."));
            if (choice.Choice is { Length: > 60 })
                problems.Add(new("workbench_choice_id_too_long", $"Choice '{choice.Choice}' exceeds 60 characters."));
            if (!WorkbenchChoiceStatus.All.Contains(choice.Status))
                problems.Add(new("workbench_choice_status_unknown", $"Choice '{choice.Choice}' has unknown status '{choice.Status}'."));
            foreach (var role in (choice.InitiateRoles ?? []).Concat(choice.CommitRoles ?? []).Where(r => !YojanaRole.IsKnown(r)))
                problems.Add(new("workbench_choice_role_unknown", $"Choice '{choice.Choice}' names unknown role '{role}'."));
            if (!choice.Fallback && (choice.InitiateRoles is not { Count: > 0 } || choice.CommitRoles is not { Count: > 0 }))
                problems.Add(new("workbench_choice_roles_required", $"Choice '{choice.Choice}' needs initiate and commit roles."));

            var options = Options(choice);
            if (!choice.Fallback && options.Count == 0)
                problems.Add(new("workbench_choice_route_required", $"Choice '{choice.Choice}' needs a command or narrowing question."));
            if (!choice.Fallback && choice.Questions.Count == 0 && choice.Commands.Count != 1)
                problems.Add(new("workbench_narrowing_required", $"Choice '{choice.Choice}' needs a narrowing question when it has more than one command."));
            if (choice.Questions.Any(q => string.IsNullOrWhiteSpace(q.Key) || string.IsNullOrWhiteSpace(q.Prompt) || q.Options.Count == 0))
                problems.Add(new("workbench_narrowing_invalid", $"Choice '{choice.Choice}' has an incomplete narrowing question."));
            if (choice.Questions.Count > 0 && !choice.Commands.Order(StringComparer.Ordinal)
                .SequenceEqual(options.Select(o => o.Command).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
                problems.Add(new("workbench_narrowing_commands_mismatch", $"Choice '{choice.Choice}' must list exactly the commands offered by its narrowing options."));
            if (options.GroupBy(o => o.Value, StringComparer.Ordinal).Any(g => g.Count() > 1))
                problems.Add(new("workbench_narrowing_option_duplicate", $"Choice '{choice.Choice}' repeats a narrowing option value."));
            if (!choice.Fallback && options.Any(o => o.RootCause is < CorrectionRootCause.DocumentSerials or > CorrectionRootCause.Warranty))
                problems.Add(new("workbench_root_cause_invalid", $"Choice '{choice.Choice}' has a KPI root cause outside 1 to 7."));
            foreach (var option in options)
            {
                if (string.IsNullOrWhiteSpace(option.Value) || string.IsNullOrWhiteSpace(option.Label) || string.IsNullOrWhiteSpace(option.Command))
                {
                    problems.Add(new("workbench_route_invalid", $"Choice '{choice.Choice}' has an incomplete command route."));
                    continue;
                }
                if (!AvailableCommands.Contains(option.Command))
                    problems.Add(new("workbench_command_unknown", $"Choice '{choice.Choice}' names unavailable command '{option.Command}'.", "choose a command from the product's list"));
                if (string.IsNullOrWhiteSpace(option.DefaultReason))
                    problems.Add(new("workbench_default_reason_required", $"Route '{option.Value}' on choice '{choice.Choice}' needs a default reason."));
                else if (option.DefaultReason is { Length: > 60 })
                    problems.Add(new("workbench_default_reason_too_long", $"Route '{option.Value}' has a default reason longer than 60 characters."));
                if (InlineCommands.Contains(option.Command)
                    && !CorrectionRules.Reasons(CorrectionCommandForRoute(option.Command)).Contains(option.DefaultReason))
                    problems.Add(new("workbench_default_reason_unknown", $"Route '{option.Value}' uses a reason not recognised by '{option.Command}'."));
            }
            if (!choice.Fallback && CorrectionApprovalRules.WorkbenchChoiceApprovalProblem(approvalPolicy, choice, options.Select(o => o.Command).ToList()) is { } approvalProblem)
                problems.Add(approvalProblem);
        }
        return problems;
    }

    public static IReadOnlyList<WorkbenchChoice> Normalise(IReadOnlyList<WorkbenchChoice> choices) => choices.Select(c => c with
    {
        Choice = c.Choice.Trim(), Label = c.Label.Trim(), Area = c.Area.Trim(), HelpText = c.HelpText.Trim(),
        SearchWords = (c.SearchWords ?? []).Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        InitiateRoles = (c.InitiateRoles ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
        CommitRoles = (c.CommitRoles ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
    }).OrderBy(c => c.Fallback ? int.MaxValue : c.Order).ThenBy(c => c.Label, StringComparer.Ordinal).ToList();

    public static IReadOnlyList<WorkbenchChoiceOption> Options(WorkbenchChoice choice)
    {
        var options = choice.Questions.SelectMany(q => q.Options).ToList();
        if (options.Count > 0) return options;
        return choice.Commands.Select(command => new WorkbenchChoiceOption("route", choice.Label, command, choice.DefaultReason, choice.RootCause)).ToList();
    }

    public static string CorrectionCommandForRoute(string command) => command switch
    {
        "entitlement.amend" => CorrectionCommand.AmendEntitlement,
        "entitlement.supersede" => CorrectionCommand.SupersedeEntitlement,
        "entitlement.attach_extended" => CorrectionCommand.AttachExtendedWarranty,
        "entitlement.close" => CorrectionCommand.CloseEntitlement,
        "entitlement.backfill" => CorrectionCommand.BackfillEntitlements,
        _ => command,
    };

    private static bool SameRoutes(WorkbenchChoice left, WorkbenchChoice right)
    {
        var a = Options(left).Select(o => (o.Value, o.Command, o.DefaultReason, o.RootCause)).Order().ToList();
        var b = Options(right).Select(o => (o.Value, o.Command, o.DefaultReason, o.RootCause)).Order().ToList();
        return a.SequenceEqual(b);
    }
}

/// <summary>
/// Stable suggestion table. Rules inspect unit facts only; wording, order and availability still come from tenant data.
/// Contextual matches precede common state-safe defaults, yielding three distinct standard choices in rule order.
/// </summary>
public static class WorkbenchSuggestionRules
{
    private sealed record Rule(string Name, Func<WorkbenchSuggestionFacts, bool> Applies, string Choice);

    private static readonly IReadOnlyList<Rule> Rules =
    [
        new("document_mismatch", f => f.DocumentMismatch, CorrectionWorkbenchChoice.WrongItem),
        new("awaiting_vendor_serial", f => f.AwaitingVendorSerial, CorrectionWorkbenchChoice.WrongComponents),
        new("duplicate_vendor_serial", f => f.DuplicateVendorSerial, CorrectionWorkbenchChoice.DuplicateUnit),
        new("uncaptured_assembly_children", f => f.OpenAssemblyWithUncapturedChildren, CorrectionWorkbenchChoice.WrongComponents),
        new("common_identifier_correction", _ => true, CorrectionWorkbenchChoice.MistypedOrCrossed),
        new("common_document_correction", _ => true, CorrectionWorkbenchChoice.WrongDocumentSerials),
        new("common_source_document_correction", _ => true, CorrectionWorkbenchChoice.WrongOrder),
    ];

    public static IReadOnlyList<(string Choice, string Rule)> Suggest(WorkbenchSuggestionFacts facts) => Rules
        .Where(r => r.Applies(facts)).Select(r => (r.Choice, r.Name)).DistinctBy(x => x.Choice).Take(3).ToList();
}
