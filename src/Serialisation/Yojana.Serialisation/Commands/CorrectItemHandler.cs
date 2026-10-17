using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Commands;

// TIGHT COUPLING (declared, ADR-0001 "Integration boundaries and coupling", coupling 1)
// WHAT:  the in-stock path calls IStockReclassificationPort inside the SAME local transaction as the new item assignment
//        and unit.item_corrected fact. WHY: stock quantity and in-stock serial count must remain equal under both items at
//        every commit. WHAT BREAKS IF SEPARATED: a crash or retry can leave Inventory and the unit under different items.
// The full contract is documented on IStockReclassificationPort; change its signature and both comments together.

/// <summary>
/// P3-T04, case 118: corrects a unit's item by appending an assignment and one correction fact. Stock is reclassified
/// through coupling 1; sold-unit documents stay immutable and the mismatch plus finance-review obligation are recorded.
/// </summary>
public static class CorrectItemHandler
{
    public static async Task<CorrectItemResult> Handle(
        CorrectItem command,
        SerialisationDbContext db,
        IUnitEventWriter writer,
        IStockReclassificationPort stock,
        IItemQueryPort items,
        IItemPolicyPort itemPolicies,
        ICustomerContractPort contracts,
        CorrectionApprovalSettings approvals,
        CorrectionLedger corrections,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var key = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, command.UnitId, UnitEventType.ItemCorrected, command.EventTime);
        var replayed = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IdempotencyKey == key && e.EventTime == command.EventTime, cancellationToken);
        if (replayed is not null) return Replay(replayed);

        var correction = command.Correction ?? throw new CommandRefusedException("correction_metadata_required", "CorrectItem requires correction metadata.");
        var committer = command.CommittedBy ?? throw new CommandRefusedException("committer_required", "CorrectItem names who commits it and the roles they hold.");
        var metadata = await corrections.PrepareAsync(CorrectionCommand.CorrectItem, correction, command.CommandId, command.Reason, correction.Note,
            null, command.EventTime, correction.InitiatingRole ?? throw new CommandRefusedException("initiating_role_required", "CorrectItem requires the initiating role."), cancellationToken);
        var plan = await CorrectItemPlanning.PlanAsync(command, committer, db, items, itemPolicies, contracts, approvals, tenant, cancellationToken);

        string? reclassificationRef = null;
        if (!plan.Sold)
        {
            var posting = await stock.ReclassifyAsync(new StockReclassificationRequest(
                command.CommandId, metadata.CorrectionId, command.UnitId, plan.CurrentAssignment.ItemId, command.CorrectedItemId,
                plan.CurrentState.LocationId, plan.CurrentState.LocationKind, plan.CurrentState.Disposition, command.EventTime,
                command.ActorRef, command.Reason), cancellationToken);
            reclassificationRef = posting.PostingReference;
        }

        var eventId = TimeOrderedId.New(command.EventTime);
        plan.CurrentAssignment.ValidTo = command.EventTime;
        var next = new UnitItemAssignment
        {
            TenantId = tenantId,
            AssignmentId = TimeOrderedId.New(command.EventTime),
            UnitId = command.UnitId,
            ItemId = command.CorrectedItemId,
            ModelVersionId = command.CorrectedModelVersionId,
            ValidFrom = command.EventTime,
            Reason = ItemAssignmentReason.Correction,
            EventId = eventId,
        };
        db.ItemAssignments.Add(next);

        if (plan.DocumentMismatch)
        {
            if (!plan.Flags.Contains(OverlayFlag.DocumentMismatch, StringComparer.Ordinal)) plan.Flags.Add(OverlayFlag.DocumentMismatch);
            plan.Unit.OverlayFlags = JsonSerializer.Serialize(plan.Flags, LedgerJson.Options);
        }

        await writer.AppendAsync(
        [
            new UnitEventDraft
            {
                UnitId = command.UnitId,
                EventTime = command.EventTime,
                EventType = UnitEventType.ItemCorrected,
                PresetEventId = eventId,
                LegalEntityId = command.LegalEntityId,
                ActorRef = command.ActorRef,
                IdempotencyKey = key,
                Attributes = CorrectionLedger.Attributes(metadata, new
                {
                    command_id = command.CommandId,
                    before = new { assignment_id = plan.CurrentAssignment.AssignmentId, item_id = plan.CurrentAssignment.ItemId, model_version_id = plan.CurrentAssignment.ModelVersionId },
                    after = new { assignment_id = next.AssignmentId, item_id = next.ItemId, model_version_id = next.ModelVersionId, item_code = plan.CorrectedItem.ItemCode },
                    sold = plan.Sold,
                    document_mismatch = plan.DocumentMismatch,
                    inventory_reclassification_ref = reclassificationRef,
                    finance_review_required = plan.FinanceReviewRequired,
                    finance_review_roles = plan.Approval.ReviewTasks,
                    warranty_policy_changed = plan.WarrantyPolicyChanges.Count > 0,
                    warranty_policy_changes = plan.WarrantyPolicyChanges,
                }),
                Delta = new CurrentStateDelta
                {
                    ItemId = command.CorrectedItemId,
                    ModelVersionId = command.CorrectedModelVersionId,
                    ClearModelVersionId = command.CorrectedModelVersionId is null,
                    OverlayFlags = plan.DocumentMismatch ? plan.Flags : null,
                },
            },
        ], cancellationToken);
        corrections.Record(CorrectionCommand.CorrectItem, metadata, command.ActorRef, command.EventTime, 1);
        return new CorrectItemResult(command.UnitId, plan.CurrentAssignment.AssignmentId, next.AssignmentId,
            plan.CurrentAssignment.ItemId, next.ItemId, plan.Sold, plan.DocumentMismatch, reclassificationRef,
            plan.FinanceReviewRequired, plan.WarrantyPolicyChanges, metadata.CorrectionId, eventId, false);
    }

    private static CorrectItemResult Replay(UnitEvent replayed)
    {
        using var document = JsonDocument.Parse(replayed.Attributes ?? "{}");
        var root = document.RootElement;
        var before = root.GetProperty("before");
        var after = root.GetProperty("after");
        var changes = root.TryGetProperty("warranty_policy_changes", out var policies)
            ? policies.Deserialize<List<CorrectedItemWarrantyPolicy>>(LedgerJson.Options) ?? []
            : [];
        return new CorrectItemResult(
            replayed.UnitId,
            before.GetProperty("assignment_id").GetGuid(),
            after.GetProperty("assignment_id").GetGuid(),
            before.GetProperty("item_id").GetGuid(),
            after.GetProperty("item_id").GetGuid(),
            root.GetProperty("sold").GetBoolean(),
            root.GetProperty("document_mismatch").GetBoolean(),
            root.TryGetProperty("inventory_reclassification_ref", out var posting) && posting.ValueKind == JsonValueKind.String ? posting.GetString() : null,
            root.GetProperty("finance_review_required").GetBoolean(),
            changes,
            root.GetProperty("correction_id").GetGuid(),
            replayed.EventId,
            true);
    }
}

public static class PreviewCorrectItemHandler
{
    public static async Task<CorrectionPreview> Handle(
        PreviewCorrectItem preview,
        SerialisationDbContext db,
        IItemQueryPort items,
        IItemPolicyPort itemPolicies,
        ICustomerContractPort contracts,
        CorrectionApprovalSettings approvals,
        CorrectionLedger corrections,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var command = preview.Command;
        try
        {
            var correction = command.Correction ?? throw new CommandRefusedException("correction_metadata_required", "CorrectItem requires correction metadata.");
            var committer = command.CommittedBy ?? throw new CommandRefusedException("committer_required", "CorrectItem names who commits it and the roles they hold.");
            await corrections.PrepareAsync(CorrectionCommand.CorrectItem, correction, command.CommandId, command.Reason, correction.Note,
                null, command.EventTime, correction.InitiatingRole ?? throw new CommandRefusedException("initiating_role_required", "CorrectItem requires the initiating role."), cancellationToken);
            var plan = await CorrectItemPlanning.PlanAsync(command, committer, db, items, itemPolicies, contracts, approvals, tenant, cancellationToken);
            var detail = JsonSerializer.Serialize(new
            {
                previous_item_id = plan.CurrentAssignment.ItemId,
                corrected_item_id = command.CorrectedItemId,
                previous_model_version_id = plan.CurrentAssignment.ModelVersionId,
                corrected_model_version_id = command.CorrectedModelVersionId,
                path = plan.Sold ? "sold" : "in_stock",
                document_mismatch = plan.DocumentMismatch,
                inventory_reclassification_requested = !plan.Sold,
                finance_review_required = plan.FinanceReviewRequired,
                warranty_policy_changed = plan.WarrantyPolicyChanges.Count > 0,
                warranty_policy_changes = plan.WarrantyPolicyChanges,
            }, LedgerJson.Options);
            var certificates = new List<CorrectionCertificatePreview>
            {
                new(command.UnitId, "release_record", "reissue", "correction"),
            };
            if (plan.WarrantyPolicyChanges.Count > 0)
            {
                certificates.Add(new(command.UnitId, "warranty_certificate", "reissue", "item_policy_changed"));
            }

            return new CorrectionPreview(null, CorrectionCommand.CorrectItem, 1, 1, 0, 0,
                [new(command.UnitId, null, UnitOutcome.Outcomes.Change, null, detail)],
                [new(command.UnitId, UnitEventType.ItemCorrected, command.EventTime, plan.Sold ? "flag document mismatch and raise finance review" : "reclassify stock and correct item")],
                [], certificates, []);
        }
        catch (CommandRefusedException ex)
        {
            return new CorrectionPreview(null, CorrectionCommand.CorrectItem, 1, 0, 0, 1,
                [new(command.UnitId, null, UnitOutcome.Outcomes.Block, ex.Rule, ex.Message, ex.UnblockingStep)],
                [], [], [], [], ex.Refusal);
        }
    }
}

internal sealed record CorrectItemPlan(
    Unit Unit,
    UnitCurrent CurrentState,
    UnitItemAssignment CurrentAssignment,
    ItemSerialisationProfile CorrectedItem,
    bool Sold,
    bool DocumentMismatch,
    List<string> Flags,
    CorrectionApprovalDecision Approval,
    bool FinanceReviewRequired,
    IReadOnlyList<CorrectedItemWarrantyPolicy> WarrantyPolicyChanges);

internal static class CorrectItemPlanning
{
    private static readonly string[] CustomerOwnerTypes = [PartyType.Customer, PartyType.Distributor, PartyType.EndUser, PartyType.MarketplaceBuyer];

    public static async Task<CorrectItemPlan> PlanAsync(
        CorrectItem command,
        CorrectionCommitter committer,
        SerialisationDbContext db,
        IItemQueryPort items,
        IItemPolicyPort itemPolicies,
        ICustomerContractPort contracts,
        CorrectionApprovalSettings approvals,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        CorrectionRules.ValidateReason(CorrectionCommand.CorrectItem, command.Reason);
        var tenantId = tenant.TenantId;
        var unit = await db.Units.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UnitId == command.UnitId, cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Unit {command.UnitId} does not exist.");
        var current = await db.Current.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.UnitId == command.UnitId, cancellationToken)
            ?? throw new CommandRefusedException("unit_current_missing", $"Unit {command.UnitId} has no current-state row.");
        var assignment = await db.ItemAssignments.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.UnitId == command.UnitId && a.ValidTo == null, cancellationToken)
            ?? throw new CommandRefusedException("no_item_assignment", $"Unit {command.UnitId} has no current item assignment.");
        if (assignment.ItemId == command.CorrectedItemId && assignment.ModelVersionId == command.CorrectedModelVersionId)
            throw new CommandRefusedException("no_change", "The unit already has that item and model version.");
        if (command.EventTime < assignment.ValidFrom)
            throw new CommandRefusedException("effective_before_current", $"The correction is dated {command.EventTime:O}, before the current assignment started ({assignment.ValidFrom:O}).", "date the correction on or after the current assignment");

        var profile = await items.GetAsync(command.CorrectedItemId, cancellationToken)
            ?? throw new CommandRefusedException("item_unknown", $"Item {command.CorrectedItemId} is not known to the host.");
        if (profile.TrackingMode != ItemSerialisationProfile.TrackingModes.Serial)
            throw new CommandRefusedException("item_not_serial_tracked", $"Item {profile.ItemCode} is {profile.TrackingMode}-tracked.");

        var ownership = await db.Episodes.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.UnitId == command.UnitId
            && e.Kind == EpisodeKind.Ownership && e.ValidTo == null && CustomerOwnerTypes.Contains(e.PartyType!), cancellationToken);
        var sold = ownership is not null;
        var decision = CorrectionApprovalRules.Evaluate(await approvals.GetAsync(cancellationToken), CorrectionCommand.CorrectItem, new(UnitSold: sold), committer.Roles);
        if (decision.Status != CorrectionApprovalStatus.Allowed)
        {
            var refusal = decision.Refusal ?? new CommandRefusal("approval_required", "CorrectItem requires approval.");
            throw new CommandRefusedException(refusal.Rule, refusal.Message, refusal.UnblockingStep);
        }

        var contractDate = DateOnly.FromDateTime(ownership?.ValidFrom ?? command.EventTime);
        var changes = await WarrantyPolicyChangesAsync(assignment.ItemId, command.CorrectedItemId, ownership?.PartyRef, contractDate, itemPolicies, contracts, cancellationToken);
        var flags = JsonSerializer.Deserialize<List<string>>(unit.OverlayFlags, LedgerJson.Options) ?? [];
        return new CorrectItemPlan(unit, current, assignment, profile, sold, sold, flags, decision,
            decision.ReviewTasks.Contains(YojanaRole.FinanceReview, StringComparer.Ordinal), changes);
    }

    private static async Task<IReadOnlyList<CorrectedItemWarrantyPolicy>> WarrantyPolicyChangesAsync(
        Guid previousItemId,
        Guid correctedItemId,
        Guid? ownerPartyRef,
        DateOnly contractDate,
        IItemPolicyPort itemPolicies,
        ICustomerContractPort contracts,
        CancellationToken cancellationToken)
    {
        var before = await SelectedPoliciesAsync(previousItemId, ownerPartyRef, contractDate, itemPolicies, contracts, cancellationToken);
        var after = await SelectedPoliciesAsync(correctedItemId, ownerPartyRef, contractDate, itemPolicies, contracts, cancellationToken);
        return before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(family => new CorrectedItemWarrantyPolicy(family, before.GetValueOrDefault(family), after.GetValueOrDefault(family)))
            .Where(change => !string.Equals(change.PreviousPolicyCode, change.CorrectedPolicyCode, StringComparison.Ordinal))
            .ToList();
    }

    private static async Task<Dictionary<string, string>> SelectedPoliciesAsync(
        Guid itemId,
        Guid? ownerPartyRef,
        DateOnly contractDate,
        IItemPolicyPort itemPolicies,
        ICustomerContractPort contracts,
        CancellationToken cancellationToken)
    {
        var selected = (await itemPolicies.PoliciesForAsync(itemId, cancellationToken))
            .GroupBy(policy => policy.CoverageFamily, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().PolicyCode, StringComparer.Ordinal);
        if (ownerPartyRef is not { } owner) return selected;

        var contractPolicies = (await contracts.ContractsForAsync(owner, itemId, contractDate, cancellationToken))
            .GroupBy(term => term.CoverageFamily, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(term => term.Priority).ThenByDescending(term => term.ValidFrom).First());
        foreach (var term in contractPolicies) selected[term.CoverageFamily] = term.PolicyCode;
        return selected;
    }
}
