using System.Text.Json;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
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
/// </summary>
public sealed class WorkbenchRequestService(
    SerialisationDbContext db,
    IWorkbenchChoiceCatalog catalog,
    WorkbenchCommandDispatcher dispatcher,
    CorrectionApprovalSettings approvalSettings,
    ITenantContext tenant,
    IClock clock)
{
    public async Task<WorkbenchPreviewResult> PreviewAsync(WorkbenchPreviewRequest request, CancellationToken cancellationToken)
    {
        ValidateRoute(request);
        var preview = await dispatcher.PreviewForDisplayAsync(request, cancellationToken);
        return new(request.Choice, request.Answers.Command, preview, WorkbenchPreviewHasher.Hash(preview));
    }

    public async Task<WorkbenchRequestRow> InitiateAsync(Guid requestId, WorkbenchPreviewRequest request, WorkbenchActor initiator,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.WorkbenchRequests.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.CommandId == request.CommandId, cancellationToken) is { } replay)
            return ToRow(replay);
        ValidateRoute(request);
        var policy = await approvalSettings.GetAsync(cancellationToken);
        if (!initiator.Roles.Intersect(policy.WorkbenchInitiatorRoles, StringComparer.Ordinal).Any())
            throw new CommandRefusedException("workbench_initiator_required", "Your tenant role may not initiate a correction workbench request.");

        var preview = await dispatcher.PreviewForDisplayAsync(request, cancellationToken);
        var hash = WorkbenchPreviewHasher.Hash(preview);
        var required = RequiredRoles(policy, request.Choice, request.Answers.Command);
        var initiatingRole = CorrectionRules.InitiatingRole(initiator.Roles);
        var ready = preview.Block == 0 && await ReadyAsync(request, initiator, [], initiatingRole, policy, cancellationToken);
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
        var policy = await approvalSettings.GetAsync(cancellationToken);
        var initiatingRole = CorrectionRules.InitiatingRole(Read<List<string>>(row.InitiatorRolesJson, row.RequestId));
        var participants = approvals.Select(a => new WorkbenchActor(a.UserRef, a.Roles)).ToList();
        row.Status = await AnyReadyAsync(request, participants, approvals, initiatingRole, policy, cancellationToken)
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
        var policy = await approvalSettings.GetAsync(cancellationToken);
        var workbench = policy.Workbench.FirstOrDefault(w => w.Choice == row.Choice)
            ?? throw new CommandRefusedException("workbench_approval_policy_missing", $"Workbench choice '{row.Choice}' has no tenant approval rule.");
        if (!committer.Roles.Intersect(workbench.CommitRoles, StringComparer.Ordinal).Any())
            throw new CommandRefusedException("approval_required", "Your role may initiate this choice but may not commit it.", $"ask someone with {string.Join(" or ", workbench.CommitRoles)}");

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
        row.Status = WorkbenchRequestStatus.Committed;
        row.CommittedBy = committer.UserRef;
        row.CommittedAt = clock.UtcNow;
        row.ResultJson = result.GetRawText();
        await db.SaveChangesAsync(cancellationToken);
        return ToRow(row);
    }

    private async Task RefuseIfChangedAsync(WorkbenchRequest row, WorkbenchPreviewRequest request, CancellationToken cancellationToken)
    {
        var current = await dispatcher.PreviewForDisplayAsync(request, cancellationToken);
        if (WorkbenchPreviewHasher.Hash(current) == row.PreviewHash) return;
        row.Status = WorkbenchRequestStatus.Superseded;
        await db.SaveChangesAsync(cancellationToken);
        throw new CommandRefusedException("preview_changed", "The command preview changed after this request was raised.", "review the new preview and raise a new request");
    }

    private async Task<bool> AnyReadyAsync(WorkbenchPreviewRequest request, IReadOnlyList<WorkbenchActor> participants,
        IReadOnlyList<WorkbenchApproval> approvals, string initiatingRole, CorrectionApprovalPolicy policy, CancellationToken cancellationToken)
    {
        foreach (var participant in participants)
            if (await ReadyAsync(request, participant, approvals, initiatingRole, policy, cancellationToken)) return true;
        return false;
    }

    private async Task<bool> ReadyAsync(WorkbenchPreviewRequest request, WorkbenchActor committer, IReadOnlyList<WorkbenchApproval> approvals,
        string initiatingRole, CorrectionApprovalPolicy policy, CancellationToken cancellationToken)
    {
        var choice = policy.Workbench.FirstOrDefault(w => w.Choice == request.Choice);
        if (choice is null || !committer.Roles.Intersect(choice.CommitRoles, StringComparer.Ordinal).Any()) return false;
        var preview = await dispatcher.PreviewAsync(request, committer, approvals, initiatingRole, cancellationToken);
        return preview.Block == 0;
    }

    private void ValidateRoute(WorkbenchPreviewRequest request)
    {
        var route = catalog.Route(request.Choice, request.Answers.Command);
        if (!CorrectionRules.Reasons(request.Answers.Command).Contains(request.Reason)
            && !request.Answers.Command.StartsWith("entitlement.", StringComparison.Ordinal))
            throw new CommandRefusedException("reason_unknown", $"Reason '{request.Reason}' is not valid for '{request.Answers.Command}'.", $"use the route default '{route.DefaultReason}' or another command reason");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new CommandRefusedException("reason_required", "A workbench request needs a reason.");
        if (request.Evidence?.Any(e => string.IsNullOrWhiteSpace(e.Kind) || string.IsNullOrWhiteSpace(e.Ref)) == true)
            throw new CommandRefusedException("evidence_invalid", "Every evidence reference needs a kind and reference.");
    }

    private static List<string> RequiredRoles(CorrectionApprovalPolicy policy, string choice, string command)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        if (policy.Workbench.FirstOrDefault(w => w.Choice == choice) is { } workbench) roles.UnionWith(workbench.CommitRoles);
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
        row.CommittedBy, row.CommittedAt, row.ResultJson is null ? null : JsonSerializer.Deserialize<JsonElement>(row.ResultJson, LedgerJson.Options));

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
        var query = db.WorkbenchRequests.AsNoTracking().Where(r => r.TenantId == tenant.TenantId && r.Status == WorkbenchRequestStatus.AwaitingApproval);
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
