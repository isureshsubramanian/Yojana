using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Commands;

// TIGHT COUPLING (P3-T02 correction audit projection)
// WHAT: this handler appends every corrected as-built fact and asks CorrectionLedger to add one correction_record in
// the same SerialisationDbContext transaction. WHY: a multi-parent correction counts once and its reversal link must
// never outlive the facts. WHAT BREAKS IF SEPARATED: KPI counts drift or corrects_correction_id can dangle. Keep the
// pairing behind CorrectionLedger, call Attributes on every fact, and call Record exactly once per command.

/// <summary>
/// P3-T05. Corrects effective-dated AssemblyLink history without deleting it. Planning is shared by preview and commit;
/// in particular, the named <c>child_effective_interval_overlap</c> rule checks the complete back-dated interval, not
/// merely the child's current parent.
/// </summary>
public static class CorrectAsBuiltHandler
{
    public static async Task<CorrectAsBuiltResult> Handle(
        CorrectAsBuilt command,
        CorrectAsBuiltPlanner planner,
        SerialisationDbContext db,
        IUnitEventWriter writer,
        CorrectionLedger corrections,
        CorrectionApprovalSettings approvalSettings,
        ITenantContext tenant,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var correctionId = command.Correction?.CorrectionId ?? command.CommandId;
        if (await db.Corrections.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenant.TenantId && c.CorrectionId == correctionId, cancellationToken) is { } replay)
        {
            if (replay.Command != CorrectionCommand.CorrectAsBuilt)
                throw new CommandRefusedException("correction_id_in_use", $"Correction id {correctionId} belongs to '{replay.Command}'.", "send a new correction id");
            return new(correctionId, replay.UnitCount, 0, [], true);
        }

        var plan = await planner.PlanAsync(command, cancellationToken);
        var metadata = await corrections.PrepareAsync(CorrectionCommand.CorrectAsBuilt, command.Correction, command.CommandId, command.Reason, command.Note,
            null, plan.Parents.Min(p => p.EffectiveAt), CorrectionInitiatingRole.ProductionSupervisor, cancellationToken);
        if (await CorrectAsBuiltApproval.RefusalAsync(command, metadata, plan.Parents.Any(p => p.HasShipped), approvalSettings, cancellationToken) is { } approvalRefusal)
            throw new CommandRefusedException(approvalRefusal.Rule, approvalRefusal.Message, approvalRefusal.UnblockingStep);

        return await ExecuteApprovedAsync(command, plan, metadata, db, writer, corrections, tenant, clock, true, cancellationToken);
    }

    // TIGHT COUPLING (P3-T06 LateSerialization composition)
    // WHAT: LateSerialization calls this executor after its own item-owner + inventory-controller policy has approved
    // the whole correction; this method deliberately does not evaluate CorrectAsBuilt's separate commit roles.
    // WHY: the ADR gives LateSerialization one approval policy and one KPI correction even when it captures kit links.
    // WHAT BREAKS IF SEPARATED: copying this writer would let late link rules drift; applying both policies would add
    // roles the ADR does not require. Keep the matching comment in LateSerializationService and never call this seam
    // without an already-approved outer correction.
    internal static async Task<CorrectAsBuiltResult> ExecuteApprovedAsync(
        CorrectAsBuilt command,
        CorrectAsBuiltPlan plan,
        CorrectionMetadata metadata,
        SerialisationDbContext db,
        IUnitEventWriter writer,
        CorrectionLedger corrections,
        ITenantContext tenant,
        IClock clock,
        bool recordCorrection,
        CancellationToken cancellationToken)
    {

        var recordedAt = clock.UtcNow;
        var drafts = new List<UnitEventDraft>();
        var results = new List<CorrectedAsBuiltParentResult>();
        var affectedChildren = new HashSet<Guid>();
        var originalOpenParents = await OpenParentsAsync(db, tenant.TenantId, plan.Parents.SelectMany(p => p.Changes).SelectMany(c => c.ChildUnitIds).Distinct().ToList(), cancellationToken);

        foreach (var parent in plan.Parents)
        {
            var eventId = TimeOrderedId.New(parent.EffectiveAt);
            var publishedChanges = new List<AsBuiltFactChange>();
            var installedIds = new List<Guid>();
            var removedIds = new List<Guid>();
            foreach (var change in parent.Changes)
            {
                var removed = change.Removed is null ? null : Snapshot(change.Removed);
                if (change.Removed is { } closed)
                {
                    closed.RemovedAt = parent.EffectiveAt;
                    closed.RemovingEventId = eventId;
                    removedIds.Add(closed.LinkId);
                    if (closed.ChildUnitId is { } childId)
                    {
                        affectedChildren.Add(childId);
                        drafts.Add(ChildFact(command, metadata, childId, parent.ParentUnitId, closed.LinkId, UnitEventType.Removed, parent.EffectiveAt,
                            new { change = change.Change, reason = command.Reason }));
                    }
                }

                AssemblyLink? installed = null;
                if (change.Installed is { } addition)
                {
                    installed = new AssemblyLink
                    {
                        TenantId = tenant.TenantId,
                        LinkId = change.InstalledLinkId!.Value,
                        ParentUnitId = parent.ParentUnitId,
                        ChildUnitId = addition.ChildUnitId,
                        ChildItemId = addition.ChildUnitId is null ? addition.ItemId : null,
                        ChildLotId = addition.ChildUnitId is null ? addition.LotId : null,
                        Quantity = addition.ChildUnitId is null ? addition.Quantity : null,
                        Relation = addition.Relation,
                        Slot = addition.Slot,
                        InstalledAt = parent.EffectiveAt,
                        RecordedAt = recordedAt,
                        RemovedAt = addition.EffectiveUntil,
                        InstallingEventId = eventId,
                        RemovingEventId = addition.EffectiveUntil is null ? null : eventId,
                        ThroughPhantomItemId = addition.ThroughPhantomItemId,
                        ReleaseStatus = addition.ReleaseStatus,
                        ReleaseDocRef = addition.ReleaseDocRef,
                    };
                    db.AssemblyLinks.Add(installed);
                    installedIds.Add(installed.LinkId);
                    if (installed.ChildUnitId is { } childId)
                    {
                        affectedChildren.Add(childId);
                        drafts.Add(ChildFact(command, metadata, childId, parent.ParentUnitId, installed.LinkId, UnitEventType.Installed, parent.EffectiveAt,
                            new { change = change.Change, slot = installed.Slot, relation = installed.Relation }));
                        if (installed.RemovedAt is { } removedAt)
                            drafts.Add(ChildFact(command, metadata, childId, parent.ParentUnitId, installed.LinkId, UnitEventType.Removed, removedAt,
                                new { change = change.Change, reason = "corrected_bounded_interval" }));
                    }
                }

                publishedChanges.Add(new(change.Change, removed, installed is null ? null : Snapshot(installed)));
            }

            var summary = Summary(parent.Changes);
            drafts.Add(new UnitEventDraft
            {
                UnitId = parent.ParentUnitId,
                EventTime = parent.EffectiveAt,
                EventType = UnitEventType.AsBuiltCorrected,
                PresetEventId = eventId,
                BizStep = "correcting_as_built",
                LegalEntityId = command.LegalEntityId,
                ActorRef = command.ActorRef,
                IdempotencyKey = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, parent.ParentUnitId, UnitEventType.AsBuiltCorrected, parent.EffectiveAt),
                Attributes = CorrectionLedger.Attributes(metadata, new
                {
                    command_id = command.CommandId,
                    change = summary,
                    effective_at = parent.EffectiveAt,
                    removed = parent.Changes.Count == 1 && parent.Changes[0].Removed is { } oneRemoved ? Snapshot(oneRemoved) : null,
                    installed = parent.Changes.Count == 1 ? publishedChanges[0].Installed : null,
                    changes = publishedChanges,
                }),
                Delta = null,
            });
            results.Add(new(parent.ParentUnitId, eventId, installedIds, removedIds));
        }

        await ApplyFinalChildStateAsync(db, tenant.TenantId, drafts, affectedChildren, originalOpenParents, plan, cancellationToken);
        await writer.AppendAsync(drafts, cancellationToken);
        if (recordCorrection)
            await corrections.RecordAsync(CorrectionCommand.CorrectAsBuilt, metadata, command.ActorRef, plan.Parents.Min(p => p.EffectiveAt), plan.Parents.Select(p => p.ParentUnitId).ToList(), cancellationToken);

        // P6 hook: Unit.AsBuiltCorrected is the release-record reissue trigger. Certificate work does not run here.
        return new(metadata.CorrectionId, plan.Parents.Count, plan.Parents.Sum(p => p.Changes.Count), results);
    }

    private static async Task<Dictionary<Guid, Guid?>> OpenParentsAsync(SerialisationDbContext db, TenantId tenantId, IReadOnlyList<Guid> childIds, CancellationToken cancellationToken)
    {
        var answer = childIds.ToDictionary(id => id, _ => (Guid?)null);
        foreach (var chunk in childIds.Chunk(2000))
        {
            var rows = await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenantId && l.ChildUnitId != null && chunk.Contains(l.ChildUnitId.Value) && l.RemovedAt == null)
                .Select(l => new { Child = l.ChildUnitId!.Value, l.ParentUnitId }).ToListAsync(cancellationToken);
            foreach (var row in rows) answer[row.Child] = row.ParentUnitId;
        }
        return answer;
    }

    private static async Task ApplyFinalChildStateAsync(SerialisationDbContext db, TenantId tenantId, List<UnitEventDraft> drafts, HashSet<Guid> childIds,
        IReadOnlyDictionary<Guid, Guid?> originalOpenParents, CorrectAsBuiltPlan plan, CancellationToken cancellationToken)
    {
        var finalParents = childIds.ToDictionary(id => id, _ => (Guid?)null);
        foreach (var chunk in childIds.Chunk(2000))
        {
            var existing = await db.AssemblyLinks.Where(l => l.TenantId == tenantId && l.ChildUnitId != null && chunk.Contains(l.ChildUnitId.Value)).ToListAsync(cancellationToken);
            foreach (var link in existing.Where(l => l.RemovedAt == null)) finalParents[link.ChildUnitId!.Value] = link.ParentUnitId;
        }
        foreach (var planned in plan.Parents.SelectMany(p => p.Changes.Select(c => (Parent: p.ParentUnitId, Change: c)))
            .Where(x => x.Change.Installed?.ChildUnitId is not null && x.Change.Installed.EffectiveUntil == null))
            finalParents[planned.Change.Installed!.ChildUnitId!.Value] = planned.Parent;

        foreach (var childId in childIds)
        {
            var finalParent = finalParents[childId];
            if (originalOpenParents.GetValueOrDefault(childId) == finalParent) continue;
            var index = drafts.FindLastIndex(d => d.UnitId == childId);
            if (index < 0) continue;
            drafts[index] = drafts[index] with { Delta = new CurrentStateDelta
            {
                Disposition = finalParent is null ? UnitDisposition.Available : UnitDisposition.InProgress,
                Attributes = new Dictionary<string, JsonElement?> { ["parent_unit_id"] = finalParent is null ? null : JsonSerializer.SerializeToElement(finalParent.Value) },
            } };
        }
    }

    private static UnitEventDraft ChildFact(CorrectAsBuilt command, CorrectionMetadata metadata, Guid childId, Guid parentId, Guid linkId, string eventType, DateTime at, object details) => new()
    {
        UnitId = childId,
        EventTime = at,
        EventType = eventType,
        BizStep = eventType == UnitEventType.Installed ? "installing" : "removing",
        LegalEntityId = command.LegalEntityId,
        ActorRef = command.ActorRef,
        IdempotencyKey = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, childId, eventType, at),
        Attributes = CorrectionLedger.Attributes(metadata, new { command_id = command.CommandId, parent_unit_id = parentId, link_id = linkId, details }),
    };

    private static object Snapshot(AssemblyLink link) => new
    {
        assembly_link_id = link.LinkId,
        child_unit_id = link.ChildUnitId,
        child_item_id = link.ChildItemId,
        child_lot_id = link.ChildLotId,
        quantity = link.Quantity,
        relation = link.Relation,
        slot = link.Slot,
    };

    private static string Summary(IReadOnlyList<PlannedAsBuiltChange> changes) =>
        changes.All(c => c.Change == AsBuiltCorrectionChange.Add) ? AsBuiltCorrectionChange.Add
        : changes.All(c => c.Change == AsBuiltCorrectionChange.Remove) ? AsBuiltCorrectionChange.Remove
        : AsBuiltCorrectionChange.Replace;
}

public static class PreviewCorrectAsBuiltHandler
{
    public static async Task<CorrectionPreview> Handle(PreviewCorrectAsBuilt preview, CorrectAsBuiltPlanner planner, CorrectionLedger corrections,
        CorrectionApprovalSettings approvalSettings, CancellationToken cancellationToken)
    {
        var command = preview.Command;
        try
        {
            var plan = await planner.PlanAsync(command, cancellationToken);
            var metadata = await corrections.PrepareAsync(CorrectionCommand.CorrectAsBuilt, command.Correction, command.CommandId, command.Reason, command.Note,
                null, plan.Parents.Min(p => p.EffectiveAt), CorrectionInitiatingRole.ProductionSupervisor, cancellationToken);
            var approvalRefusal = await CorrectAsBuiltApproval.RefusalAsync(command, metadata, plan.Parents.Any(p => p.HasShipped), approvalSettings, cancellationToken);
            var outcome = approvalRefusal is null ? UnitOutcome.Outcomes.Change : UnitOutcome.Outcomes.Block;
            var units = plan.Parents.Select(p => new UnitOutcome(p.ParentUnitId, p.ParentSerial, outcome, approvalRefusal?.Rule,
                $"{p.Changes.Count} as-built link change(s)", approvalRefusal?.UnblockingStep)).ToList();
            var events = new List<CorrectionEventPreview>();
            foreach (var parent in plan.Parents)
            {
                events.Add(new(parent.ParentUnitId, UnitEventType.AsBuiltCorrected, parent.EffectiveAt, $"{parent.Changes.Count} link change(s)"));
                foreach (var change in parent.Changes)
                {
                    if (change.Removed?.ChildUnitId is { } removedChild) events.Add(new(removedChild, UnitEventType.Removed, parent.EffectiveAt, $"from {parent.ParentSerial}"));
                    if (change.Installed?.ChildUnitId is { } installedChild)
                    {
                        events.Add(new(installedChild, UnitEventType.Installed, parent.EffectiveAt, $"in {parent.ParentSerial}"));
                        if (change.Installed.EffectiveUntil is { } until) events.Add(new(installedChild, UnitEventType.Removed, until, $"from {parent.ParentSerial}"));
                    }
                }
            }
            var changed = approvalRefusal is null ? units.Count : 0;
            return new(null, CorrectionCommand.CorrectAsBuilt, units.Count, changed, 0, units.Count - changed, units, events, [], [], [], approvalRefusal);
        }
        catch (CommandRefusedException ex)
        {
            return new(null, CorrectionCommand.CorrectAsBuilt, command.Targets.Count, 0, 0, command.Targets.Count, [], [], [], [], [], ex.Refusal);
        }
    }
}

internal static class CorrectAsBuiltApproval
{
    public static async Task<CommandRefusal?> RefusalAsync(CorrectAsBuilt command, CorrectionMetadata metadata, bool shipped, CorrectionApprovalSettings settings, CancellationToken cancellationToken)
    {
        var committer = command.CommittedBy ?? new CorrectionCommitter(command.ActorRef, []);
        var decision = CorrectionApprovalRules.Evaluate(await settings.GetAsync(cancellationToken), CorrectionCommand.CorrectAsBuilt,
            new CorrectionApprovalFacts(UnitHasShipped: shipped), committer.Roles);
        if (decision.Status == CorrectionApprovalStatus.Refused)
            return decision.Refusal;
        if (decision.Status != CorrectionApprovalStatus.NeedsApproval) return null;

        var approved = (metadata.Approvers ?? []).Any(a => !string.Equals(a.UserRef, committer.UserRef, StringComparison.Ordinal)
            && a.Roles.Intersect(decision.ApproverRoles, StringComparer.Ordinal).Any());
        return approved ? null : decision.Refusal;
    }
}

public sealed class CorrectAsBuiltPlanner(SerialisationDbContext db, UnitResolver resolver, ITenantContext tenant, IClock clock)
{
    internal async Task<CorrectAsBuiltPlan> PlanAsync(CorrectAsBuilt command, CancellationToken cancellationToken)
    {
        CorrectionRules.ValidateReason(CorrectionCommand.CorrectAsBuilt, command.Reason);
        if (command.Targets.Count == 0) throw new CommandRefusedException("nothing_to_correct", "CorrectAsBuilt needs at least one parent.");
        var parents = new List<PlannedAsBuiltParent>();
        var seenParents = new HashSet<Guid>();
        var removedIds = new HashSet<Guid>();
        foreach (var target in command.Targets)
        {
            if (target.EffectiveAt > clock.UtcNow) throw new CommandRefusedException("effective_time_in_future", "An as-built correction is back-dated to a time that has already occurred.", "enter when the component was physically fitted");
            if (target.Changes.Count == 0) throw new CommandRefusedException("nothing_to_correct", "Every parent needs at least one link change.");
            var parent = await ResolveAsync(target.Parent, "parent", cancellationToken);
            if (!seenParents.Add(parent.UnitId)) throw new CommandRefusedException("parent_repeated", $"Parent {parent.UnitId} appears more than once; group its component changes under one target.");
            if (parent.LifecycleStatus is LifecycleStatus.Void or LifecycleStatus.Scrapped or LifecycleStatus.ScrappedPreRelease or LifecycleStatus.Decommissioned or LifecycleStatus.Merged)
                throw new CommandRefusedException("parent_not_correctable", $"Parent unit {parent.UnitId} is {parent.LifecycleStatus}.");

            var changes = new List<PlannedAsBuiltChange>();
            foreach (var input in target.Changes)
            {
                if (!AsBuiltCorrectionChange.All.Contains(input.Change)) throw new CommandRefusedException("asbuilt_change_unknown", $"'{input.Change}' is not add, remove or replace.");
                var needsRemoved = input.Change is AsBuiltCorrectionChange.Remove or AsBuiltCorrectionChange.Replace;
                var needsInstalled = input.Change is AsBuiltCorrectionChange.Add or AsBuiltCorrectionChange.Replace;
                if (needsRemoved != (input.RemovedLinkId is not null) || needsInstalled != (input.Installed is not null))
                    throw new CommandRefusedException("asbuilt_change_invalid", $"A '{input.Change}' change has the wrong removed or installed side.");

                AssemblyLink? removed = null;
                if (input.RemovedLinkId is { } removedId)
                {
                    if (!removedIds.Add(removedId)) throw new CommandRefusedException("link_repeated", $"Assembly link {removedId} is removed more than once.");
                    removed = await db.AssemblyLinks.FirstOrDefaultAsync(l => l.TenantId == tenant.TenantId && l.LinkId == removedId && l.ParentUnitId == parent.UnitId, cancellationToken)
                        ?? throw new CommandRefusedException("link_unknown", $"Link {removedId} is not a link of parent {parent.UnitId}.");
                    if (target.EffectiveAt < removed.InstalledAt) throw new CommandRefusedException("removal_before_install", $"Link {removedId} was installed after the proposed removal time.", "enter a removal time on or after its fit time");
                    if (removed.RemovedAt is { } wasRemoved && target.EffectiveAt >= wasRemoved)
                        throw new CommandRefusedException("link_not_effective_at", $"Link {removedId} was already removed at {wasRemoved:O}.", "choose a time while the link was effective");
                }

                var installed = needsInstalled ? await PrepareInstalledAsync(input.Installed!, parent.UnitId, target.EffectiveAt, cancellationToken) : null;
                changes.Add(new(input.Change, removed, installed, installed is null ? null : TimeOrderedId.New(target.EffectiveAt)));
            }

            var shipped = await db.Events.AsNoTracking().AnyAsync(e => e.TenantId == tenant.TenantId && e.UnitId == parent.UnitId && e.EventType == UnitEventType.Shipped, cancellationToken);
            parents.Add(new(parent.UnitId, parent.NormalisedValue, target.EffectiveAt, changes, shipped));
        }

        var plan = new CorrectAsBuiltPlan(parents);
        await ValidateIntervalsAsync(plan, cancellationToken);
        await ValidateCyclesAsync(plan, cancellationToken);
        return plan;
    }

    private async Task<PlannedInstalledComponent> PrepareInstalledAsync(CorrectedAsBuiltComponent input, Guid parentId, DateTime effectiveAt, CancellationToken cancellationToken)
    {
        if (input.EffectiveUntil is { } until && (until <= effectiveAt || until > clock.UtcNow))
            throw new CommandRefusedException("effective_interval_invalid", "A corrected component interval must end after it starts and not in the future.");
        if (!new[] { AssemblyRelation.Component, AssemblyRelation.Kit, AssemblyRelation.CoProductSibling }.Contains(input.Relation))
            throw new CommandRefusedException("assembly_relation_unknown", $"Assembly relation '{input.Relation}' is not known.");
        if (input.ReleaseStatus is not null && !new[] { ReleaseStatus.New, ReleaseStatus.Repaired, ReleaseStatus.Overhauled, ReleaseStatus.RebuiltOemOnly }.Contains(input.ReleaseStatus))
            throw new CommandRefusedException("release_status_unknown", $"Release status '{input.ReleaseStatus}' is not known.");

        var serialised = input.Unit is not null;
        if (serialised == (input.ItemId is not null || input.LotId is not null || input.Quantity is not null))
            throw new CommandRefusedException("component_input_invalid", "A component is either a unit reference, or an item with a positive quantity.");
        if (!serialised && (input.ItemId is null || input.Quantity is not > 0))
            throw new CommandRefusedException("component_input_invalid", "A lot or non-serialised component needs ItemId and a positive Quantity.");
        if (!serialised) return new(null, input.ItemId, input.LotId, input.Quantity, input.Relation, input.Slot, input.ThroughPhantomItemId, input.ReleaseStatus ?? ReleaseStatus.New, input.ReleaseDocRef, input.EffectiveUntil);

        var child = await ResolveAsync(input.Unit!, "component", cancellationToken);
        if (child.UnitId == parentId) throw new CommandRefusedException("assembly_cycle", $"Unit {parentId} cannot contain itself.", "check the parent and component serials");
        if (child.LifecycleStatus is LifecycleStatus.Void or LifecycleStatus.Scrapped or LifecycleStatus.ScrappedPreRelease or LifecycleStatus.Decommissioned or LifecycleStatus.Merged)
            throw new CommandRefusedException("child_not_installable", $"Child unit {child.UnitId} is {child.LifecycleStatus}.");
        return new(child.UnitId, null, null, null, input.Relation, input.Slot, input.ThroughPhantomItemId, input.ReleaseStatus ?? ReleaseStatus.New, input.ReleaseDocRef, input.EffectiveUntil);
    }

    private async Task<ResolvedUnit> ResolveAsync(AsBuiltUnitReference reference, string kind, CancellationToken cancellationToken)
    {
        if ((reference.UnitId is null) == string.IsNullOrWhiteSpace(reference.Serial))
            throw new CommandRefusedException("unit_reference_invalid", $"A {kind} is named by either UnitId or Serial, not both.");
        if (reference.UnitId is { } id)
        {
            var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.TenantId == tenant.TenantId && u.UnitId == id, cancellationToken)
                ?? throw new CommandRefusedException($"{kind}_unknown", $"{kind} unit {id} does not exist.");
            var serial = await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenant.TenantId && i.UnitId == id && i.ValidTo == null)
                .OrderBy(i => i.Type == IdentifierType.InternalSerial ? 0 : 1).Select(i => i.NormalisedValue).FirstOrDefaultAsync(cancellationToken) ?? id.ToString();
            return new(id, Guid.Empty, unit.LifecycleStatus, serial, reference.IdentifierType ?? "");
        }

        return await resolver.ResolveAsync(new DocumentSerial(reference.Serial!.Trim(), reference.IssuerPartyRef, reference.IdentifierType), null, cancellationToken)
            ?? throw new CommandRefusedException($"{kind}_unknown", $"No unit has serial '{reference.Serial}'.", "commission or identify the unit first");
    }

    private async Task ValidateIntervalsAsync(CorrectAsBuiltPlan plan, CancellationToken cancellationToken)
    {
        var candidates = plan.Parents.SelectMany(p => p.Changes.Where(c => c.Installed?.ChildUnitId is not null)
            .Select(c => new LinkInterval(c.Installed!.ChildUnitId!.Value, p.ParentUnitId, c.InstalledLinkId!.Value, p.EffectiveAt, c.Installed.EffectiveUntil))).ToList();
        var revisedEnds = plan.Parents.SelectMany(p => p.Changes.Where(c => c.Removed is not null).Select(c => (c.Removed!.LinkId, End: (DateTime?)p.EffectiveAt))).ToDictionary(x => x.LinkId, x => x.End);
        var childIds = candidates.Select(c => c.ChildId).Distinct().ToList();
        var existing = new List<AssemblyLink>();
        foreach (var chunk in childIds.Chunk(2000))
            existing.AddRange(await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenant.TenantId && l.ChildUnitId != null && chunk.Contains(l.ChildUnitId.Value)).ToListAsync(cancellationToken));

        foreach (var candidate in candidates)
        {
            foreach (var link in existing.Where(l => l.ChildUnitId == candidate.ChildId))
            {
                var end = revisedEnds.GetValueOrDefault(link.LinkId, link.RemovedAt);
                if (EffectiveIntervalsOverlap(candidate.Start, candidate.End, link.InstalledAt, end))
                    throw Overlap(candidate, link.ParentUnitId, link.LinkId, link.InstalledAt, end);
            }
            foreach (var other in candidates.Where(c => c.LinkId != candidate.LinkId && c.ChildId == candidate.ChildId))
                if (EffectiveIntervalsOverlap(candidate.Start, candidate.End, other.Start, other.End)) throw Overlap(candidate, other.ParentId, other.LinkId, other.Start, other.End);
        }
    }

    private async Task ValidateCyclesAsync(CorrectAsBuiltPlan plan, CancellationToken cancellationToken)
    {
        var pending = plan.Parents.SelectMany(p => p.Changes.Where(c => c.Installed?.ChildUnitId is not null)
            .Select(c => new LinkInterval(c.Installed!.ChildUnitId!.Value, p.ParentUnitId, c.InstalledLinkId!.Value, p.EffectiveAt, c.Installed.EffectiveUntil))).ToList();
        var revisedEnds = plan.Parents.SelectMany(p => p.Changes.Where(c => c.Removed is not null).Select(c => (c.Removed!.LinkId, End: (DateTime?)p.EffectiveAt))).ToDictionary(x => x.LinkId, x => x.End);
        foreach (var candidate in pending)
        {
            var queue = new Queue<(Guid UnitId, DateTime Start, DateTime? End)>();
            queue.Enqueue((candidate.ParentId, candidate.Start, candidate.End));
            var visited = new HashSet<(Guid, DateTime, DateTime?)>();
            while (queue.TryDequeue(out var state) && visited.Add(state))
            {
                if (state.UnitId == candidate.ChildId)
                    throw new CommandRefusedException("assembly_cycle", $"Installing unit {candidate.ChildId} in {candidate.ParentId} creates an effective-dated assembly cycle.", "correct the ancestor link first");
                var ancestors = await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenant.TenantId && l.ChildUnitId == state.UnitId).ToListAsync(cancellationToken);
                foreach (var link in ancestors)
                {
                    var end = revisedEnds.GetValueOrDefault(link.LinkId, link.RemovedAt);
                    if (Intersection(state.Start, state.End, link.InstalledAt, end) is { } overlap) queue.Enqueue((link.ParentUnitId, overlap.Start, overlap.End));
                }
                foreach (var link in pending.Where(l => l.ChildId == state.UnitId && l.LinkId != candidate.LinkId))
                    if (Intersection(state.Start, state.End, link.Start, link.End) is { } overlap) queue.Enqueue((link.ParentId, overlap.Start, overlap.End));
            }
        }
    }

    /// <summary>Half-open effective intervals: a removal exactly when the next fit starts is not an overlap.</summary>
    public static bool EffectiveIntervalsOverlap(DateTime aStart, DateTime? aEnd, DateTime bStart, DateTime? bEnd) =>
        (aEnd is null || bStart < aEnd) && (bEnd is null || aStart < bEnd);

    private static (DateTime Start, DateTime? End)? Intersection(DateTime aStart, DateTime? aEnd, DateTime bStart, DateTime? bEnd)
    {
        var start = aStart > bStart ? aStart : bStart;
        var end = aEnd is null ? bEnd : bEnd is null ? aEnd : aEnd < bEnd ? aEnd : bEnd;
        return end is null || start < end ? (start, end) : null;
    }

    private static CommandRefusedException Overlap(LinkInterval candidate, Guid otherParent, Guid otherLink, DateTime otherStart, DateTime? otherEnd) =>
        new("child_effective_interval_overlap",
            $"Child {candidate.ChildId} in parent {candidate.ParentId} from {candidate.Start:O} overlaps link {otherLink} in parent {otherParent} ({otherStart:O} to {(otherEnd is null ? "open" : otherEnd.Value.ToString("O"))}).",
            "correct or close the overlapping AssemblyLink interval first");
}

internal sealed record CorrectAsBuiltPlan(IReadOnlyList<PlannedAsBuiltParent> Parents);
internal sealed record PlannedAsBuiltParent(Guid ParentUnitId, string ParentSerial, DateTime EffectiveAt, IReadOnlyList<PlannedAsBuiltChange> Changes, bool HasShipped);
internal sealed record PlannedAsBuiltChange(string Change, AssemblyLink? Removed, PlannedInstalledComponent? Installed, Guid? InstalledLinkId)
{
    public IEnumerable<Guid> ChildUnitIds
    {
        get
        {
            if (Removed?.ChildUnitId is { } removed) yield return removed;
            if (Installed?.ChildUnitId is { } installed) yield return installed;
        }
    }
}
internal sealed record PlannedInstalledComponent(Guid? ChildUnitId, Guid? ItemId, Guid? LotId, decimal? Quantity, string Relation, string? Slot,
    Guid? ThroughPhantomItemId, string ReleaseStatus, string? ReleaseDocRef, DateTime? EffectiveUntil);
internal sealed record LinkInterval(Guid ChildId, Guid ParentId, Guid LinkId, DateTime Start, DateTime? End);
internal sealed record AsBuiltFactChange(string Change, object? Removed, object? Installed);
