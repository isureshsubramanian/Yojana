using System.Text.Json;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Workbench;

/// <summary>
/// Persists the preview/approval workflow. Commands still decide whether a participant set may commit: readiness is a
/// re-run of the existing preview with server-recorded people, never a second implementation of approval rules.
/// TIGHT COUPLING: fallback rows and their CorrectionLedger KPI projections are saved together; splitting those writes
/// would make the serial-administrator inbox and unclassified KPI disagree after a partial failure.
/// </summary>
public sealed class WorkbenchRequestService(
    SerialisationDbContext db,
    IWorkbenchChoiceCatalog catalog,
    WorkbenchCommandDispatcher dispatcher,
    CorrectionApprovalSettings approvalSettings,
    CorrectionLedger corrections,
    ITenantContext tenant,
    IClock clock)
{
    public async Task<WorkbenchPreviewResult> PreviewAsync(WorkbenchPreviewRequest request, CancellationToken cancellationToken)
    {
        await ValidateRouteAsync(request, cancellationToken);
        var preview = await dispatcher.PreviewForDisplayAsync(request, cancellationToken);
        return new(request.Choice, request.Answers.Command, preview, WorkbenchPreviewHasher.Hash(preview));
    }

    public async Task<WorkbenchRequestRow> InitiateAsync(Guid requestId, WorkbenchPreviewRequest request, WorkbenchActor initiator,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.WorkbenchRequests.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.CommandId == request.CommandId, cancellationToken) is { } replay)
            return ToRow(replay);
        var choice = await ValidateRouteAsync(request, cancellationToken);
        var policy = await approvalSettings.GetAsync(cancellationToken);
        if (!initiator.Roles.Intersect(choice.InitiateRoles ?? [], StringComparer.Ordinal).Any())
            throw new CommandRefusedException("workbench_initiator_required", "Your tenant role may not initiate a correction workbench request.");

        var preview = await dispatcher.PreviewForDisplayAsync(request, cancellationToken);
        var hash = WorkbenchPreviewHasher.Hash(preview);
        var required = RequiredRoles(policy, choice, request.Answers.Command);
        var initiatingRole = CorrectionRules.InitiatingRole(initiator.Roles);
        var ready = preview.Block == 0 && await ReadyAsync(request, choice, initiator, [], initiatingRole, cancellationToken);
        var status = preview.Block > 0 ? WorkbenchRequestStatus.Draft : ready ? WorkbenchRequestStatus.Approved : WorkbenchRequestStatus.AwaitingApproval;
        var now = clock.UtcNow;
        var row = new WorkbenchRequest
        {
            TenantId = tenantId,
            RequestId = requestId,
            CommandId = request.CommandId,
            Choice = request.Choice,
            Command = request.Answers.Command,
            TargetJson = JsonSerializer.Serialize(request.Target, LedgerJson.Options),
            AnswersJson = JsonSerializer.Serialize(request.Answers, LedgerJson.Options),
            Reason = request.Reason,
            Note = Trim(request.Note),
            EvidenceJson = JsonSerializer.Serialize(request.Evidence ?? [], LedgerJson.Options),
            PreviewJson = JsonSerializer.Serialize(preview, LedgerJson.Options),
            PreviewHash = hash,
            Status = status,
            InitiatedBy = initiator.UserRef,
            InitiatorRolesJson = JsonSerializer.Serialize(initiator.Roles.Distinct(StringComparer.Ordinal).ToList(), LedgerJson.Options),
            InitiatedAt = now,
            RequiredApproverRolesJson = JsonSerializer.Serialize(required, LedgerJson.Options),
            ApprovalsJson = "[]",
        };
        db.WorkbenchRequests.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return ToRow(row);
    }

    public async Task<WorkbenchRequestRow> ApproveAsync(Guid requestId, WorkbenchActor approver, string? note, CancellationToken cancellationToken)
    {
        var row = await LoadAsync(requestId, cancellationToken);
        if (row.Status == WorkbenchRequestStatus.Approved || row.Status == WorkbenchRequestStatus.Committed) return ToRow(row);
        if (row.Status != WorkbenchRequestStatus.AwaitingApproval)
            throw new CommandRefusedException("workbench_request_not_awaiting", $"Workbench request {requestId} is {row.Status}.");
        if (string.Equals(row.InitiatedBy, approver.UserRef, StringComparison.Ordinal))
            throw new CommandRefusedException("approver_is_initiator", "The person who initiated a correction cannot approve it.", "ask another authorised person");
        var required = Read<List<string>>(row.RequiredApproverRolesJson, row.RequestId);
        if (!approver.Roles.Intersect(required, StringComparer.Ordinal).Any())
            throw new CommandRefusedException("approval_required", "Your roles do not match this request's approver roles.", $"ask someone with {string.Join(" or ", required)}");

        var request = Request(row);
        await RefuseIfChangedAsync(row, request, cancellationToken);
        var approvals = Approvals(row);
        if (approvals.All(a => a.UserRef != approver.UserRef)) approvals.Add(new WorkbenchApproval(approver.UserRef, approver.Roles, clock.UtcNow, Trim(note)));
        row.ApprovalsJson = JsonSerializer.Serialize(approvals, LedgerJson.Options);
        var initiatingRole = CorrectionRules.InitiatingRole(Read<List<string>>(row.InitiatorRolesJson, row.RequestId));
        var participants = approvals.Select(a => new WorkbenchActor(a.UserRef, a.Roles)).ToList();
        var choice = await catalog.GetAsync(row.Choice, cancellationToken);
        row.Status = await AnyReadyAsync(request, choice, participants, approvals, initiatingRole, cancellationToken)
            ? WorkbenchRequestStatus.Approved
            : WorkbenchRequestStatus.AwaitingApproval;
        await db.SaveChangesAsync(cancellationToken);
        return ToRow(row);
    }

    public async Task<WorkbenchRequestRow> RejectAsync(Guid requestId, WorkbenchActor decider, string note, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(note)) throw new CommandRefusedException("note_required", "A rejection needs a note.");
        var row = await LoadAsync(requestId, cancellationToken);
        if (row.Status != WorkbenchRequestStatus.AwaitingApproval)
            throw new CommandRefusedException("workbench_request_not_awaiting", $"Workbench request {requestId} is {row.Status}.");
        var required = Read<List<string>>(row.RequiredApproverRolesJson, row.RequestId);
        if (!decider.Roles.Intersect(required, StringComparer.Ordinal).Any())
            throw new CommandRefusedException("approval_required", "Only someone eligible to approve this request may reject it.");
        row.Status = WorkbenchRequestStatus.Rejected;
        row.RejectedBy = decider.UserRef;
        row.RejectedAt = clock.UtcNow;
        row.DecisionNote = Trim(note);
        await db.SaveChangesAsync(cancellationToken);
        return ToRow(row);
    }

    public async Task<WorkbenchRequestRow> CommitAsync(Guid requestId, WorkbenchActor committer, CancellationToken cancellationToken)
    {
        var row = await LoadAsync(requestId, cancellationToken);
        if (row.Status == WorkbenchRequestStatus.Committed) return ToRow(row);
        if (row.Status != WorkbenchRequestStatus.Approved)
            throw new CommandRefusedException("workbench_request_not_approved", $"Workbench request {requestId} is {row.Status}.");
        var choice = await catalog.GetAsync(row.Choice, cancellationToken);
        var route = await catalog.RouteAsync(row.Choice, row.Command, cancellationToken);
        if (!committer.Roles.Intersect(choice.CommitRoles ?? [], StringComparer.Ordinal).Any())
            throw new CommandRefusedException("approval_required", "Your role may initiate this choice but may not commit it.", $"ask someone with {string.Join(" or ", choice.CommitRoles ?? [])}");

        var request = Request(row);
        await RefuseIfChangedAsync(row, request, cancellationToken);
        var approvals = Approvals(row);
        var initiatingRole = CorrectionRules.InitiatingRole(Read<List<string>>(row.InitiatorRolesJson, row.RequestId));
        var checkedPreview = await dispatcher.PreviewAsync(request, committer, approvals, initiatingRole, cancellationToken);
        if (checkedPreview.Block > 0)
        {
            var refusal = checkedPreview.Refusal ?? new CommandRefusal("approval_required", "The recorded approvals do not satisfy the command.");
            throw new CommandRefusedException(refusal.Rule, refusal.Message, refusal.UnblockingStep);
        }

        // The command id is stable, so a crash after the command commits but before this row is marked committed is
        // recovered by retry: the command replays, then this row advances. No command rule or fact is duplicated here.
        var result = await dispatcher.CommitAsync(request, committer, approvals, initiatingRole, cancellationToken);
        // TIGHT COUPLING: the existing command must record its CorrectionLedger row before the tenant choice's KPI root
        // cause is applied. A fallback projection is retired only after that succeeds, otherwise an uncommitted re-file
        // would vanish from the KPI. Swapping these calls leaves no classified row; omitting them double-counts or loses attribution.
        await corrections.SetWorkbenchRootCauseAsync(request.CommandId, route.RootCause, cancellationToken);
        if (row.UnclassifiedDescription is not null)
            await corrections.SetRequestStatusAsync(row.RequestId, CorrectionRequestStatus.Refiled, cancellationToken);
        row.Status = WorkbenchRequestStatus.Committed;
        row.CommittedBy = committer.UserRef;
        row.CommittedAt = clock.UtcNow;
        row.ResultJson = result.GetRawText();
        await db.SaveChangesAsync(cancellationToken);
        return ToRow(row);
    }

    public async Task<WorkbenchRequestRow> FileUnclassifiedAsync(Guid requestId, FileUnclassifiedWorkbenchRequest request,
        WorkbenchActor initiator, CancellationToken cancellationToken)
    {
        if (await db.WorkbenchRequests.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == requestId, cancellationToken) is { } replay)
            return ToRow(replay);
        var fallback = await catalog.GetAsync(WorkbenchChoiceId.Fallback, cancellationToken);
        if (fallback.Status != WorkbenchChoiceStatus.Shown)
            throw new CommandRefusedException("workbench_fallback_required", "The unclassified-request fallback is not available.");
        if (!initiator.Roles.Intersect(fallback.InitiateRoles ?? [], StringComparer.Ordinal).Any())
            throw new CommandRefusedException("workbench_initiator_required", "Your tenant role may not file a correction request.");
        var description = Trim(request.Description) ?? throw new CommandRefusedException("description_required", "An unclassified correction request needs a description.");
        ValidateTarget(request.Target);
        if (request.Evidence?.Any(e => string.IsNullOrWhiteSpace(e.Kind) || string.IsNullOrWhiteSpace(e.Ref)) == true)
            throw new CommandRefusedException("evidence_invalid", "Every evidence reference needs a kind and reference.");

        var now = clock.UtcNow;
        var unitIds = await TargetUnitsAsync(request.Target, cancellationToken);
        var preview = await RequestSnapshotAsync(unitIds, cancellationToken);
        var answers = new WorkbenchAnswers("correction_request", JsonSerializer.SerializeToElement(new { }, LedgerJson.Options));
        var row = new WorkbenchRequest
        {
            TenantId = tenant.TenantId,
            RequestId = requestId,
            CommandId = requestId,
            Choice = fallback.Choice,
            Command = "correction_request",
            TargetJson = JsonSerializer.Serialize(request.Target, LedgerJson.Options),
            AnswersJson = JsonSerializer.Serialize(answers, LedgerJson.Options),
            Reason = "unclassified",
            Note = description,
            UnclassifiedDescription = description,
            EvidenceJson = JsonSerializer.Serialize(request.Evidence ?? [], LedgerJson.Options),
            PreviewJson = JsonSerializer.Serialize(preview, LedgerJson.Options),
            PreviewHash = WorkbenchPreviewHasher.Hash(preview),
            Status = WorkbenchRequestStatus.Unclassified,
            InitiatedBy = initiator.UserRef,
            InitiatorRolesJson = JsonSerializer.Serialize(initiator.Roles.Distinct(StringComparer.Ordinal).ToList(), LedgerJson.Options),
            InitiatedAt = now,
            RequiredApproverRolesJson = JsonSerializer.Serialize(new[] { YojanaRole.SerialAdministrator }, LedgerJson.Options),
            ApprovalsJson = "[]",
        };
        db.WorkbenchRequests.Add(row);
        await corrections.RecordRequestAsync(requestId, initiator.UserRef, CorrectionRules.InitiatingRole(initiator.Roles), now,
            description, unitIds, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToRow(row);
    }

    public async Task<WorkbenchRequestRow> RefileAsync(Guid requestId, RefileWorkbenchRequest refile,
        WorkbenchActor administrator, CancellationToken cancellationToken)
    {
        RequireSerialAdministrator(administrator);
        var row = await LoadAsync(requestId, cancellationToken);
        if (row.Status != WorkbenchRequestStatus.Unclassified)
            throw new CommandRefusedException("workbench_request_not_unclassified", $"Workbench request {requestId} is {row.Status}.");
        var originalTarget = Read<WorkbenchTarget>(row.TargetJson, row.RequestId);
        var evidence = Read<List<WorkbenchEvidenceRef>>(row.EvidenceJson, row.RequestId).Concat(refile.Request.Evidence ?? [])
            .DistinctBy(e => (e.Kind, e.Ref)).ToList();
        var request = refile.Request with { Target = originalTarget, Evidence = evidence };
        var choice = await ValidateRouteAsync(request, cancellationToken);
        var policy = await approvalSettings.GetAsync(cancellationToken);
        var preview = await dispatcher.PreviewForDisplayAsync(request, cancellationToken);
        var initiatingRole = CorrectionRules.InitiatingRole(Read<List<string>>(row.InitiatorRolesJson, row.RequestId));
        var ready = preview.Block == 0 && await ReadyAsync(request, choice, administrator, [], initiatingRole, cancellationToken);

        row.CommandId = request.CommandId;
        row.Choice = request.Choice;
        row.Command = request.Answers.Command;
        row.AnswersJson = JsonSerializer.Serialize(request.Answers, LedgerJson.Options);
        row.Reason = request.Reason;
        row.Note = Trim(request.Note);
        row.EvidenceJson = JsonSerializer.Serialize(request.Evidence ?? [], LedgerJson.Options);
        row.PreviewJson = JsonSerializer.Serialize(preview, LedgerJson.Options);
        row.PreviewHash = WorkbenchPreviewHasher.Hash(preview);
        row.RequiredApproverRolesJson = JsonSerializer.Serialize(RequiredRoles(policy, choice, request.Answers.Command), LedgerJson.Options);
        row.ApprovalsJson = "[]";
        row.Status = preview.Block > 0 ? WorkbenchRequestStatus.Draft : ready ? WorkbenchRequestStatus.Approved : WorkbenchRequestStatus.AwaitingApproval;
        row.ClassifiedBy = administrator.UserRef;
        row.ClassifiedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToRow(row);
    }

    public async Task<WorkbenchRequestRow> EscalateAsync(Guid requestId, WorkbenchActor administrator, string note,
        CancellationToken cancellationToken)
    {
        RequireSerialAdministrator(administrator);
        if (Trim(note) is not { } decision) throw new CommandRefusedException("note_required", "A no-command-fits escalation needs a note.");
        var row = await LoadAsync(requestId, cancellationToken);
        if (row.Status == WorkbenchRequestStatus.NoCommandFits) return ToRow(row);
        if (row.Status != WorkbenchRequestStatus.Unclassified)
            throw new CommandRefusedException("workbench_request_not_unclassified", $"Workbench request {requestId} is {row.Status}.");
        row.Status = WorkbenchRequestStatus.NoCommandFits;
        row.DecisionNote = decision;
        row.EscalatedBy = administrator.UserRef;
        row.EscalatedAt = clock.UtcNow;
        await corrections.SetRequestStatusAsync(requestId, CorrectionRequestStatus.NoCommandFits, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToRow(row);
    }

    private async Task<IReadOnlyList<Guid>> TargetUnitsAsync(WorkbenchTarget target, CancellationToken cancellationToken)
    {
        if (target.UnitId is { } unitId)
        {
            if (!await db.Current.AsNoTracking().AnyAsync(c => c.TenantId == tenant.TenantId && c.UnitId == unitId, cancellationToken))
                throw new CommandRefusedException("unit_unknown", $"Unit {unitId} does not exist.");
            return [unitId];
        }
        if (target.SelectionId is { } selectionId)
        {
            if (!await db.SelectionSets.AsNoTracking().AnyAsync(s => s.TenantId == tenant.TenantId && s.SelectionId == selectionId, cancellationToken))
                throw new CommandRefusedException("selection_unknown", $"Selection set {selectionId} does not exist.");
            return await db.SelectionSetMembers.AsNoTracking().Where(m => m.TenantId == tenant.TenantId && m.SelectionId == selectionId)
                .Select(m => m.UnitId).ToListAsync(cancellationToken);
        }
        if (target.Document is not { } document) return [];
        var tenantId = tenant.TenantId;
        var live = db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.DocumentType == document.Type && e.DocumentId == document.Id
            && (document.Line == null || e.DocumentLine == document.Line)).Select(e => e.UnitId);
        var archived = db.ArchivedEvents.AsNoTracking().Where(e => e.TenantId == tenantId && e.DocumentType == document.Type && e.DocumentId == document.Id
            && (document.Line == null || e.DocumentLine == document.Line)).Select(e => e.UnitId);
        return await live.Concat(archived).Distinct().ToListAsync(cancellationToken);
    }

    private static void ValidateTarget(WorkbenchTarget target)
    {
        var valid = target.Kind switch
        {
            WorkbenchTargetKind.Unit => target.UnitId is not null,
            WorkbenchTargetKind.DocumentLine => target.Document is not null,
            WorkbenchTargetKind.SelectionSet => target.SelectionId is not null,
            _ => false,
        };
        if (!valid) throw new CommandRefusedException("workbench_target_invalid", "A correction request needs a unit, document line or selection-set target.");
    }

    private async Task<CorrectionPreview> RequestSnapshotAsync(IReadOnlyList<Guid> unitIds, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var states = await db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && unitIds.Contains(c.UnitId))
            .Select(c => new { c.UnitId, c.ItemId, c.LifecycleStatus, c.Disposition, c.OverlayFlags, c.LastEventTime }).ToListAsync(cancellationToken);
        var identifiers = await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && unitIds.Contains(i.UnitId) && i.ValidTo == null)
            .OrderBy(i => i.Type == IdentifierType.InternalSerial ? 0 : 1).ThenBy(i => i.RecordedTime)
            .Select(i => new { i.UnitId, i.RawValue }).ToListAsync(cancellationToken);
        var serials = identifiers.GroupBy(i => i.UnitId).ToDictionary(g => g.Key, g => g.First().RawValue);
        var units = states.OrderBy(s => s.UnitId).Select(s => new UnitOutcome(s.UnitId, serials.GetValueOrDefault(s.UnitId), UnitOutcome.Outcomes.Skip,
            "unclassified_request", JsonSerializer.Serialize(new
            {
                item_id = s.ItemId,
                lifecycle_status = s.LifecycleStatus,
                disposition = s.Disposition,
                overlay_flags = JsonSerializer.Deserialize<List<string>>(s.OverlayFlags, LedgerJson.Options) ?? [],
                last_event_time = s.LastEventTime,
            }, LedgerJson.Options))).ToList();
        return EmptyRequestPreview() with { UnitCount = units.Count, Skip = units.Count, Units = units };
    }

    private static CorrectionPreview EmptyRequestPreview() => new(
        SelectionId: null,
        Command: "correction_request",
        UnitCount: 0,
        Change: 0,
        Skip: 0,
        Block: 0,
        Units: [],
        UnitEvents: [],
        EntitlementChanges: [],
        CertificateVersions: [],
        PrintJobs: []);

    private static void RequireSerialAdministrator(WorkbenchActor actor)
    {
        if (!actor.Roles.Contains(YojanaRole.SerialAdministrator, StringComparer.Ordinal))
            throw new CommandRefusedException("serial_administrator_required", "Only a serial administrator may classify or escalate an unclassified request.");
    }

    private async Task RefuseIfChangedAsync(WorkbenchRequest row, WorkbenchPreviewRequest request, CancellationToken cancellationToken)
    {
        var current = await dispatcher.PreviewForDisplayAsync(request, cancellationToken);
        if (WorkbenchPreviewHasher.Hash(current) == row.PreviewHash) return;
        row.Status = WorkbenchRequestStatus.Superseded;
        await db.SaveChangesAsync(cancellationToken);
        throw new CommandRefusedException("preview_changed", "The command preview changed after this request was raised.", "review the new preview and raise a new request");
    }

    private async Task<bool> AnyReadyAsync(WorkbenchPreviewRequest request, WorkbenchChoice choice, IReadOnlyList<WorkbenchActor> participants,
        IReadOnlyList<WorkbenchApproval> approvals, string initiatingRole, CancellationToken cancellationToken)
    {
        foreach (var participant in participants)
            if (await ReadyAsync(request, choice, participant, approvals, initiatingRole, cancellationToken)) return true;
        return false;
    }

    private async Task<bool> ReadyAsync(WorkbenchPreviewRequest request, WorkbenchChoice choice, WorkbenchActor committer,
        IReadOnlyList<WorkbenchApproval> approvals, string initiatingRole, CancellationToken cancellationToken)
    {
        if (!committer.Roles.Intersect(choice.CommitRoles ?? [], StringComparer.Ordinal).Any()) return false;
        var preview = await dispatcher.PreviewAsync(request, committer, approvals, initiatingRole, cancellationToken);
        return preview.Block == 0;
    }

    private async Task<WorkbenchChoice> ValidateRouteAsync(WorkbenchPreviewRequest request, CancellationToken cancellationToken)
    {
        var choice = await catalog.GetAsync(request.Choice, cancellationToken);
        if (choice.Status != WorkbenchChoiceStatus.Shown)
            throw new CommandRefusedException("workbench_choice_retired", $"Workbench choice '{request.Choice}' is retired.", "choose a shown workbench choice");
        var route = await catalog.RouteAsync(request.Choice, request.Answers.Command, cancellationToken);
        if (!CorrectionRules.Reasons(WorkbenchChoiceRules.CorrectionCommandForRoute(request.Answers.Command)).Contains(request.Reason))
            throw new CommandRefusedException("reason_unknown", $"Reason '{request.Reason}' is not valid for '{request.Answers.Command}'.", $"use the route default '{route.DefaultReason}' or another command reason");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new CommandRefusedException("reason_required", "A workbench request needs a reason.");
        if (request.Evidence?.Any(e => string.IsNullOrWhiteSpace(e.Kind) || string.IsNullOrWhiteSpace(e.Ref)) == true)
            throw new CommandRefusedException("evidence_invalid", "Every evidence reference needs a kind and reference.");
        return choice;
    }

    private static List<string> RequiredRoles(CorrectionApprovalPolicy policy, WorkbenchChoice choice, string command)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        roles.UnionWith(choice.CommitRoles ?? []);
        if (policy.Commands.FirstOrDefault(c => c.Command == command) is { } rule)
        {
            roles.UnionWith(rule.CommitRoles);
            roles.UnionWith(rule.SoldCommitRoles ?? []);
            roles.UnionWith(rule.RequiredRoles ?? []);
            roles.UnionWith((rule.Conditions ?? []).Where(c => c.Action == CorrectionApprovalCondition.NeedsApprover).SelectMany(c => c.Roles));
        }
        return roles.Order(StringComparer.Ordinal).ToList();
    }

    private async Task<WorkbenchRequest> LoadAsync(Guid requestId, CancellationToken cancellationToken) =>
        await db.WorkbenchRequests.FirstOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == requestId, cancellationToken)
        ?? throw new CommandRefusedException("workbench_request_unknown", $"Workbench request {requestId} does not exist.");

    internal static WorkbenchPreviewRequest Request(WorkbenchRequest row) => new(row.CommandId, row.Choice,
        Read<WorkbenchAnswers>(row.AnswersJson, row.RequestId), Read<WorkbenchTarget>(row.TargetJson, row.RequestId), row.Reason, row.Note,
        Read<List<WorkbenchEvidenceRef>>(row.EvidenceJson, row.RequestId));

    internal static List<WorkbenchApproval> Approvals(WorkbenchRequest row) => Read<List<WorkbenchApproval>>(row.ApprovalsJson, row.RequestId);

    internal static WorkbenchRequestRow ToRow(WorkbenchRequest row) => new(row.RequestId, row.CommandId, row.Choice, row.Command,
        Read<WorkbenchTarget>(row.TargetJson, row.RequestId), Read<WorkbenchAnswers>(row.AnswersJson, row.RequestId), row.Reason, row.Note,
        Read<List<WorkbenchEvidenceRef>>(row.EvidenceJson, row.RequestId), Read<CorrectionPreview>(row.PreviewJson, row.RequestId), row.PreviewHash,
        row.Status, row.InitiatedBy, Read<List<string>>(row.InitiatorRolesJson, row.RequestId), row.InitiatedAt,
        Read<List<string>>(row.RequiredApproverRolesJson, row.RequestId), Approvals(row), row.RejectedBy, row.RejectedAt, row.DecisionNote,
        row.CommittedBy, row.CommittedAt, row.ResultJson is null ? null : JsonSerializer.Deserialize<JsonElement>(row.ResultJson, LedgerJson.Options),
        row.UnclassifiedDescription, row.ClassifiedBy, row.ClassifiedAt, row.EscalatedBy, row.EscalatedAt);

    private static T Read<T>(string json, Guid requestId) => JsonSerializer.Deserialize<T>(json, LedgerJson.Options)
        ?? throw new InvalidOperationException($"Workbench request {requestId} contains unreadable {typeof(T).Name} JSON.");

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim() is { Length: > 2000 } longValue ? longValue[..2000] : value.Trim();
}

public sealed class WorkbenchRequestQueries(SerialisationDbContext db, ITenantContext tenant)
{
    public async Task<WorkbenchRequestRow?> GetAsync(Guid requestId, CancellationToken cancellationToken) =>
        await db.WorkbenchRequests.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == requestId, cancellationToken) is { } row
            ? WorkbenchRequestService.ToRow(row) : null;

    public async Task<WorkbenchRequestPage> InboxAsync(IReadOnlyList<string> roles, int skip, int take, CancellationToken cancellationToken)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);
        var query = db.WorkbenchRequests.AsNoTracking().Where(r => r.TenantId == tenant.TenantId
            && (r.Status == WorkbenchRequestStatus.AwaitingApproval || r.Status == WorkbenchRequestStatus.Unclassified));
        if (roles.Count == 0) return new([], 0, skip, take);
        query = query.Where(HasAnyRole(roles));
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(r => r.InitiatedAt).ThenBy(r => r.RequestId).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return new(rows.Select(WorkbenchRequestService.ToRow).ToList(), total, skip, take);
    }

    private static Expression<Func<WorkbenchRequest, bool>> HasAnyRole(IReadOnlyList<string> roles)
    {
        var row = Expression.Parameter(typeof(WorkbenchRequest), "row");
        var json = Expression.Property(row, nameof(WorkbenchRequest.RequiredApproverRolesJson));
        var contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        Expression body = Expression.Constant(false);
        foreach (var role in roles.Distinct(StringComparer.Ordinal))
            body = Expression.OrElse(body, Expression.Call(json, contains, Expression.Constant($"\"{role}\"")));
        return Expression.Lambda<Func<WorkbenchRequest, bool>>(body, row);
    }
}
