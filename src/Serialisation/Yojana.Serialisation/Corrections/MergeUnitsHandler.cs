using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Search;

namespace Yojana.Serialisation.Corrections;

public static class PreviewMergeUnitsHandler
{
    public static async Task<MergeUnitsPreviewResult> Handle(PreviewMergeUnits command, SerialisationDbContext db, CorrectionApprovalSettings settings, ITenantContext tenant, CancellationToken cancellationToken)
    {
        if (command.FirstUnitId == command.SecondUnitId) throw new CommandRefusedException("cannot_merge_unit_into_itself", "A unit cannot be merged into itself.", "select two different unit records");
        var tenantId = tenant.TenantId;
        var units = await db.Units.AsNoTracking().Where(u => u.TenantId == tenantId && (u.UnitId == command.FirstUnitId || u.UnitId == command.SecondUnitId)).ToListAsync(cancellationToken);
        if (units.Count != 2) throw new CommandRefusedException("unit_unknown", "Both unit records must exist in this tenant.");
        var commissioned = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && (e.UnitId == command.FirstUnitId || e.UnitId == command.SecondUnitId) &&
                (e.EventType == UnitEventType.Commissioned || e.EventType == UnitEventType.CreatedPendingIdentifier))
            .GroupBy(e => e.UnitId).Select(g => new { UnitId = g.Key, At = g.Min(e => e.EventTime) }).ToListAsync(cancellationToken);
        var candidates = units.Select(u => (u.UnitId, commissioned.FirstOrDefault(e => e.UnitId == u.UnitId)?.At ?? u.CreatedAt)).ToList();
        var recommended = MergeUnitsRules.RecommendedSurvivor(candidates);
        var survivorId = command.SurvivorUnitId ?? recommended;
        if (survivorId != command.FirstUnitId && survivorId != command.SecondUnitId) throw new CommandRefusedException("survivor_not_in_pair", "The survivor must be one of the two previewed units.");
        var absorbedId = survivorId == command.FirstUnitId ? command.SecondUnitId : command.FirstUnitId;
        var survivor = units.Single(u => u.UnitId == survivorId);
        var absorbed = units.Single(u => u.UnitId == absorbedId);
        var refusal = MergeUnitsRules.PairProblem(survivorId, absorbedId, survivor.LifecycleStatus, absorbed.LifecycleStatus)
            ?? await DifferentShippedPartyRefusal(db, tenantId, survivorId, absorbedId, cancellationToken);
        var live = await db.Entitlements.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && (e.UnitId == survivorId || e.UnitId == absorbedId) && e.Status != EntitlementStatus.Closed, cancellationToken);
        var policy = await settings.GetAsync(cancellationToken);
        var roles = command.CommittedBy?.Roles ?? [];
        var facts = new CorrectionApprovalFacts(UnitHasLiveEntitlement: live);
        var approval = CorrectionApprovalRules.Evaluate(policy, CorrectionCommand.MergeUnits, facts, roles);
        var approvalRefusal = command.CommittedBy is { } committer
            && CorrectionApprovalRules.ApprovalsSatisfied(policy, CorrectionCommand.MergeUnits, facts, committer, command.Approvers ?? [])
                ? null
                : approval.Refusal;
        var count = await CountRowsAsync(db, tenantId, absorbedId, cancellationToken);
        var events = new List<CorrectionEventPreview>
        {
            new(survivorId, UnitEventType.Merged, command.EventTime, $"survives {absorbedId}"),
            new(absorbedId, UnitEventType.Merged, command.EventTime, $"alias of {survivorId}"),
        };
        var outcomes = refusal is null
            ? new[] { new UnitOutcome(absorbedId, null, UnitOutcome.Outcomes.Change, "merge", $"re-point {count} rows to {survivorId}") }
            : new[] { new UnitOutcome(absorbedId, null, UnitOutcome.Outcomes.Block, refusal.Rule, refusal.Message, refusal.UnblockingStep) };
        var entitlementPreview = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == absorbedId)
            .Select(e => new CorrectionEntitlementPreview(absorbedId, "re-point", e.EntitlementId, e.CoverageFamily)).ToListAsync(cancellationToken);
        var certificates = new List<CorrectionCertificatePreview> { new(survivorId, "release_record", "new_version", "merged_identifier_sets") };
        if (live) certificates.Add(new(survivorId, "warranty_certificate", "new_version", "live_entitlements_merged"));
        var effectiveRefusal = refusal ?? approvalRefusal;
        var preview = new CorrectionPreview(null, CorrectionCommand.MergeUnits, 2, effectiveRefusal is null ? 1 : 0, 0, effectiveRefusal is null ? 0 : 1,
            effectiveRefusal is null ? outcomes : [new UnitOutcome(absorbedId, null, UnitOutcome.Outcomes.Block, effectiveRefusal.Rule, effectiveRefusal.Message, effectiveRefusal.UnblockingStep)],
            events, entitlementPreview, certificates, [], effectiveRefusal);
        var duplicate = await DuplicateAlternativeAsync(db, tenantId, survivorId, absorbedId, cancellationToken);
        return new(recommended, survivorId, absorbedId, preview, approval,
        [
            new(MergeUnitsResolution.Merge, refusal is null, "The records are one physical unit; keep the named survivor and leave an alias.", refusal),
            duplicate,
        ]);
    }

    internal static async Task<CommandRefusal?> DifferentShippedPartyRefusal(SerialisationDbContext db, TenantId tenantId, Guid first, Guid second, CancellationToken cancellationToken)
    {
        var rows = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && (e.UnitId == first || e.UnitId == second) && e.EventType == UnitEventType.Shipped && e.Attributes != null)
            .Select(e => new { e.UnitId, e.Attributes }).ToListAsync(cancellationToken);
        var parties = rows.GroupBy(e => e.UnitId).ToDictionary(g => g.Key, g => (IReadOnlyCollection<Guid>)g.SelectMany(e => ShippedParties(e.Attributes!)).Distinct().ToList());
        if (MergeUnitsRules.DifferentShippedParties(parties.GetValueOrDefault(first, []), parties.GetValueOrDefault(second, [])))
            return new("shipped_to_different_parties", "Both records shipped to different parties, so they represent two physical units.", "keep both units and acknowledge a genuine vendor-serial collision when that is the duplicate");
        return null;
    }

    private static IEnumerable<Guid> ShippedParties(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.Object && to.TryGetProperty("party_ref", out var party) && party.ValueKind == JsonValueKind.String && party.TryGetGuid(out var id)) yield return id;
    }

    private static async Task<MergeUnitsAlternative> DuplicateAlternativeAsync(SerialisationDbContext db, TenantId tenantId, Guid first, Guid second, CancellationToken cancellationToken)
    {
        var ids = await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && (i.UnitId == first || i.UnitId == second) && i.Type == IdentifierType.VendorSerial && i.ValidTo == null).ToListAsync(cancellationToken);
        var available = ids.Count > 0;
        var refusal = available ? null : new CommandRefusal("vendor_serial_required", "A duplicate acknowledgement needs an open vendor serial on at least one record.", "capture the vendor serial and preview again");
        return new(MergeUnitsResolution.AcknowledgeVendorSerialDuplicate, available, "The records are different physical units; keep both and acknowledge the vendor-scoped collision.", refusal);
    }

    private static async Task<int> CountRowsAsync(SerialisationDbContext db, TenantId tenantId, Guid unitId, CancellationToken cancellationToken) =>
        await db.Events.CountAsync(e => e.TenantId == tenantId && e.UnitId == unitId, cancellationToken)
        + await db.ArchivedEvents.CountAsync(e => e.TenantId == tenantId && e.UnitId == unitId, cancellationToken)
        + await db.Identifiers.CountAsync(e => e.TenantId == tenantId && e.UnitId == unitId, cancellationToken)
        + await db.ItemAssignments.CountAsync(e => e.TenantId == tenantId && e.UnitId == unitId, cancellationToken)
        + await db.Episodes.CountAsync(e => e.TenantId == tenantId && e.UnitId == unitId, cancellationToken)
        + await db.Entitlements.CountAsync(e => e.TenantId == tenantId && e.UnitId == unitId, cancellationToken)
        + await db.BuildRecords.CountAsync(e => e.TenantId == tenantId && e.UnitId == unitId, cancellationToken)
        + await db.AssemblyLinks.CountAsync(e => e.TenantId == tenantId && (e.ParentUnitId == unitId || e.ChildUnitId == unitId), cancellationToken)
        + await db.EntitlementRequirements.CountAsync(e => e.TenantId == tenantId && e.UnitId == unitId, cancellationToken)
        + await db.SelectionSetMembers.CountAsync(e => e.TenantId == tenantId && e.UnitId == unitId, cancellationToken);
}

// TIGHT COUPLING (P3-T09 reversible re-point)
// WHAT: this handler changes every unit-owned row and writes UnitMergeRecord in the same SerialisationDbContext unit of
// work. WHY: the alias is useful only when every reference moves atomically, and a split needs the exact secondary state
// that uniqueness conflicts forced the merge to close or renumber. WHAT BREAKS IF SEPARATED: partial identity histories,
// dangling aliases, or a reversal that cannot restore the pre-merge shape. UnitMergeRecord documents the same coupling.
// HIDDEN COUPLING with UnitCurrentHealer: moved history keeps its original projection delta, so the healer must ignore
// deltas on rows with merged_from_unit_id while still advancing to their seq. Applying a second commissioning delta
// would replace the survivor's state; dropping the rows would make the absorbed history unreachable.
public static class MergeUnitsHandler
{
    public static async Task<MergeUnitsResult> Handle(MergeUnits command, SerialisationDbContext db, IUnitEventWriter writer, CorrectionLedger corrections,
        CorrectionApprovalSettings approvalSettings, IdentifierService identifiers, IMessageBus bus, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        if (!MergeUnitsResolution.All.Contains(command.Resolution)) throw new CommandRefusedException("resolution_unknown", $"'{command.Resolution}' is not a MergeUnits resolution.");
        if (command.SurvivorUnitId == command.AbsorbedUnitId) throw new CommandRefusedException("cannot_merge_unit_into_itself", "A unit cannot be merged into itself.", "select two different unit records");
        CorrectionRules.ValidateReason(CorrectionCommand.MergeUnits, command.Reason);
        var tenantId = tenant.TenantId;
        var correctionId = command.Correction?.CorrectionId ?? command.CommandId;
        if (await db.Corrections.AsNoTracking().AnyAsync(c => c.TenantId == tenantId && c.CorrectionId == correctionId, cancellationToken))
            return new(command.SurvivorUnitId, command.AbsorbedUnitId, command.Resolution, correctionId, 0, false, true);

        var units = await db.Units.Where(u => u.TenantId == tenantId && (u.UnitId == command.SurvivorUnitId || u.UnitId == command.AbsorbedUnitId)).ToListAsync(cancellationToken);
        if (units.Count != 2) throw new CommandRefusedException("unit_unknown", "Both unit records must exist in this tenant.");
        var survivor = units.Single(u => u.UnitId == command.SurvivorUnitId);
        var absorbed = units.Single(u => u.UnitId == command.AbsorbedUnitId);
        var corrects = command.Correction?.CorrectsCorrectionId;
        var reversing = corrects is { } priorId
            ? await db.UnitMerges.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.CorrectionId == priorId, cancellationToken)
            : null;
        if (corrects is not null && reversing is null)
            throw new CommandRefusedException("merge_reversal_mismatch", "The correction named is not a merge of this survivor and alias.", "select the active merge from the alias history");
        if (reversing is null)
        {
            if (MergeUnitsRules.PairProblem(survivor.UnitId, absorbed.UnitId, survivor.LifecycleStatus, absorbed.LifecycleStatus) is { } problem) throw new CommandRefusedException(problem.Rule, problem.Message, problem.UnblockingStep);
            if (await PreviewMergeUnitsHandler.DifferentShippedPartyRefusal(db, tenantId, survivor.UnitId, absorbed.UnitId, cancellationToken) is { } shipped) throw new CommandRefusedException(shipped.Rule, shipped.Message, shipped.UnblockingStep);
        }
        else if (reversing.SurvivorUnitId != survivor.UnitId || reversing.AbsorbedUnitId != absorbed.UnitId || reversing.ReversedAt is not null)
        {
            throw new CommandRefusedException("merge_reversal_mismatch", "The correction named is not the active merge of this survivor and alias.", "select the active merge from the alias history");
        }

        var live = await db.Entitlements.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && (e.UnitId == survivor.UnitId || e.UnitId == absorbed.UnitId) && e.Status != EntitlementStatus.Closed, cancellationToken);
        var metadata = await corrections.PrepareAsync(CorrectionCommand.MergeUnits, command.Correction, command.CommandId, command.Reason, command.Note, null,
            command.EventTime, CorrectionInitiatingRole.SerialAdministrator, cancellationToken);
        await DemandApprovalAsync(command, metadata, live, approvalSettings, cancellationToken);

        if (command.Resolution == MergeUnitsResolution.AcknowledgeVendorSerialDuplicate)
            return await AcknowledgeDuplicateAsync(command, metadata, survivor, absorbed, db, writer, corrections, identifiers, clock, cancellationToken);

        var result = reversing is null
            ? await MergeAsync(command, metadata, survivor, absorbed, db, writer, corrections, tenantId, clock, cancellationToken)
            : await ReverseAsync(command, metadata, survivor, absorbed, reversing, db, writer, corrections, tenantId, clock, cancellationToken);
        await bus.PublishAsync(new SearchReindexRequested(result.Reversed ? [survivor.UnitId, absorbed.UnitId] : [survivor.UnitId]), new DeliveryOptions { TenantId = tenantId.ToString() });
        return result;
    }

    private static async Task DemandApprovalAsync(MergeUnits command, CorrectionMetadata metadata, bool live, CorrectionApprovalSettings settings, CancellationToken cancellationToken)
    {
        var committer = command.CommittedBy ?? throw new CommandRefusedException("committer_required", "MergeUnits names who commits it and the roles they hold.");
        var policy = await settings.GetAsync(cancellationToken);
        var rule = policy.Commands.FirstOrDefault(r => r.Command == CorrectionCommand.MergeUnits)
            ?? throw new CommandRefusedException("approval_policy_missing", "MergeUnits has no tenant approval rule.", "add it to the tenant correction approval policy");
        if (!committer.Roles.Intersect(rule.CommitRoles, StringComparer.Ordinal).Any())
            throw new CommandRefusedException("approval_required", $"MergeUnits is committed by {string.Join(" or ", rule.CommitRoles)}.", "route the identical preview to an authorised committer");
        var decision = CorrectionApprovalRules.Evaluate(policy, CorrectionCommand.MergeUnits, new(UnitHasLiveEntitlement: live), committer.Roles);
        var approvers = (metadata.Approvers ?? []).Where(a => a.UserRef != committer.UserRef).ToList();
        foreach (var role in decision.ApproverRoles)
            if (!approvers.Any(a => a.Roles.Contains(role, StringComparer.Ordinal)))
                throw new CommandRefusedException("approval_required", $"MergeUnits needs a second approver with {role}.", $"route the identical preview to a {role}");
    }

    private static async Task<MergeUnitsResult> AcknowledgeDuplicateAsync(MergeUnits command, CorrectionMetadata metadata, Unit first, Unit second,
        SerialisationDbContext db, IUnitEventWriter writer, CorrectionLedger corrections, IdentifierService identifiers, IClock clock, CancellationToken cancellationToken)
    {
        if (metadata.CorrectsCorrectionId is not null) throw new CommandRefusedException("duplicate_acknowledgement_not_reversal", "A vendor-serial acknowledgement does not split a prior merge.");
        var tenantId = first.TenantId;
        var rows = await db.Identifiers.Where(i => i.TenantId == tenantId && (i.UnitId == first.UnitId || i.UnitId == second.UnitId) && i.Type == IdentifierType.VendorSerial && i.ValidTo == null).ToListAsync(cancellationToken);
        var value = command.VendorSerialValue;
        var source = rows.FirstOrDefault(i => value is null || i.RawValue == value || i.NormalisedValue == value)
            ?? throw new CommandRefusedException("vendor_serial_required", "The named vendor serial is not open on either unit.", "capture the vendor serial and preview again");
        var targetId = source.UnitId == first.UnitId ? second.UnitId : first.UnitId;
        var target = rows.FirstOrDefault(i => i.UnitId == targetId);
        if (target is not null && target.NormalisedValue != source.NormalisedValue)
            throw new CommandRefusedException("target_has_other_vendor_serial", "The other unit already has a different open vendor serial.", "correct that identifier first");
        var eventId = TimeOrderedId.New(command.EventTime);
        var created = target is null;
        if (target is null)
        {
            var item = await db.ItemAssignments.AsNoTracking().Where(a => a.TenantId == tenantId && a.UnitId == targetId && a.ValidTo == null).Select(a => (Guid?)a.ItemId).FirstOrDefaultAsync(cancellationToken);
            var prepared = identifiers.Prepare(new(IdentifierType.VendorSerial, source.RawValue, source.IssuerPartyRef, source.Qualifier, source.EnterpriseIdentifier), item, null);
            target = prepared.ToRow(tenantId, targetId, command.EventTime, command.ActorRef, clock.UtcNow, eventId);
            target.DuplicateAcknowledged = true;
            db.Identifiers.Add(target);
        }
        source.DuplicateAcknowledged = true;
        target.DuplicateAcknowledged = true;
        var drafts = new[] { first.UnitId, second.UnitId }.Select(unitId => new UnitEventDraft
        {
            UnitId = unitId, EventTime = command.EventTime, EventType = UnitEventType.VendorSerialDuplicateAcknowledged, LegalEntityId = command.LegalEntityId,
            ActorRef = command.ActorRef, IdempotencyKey = IdempotencyKey.ForCommand("merge-units", command.CommandId, unitId, UnitEventType.VendorSerialDuplicateAcknowledged, command.EventTime),
            PresetEventId = created && unitId == targetId ? eventId : null,
            Attributes = CorrectionLedger.Attributes(metadata, new { command_id = command.CommandId, resolution = command.Resolution, vendor_serial = source.NormalisedValue, counterpart_unit_id = unitId == first.UnitId ? second.UnitId : first.UnitId }),
        }).ToList();
        await writer.AppendAsync(drafts, cancellationToken);
        await corrections.RecordAsync(CorrectionCommand.MergeUnits, metadata, command.ActorRef, command.EventTime, [first.UnitId, second.UnitId], cancellationToken);
        return new(first.UnitId, second.UnitId, command.Resolution, metadata.CorrectionId, created ? 1 : 2, false, false);
    }

    private static async Task<MergeUnitsResult> MergeAsync(MergeUnits command, CorrectionMetadata metadata, Unit survivor, Unit absorbed, SerialisationDbContext db,
        IUnitEventWriter writer, CorrectionLedger corrections, TenantId tenantId, IClock clock, CancellationToken cancellationToken)
    {
        var survivorId = survivor.UnitId;
        var absorbedId = absorbed.UnitId;
        var openFamilies = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId && (e.UnitId == survivorId || e.UnitId == absorbedId) && e.Status != EntitlementStatus.Closed)
            .GroupBy(e => e.CoverageFamily).Where(g => g.Select(e => e.UnitId).Distinct().Count() > 1).Select(g => g.Key).ToListAsync(cancellationToken);
        if (openFamilies.Count > 0) throw new CommandRefusedException("live_entitlement_family_conflict", $"Both records carry live cover in {string.Join(", ", openFamilies)}.", "supersede or close the duplicate cover before merging; no entitlement is discarded by MergeUnits");

        var state = await CaptureStateAsync(db, tenantId, survivor, absorbed, command.EventTime, cancellationToken);
        var rows = 0;
        rows += await db.Events.Where(e => e.TenantId == tenantId && e.UnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.UnitId, survivorId).SetProperty(e => e.MergedFromUnitId, absorbedId), cancellationToken);
        rows += await db.ArchivedEvents.Where(e => e.TenantId == tenantId && e.UnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.UnitId, survivorId).SetProperty(e => e.MergedFromUnitId, absorbedId), cancellationToken);
        rows += await db.ArchiveStage.Where(e => e.TenantId == tenantId && e.UnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.UnitId, survivorId).SetProperty(e => e.MergedFromUnitId, absorbedId), cancellationToken);
        rows += await db.Identifiers.Where(i => i.TenantId == tenantId && i.UnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(i => i.UnitId, survivorId).SetProperty(i => i.MergedFromUnitId, absorbedId), cancellationToken);
        rows += await db.ItemAssignments.Where(a => a.TenantId == tenantId && a.UnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(a => a.UnitId, survivorId).SetProperty(a => a.MergedFromUnitId, absorbedId), cancellationToken);
        rows += await db.Episodes.Where(e => e.TenantId == tenantId && e.UnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.UnitId, survivorId).SetProperty(e => e.MergedFromUnitId, absorbedId), cancellationToken);
        rows += await db.Entitlements.Where(e => e.TenantId == tenantId && e.UnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.UnitId, survivorId).SetProperty(e => e.MergedFromUnitId, absorbedId), cancellationToken);
        rows += await db.EntitlementRequirements.Where(e => e.TenantId == tenantId && e.UnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.UnitId, survivorId).SetProperty(e => e.MergedFromUnitId, absorbedId), cancellationToken);
        rows += await db.SerialRangeMembers.Where(m => m.TenantId == tenantId && m.CommissionedUnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(m => m.CommissionedUnitId, survivorId).SetProperty(m => m.MergedFromUnitId, absorbedId), cancellationToken);
        rows += await db.Units.Where(u => u.TenantId == tenantId && u.DerivedFromUnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(u => u.DerivedFromUnitId, survivorId), cancellationToken);
        rows += await db.Entitlements.Where(e => e.TenantId == tenantId && e.ScopeChildUnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.ScopeChildUnitId, survivorId), cancellationToken);
        rows += await db.Entitlements.Where(e => e.TenantId == tenantId && e.SuccessorUnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.SuccessorUnitId, survivorId), cancellationToken);

        foreach (var record in await db.BuildRecords.Where(b => b.TenantId == tenantId && b.UnitId == absorbedId).OrderBy(b => b.Version).ToListAsync(cancellationToken))
        {
            record.UnitId = survivorId;
            record.MergedFromUnitId = absorbedId;
            record.Version = state.BuildVersions[record.BuildRecordId] + state.SurvivorBuildVersionMax;
            rows++;
        }
        rows += await db.AssemblyLinks.Where(l => l.TenantId == tenantId && l.ParentUnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(l => l.ParentUnitId, survivorId).SetProperty(l => l.MergedFromUnitId, absorbedId), cancellationToken);
        rows += await db.AssemblyLinks.Where(l => l.TenantId == tenantId && l.ChildUnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(l => l.ChildUnitId, survivorId).SetProperty(l => l.MergedFromUnitId, absorbedId), cancellationToken);
        rows += await MoveSelectionsAsync(db, tenantId, survivorId, absorbedId, state, cancellationToken);
        rows += await MergeSearchAsync(db, tenantId, survivorId, absorbedId, state, cancellationToken);

        survivor.OverlayFlags = JsonSerializer.Serialize(state.CombinedFlags, LedgerJson.Options);
        absorbed.LifecycleStatus = LifecycleStatus.Merged;
        absorbed.MergedIntoUnitId = survivorId;
        db.UnitMerges.Add(new UnitMergeRecord { TenantId = tenantId, CorrectionId = metadata.CorrectionId, SurvivorUnitId = survivorId, AbsorbedUnitId = absorbedId, MergedAt = command.EventTime, ReversalData = JsonSerializer.Serialize(state, LedgerJson.Options) });
        var coverage = await CoverageAsync(db, tenantId, survivorId, cancellationToken);
        var drafts = MergeDrafts(command, metadata, survivorId, absorbedId, UnitEventType.Merged, state.CombinedFlags, coverage, LifecycleStatus.Merged);
        await writer.AppendAsync(drafts, cancellationToken);
        await corrections.RecordAsync(CorrectionCommand.MergeUnits, metadata, command.ActorRef, command.EventTime, [survivorId, absorbedId], cancellationToken);
        return new(survivorId, absorbedId, command.Resolution, metadata.CorrectionId, rows, false, false);
    }

    private static async Task<MergeUnitsResult> ReverseAsync(MergeUnits command, CorrectionMetadata metadata, Unit survivor, Unit absorbed, UnitMergeRecord merge,
        SerialisationDbContext db, IUnitEventWriter writer, CorrectionLedger corrections, TenantId tenantId, IClock clock, CancellationToken cancellationToken)
    {
        var state = JsonSerializer.Deserialize<MergeReversalState>(merge.ReversalData, LedgerJson.Options)!;
        var survivorId = survivor.UnitId;
        var absorbedId = absorbed.UnitId;
        var rows = 0;
        rows += await db.Events.Where(e => e.TenantId == tenantId && e.UnitId == survivorId && e.MergedFromUnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.UnitId, absorbedId).SetProperty(e => e.MergedFromUnitId, (Guid?)null), cancellationToken);
        rows += await db.ArchivedEvents.Where(e => e.TenantId == tenantId && e.UnitId == survivorId && e.MergedFromUnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.UnitId, absorbedId).SetProperty(e => e.MergedFromUnitId, (Guid?)null), cancellationToken);
        rows += await db.ArchiveStage.Where(e => e.TenantId == tenantId && e.UnitId == survivorId && e.MergedFromUnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.UnitId, absorbedId).SetProperty(e => e.MergedFromUnitId, (Guid?)null), cancellationToken);
        rows += await db.Identifiers.Where(e => e.TenantId == tenantId && e.UnitId == survivorId && e.MergedFromUnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.UnitId, absorbedId).SetProperty(e => e.MergedFromUnitId, (Guid?)null), cancellationToken);
        rows += await db.ItemAssignments.Where(e => e.TenantId == tenantId && e.UnitId == survivorId && e.MergedFromUnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.UnitId, absorbedId).SetProperty(e => e.MergedFromUnitId, (Guid?)null), cancellationToken);
        rows += await db.Episodes.Where(e => e.TenantId == tenantId && e.UnitId == survivorId && e.MergedFromUnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.UnitId, absorbedId).SetProperty(e => e.MergedFromUnitId, (Guid?)null), cancellationToken);
        rows += await db.Entitlements.Where(e => e.TenantId == tenantId && e.UnitId == survivorId && e.MergedFromUnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.UnitId, absorbedId).SetProperty(e => e.MergedFromUnitId, (Guid?)null), cancellationToken);
        rows += await db.EntitlementRequirements.Where(e => e.TenantId == tenantId && e.UnitId == survivorId && e.MergedFromUnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.UnitId, absorbedId).SetProperty(e => e.MergedFromUnitId, (Guid?)null), cancellationToken);
        rows += await db.SerialRangeMembers.Where(e => e.TenantId == tenantId && e.CommissionedUnitId == survivorId && e.MergedFromUnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(e => e.CommissionedUnitId, absorbedId).SetProperty(e => e.MergedFromUnitId, (Guid?)null), cancellationToken);
        await db.Identifiers.Where(e => e.TenantId == tenantId && state.ClosedIdentifierIds.Contains(e.IdentifierId)).ExecuteUpdateAsync(s => s.SetProperty(e => e.ValidTo, (DateTime?)null).SetProperty(e => e.CorrectionReason, (string?)null), cancellationToken);
        await db.Episodes.Where(e => e.TenantId == tenantId && state.ClosedEpisodeIds.Contains(e.EpisodeId)).ExecuteUpdateAsync(s => s.SetProperty(e => e.ValidTo, (DateTime?)null), cancellationToken);
        await db.ItemAssignments.Where(e => e.TenantId == tenantId && state.ClosedAssignmentIds.Contains(e.AssignmentId)).ExecuteUpdateAsync(s => s.SetProperty(e => e.ValidTo, (DateTime?)null), cancellationToken);
        await db.AssemblyLinks.Where(e => e.TenantId == tenantId && state.ClosedAssemblyLinkIds.Contains(e.LinkId)).ExecuteUpdateAsync(s => s.SetProperty(e => e.RemovedAt, (DateTime?)null), cancellationToken);
        foreach (var record in await db.BuildRecords.Where(b => b.TenantId == tenantId && b.UnitId == survivorId && b.MergedFromUnitId == absorbedId).ToListAsync(cancellationToken))
        {
            record.UnitId = absorbedId;
            record.MergedFromUnitId = null;
            record.Version = state.BuildVersions[record.BuildRecordId];
            rows++;
        }
        rows += await db.AssemblyLinks.Where(l => l.TenantId == tenantId && state.ParentAssemblyLinkIds.Contains(l.LinkId)).ExecuteUpdateAsync(s => s.SetProperty(l => l.ParentUnitId, absorbedId).SetProperty(l => l.MergedFromUnitId, (Guid?)null), cancellationToken);
        rows += await db.AssemblyLinks.Where(l => l.TenantId == tenantId && state.ChildAssemblyLinkIds.Contains(l.LinkId)).ExecuteUpdateAsync(s => s.SetProperty(l => l.ChildUnitId, absorbedId).SetProperty(l => l.MergedFromUnitId, (Guid?)null), cancellationToken);
        rows += await db.Units.Where(u => u.TenantId == tenantId && state.DerivedUnitIds.Contains(u.UnitId)).ExecuteUpdateAsync(s => s.SetProperty(u => u.DerivedFromUnitId, absorbedId), cancellationToken);
        rows += await db.Entitlements.Where(e => e.TenantId == tenantId && state.ScopeChildEntitlementIds.Contains(e.EntitlementId)).ExecuteUpdateAsync(s => s.SetProperty(e => e.ScopeChildUnitId, absorbedId), cancellationToken);
        rows += await db.Entitlements.Where(e => e.TenantId == tenantId && state.SuccessorEntitlementIds.Contains(e.EntitlementId)).ExecuteUpdateAsync(s => s.SetProperty(e => e.SuccessorUnitId, absorbedId), cancellationToken);
        rows += await RestoreSelectionsAsync(db, tenantId, survivorId, absorbedId, state, cancellationToken);
        rows += await db.SearchTerms.Where(t => t.TenantId == tenantId && t.EntityType == SearchEntityType.Unit && t.EntityId == survivorId && t.MergedFromUnitId == absorbedId).ExecuteDeleteAsync(cancellationToken);
        rows += await db.SearchEntries.Where(e => e.TenantId == tenantId && e.EntityType == SearchEntityType.Unit && e.EntityId == survivorId && e.MergedFromUnitId == absorbedId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.MergedFromUnitId, (Guid?)null), cancellationToken);
        rows += await db.SearchTerms.Where(t => t.TenantId == tenantId && t.EntityType == SearchEntityType.Unit && t.EntityId == absorbedId).ExecuteDeleteAsync(cancellationToken);
        rows += await db.SearchEntries.Where(e => e.TenantId == tenantId && e.EntityType == SearchEntityType.Unit && e.EntityId == absorbedId).ExecuteDeleteAsync(cancellationToken);
        if (state.AbsorbedSearchEntry is { } entry)
        {
            db.SearchEntries.Add(new SearchEntry
            {
                TenantId = tenantId, EntityType = SearchEntityType.Unit, EntityId = absorbedId, EntryId = entry.EntryId,
                Title = entry.Title, TitleMono = entry.TitleMono, Subtitle = entry.Subtitle, SearchText = entry.SearchText,
                TypeWeight = entry.TypeWeight, UpdatedAt = entry.UpdatedAt, Metadata = entry.Metadata, Href = entry.Href,
            });
            rows++;
        }
        foreach (var term in state.AbsorbedSearchTerms)
        {
            db.SearchTerms.Add(new SearchTerm
            {
                TenantId = tenantId, EntityType = SearchEntityType.Unit, EntityId = absorbedId,
                Term = term.Term, Kind = term.Kind, Detail = term.Detail,
            });
            rows++;
        }

        survivor.OverlayFlags = state.SurvivorFlags;
        absorbed.LifecycleStatus = state.AbsorbedLifecycle;
        absorbed.MergedIntoUnitId = null;
        merge.ReversedByCorrectionId = metadata.CorrectionId;
        merge.ReversedAt = command.EventTime;
        var survivorCoverage = await CoverageAsync(db, tenantId, survivorId, cancellationToken);
        var absorbedCoverage = await CoverageAsync(db, tenantId, absorbedId, cancellationToken);
        var survivorFlags = JsonSerializer.Deserialize<List<string>>(state.SurvivorFlags, LedgerJson.Options) ?? [];
        var absorbedFlags = JsonSerializer.Deserialize<List<string>>(state.AbsorbedFlags, LedgerJson.Options) ?? [];
        var drafts = new List<UnitEventDraft>
        {
            Draft(command, metadata, survivorId, absorbedId, "survivor", UnitEventType.MergeReversed, new CurrentStateDelta { OverlayFlags = survivorFlags, LiveEntitlementIds = survivorCoverage.LiveEntitlementIds, WarrantyEndMax = new Optional<DateOnly?>(survivorCoverage.WarrantyEndMax) }),
            Draft(command, metadata, absorbedId, survivorId, "restored", UnitEventType.MergeReversed, new CurrentStateDelta { LifecycleStatus = state.AbsorbedLifecycle, OverlayFlags = absorbedFlags, LiveEntitlementIds = absorbedCoverage.LiveEntitlementIds, WarrantyEndMax = new Optional<DateOnly?>(absorbedCoverage.WarrantyEndMax) }),
        };
        await writer.AppendAsync(drafts, cancellationToken);
        await corrections.RecordAsync(CorrectionCommand.MergeUnits, metadata, command.ActorRef, command.EventTime, [survivorId, absorbedId], cancellationToken);
        return new(survivorId, absorbedId, command.Resolution, metadata.CorrectionId, rows, true, false);
    }

    private static async Task<MergeReversalState> CaptureStateAsync(SerialisationDbContext db, TenantId tenantId, Unit survivor, Unit absorbed, DateTime at, CancellationToken cancellationToken)
    {
        var survivorOpenTypes = await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && i.UnitId == survivor.UnitId && i.ValidTo == null).Select(i => i.Type).ToListAsync(cancellationToken);
        var closedIdentifiers = await db.Identifiers.Where(i => i.TenantId == tenantId && i.UnitId == absorbed.UnitId && i.ValidTo == null && survivorOpenTypes.Contains(i.Type)).Select(i => i.IdentifierId).ToListAsync(cancellationToken);
        await db.Identifiers.Where(i => i.TenantId == tenantId && closedIdentifiers.Contains(i.IdentifierId)).ExecuteUpdateAsync(s => s.SetProperty(i => i.ValidTo, at).SetProperty(i => i.CorrectionReason, MergeUnitsReason.DuplicateRecord), cancellationToken);
        var survivorOpenKinds = await db.Episodes.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == survivor.UnitId && e.ValidTo == null).Select(e => e.Kind).ToListAsync(cancellationToken);
        var closedEpisodes = await db.Episodes.Where(e => e.TenantId == tenantId && e.UnitId == absorbed.UnitId && e.ValidTo == null && survivorOpenKinds.Contains(e.Kind)).Select(e => e.EpisodeId).ToListAsync(cancellationToken);
        await db.Episodes.Where(e => e.TenantId == tenantId && closedEpisodes.Contains(e.EpisodeId)).ExecuteUpdateAsync(s => s.SetProperty(e => e.ValidTo, at), cancellationToken);
        var survivorHasAssignment = await db.ItemAssignments.AsNoTracking().AnyAsync(a => a.TenantId == tenantId && a.UnitId == survivor.UnitId && a.ValidTo == null, cancellationToken);
        var closedAssignments = survivorHasAssignment
            ? await db.ItemAssignments.Where(a => a.TenantId == tenantId && a.UnitId == absorbed.UnitId && a.ValidTo == null).Select(a => a.AssignmentId).ToListAsync(cancellationToken) : [];
        await db.ItemAssignments.Where(a => a.TenantId == tenantId && closedAssignments.Contains(a.AssignmentId)).ExecuteUpdateAsync(s => s.SetProperty(a => a.ValidTo, at), cancellationToken);
        var survivorParent = await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenantId && l.ChildUnitId == survivor.UnitId && l.RemovedAt == null).Select(l => (Guid?)l.ParentUnitId).FirstOrDefaultAsync(cancellationToken);
        var absorbedOpenLinks = await db.AssemblyLinks.Where(l => l.TenantId == tenantId && l.ChildUnitId == absorbed.UnitId && l.RemovedAt == null).ToListAsync(cancellationToken);
        if (survivorParent is not null && absorbedOpenLinks.Any(l => l.ParentUnitId != survivorParent))
            throw new CommandRefusedException("assembly_parent_conflict", "The records are installed in different open parents.", "correct the as-maintained trees before merging");
        var closedLinks = survivorParent is null ? [] : absorbedOpenLinks.Select(l => l.LinkId).ToList();
        await db.AssemblyLinks.Where(l => l.TenantId == tenantId && closedLinks.Contains(l.LinkId)).ExecuteUpdateAsync(s => s.SetProperty(l => l.RemovedAt, at), cancellationToken);
        var buildVersions = await db.BuildRecords.AsNoTracking().Where(b => b.TenantId == tenantId && b.UnitId == absorbed.UnitId).ToDictionaryAsync(b => b.BuildRecordId, b => b.Version, cancellationToken);
        var maxVersion = await db.BuildRecords.AsNoTracking().Where(b => b.TenantId == tenantId && b.UnitId == survivor.UnitId).Select(b => (int?)b.Version).MaxAsync(cancellationToken) ?? 0;
        var parentLinks = await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenantId && l.ParentUnitId == absorbed.UnitId).Select(l => l.LinkId).ToListAsync(cancellationToken);
        var childLinks = await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenantId && l.ChildUnitId == absorbed.UnitId).Select(l => l.LinkId).ToListAsync(cancellationToken);
        var derived = await db.Units.AsNoTracking().Where(u => u.TenantId == tenantId && u.DerivedFromUnitId == absorbed.UnitId).Select(u => u.UnitId).ToListAsync(cancellationToken);
        var scopeChildren = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId && e.ScopeChildUnitId == absorbed.UnitId).Select(e => e.EntitlementId).ToListAsync(cancellationToken);
        var successors = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId && e.SuccessorUnitId == absorbed.UnitId).Select(e => e.EntitlementId).ToListAsync(cancellationToken);
        var searchEntry = await db.SearchEntries.AsNoTracking().Where(e => e.TenantId == tenantId && e.EntityType == SearchEntityType.Unit && e.EntityId == absorbed.UnitId)
            .Select(e => new SearchEntrySnapshot(e.EntryId, e.Title, e.TitleMono, e.Subtitle, e.SearchText, e.TypeWeight, e.UpdatedAt, e.Metadata, e.Href)).FirstOrDefaultAsync(cancellationToken);
        var searchTerms = await db.SearchTerms.AsNoTracking().Where(t => t.TenantId == tenantId && t.EntityType == SearchEntityType.Unit && t.EntityId == absorbed.UnitId)
            .Select(t => new SearchTermSnapshot(t.Term, t.Kind, t.Detail)).ToListAsync(cancellationToken);
        var flags = (JsonSerializer.Deserialize<List<string>>(survivor.OverlayFlags, LedgerJson.Options) ?? []).Concat(JsonSerializer.Deserialize<List<string>>(absorbed.OverlayFlags, LedgerJson.Options) ?? []).Distinct().Order().ToList();
        return new(survivor.LifecycleStatus, absorbed.LifecycleStatus, survivor.OverlayFlags, absorbed.OverlayFlags, flags, closedIdentifiers, closedEpisodes, closedAssignments, closedLinks,
            buildVersions, maxVersion, parentLinks, childLinks, derived, scopeChildren, successors, [], [], searchEntry, searchTerms);
    }

    private static async Task<int> MoveSelectionsAsync(SerialisationDbContext db, TenantId tenantId, Guid survivorId, Guid absorbedId, MergeReversalState state, CancellationToken cancellationToken)
    {
        var absorbed = await db.SelectionSetMembers.AsNoTracking().Where(m => m.TenantId == tenantId && m.UnitId == absorbedId).ToListAsync(cancellationToken);
        var selections = absorbed.Select(m => m.SelectionId).ToList();
        var existing = await db.SelectionSetMembers.AsNoTracking().Where(m => m.TenantId == tenantId && m.UnitId == survivorId && selections.Contains(m.SelectionId)).Select(m => m.SelectionId).ToListAsync(cancellationToken);
        state.RemovedSelectionMembers.AddRange(absorbed.Where(m => existing.Contains(m.SelectionId)).Select(m => new SelectionSnapshot(m.SelectionId, m.Serial, m.Outcome, m.Reason, m.Detail)));
        state.MovedSelectionIds.AddRange(absorbed.Where(m => !existing.Contains(m.SelectionId)).Select(m => m.SelectionId));
        var removed = await db.SelectionSetMembers.Where(m => m.TenantId == tenantId && m.UnitId == absorbedId && existing.Contains(m.SelectionId)).ExecuteDeleteAsync(cancellationToken);
        var moved = await db.SelectionSetMembers.Where(m => m.TenantId == tenantId && m.UnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(m => m.UnitId, survivorId).SetProperty(m => m.MergedFromUnitId, absorbedId), cancellationToken);
        return removed + moved;
    }

    private static async Task<int> RestoreSelectionsAsync(SerialisationDbContext db, TenantId tenantId, Guid survivorId, Guid absorbedId, MergeReversalState state, CancellationToken cancellationToken)
    {
        var moved = await db.SelectionSetMembers.Where(m => m.TenantId == tenantId && m.UnitId == survivorId && m.MergedFromUnitId == absorbedId).ExecuteUpdateAsync(s => s.SetProperty(m => m.UnitId, absorbedId).SetProperty(m => m.MergedFromUnitId, (Guid?)null), cancellationToken);
        foreach (var row in state.RemovedSelectionMembers)
            db.SelectionSetMembers.Add(new SelectionSetMember { TenantId = tenantId, SelectionId = row.SelectionId, UnitId = absorbedId, Serial = row.Serial, Outcome = row.Outcome, Reason = row.Reason, Detail = row.Detail });
        return moved + state.RemovedSelectionMembers.Count;
    }

    private static async Task<int> MergeSearchAsync(SerialisationDbContext db, TenantId tenantId, Guid survivorId, Guid absorbedId, MergeReversalState state, CancellationToken cancellationToken)
    {
        var survivorEntry = await db.SearchEntries.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EntityType == SearchEntityType.Unit && e.EntityId == survivorId, cancellationToken);
        var absorbedTerms = await db.SearchTerms.AsNoTracking().Where(t => t.TenantId == tenantId && t.EntityType == SearchEntityType.Unit && t.EntityId == absorbedId).ToListAsync(cancellationToken);
        var existing = await db.SearchTerms.AsNoTracking().Where(t => t.TenantId == tenantId && t.EntityType == SearchEntityType.Unit && t.EntityId == survivorId)
            .Select(t => new { t.Term, t.Kind }).ToListAsync(cancellationToken);
        var keys = existing.Select(t => (t.Term, t.Kind)).ToHashSet();
        foreach (var term in absorbedTerms.Where(t => !keys.Contains((t.Term, t.Kind))))
            db.SearchTerms.Add(new SearchTerm { TenantId = tenantId, Term = term.Term, EntityType = term.EntityType, EntityId = survivorId, Kind = term.Kind, Detail = term.Detail, MergedFromUnitId = absorbedId });
        if (survivorEntry is not null) survivorEntry.MergedFromUnitId = absorbedId;
        else if (state.AbsorbedSearchEntry is { } absorbedEntry)
            db.SearchEntries.Add(new SearchEntry { TenantId = tenantId, EntityType = SearchEntityType.Unit, EntityId = survivorId, EntryId = TimeOrderedId.New(absorbedEntry.UpdatedAt), Title = absorbedEntry.Title, TitleMono = absorbedEntry.TitleMono, Subtitle = absorbedEntry.Subtitle, SearchText = absorbedEntry.SearchText, TypeWeight = absorbedEntry.TypeWeight, UpdatedAt = absorbedEntry.UpdatedAt, Metadata = absorbedEntry.Metadata, Href = $"/units/{survivorId}", MergedFromUnitId = absorbedId });
        var deletedTerms = await db.SearchTerms.Where(t => t.TenantId == tenantId && t.EntityType == SearchEntityType.Unit && t.EntityId == absorbedId).ExecuteDeleteAsync(cancellationToken);
        var deletedEntries = await db.SearchEntries.Where(e => e.TenantId == tenantId && e.EntityType == SearchEntityType.Unit && e.EntityId == absorbedId).ExecuteDeleteAsync(cancellationToken);
        return deletedTerms + deletedEntries + absorbedTerms.Count(t => !keys.Contains((t.Term, t.Kind)));
    }

    private static async Task<EntitlementCoverage> CoverageAsync(SerialisationDbContext db, TenantId tenantId, Guid unitId, CancellationToken cancellationToken)
    {
        var rows = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId)
            .Select(e => new EntitlementCoverageEntry(e.EntitlementId, e.Status, e.EndDate, e.Direction)).ToListAsync(cancellationToken);
        return EntitlementCoverage.From(rows);
    }

    private static IReadOnlyList<UnitEventDraft> MergeDrafts(MergeUnits command, CorrectionMetadata metadata, Guid survivorId, Guid absorbedId, string type, IReadOnlyList<string> flags, EntitlementCoverage coverage, string aliasStatus) =>
    [
        Draft(command, metadata, survivorId, absorbedId, "survivor", type, new CurrentStateDelta { OverlayFlags = flags, LiveEntitlementIds = coverage.LiveEntitlementIds, WarrantyEndMax = new Optional<DateOnly?>(coverage.WarrantyEndMax) }),
        Draft(command, metadata, absorbedId, survivorId, "absorbed", type, new CurrentStateDelta { LifecycleStatus = aliasStatus, LiveEntitlementIds = [], WarrantyEndMax = new Optional<DateOnly?>(null) }),
    ];

    private static UnitEventDraft Draft(MergeUnits command, CorrectionMetadata metadata, Guid unitId, Guid counterpart, string role, string type, CurrentStateDelta delta) => new()
    {
        UnitId = unitId, EventTime = command.EventTime, EventType = type, LegalEntityId = command.LegalEntityId, ActorRef = command.ActorRef,
        IdempotencyKey = IdempotencyKey.ForCommand("merge-units", command.CommandId, unitId, type, command.EventTime),
        Attributes = CorrectionLedger.Attributes(metadata, new { command_id = command.CommandId, survivor_unit_id = command.SurvivorUnitId, absorbed_unit_id = command.AbsorbedUnitId, counterpart_unit_id = counterpart, role, resolution = command.Resolution }),
        Delta = delta,
    };

    private sealed record SelectionSnapshot(Guid SelectionId, string? Serial, string? Outcome, string? Reason, string? Detail);

    private sealed record SearchEntrySnapshot(Guid EntryId, string Title, bool TitleMono, string? Subtitle, string SearchText, int TypeWeight, DateTime UpdatedAt, string Metadata, string Href);

    private sealed record SearchTermSnapshot(string Term, string Kind, string? Detail);

    private sealed record MergeReversalState(
        string SurvivorLifecycle,
        string AbsorbedLifecycle,
        string SurvivorFlags,
        string AbsorbedFlags,
        List<string> CombinedFlags,
        List<Guid> ClosedIdentifierIds,
        List<Guid> ClosedEpisodeIds,
        List<Guid> ClosedAssignmentIds,
        List<Guid> ClosedAssemblyLinkIds,
        Dictionary<Guid, int> BuildVersions,
        int SurvivorBuildVersionMax,
        List<Guid> ParentAssemblyLinkIds,
        List<Guid> ChildAssemblyLinkIds,
        List<Guid> DerivedUnitIds,
        List<Guid> ScopeChildEntitlementIds,
        List<Guid> SuccessorEntitlementIds,
        List<Guid> MovedSelectionIds,
        List<SelectionSnapshot> RemovedSelectionMembers,
        SearchEntrySnapshot? AbsorbedSearchEntry,
        List<SearchTermSnapshot> AbsorbedSearchTerms);
}
