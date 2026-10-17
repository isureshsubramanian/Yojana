using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wolverine;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Selection;

namespace Yojana.Serialisation.Workbench;

/// <summary>
/// Narrow command seam for the workbench. It translates an opaque answer into the existing preview or command message;
/// it owns no correction rule and always overwrites identity, route metadata and approvers with trusted values.
/// TIGHT COUPLING: every adapter command id is mirrored by WorkbenchChoiceRules.AvailableCommands because settings must
/// reject dead-end routes before saving. There is no safe generic adapter; changing this switch requires changing that list.
/// </summary>
public sealed class WorkbenchCommandDispatcher(IMessageBus bus, ITenantContext tenant, BulkExecutor bulk)
{
    private static readonly IReadOnlyList<string> AllRoles = YojanaRole.All;

    public Task<CorrectionPreview> PreviewForDisplayAsync(WorkbenchPreviewRequest request, CancellationToken cancellationToken)
    {
        var actor = new WorkbenchActor("workbench-preview", AllRoles);
        var approvers = Enumerable.Range(1, 4).Select(i => new WorkbenchApproval($"workbench-preview-{i}", AllRoles, DateTime.UnixEpoch)).ToList();
        return PreviewAsync(request, actor, approvers, CorrectionInitiatingRole.System, cancellationToken);
    }

    public async Task<CorrectionPreview> PreviewAsync(WorkbenchPreviewRequest request, WorkbenchActor committer,
        IReadOnlyList<WorkbenchApproval> approvals, string initiatingRole, CancellationToken cancellationToken)
    {
        var evidence = (request.Evidence ?? []).Select(e => e.Ref).ToList();
        var metadata = Metadata(request, committer, approvals, evidence, initiatingRole, includeCommitterApproval: false);
        var options = new DeliveryOptions { TenantId = tenant.TenantId.ToString() };
        switch (request.Answers.Command)
        {
            case CorrectionCommand.CorrectIdentifier:
            {
                var command = Read<CorrectIdentifier>(request) with
                {
                    CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef, Note = request.Note,
                    Correction = metadata, ApproverRef = null,
                };
                ValidateUnitTarget(request.Target, command.UnitId);
                return await bus.InvokeAsync<CorrectionPreview>(new PreviewCorrectIdentifier(command), options, cancellationToken);
            }
            case CorrectionCommand.SwapIdentifiers:
            {
                var command = Read<SwapIdentifiers>(request) with
                {
                    CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef, Note = request.Note,
                    Correction = metadata, CommittedBy = Committer(committer),
                };
                return await bus.InvokeAsync<CorrectionPreview>(new PreviewSwapIdentifiers(command), options, cancellationToken);
            }
            case CorrectionCommand.VoidUnit:
            {
                var command = Read<VoidUnit>(request) with
                {
                    CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef, Note = request.Note,
                    Correction = metadata, CommittedBy = Committer(committer),
                };
                ValidateUnitTarget(request.Target, command.UnitId);
                return await bus.InvokeAsync<CorrectionPreview>(new PreviewVoidUnit(command), options, cancellationToken);
            }
            case CorrectionCommand.CorrectItem:
            {
                var command = Read<CorrectItem>(request) with
                {
                    CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef,
                    Correction = metadata, CommittedBy = Committer(committer),
                };
                ValidateUnitTarget(request.Target, command.UnitId);
                return await bus.InvokeAsync<CorrectionPreview>(new PreviewCorrectItem(command), options, cancellationToken);
            }
            case CorrectionCommand.CorrectAsBuilt:
            {
                var command = Read<CorrectAsBuilt>(request) with
                {
                    CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef, Note = request.Note,
                    Correction = metadata, CommittedBy = Committer(committer),
                };
                return await bus.InvokeAsync<CorrectionPreview>(new PreviewCorrectAsBuilt(command), options, cancellationToken);
            }
            case CorrectionCommand.LateSerialization:
            {
                var command = Read<LateSerialization>(request) with
                {
                    CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef, Note = request.Note,
                    Correction = metadata, CommittedBy = Committer(committer),
                };
                return await bus.InvokeAsync<CorrectionPreview>(new PreviewLateSerialization(command), options, cancellationToken);
            }
            case CorrectionCommand.MergeUnits:
            {
                var command = Read<MergeUnits>(request);
                var preview = await bus.InvokeAsync<MergeUnitsPreviewResult>(new PreviewMergeUnits(command.SurvivorUnitId, command.AbsorbedUnitId,
                    command.EventTime, command.SurvivorUnitId, Committer(committer),
                    approvals.Where(a => a.UserRef != committer.UserRef).Select(a => new CorrectionApprover(a.UserRef, a.Roles)).ToList()), options, cancellationToken);
                return preview.Preview;
            }
            case CorrectionCommand.AssignIdentifiers:
            {
                var command = Read<AssignIdentifiers>(request) with
                {
                    CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef, Note = request.Note,
                    Correction = metadata, CommittedBy = new CorrectionApprover(committer.UserRef, committer.Roles),
                };
                return (await bus.InvokeAsync<AssignIdentifiersPreview>(new PreviewAssignIdentifiers(command), options, cancellationToken)).Preview;
            }
            case CorrectionCommand.ReassignAllocation:
            {
                var command = Read<ReassignAllocation>(request) with
                {
                    CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef, Note = request.Note,
                    Correction = metadata, CommittedBy = Committer(committer), PreviewOnly = true,
                };
                return (await bus.InvokeAsync<ReassignAllocationResult>(command, options, cancellationToken)).Preview;
            }
            case CorrectionCommand.CorrectDocumentSerials:
            {
                var command = Read<CorrectDocumentSerials>(request);
                var preview = new PreviewCorrectDocumentSerials(command.Document, command.RecordedSelectionId, command.PhysicalSelectionId,
                    request.Reason, command.EventTime, committer.UserRef, command.LegalEntityId, metadata, Committer(committer));
                return await bus.InvokeAsync<CorrectionPreview>(preview, options, cancellationToken);
            }
            case CorrectionCommand.CorrectSourceDocument:
            {
                var command = Read<CorrectSourceDocument>(request);
                var preview = new PreviewCorrectSourceDocument(command.SelectionId, command.PreviousDocument, command.CorrectedDocument,
                    request.Reason, command.EventTime, committer.UserRef, command.LegalEntityId, request.Note, metadata, Committer(committer));
                return await bus.InvokeAsync<CorrectionPreview>(preview, options, cancellationToken);
            }
            case "entitlement.amend":
            case "entitlement.supersede":
            case "entitlement.attach_extended":
            case "entitlement.close":
            case "entitlement.backfill":
                return await PreviewBulkAsync(request, committer, approvals, initiatingRole, cancellationToken);
            default:
                throw new CommandRefusedException("workbench_command_unavailable", $"Command '{request.Answers.Command}' has no workbench preview adapter.", "use the command's own API until its workbench adapter is available");
        }
    }

    public async Task<JsonElement> CommitAsync(WorkbenchPreviewRequest request, WorkbenchActor committer,
        IReadOnlyList<WorkbenchApproval> approvals, string initiatingRole, CancellationToken cancellationToken)
    {
        var evidence = (request.Evidence ?? []).Select(e => e.Ref).ToList();
        var metadata = Metadata(request, committer, approvals, evidence, initiatingRole, includeCommitterApproval: true);
        var options = new DeliveryOptions { TenantId = tenant.TenantId.ToString() };
        object result = request.Answers.Command switch
        {
            CorrectionCommand.CorrectIdentifier => await bus.InvokeAsync<CorrectIdentifierResult>(Read<CorrectIdentifier>(request) with
            {
                CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef, Note = request.Note,
                Correction = metadata, ApproverRef = null,
            }, options, cancellationToken),
            CorrectionCommand.SwapIdentifiers => await bus.InvokeAsync<SwapIdentifiersResult>(Read<SwapIdentifiers>(request) with
            {
                CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef, Note = request.Note,
                Correction = metadata, CommittedBy = Committer(committer),
            }, options, cancellationToken),
            CorrectionCommand.VoidUnit => await bus.InvokeAsync<VoidUnitResult>(Read<VoidUnit>(request) with
            {
                CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef, Note = request.Note,
                Correction = metadata, CommittedBy = Committer(committer),
            }, options, cancellationToken),
            CorrectionCommand.CorrectItem => await bus.InvokeAsync<CorrectItemResult>(Read<CorrectItem>(request) with
            {
                CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef,
                Correction = metadata, CommittedBy = Committer(committer),
            }, options, cancellationToken),
            CorrectionCommand.CorrectAsBuilt => await bus.InvokeAsync<CorrectAsBuiltResult>(Read<CorrectAsBuilt>(request) with
            {
                CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef, Note = request.Note,
                Correction = metadata, CommittedBy = Committer(committer),
            }, options, cancellationToken),
            CorrectionCommand.LateSerialization => await bus.InvokeAsync<LateSerializationResult>(Read<LateSerialization>(request) with
            {
                CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef, Note = request.Note,
                Correction = metadata, CommittedBy = Committer(committer),
            }, options, cancellationToken),
            CorrectionCommand.MergeUnits => await CommitMergeAsync(request, committer, metadata, options, cancellationToken),
            CorrectionCommand.AssignIdentifiers => await bus.InvokeAsync<AssignIdentifiersResult>(Read<AssignIdentifiers>(request) with
            {
                CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef, Note = request.Note,
                Correction = metadata, CommittedBy = new CorrectionApprover(committer.UserRef, committer.Roles),
            }, options, cancellationToken),
            CorrectionCommand.ReassignAllocation => await bus.InvokeAsync<ReassignAllocationResult>(Read<ReassignAllocation>(request) with
            {
                CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef, Note = request.Note,
                Correction = metadata, CommittedBy = Committer(committer), PreviewOnly = false,
            }, options, cancellationToken),
            CorrectionCommand.CorrectDocumentSerials => await CommitDocumentAsync(request, committer, metadata, options, cancellationToken),
            CorrectionCommand.CorrectSourceDocument => await bus.InvokeAsync<CorrectSourceDocumentResult>(Read<CorrectSourceDocument>(request) with
            {
                CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef, Note = request.Note,
                Correction = metadata, CommittedBy = Committer(committer),
            }, options, cancellationToken),
            "entitlement.amend" or "entitlement.supersede" or "entitlement.attach_extended" or "entitlement.close" or "entitlement.backfill"
                => await CommitBulkAsync(request, committer, approvals, initiatingRole, cancellationToken),
            _ => throw new CommandRefusedException("workbench_command_unavailable", $"Command '{request.Answers.Command}' has no workbench commit adapter."),
        };
        return JsonSerializer.SerializeToElement(result, LedgerJson.Options);
    }

    private async Task<CorrectionPreview> PreviewBulkAsync(WorkbenchPreviewRequest request, WorkbenchActor committer,
        IReadOnlyList<WorkbenchApproval> approvals, string initiatingRole, CancellationToken cancellationToken)
    {
        var selectionId = request.Target.SelectionId ?? throw new CommandRefusedException("selection_required", "An entitlement workbench route needs a selection set target.");
        var arguments = StampBulk(Read<BulkOperationArguments>(request), request, committer, initiatingRole);
        var (_, operation, outcomes) = await bulk.EvaluateAsync(selectionId, request.Answers.Command, arguments,
            BulkApproval(request, committer, approvals, includeCommitterApproval: false), cancellationToken);
        var refusal = outcomes.FirstOrDefault(o => o.Outcome == UnitOutcome.Outcomes.Block) is { } blocked
            ? new CommandRefusal(blocked.Reason ?? "preview_blocked", blocked.Detail ?? "The preview is blocked.", blocked.UnblockingStep)
            : null;
        return new CorrectionPreview(selectionId, operation, outcomes.Count, outcomes.Count(o => o.Outcome == UnitOutcome.Outcomes.Change),
            outcomes.Count(o => o.Outcome == UnitOutcome.Outcomes.Skip), outcomes.Count(o => o.Outcome == UnitOutcome.Outcomes.Block),
            outcomes, [], [], [], [], refusal);
    }

    private async Task<object> CommitBulkAsync(WorkbenchPreviewRequest request, WorkbenchActor committer,
        IReadOnlyList<WorkbenchApproval> approvals, string initiatingRole, CancellationToken cancellationToken)
    {
        var selectionId = request.Target.SelectionId ?? throw new CommandRefusedException("selection_required", "An entitlement workbench route needs a selection set target.");
        return await bulk.ExecuteAsync(new ExecuteBulkOperation(request.CommandId, selectionId, request.Answers.Command,
            StampBulk(Read<BulkOperationArguments>(request), request, committer, initiatingRole),
            BulkApproval(request, committer, approvals, includeCommitterApproval: true)), cancellationToken);
    }

    private async Task<object> CommitMergeAsync(WorkbenchPreviewRequest request, WorkbenchActor committer, CorrectionMetadata metadata,
        DeliveryOptions options, CancellationToken cancellationToken)
    {
        var command = Read<MergeUnits>(request);
        return await bus.InvokeAsync<MergeUnitsResult>(command with
        {
            CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef, Note = request.Note,
            Correction = metadata, CommittedBy = Committer(committer),
        }, options, cancellationToken);
    }

    private async Task<object> CommitDocumentAsync(WorkbenchPreviewRequest request, WorkbenchActor committer, CorrectionMetadata metadata,
        DeliveryOptions options, CancellationToken cancellationToken)
    {
        var command = Read<CorrectDocumentSerials>(request);
        return await bus.InvokeAsync<CorrectDocumentSerialsResult>(command with
        {
            CommandId = request.CommandId, Reason = request.Reason, ActorRef = committer.UserRef,
            Correction = metadata, CommittedBy = Committer(committer),
        }, options, cancellationToken);
    }

    private static BulkOperationArguments StampBulk(BulkOperationArguments arguments, WorkbenchPreviewRequest request, WorkbenchActor committer, string initiatingRole)
    {
        var extra = (arguments.Extra ?? new Dictionary<string, string>()).Where(kv => !kv.Key.StartsWith("__", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (request.Evidence is { Count: > 0 }) extra["evidence_refs"] = string.Join(',', request.Evidence.Select(e => e.Ref));
        return arguments with
        {
            ActorRef = committer.UserRef, Reason = request.Reason, Note = request.Note, Extra = extra,
            InitiatedVia = CorrectionInitiatedVia.Workbench, InitiatingRole = initiatingRole,
        };
    }

    private static CorrectionMetadata Metadata(WorkbenchPreviewRequest request, WorkbenchActor committer,
        IReadOnlyList<WorkbenchApproval> approvals, IReadOnlyList<string> evidence, string initiatingRole, bool includeCommitterApproval) =>
        new(request.CommandId, request.Reason, request.Note, evidence, CorrectionInitiatedVia.Workbench,
            Corrects(request), initiatingRole,
            approvals.Where(a => includeCommitterApproval || a.UserRef != committer.UserRef)
                .Select(a => new CorrectionApprover(a.UserRef, a.Roles)).ToList());

    private static Guid? Corrects(WorkbenchPreviewRequest request)
    {
        if (!request.Answers.Arguments.TryGetProperty("correction", out var correction) || correction.ValueKind != JsonValueKind.Object) return null;
        return correction.TryGetProperty("corrects_correction_id", out var id) && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var parsed) ? parsed : null;
    }

    private static T Read<T>(WorkbenchPreviewRequest request) where T : class =>
        request.Answers.Arguments.Deserialize<T>(LedgerJson.Options)
        ?? throw new CommandRefusedException("workbench_answers_invalid", $"Answers for '{request.Answers.Command}' are empty.");

    private static CorrectionCommitter Committer(WorkbenchActor actor) => new(actor.UserRef, actor.Roles);

    private static BulkApproval BulkApproval(WorkbenchPreviewRequest request, WorkbenchActor committer,
        IReadOnlyList<WorkbenchApproval> approvals, bool includeCommitterApproval) =>
        new(committer.UserRef, true, committer.Roles,
            approvals.Where(a => includeCommitterApproval || a.UserRef != committer.UserRef)
                .Select(a => new CorrectionApprover(a.UserRef, a.Roles)).ToList(),
            (request.Evidence ?? []).Select(e => e.Ref).ToList());

    private static void ValidateUnitTarget(WorkbenchTarget target, Guid unitId)
    {
        if (target.UnitId is { } targetId && targetId != unitId)
            throw new CommandRefusedException("workbench_target_mismatch", $"The command targets unit {unitId}, not workbench unit {targetId}.");
    }
}

public static class WorkbenchPreviewHasher
{
    public static string Hash(CorrectionPreview preview)
    {
        var node = JsonSerializer.SerializeToNode(preview, LedgerJson.Options)!;
        var canonical = Canonical(node).ToJsonString(LedgerJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static JsonNode Canonical(JsonNode node) => node switch
    {
        JsonObject value => new JsonObject(value.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key, p.Value is null ? null : Canonical(p.Value)))),
        JsonArray value => new JsonArray(value.Select(v => v is null ? null : Canonical(v)).ToArray()),
        _ => node.DeepClone(),
    };
}
