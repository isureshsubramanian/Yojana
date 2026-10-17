using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

/// <summary>
/// Moves the already-derived entitlement snapshots of a posted document from its recorded units to its physical units.
/// The P2 derivation remains the only policy calculation: a correction copies its immutable result (policy version,
/// term, coverage, floor and accrual cohort) rather than resolving today's policy and silently changing the sale.
/// </summary>
public sealed class DocumentCorrectionEntitlements(SerialisationDbContext db, IUnitEventWriter writer, ITenantContext tenant, IClock clock)
{
    public sealed record Result(int Voided, int Created, IReadOnlyList<CorrectionEntitlementPreview> Preview);

    public async Task<IReadOnlyList<CorrectionEntitlementPreview>> PreviewAsync(DocumentRef document,
        IReadOnlyDictionary<Guid, Guid?> physicalByRecorded, CancellationToken cancellationToken)
    {
        var recordedIds = physicalByRecorded.Keys.ToList();
        var sources = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenant.TenantId
            && recordedIds.Contains(e.UnitId) && e.Status != EntitlementStatus.Closed
            && e.SourceDocumentType == document.Type && e.SourceDocumentId == document.Id
            && (document.Line == null || e.SourceDocumentLine == document.Line)).ToListAsync(cancellationToken);
        EnsureTransferableState(sources);
        return sources.OrderBy(e => e.UnitId).ThenBy(e => e.CoverageFamily).SelectMany(e =>
        {
            var changes = new List<CorrectionEntitlementPreview>
            {
                new(e.UnitId, "void", e.EntitlementId, $"{e.CoverageFamily} · {EntitlementClosedReason.VoidedDocumentCorrection}"),
            };
            if (physicalByRecorded[e.UnitId] is { } physicalUnitId)
                changes.Add(new(physicalUnitId, "create", null, $"{e.CoverageFamily} · same source document"));
            return changes;
        }).ToList();
    }

    public async Task<Result> ApplyAsync(Guid commandId, DocumentRef document, IReadOnlyDictionary<Guid, Guid?> physicalByRecorded,
        DateTime effectiveAt, string actorRef, Guid legalEntityId, CorrectionMetadata metadata, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var recordedIds = physicalByRecorded.Keys.ToList();
        var physicalIds = physicalByRecorded.Values.OfType<Guid>().ToList();
        var affected = recordedIds.Concat(physicalIds).Distinct().ToList();
        var rows = await db.Entitlements.Where(e => e.TenantId == tenantId && affected.Contains(e.UnitId)).ToListAsync(cancellationToken);
        var items = await db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && affected.Contains(c.UnitId))
            .ToDictionaryAsync(c => c.UnitId, c => c.ItemId, cancellationToken);
        var sources = rows.Where(e => recordedIds.Contains(e.UnitId) && e.Status != EntitlementStatus.Closed
            && e.SourceDocumentType == document.Type && e.SourceDocumentId == document.Id
            && (document.Line == null || e.SourceDocumentLine == document.Line)).ToList();
        EnsureTransferableState(sources);
        var policyIds = sources.Select(e => e.PolicyVersionId).Distinct().ToList();
        var policies = await db.PolicyVersions.AsNoTracking().Where(v => v.TenantId == tenantId && policyIds.Contains(v.PolicyVersionId))
            .ToDictionaryAsync(v => v.PolicyVersionId, cancellationToken);
        var targetLive = rows.Where(e => physicalIds.Contains(e.UnitId) && e.Status != EntitlementStatus.Closed)
            .ToLookup(e => (e.UnitId, e.CoverageFamily));
        foreach (var source in sources)
        {
            if (physicalByRecorded[source.UnitId] is not { } targetId) continue;
            if (targetLive[(targetId, source.CoverageFamily)].Any())
            {
                throw new CommandRefusedException("physical_unit_entitlement_conflict",
                    $"Physical unit {targetId} already has a live {source.CoverageFamily} entitlement.",
                    "close or correct the conflicting entitlement before correcting the document serials");
            }
        }

        var sourceEntitlementIds = sources.Select(e => e.EntitlementId).ToList();
        List<EntitlementUsageLimit> limits = sources.Count == 0
            ? []
            : await db.EntitlementUsageLimits.AsNoTracking().Where(l => l.TenantId == tenantId && sourceEntitlementIds.Contains(l.EntitlementId)).ToListAsync(cancellationToken);
        var state = rows.GroupBy(e => e.UnitId).ToDictionary(g => g.Key,
            g => g.Select(e => new EntitlementCoverageEntry(e.EntitlementId, e.Status, e.EndDate, e.Direction)).ToList());
        foreach (var id in affected) state.TryAdd(id, []);

        var drafts = new List<UnitEventDraft>(sources.Count * 2);
        var preview = new List<CorrectionEntitlementPreview>(sources.Count * 2);
        var created = 0;
        foreach (var source in sources.OrderBy(e => e.UnitId).ThenBy(e => e.CoverageFamily))
        {
            var sourceDocument = document.Line is null && source.SourceDocumentLine is { } line
                ? document with { Line = line }
                : document;
            var before = EntitlementCoverage.From(state[source.UnitId]);
            var sourceStatus = source.Status;
            EntitlementLedger.Transition(source, EntitlementEventType.Voided);
            source.ClosedReason = EntitlementClosedReason.VoidedDocumentCorrection;
            source.ClosedEffective = DateOnly.FromDateTime(effectiveAt);
            source.ClosedAt = clock.UtcNow;
            var sourceState = state[source.UnitId];
            var sourceIndex = sourceState.FindIndex(e => e.EntitlementId == source.EntitlementId);
            sourceState[sourceIndex] = sourceState[sourceIndex] with { Status = EntitlementStatus.Closed };
            var after = EntitlementCoverage.From(state[source.UnitId]);
            var voidEventId = TimeOrderedId.New(effectiveAt);
            drafts.Add(Event(commandId, source.UnitId, source.ObligorEntityId, EntitlementEventType.Voided, source.EntitlementId, effectiveAt,
                actorRef, legalEntityId, sourceDocument, metadata, before, after, voidEventId,
                new { entitlement_id = source.EntitlementId, coverage_family = source.CoverageFamily, direction = source.Direction,
                    reason = EntitlementClosedReason.VoidedDocumentCorrection, effective = source.ClosedEffective,
                    correction_of_document_id = document.Id }));
            preview.Add(new(source.UnitId, "void", source.EntitlementId, $"{source.CoverageFamily} · {EntitlementClosedReason.VoidedDocumentCorrection}"));

            if (physicalByRecorded[source.UnitId] is not { } targetId) continue;
            var createdEventId = TimeOrderedId.New(effectiveAt);
            var target = Copy(source, sourceStatus, targetId, effectiveAt, createdEventId);
            source.SuccessorUnitId = targetId;
            source.SuccessorEntitlementId = target.EntitlementId;
            db.Entitlements.Add(target);
            foreach (var limit in limits.Where(l => l.EntitlementId == source.EntitlementId))
            {
                db.EntitlementUsageLimits.Add(new EntitlementUsageLimit
                {
                    TenantId = tenantId, EntitlementId = target.EntitlementId, MeterType = limit.MeterType,
                    LimitValue = limit.LimitValue, UsedValue = limit.UsedValue, Period = limit.Period, ResetsOn = limit.ResetsOn,
                });
            }

            var targetBefore = EntitlementCoverage.From(state[targetId]);
            state[targetId].Add(new(target.EntitlementId, target.Status, target.EndDate, target.Direction));
            var targetAfter = EntitlementCoverage.From(state[targetId]);
            drafts.Add(Event(commandId, targetId, target.ObligorEntityId, EntitlementEventType.Created, source.EntitlementId, effectiveAt,
                actorRef, legalEntityId, sourceDocument, metadata, targetBefore, targetAfter, createdEventId,
                new
                {
                    entitlement_id = target.EntitlementId, item_id = items[targetId], coverage_family = target.CoverageFamily,
                    entitlement_type = target.Type, policy_version_id = target.PolicyVersionId, policy_code = policies[target.PolicyVersionId].PolicyCode,
                    policy_version = policies[target.PolicyVersionId].Version, status = target.Status, start_basis = target.StartBasis, start_date = target.StartDate,
                    end_date = target.EndDate, accrual_cohort_id = target.AccrualCohortId, assurance_or_service = target.AssuranceOrService,
                    accrual_suppressed = target.AccrualCohortId is null && target.AssuranceOrService == AssuranceOrService.Assurance,
                    provenance = target.Provenance, owner_party_ref = target.OwnerPartyRef, direction = target.Direction,
                    correction_of_document_id = document.Id, corrected_from_entitlement_id = source.EntitlementId,
                }));
            preview.Add(new(targetId, "create", target.EntitlementId, $"{target.CoverageFamily} · same source document"));
            created++;
        }

        if (drafts.Count > 0) await writer.AppendAsync(drafts, cancellationToken);
        return new(sources.Count, created, preview);
    }

    private UnitEventDraft Event(Guid commandId, Guid unitId, Guid obligorEntityId, string eventType, Guid sourceEntitlementId, DateTime at,
        string actorRef, Guid legalEntityId, DocumentRef document, CorrectionMetadata metadata, EntitlementCoverage before,
        EntitlementCoverage after, Guid eventId, object specific)
    {
        var node = JsonSerializer.SerializeToNode(specific, LedgerJson.Options)!.AsObject();
        node["coverage_before"] = JsonSerializer.SerializeToNode(new { live_entitlement_ids = before.LiveEntitlementIds, warranty_end_max = before.WarrantyEndMax }, LedgerJson.Options);
        return new UnitEventDraft
        {
            UnitId = unitId, EventTime = at, EventType = eventType, PresetEventId = eventId,
            LegalEntityId = obligorEntityId == Guid.Empty ? legalEntityId : obligorEntityId, ActorRef = actorRef,
            DocumentType = document.Type, DocumentId = document.Id, DocumentLine = document.Line,
            IdempotencyKey = IdempotencyKey.Derive(EntitlementLedger.Key(commandId, unitId, eventType, at), sourceEntitlementId.ToString("N")),
            Attributes = CorrectionLedger.Attributes(metadata, node),
            Delta = new CurrentStateDelta { LiveEntitlementIds = after.LiveEntitlementIds, WarrantyEndMax = new Optional<DateOnly?>(after.WarrantyEndMax) },
        };
    }

    private Entitlement Copy(Entitlement source, string sourceStatus, Guid targetUnitId, DateTime effectiveAt, Guid eventId) => new()
    {
        TenantId = tenant.TenantId, EntitlementId = TimeOrderedId.New(effectiveAt), UnitId = targetUnitId,
        Scope = source.Scope, ScopeChildUnitId = source.ScopeChildUnitId, ScopeItemClass = source.ScopeItemClass,
        Type = source.Type, CoverageFamily = source.CoverageFamily, Direction = source.Direction,
        OwnerPartyRef = source.OwnerPartyRef, OwnerPartyType = source.OwnerPartyType, ObligorEntityId = source.ObligorEntityId,
        PayerPartyRef = source.PayerPartyRef, PayerPartyType = source.PayerPartyType, PolicyVersionId = source.PolicyVersionId,
        StartTrigger = source.StartTrigger, StartBasis = source.StartBasis, StartEvidenceRef = source.StartEvidenceRef,
        StartDate = source.StartDate, StartTimeZone = source.StartTimeZone, EndDate = source.EndDate,
        ComputedByRuleVersion = source.ComputedByRuleVersion, StartLatest = source.StartLatest,
        DurationValue = source.DurationValue, DurationUnit = source.DurationUnit, EndOfMonthRule = source.EndOfMonthRule,
        EndInclusive = source.EndInclusive, EndAdjustmentDays = source.EndAdjustmentDays, DurationBasis = source.DurationBasis,
        Coverage = source.Coverage, RegionScope = source.RegionScope, Transferable = source.Transferable,
        ConditionAtCreation = source.ConditionAtCreation, RefurbishedMatchedRow = source.RefurbishedMatchedRow,
        RefurbishedFloorApplied = source.RefurbishedFloorApplied, RefurbishedFloorMonths = source.RefurbishedFloorMonths,
        RefurbishedFloorSource = source.RefurbishedFloorSource, RefurbisherPartyRef = source.RefurbisherPartyRef,
        RefurbishedStatementKind = source.RefurbishedStatementKind, ParentEntitlementId = source.ParentEntitlementId,
        RootEntitlementId = source.RootEntitlementId, ReplacementLinkId = source.ReplacementLinkId,
        SupersedesEntitlementId = source.SupersedesEntitlementId,
        AccrualCohortId = source.AccrualCohortId, RevenueScheduleRef = source.RevenueScheduleRef,
        AssuranceOrService = source.AssuranceOrService, Status = sourceStatus,
        Confidence = source.Confidence, StatutoryExtensionApplied = source.StatutoryExtensionApplied,
        Provenance = source.Provenance, SourceDocumentType = source.SourceDocumentType, SourceDocumentId = source.SourceDocumentId,
        SourceDocumentLine = source.SourceDocumentLine, CustomerPoRef = source.CustomerPoRef,
        CreatedEventId = eventId, CreatedAt = clock.UtcNow,
    };

    private static void EnsureTransferableState(IReadOnlyCollection<Entitlement> sources)
    {
        if (sources.FirstOrDefault(e => e.Status == EntitlementStatus.Suspended) is { } suspended)
            throw new CommandRefusedException("source_entitlement_suspended",
                $"Entitlement {suspended.EntitlementId} is suspended and its open suspension belongs to the recorded unit.",
                "resume the entitlement, then correct the document serials and suspend the physical unit if still required");
    }
}
