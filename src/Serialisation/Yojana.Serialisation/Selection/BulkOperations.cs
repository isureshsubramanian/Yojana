using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Selection;

/// <summary>
/// A command that can run over a selection set. The preview is the command's own judgement per unit (never the
/// screen's); apply performs it for one unit inside the executor's transaction. Later phases register theirs
/// (entitlement amendments, corrections) beside P1's release.
/// </summary>
public interface IBulkOperation
{
    string Name { get; }

    Task<UnitOutcome> PreviewAsync(Guid unitId, string? serial, BulkOperationArguments arguments, CancellationToken cancellationToken);

    Task ApplyAsync(Guid unitId, Guid commandId, BulkOperationArguments arguments, CancellationToken cancellationToken);

    /// <summary>
    /// The check the executor makes before applying inside its transaction. By default the preview; an operation whose
    /// preview is a dry run (P2-T11 entitlement operations) answers with its skip rules only, because a refusal during
    /// apply fails the whole execution anyway (one transaction, nothing half done).
    /// </summary>
    Task<UnitOutcome> CheckBeforeApplyAsync(Guid unitId, string? serial, BulkOperationArguments arguments, CancellationToken cancellationToken) =>
        PreviewAsync(unitId, serial, arguments, cancellationToken);
}

/// <summary>The approver and roles travel to the operations in reserved <c>__</c> keys of <see cref="BulkOperationArguments.Extra"/>; a caller's own <c>__</c> keys are dropped.</summary>
public static class BulkApprovalArguments
{
    public const string ApproverRef = "__approver_ref";
    public const string ApproverRoles = "__approver_roles";
    public const string RecordedApprovers = "__recorded_approvers";
    public const string RecordedEvidenceRefs = "__recorded_evidence_refs";

    public static BulkOperationArguments With(BulkOperationArguments arguments, BulkApproval? approval)
    {
        var extra = (arguments.Extra ?? new Dictionary<string, string>()).Where(kv => !kv.Key.StartsWith("__", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (approval?.ApproverRef is { } approver) extra[ApproverRef] = approver;
        if (approval?.Roles is { Count: > 0 } roles) extra[ApproverRoles] = string.Join(',', roles);
        if (approval?.Approvers is { Count: > 0 } approvers) extra[RecordedApprovers] = JsonSerializer.Serialize(approvers, LedgerJson.Options);
        if (approval?.EvidenceRefs is { Count: > 0 } evidence) extra[RecordedEvidenceRefs] = JsonSerializer.Serialize(evidence, LedgerJson.Options);
        return arguments with { Extra = extra };
    }

    public static IReadOnlyList<CorrectionApprover> Approvers(BulkOperationArguments arguments) =>
        arguments.Extra?.GetValueOrDefault(RecordedApprovers) is { } json
            ? JsonSerializer.Deserialize<List<CorrectionApprover>>(json, LedgerJson.Options) ?? []
            : [];

    public static IReadOnlyList<string> EvidenceRefs(BulkOperationArguments arguments) =>
        arguments.Extra?.GetValueOrDefault(RecordedEvidenceRefs) is { } json
            ? JsonSerializer.Deserialize<List<string>>(json, LedgerJson.Options) ?? []
            : [];

    /// <summary>The arguments without any reserved key: what a stored approval request keeps (P2-T21).</summary>
    public static BulkOperationArguments Clean(BulkOperationArguments arguments) => With(arguments, null);
}

/// <summary>P1's bulk operation: release every pre-release unit of the set (the completeness guard decides block or change).</summary>
public sealed class ReleaseUnitsOperation(SerialisationDbContext db, IUnitEventWriter writer, IEnumerable<IReleaseGuard> guards, ITenantContext tenant, IClock clock) : IBulkOperation
{
    public const string OperationName = "release_units";

    public string Name => OperationName;

    public async Task<UnitOutcome> PreviewAsync(Guid unitId, string? serial, BulkOperationArguments arguments, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UnitId == unitId, cancellationToken);
        if (unit is null) return new UnitOutcome(unitId, serial, UnitOutcome.Outcomes.Block, "unit_unknown");
        if (unit.LifecycleStatus == LifecycleStatus.Released) return new UnitOutcome(unitId, serial, UnitOutcome.Outcomes.Skip, "already_released");
        if (unit.LifecycleStatus == LifecycleStatus.PendingIdentifier) return new UnitOutcome(unitId, serial, UnitOutcome.Outcomes.Block, "pending_identifier", "no identifier assigned yet");
        if (UnitLifecycle.Next(unit.LifecycleStatus, UnitEventType.Released, unit.Origin) is null) return new UnitOutcome(unitId, serial, UnitOutcome.Outcomes.Skip, "lifecycle_" + unit.LifecycleStatus);
        var assignment = await db.ItemAssignments.AsNoTracking().FirstOrDefaultAsync(a => a.TenantId == tenantId && a.UnitId == unitId && a.ValidTo == null, cancellationToken);
        if (assignment is null) return new UnitOutcome(unitId, serial, UnitOutcome.Outcomes.Block, "no_item_assignment");
        foreach (var guard in guards)
        {
            if (await guard.CheckAsync(unit, assignment.ItemId, arguments.EventTime, cancellationToken) is { } refusal)
            {
                return new UnitOutcome(unitId, serial, UnitOutcome.Outcomes.Block, refusal.Rule, refusal.Message, refusal.UnblockingStep);
            }
        }

        return new UnitOutcome(unitId, serial, UnitOutcome.Outcomes.Change, null, "release · BuildRecord next version");
    }

    public Task ApplyAsync(Guid unitId, Guid commandId, BulkOperationArguments arguments, CancellationToken cancellationToken)
    {
        // One deterministic sub-command per unit, so a replay of the bulk command finds its events already written.
        var subCommandId = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{commandId:N}|{unitId:N}")).AsSpan(0, 16));
        var signature = arguments.Extra is not null && arguments.Extra.TryGetValue("release_signature_ref", out var s) ? s : arguments.ActorRef;
        return ReleaseUnitHandler.Handle(new ReleaseUnit(subCommandId, unitId, arguments.EventTime, arguments.ActorRef, arguments.LegalEntityId, signature, arguments.SourceDocument), db, writer, guards, tenant, clock, cancellationToken);
    }
}

/// <summary>Preview, execution and find-within over stored selection sets (MOD-11: one transaction, one source document, one approval; the report is the partial-failure report).</summary>
public sealed class BulkExecutor(SerialisationDbContext db, IServiceProvider services, ITenantContext tenant,
    IClock clock, IMessageBus bus, BulkPublicationContext publication)
{
    private IBulkOperation Operation(string name) =>
        services.GetServices<IBulkOperation>().FirstOrDefault(o => o.Name == name) ?? throw new CommandRefusedException("operation_unknown", $"Bulk operation '{name}' is not registered.");

    public async Task<BulkPreviewResult> PreviewAsync(PreviewBulkOperation command, CancellationToken cancellationToken)
    {
        var (selectionId, operationName, outcomes) = await EvaluateAsync(command.SelectionId, command.Operation, command.Arguments, command.Approval, cancellationToken);

        // Blocked first, then skipped, then changes: the operator reads the problems before the bulk of the list.
        var ordered = outcomes.OrderBy(o => o.Outcome switch { UnitOutcome.Outcomes.Block => 0, UnitOutcome.Outcomes.Skip => 1, _ => 2 }).ThenBy(o => o.Serial).ToList();
        var filtered = ordered.Where(o => command.OutcomeFilter is null || o.Outcome == command.OutcomeFilter).Where(o => command.Find is null || Matches(o.Serial, command.Find)).ToList();
        var page = filtered.Skip(command.Skip).Take(command.Take).ToList();
        return new BulkPreviewResult(selectionId, operationName, outcomes.Count, outcomes.Count(o => o.Outcome == UnitOutcome.Outcomes.Change), outcomes.Count(o => o.Outcome == UnitOutcome.Outcomes.Skip), outcomes.Count(o => o.Outcome == UnitOutcome.Outcomes.Block), page, command.Skip, command.Take);
    }

    /// <summary>
    /// Every unit's outcome, in serial order, as the person in <paramref name="approval"/> would commit the set. The preview
    /// pages it; P2-T21 approval requests fingerprint it. The last outcome is kept on each member (the validation report).
    /// </summary>
    public async Task<(Guid SelectionId, string Operation, IReadOnlyList<UnitOutcome> Outcomes)> EvaluateAsync(Guid selectionId, string operationName, BulkOperationArguments arguments, BulkApproval? approval, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var selection = await db.SelectionSets.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.SelectionId == selectionId, cancellationToken)
            ?? throw new CommandRefusedException("selection_unknown", $"Selection {selectionId} does not exist.");
        var operation = Operation(operationName);
        var members = await db.SelectionSetMembers.Where(m => m.TenantId == tenantId && m.SelectionId == selection.SelectionId).OrderBy(m => m.Serial).ThenBy(m => m.UnitId).ToListAsync(cancellationToken);
        var outcomes = new List<UnitOutcome>(members.Count);
        var judged = BulkApprovalArguments.With(arguments, approval);
        foreach (var member in members)
        {
            var outcome = await operation.PreviewAsync(member.UnitId, member.Serial, judged, cancellationToken);
            member.Outcome = outcome.Outcome;
            member.Reason = outcome.Reason;
            member.Detail = outcome.Detail?.Length > 400 ? outcome.Detail[..400] : outcome.Detail;
            outcomes.Add(outcome);
        }

        return (selection.SelectionId, operation.Name, outcomes);
    }

    public async Task<BulkExecutionResult> ExecuteAsync(ExecuteBulkOperation command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var selection = await db.SelectionSets.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.SelectionId == command.SelectionId, cancellationToken)
            ?? throw new CommandRefusedException("selection_unknown", $"Selection {command.SelectionId} does not exist.");
        var members = await db.SelectionSetMembers.Where(m => m.TenantId == tenantId && m.SelectionId == selection.SelectionId).ToListAsync(cancellationToken);
        if (selection.ExecutedCommandId is { } executed)
        {
            if (executed != command.CommandId) throw new CommandRefusedException("selection_already_executed", $"Selection {selection.SelectionId} was executed by command {executed:N} on {selection.ExecutedAt:O}.", "resolve a new selection set for another run");
            return new BulkExecutionResult(command.CommandId, selection.SelectionId, selection.ExecutedOperation!, members.Count, members.Count(m => m.Outcome == UnitOutcome.Outcomes.Done), members.Count(m => m.Outcome == UnitOutcome.Outcomes.Skip), members.Count(m => m.Outcome == UnitOutcome.Outcomes.Block), selection.SelectionId, true);
        }

        if (command.Approval.Required && string.IsNullOrWhiteSpace(command.Approval.ApproverRef))
        {
            throw new CommandRefusedException("approval_required", "This execution needs an approver and none was given.", "route through the approver view");
        }

        var operation = Operation(command.Operation);
        var executionArguments = BulkApprovalArguments.With(command.Arguments, command.Approval);
        // TIGHT COUPLING with PublishedEventMapper: this scoped marker brackets only the real apply loop so its
        // published unit facts carry operation identity without widening every established command. Removing either
        // side makes an above-threshold operation emit per-unit customer events as well as its summary.
        var owned = db.Database.CurrentTransaction is null;
        var transaction = owned ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var planned = new List<(SelectionSetMember Member, UnitOutcome Outcome)>(members.Count);
            foreach (var member in members)
                planned.Add((member, await operation.CheckBeforeApplyAsync(member.UnitId, member.Serial,
                    executionArguments, cancellationToken)));
            publication.OperationId = command.CommandId;
            publication.OperationSize = planned.Count(item => item.Outcome.Outcome == UnitOutcome.Outcomes.Change);
            publication.OperationKind = operation.Name;
            var done = 0;
            foreach (var (member, outcome) in planned)
            {
                if (outcome.Outcome == UnitOutcome.Outcomes.Change)
                {
                    await operation.ApplyAsync(member.UnitId, command.CommandId, executionArguments, cancellationToken);
                    member.Outcome = UnitOutcome.Outcomes.Done;
                    member.Reason = null;
                    member.Detail = outcome.Detail;
                    done++;
                }
                else
                {
                    member.Outcome = outcome.Outcome;
                    member.Reason = outcome.Reason;
                    member.Detail = outcome.Detail?.Length > 400 ? outcome.Detail[..400] : outcome.Detail;
                }
            }

            selection.ExecutedCommandId = command.CommandId;
            selection.ExecutedOperation = operation.Name;
            selection.ExecutedAt = clock.UtcNow;
            selection.SourceDocumentType = command.Arguments.SourceDocument.Type;
            selection.SourceDocumentId = command.Arguments.SourceDocument.Id;
            selection.ApproverRef = command.Approval.ApproverRef;
            var completedUnits = members.Where(member => member.Outcome == UnitOutcome.Outcomes.Done)
                .Select(member => member.UnitId).ToList();
            if (completedUnits.Count > 0)
                await bus.PublishAsync(new BulkOperationCompleted(
                        new(BulkOperationCompleted.Schema, BulkOperationCompleted.Version,
                            TimeOrderedId.New(command.Arguments.EventTime), command.Arguments.EventTime,
                            clock.UtcNow, command.Arguments.LegalEntityId, command.Arguments.ActorRef,
                            command.Arguments.SourceDocument),
                        command.CommandId, operation.Name, completedUnits.Count, completedUnits),
                    new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return new BulkExecutionResult(command.CommandId, selection.SelectionId, operation.Name, members.Count, done, members.Count(m => m.Outcome == UnitOutcome.Outcomes.Skip), members.Count(m => m.Outcome == UnitOutcome.Outcomes.Block), selection.SelectionId, false);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            publication.Clear();
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    public async Task<BulkPreviewResult> FindAsync(FindInSelection command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var selection = await db.SelectionSets.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.SelectionId == command.SelectionId, cancellationToken)
            ?? throw new CommandRefusedException("selection_unknown", $"Selection {command.SelectionId} does not exist.");
        var members = await db.SelectionSetMembers.AsNoTracking().Where(m => m.TenantId == tenantId && m.SelectionId == selection.SelectionId).OrderBy(m => m.Serial).ToListAsync(cancellationToken);
        var hits = members.Where(m => Matches(m.Serial, command.Query)).Select(m => new UnitOutcome(m.UnitId, m.Serial, m.Outcome ?? "", m.Reason, m.Detail)).ToList();
        return new BulkPreviewResult(selection.SelectionId, selection.ExecutedOperation ?? "", hits.Count, hits.Count(o => o.Outcome == UnitOutcome.Outcomes.Change), hits.Count(o => o.Outcome == UnitOutcome.Outcomes.Skip), hits.Count(o => o.Outcome == UnitOutcome.Outcomes.Block), hits.Skip(command.Skip).Take(command.Take).ToList(), command.Skip, command.Take);
    }

    /// <summary>Find-within: a serial, a prefix, a pasted list (comma, space or newline separated) — all against the normalised serial.</summary>
    internal static bool Matches(string? serial, string query)
    {
        if (serial is null) return false;
        var terms = query.Split([',', ' ', '\n', '\r', '\t', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant()).ToList();
        return terms.Any(t => serial.StartsWith(t, StringComparison.OrdinalIgnoreCase));
    }
}

public static class SelectionHandler
{
    public static Task<ResolveSelectionSetResult> Handle(ResolveSelectionSet command, SelectionSetResolver resolver, CancellationToken ct) => resolver.ResolveAsync(command, ct);

    public static Task<BulkPreviewResult> Handle(PreviewBulkOperation command, BulkExecutor executor, CancellationToken ct) => executor.PreviewAsync(command, ct);

    public static Task<BulkExecutionResult> Handle(ExecuteBulkOperation command, BulkExecutor executor, CancellationToken ct) => executor.ExecuteAsync(command, ct);

    public static Task<BulkPreviewResult> Handle(FindInSelection command, BulkExecutor executor, CancellationToken ct) => executor.FindAsync(command, ct);
}
