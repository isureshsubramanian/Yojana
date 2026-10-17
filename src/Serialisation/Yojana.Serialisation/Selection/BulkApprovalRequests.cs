using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Selection;

/// <summary>
/// P2-T21: the approval queue for bulk executions. A request is raised only from a preview whose every block is
/// <c>approval_required</c> (anything else an approver cannot fix). It freezes the requester's preview as a fingerprint;
/// approval re-runs that preview with the requester's roles and, if anything moved, marks the request stale instead of
/// executing. Otherwise it previews again with the approver's roles (they must clear every block) and executes through
/// <see cref="BulkExecutor"/> in the handler's one transaction, so the decision and the change commit together or not at all.
/// The operations remain the only judges of who may commit what; this class never decides a role.
/// </summary>
public static class BulkApprovalRequestHandler
{
    public const string ApprovalRequired = "approval_required";

    public static async Task<BulkApprovalRequestRow> Handle(RequestBulkApproval command, BulkExecutor executor, SerialisationDbContext db, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.BulkApprovalRequests.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.CommandId == command.CommandId, cancellationToken) is { } replay) return ToRow(replay);
        var requester = command.Requester.ApproverRef ?? throw new CommandRefusedException("requester_required", "An approval request needs the signed-in requester.");
        var arguments = BulkApprovalArguments.Clean(command.Arguments);
        var (selectionId, operation, outcomes) = await executor.EvaluateAsync(command.SelectionId, command.Operation, arguments, command.Requester, cancellationToken);
        var blocked = outcomes.Where(o => o.Outcome == UnitOutcome.Outcomes.Block).ToList();
        if (blocked.Count == 0) throw new CommandRefusedException("approval_not_needed", "Nothing in this preview needs a higher approval; execute it directly.");
        if (blocked.FirstOrDefault(o => o.Reason != ApprovalRequired) is { } other)
        {
            throw new CommandRefusedException("preview_blocked", $"Unit {other.Serial ?? other.UnitId.ToString()} is blocked by {other.Reason}, which an approver cannot clear.", "fix the blocked units or narrow the selection, then preview again");
        }

        if (await db.BulkApprovalRequests.AnyAsync(r => r.TenantId == tenantId && r.SelectionId == selectionId && r.Operation == operation && r.Status == BulkApprovalStatus.Pending, cancellationToken))
        {
            throw new CommandRefusedException("approval_already_pending", "This selection already has an approval request waiting for this operation.", "open it from the approval inbox, or withdraw it first");
        }

        var now = clock.UtcNow;
        var argumentsJson = JsonSerializer.Serialize(arguments, LedgerJson.Options);
        var row = new BulkApprovalRequest
        {
            TenantId = tenantId, RequestId = TimeOrderedId.New(now), CommandId = command.CommandId, SelectionId = selectionId, Operation = operation,
            ArgumentsJson = argumentsJson, SourceDocumentType = arguments.SourceDocument.Type, SourceDocumentId = arguments.SourceDocument.Id,
            Status = BulkApprovalStatus.Pending, Fingerprint = Fingerprint(operation, argumentsJson, outcomes), Total = outcomes.Count,
            ChangeCount = outcomes.Count(o => o.Outcome == UnitOutcome.Outcomes.Change), SkipCount = outcomes.Count(o => o.Outcome == UnitOutcome.Outcomes.Skip), BlockCount = blocked.Count,
            Rules = ApprovalRequired, RequestedBy = requester, RequestedRoles = string.Join(',', command.Requester.Roles ?? []), RequestedAt = now, Note = Trim(command.Note),
        };
        db.BulkApprovalRequests.Add(row);
        return ToRow(row);
    }

    public static async Task<BulkApprovalRequestRow> Handle(ApproveBulkRequest command, BulkExecutor executor, SerialisationDbContext db, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var row = await LoadAsync(db, tenant, command.RequestId, cancellationToken);
        if (row.Status == BulkApprovalStatus.Approved && row.ExecutedCommandId == command.CommandId) return ToRow(row);
        if (row.Status != BulkApprovalStatus.Pending) throw new CommandRefusedException("approval_not_pending", $"This request is {row.Status}.");
        var approver = command.Approver.ApproverRef ?? throw new CommandRefusedException("approver_required", "Approval needs the signed-in approver.");
        if (string.Equals(approver, row.RequestedBy, StringComparison.Ordinal)) throw new CommandRefusedException("approver_is_requester", "The person who raised a request cannot approve it.", "ask another approver");

        var arguments = Arguments(row);
        var requester = new BulkApproval(row.RequestedBy, false, row.RequestedRoles.Split(',', StringSplitOptions.RemoveEmptyEntries));
        var (_, _, asRequested) = await executor.EvaluateAsync(row.SelectionId, row.Operation, arguments, requester, cancellationToken);
        if (Fingerprint(row.Operation, row.ArgumentsJson, asRequested) != row.Fingerprint)
        {
            // Returned, not thrown: a refusal would roll back the handler's transaction and the stale mark with it.
            Decide(row, BulkApprovalStatus.Stale, approver, "The units changed after the request was raised; preview again and raise a new request.", clock);
            return ToRow(row);
        }

        var (_, _, asApprover) = await executor.EvaluateAsync(row.SelectionId, row.Operation, arguments, command.Approver, cancellationToken);
        if (asApprover.FirstOrDefault(o => o.Outcome == UnitOutcome.Outcomes.Block) is { } stillBlocked)
        {
            throw new CommandRefusedException(stillBlocked.Reason ?? ApprovalRequired, $"Your roles do not clear unit {stillBlocked.Serial ?? stillBlocked.UnitId.ToString()}: {stillBlocked.Detail ?? stillBlocked.Reason}", "ask an approver who holds the required role");
        }

        var result = await executor.ExecuteAsync(new ExecuteBulkOperation(command.CommandId, row.SelectionId, row.Operation, arguments, command.Approver with { Required = true }), cancellationToken);
        Decide(row, BulkApprovalStatus.Approved, approver, command.Note, clock);
        row.ExecutedCommandId = result.CommandId;
        return ToRow(row);
    }

    public static async Task<BulkApprovalRequestRow> Handle(RejectBulkRequest command, BulkExecutor executor, SerialisationDbContext db, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var row = await LoadAsync(db, tenant, command.RequestId, cancellationToken);
        if (row.Status != BulkApprovalStatus.Pending) throw new CommandRefusedException("approval_not_pending", $"This request is {row.Status}.");
        var decider = command.Decider.ApproverRef ?? throw new CommandRefusedException("approver_required", "A decision needs the signed-in user.");
        if (string.IsNullOrWhiteSpace(command.Note)) throw new CommandRefusedException("note_required", "Say why the request is rejected or withdrawn.");
        if (string.Equals(decider, row.RequestedBy, StringComparison.Ordinal))
        {
            Decide(row, BulkApprovalStatus.Withdrawn, decider, command.Note, clock);
            return ToRow(row);
        }

        // Only someone who could approve may reject: the same judgement as approval, without executing.
        var (_, _, asDecider) = await executor.EvaluateAsync(row.SelectionId, row.Operation, Arguments(row), command.Decider, cancellationToken);
        if (asDecider.Any(o => o.Outcome == UnitOutcome.Outcomes.Block && o.Reason == ApprovalRequired))
        {
            throw new CommandRefusedException(ApprovalRequired, "Only someone who could approve this request may reject it.", "ask an approver who holds the required role");
        }

        Decide(row, BulkApprovalStatus.Rejected, decider, command.Note, clock);
        return ToRow(row);
    }

    /// <summary>Hash of the operation, the arguments and every unit's outcome in serial order: what the requester saw, and so what the approver approves.</summary>
    internal static string Fingerprint(string operation, string argumentsJson, IReadOnlyList<UnitOutcome> outcomes)
    {
        var text = new StringBuilder(operation).Append('\n').Append(argumentsJson);
        foreach (var o in outcomes) text.Append('\n').Append(o.UnitId.ToString("N")).Append('|').Append(o.Outcome).Append('|').Append(o.Reason).Append('|').Append(o.Detail);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static async Task<BulkApprovalRequest> LoadAsync(SerialisationDbContext db, ITenantContext tenant, Guid requestId, CancellationToken cancellationToken) =>
        await db.BulkApprovalRequests.SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == requestId, cancellationToken)
            ?? throw new CommandRefusedException("approval_request_unknown", $"Approval request {requestId} does not exist.");

    private static void Decide(BulkApprovalRequest row, string status, string by, string? note, IClock clock)
    {
        row.Status = status;
        row.DecidedBy = by;
        row.DecidedAt = clock.UtcNow;
        row.DecisionNote = Trim(note);
    }

    private static string? Trim(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim() is { Length: > 1000 } longNote ? longNote[..1000] : note.Trim();

    internal static BulkOperationArguments Arguments(BulkApprovalRequest row) =>
        JsonSerializer.Deserialize<BulkOperationArguments>(row.ArgumentsJson, LedgerJson.Options) ?? throw new InvalidOperationException($"Approval request {row.RequestId} has unreadable arguments.");

    internal static BulkApprovalRequestRow ToRow(BulkApprovalRequest row) => new(row.RequestId, row.SelectionId, row.Operation, row.Status, new DocumentRef(row.SourceDocumentType, row.SourceDocumentId),
        Arguments(row), row.Total, row.ChangeCount, row.SkipCount, row.BlockCount, row.Rules.Split(',', StringSplitOptions.RemoveEmptyEntries), row.RequestedBy, row.RequestedAt, row.Note,
        row.DecidedBy, row.DecidedAt, row.DecisionNote, row.ExecutedCommandId);
}

/// <summary>The approval inbox reads (P2-T21).</summary>
public sealed class BulkApprovalRequestQueries(SerialisationDbContext db, ITenantContext tenant)
{
    public async Task<BulkApprovalRequestPage> PageAsync(string? status, int skip, int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);
        var query = db.BulkApprovalRequests.AsNoTracking().Where(r => r.TenantId == tenant.TenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(r => r.RequestedAt).ThenBy(r => r.RequestId).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return new BulkApprovalRequestPage(rows.Select(BulkApprovalRequestHandler.ToRow).ToList(), total, skip, take);
    }

    public async Task<BulkApprovalRequestRow?> GetAsync(Guid requestId, CancellationToken cancellationToken) =>
        await db.BulkApprovalRequests.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == requestId, cancellationToken) is { } row
            ? BulkApprovalRequestHandler.ToRow(row) : null;
}
