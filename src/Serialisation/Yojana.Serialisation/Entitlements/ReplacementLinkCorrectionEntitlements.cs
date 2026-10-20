using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Entitlements;

/// <summary>
/// Compensates one mistaken replacement transfer, then delegates the corrected leg to the ordinary transfer handler.
/// It owns no policy arithmetic: the named policy and original replacement date are passed back through that seam.
/// </summary>
public sealed class ReplacementLinkCorrectionEntitlements(
    SerialisationDbContext db,
    EntitlementLedger ledger,
    IJurisdictionRuleResolver jurisdictionRules,
    IJurisdictionRegionPort regions,
    ITenantContext tenant,
    IClock clock)
{
    public sealed record Result(Guid VoidedEntitlementId, Guid RestoredSourceEntitlementId,
        TransferEntitlementResult Transfer, Guid ReversalEventId);

    public async Task<Result> ApplyAsync(Guid commandId, ReplacementLink previousLink, Guid correctedLinkId,
        Guid actualReplacementUnitId, DateOnly replacementDate, DateTime eventTime, string actorRef,
        CorrectionMetadata metadata, CancellationToken cancellationToken)
    {
        if (previousLink.TargetEntitlementId is not { } wrongEntitlementId)
            throw new CommandRefusedException("replacement_target_entitlement_missing",
                "The recorded replacement link has no target entitlement to reverse.");
        if (string.IsNullOrWhiteSpace(previousLink.PolicyApplied))
            throw new CommandRefusedException("replacement_policy_missing",
                "The recorded replacement link has no applied policy to preserve.");

        var source = await ledger.LoadAsync(previousLink.SourceEntitlementId, cancellationToken);
        var wrong = await ledger.LoadAsync(wrongEntitlementId, cancellationToken);
        if (wrong.UnitId != previousLink.ReplacementUnitId || wrong.ReplacementLinkId != previousLink.ReplacementLinkId)
            throw new CommandRefusedException("replacement_entitlement_mismatch",
                "The recorded target entitlement does not belong to the recorded replacement unit and link.");
        EntitlementLedger.Transition(wrong, EntitlementEventType.Voided);
        wrong.ClosedReason = EntitlementClosedReason.VoidedDocumentCorrection;
        wrong.ClosedEffective = replacementDate;
        wrong.ClosedAt = clock.UtcNow;
        var voidKey = EntitlementLedger.Key(commandId, wrong.UnitId, EntitlementEventType.Voided, eventTime);
        await ledger.AppendAsync(wrong, EntitlementEventType.Voided, voidKey, eventTime, actorRef, new
        {
            entitlement_id = wrong.EntitlementId,
            coverage_family = wrong.CoverageFamily,
            direction = wrong.Direction,
            reason = EntitlementClosedReason.VoidedDocumentCorrection,
            effective = replacementDate,
            replacement_link_id = previousLink.ReplacementLinkId,
        }, null, cancellationToken, correction: metadata);

        var reversalEventId = TimeOrderedId.New(eventTime);
        var restored = CopySource(source, previousLink, reversalEventId, eventTime);
        db.Entitlements.Add(restored);
        var limits = await db.EntitlementUsageLimits.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.EntitlementId == source.EntitlementId)
            .ToListAsync(cancellationToken);
        foreach (var limit in limits)
        {
            db.EntitlementUsageLimits.Add(new EntitlementUsageLimit
            {
                TenantId = tenant.TenantId,
                EntitlementId = restored.EntitlementId,
                MeterType = limit.MeterType,
                LimitValue = limit.LimitValue,
                UsedValue = limit.UsedValue,
                Period = limit.Period,
                ResetsOn = limit.ResetsOn,
            });
        }

        var reversalKey = EntitlementLedger.Key(commandId, restored.UnitId, EntitlementEventType.Transferred, eventTime);
        await ledger.AppendAsync(restored, EntitlementEventType.Transferred, reversalKey, eventTime, actorRef, new
        {
            source_entitlement_id = wrong.EntitlementId,
            target_entitlement_id = restored.EntitlementId,
            source_unit_id = wrong.UnitId,
            target_unit_id = restored.UnitId,
            replacement_link_id = previousLink.ReplacementLinkId,
            policy_applied = previousLink.PolicyApplied,
            computed_end = restored.EndDate,
            root_entitlement_id = restored.RootEntitlementId,
            chain_length = 0,
            accrual_suppressed = true,
            reversal = true,
        }, null, cancellationToken, reversalEventId, metadata);

        var transfer = await TransferEntitlementHandler.TransferAsync(new TransferEntitlement(
                commandId, restored.EntitlementId, actualReplacementUnitId, correctedLinkId, replacementDate,
                eventTime, actorRef, previousLink.PolicyApplied, Correction: metadata),
            db, ledger, tenant, clock, null, false, jurisdictionRules, regions, cancellationToken);
        return new(wrong.EntitlementId, restored.EntitlementId, transfer, reversalEventId);
    }

    private Entitlement CopySource(Entitlement source, ReplacementLink link, Guid eventId, DateTime eventTime) => new()
    {
        TenantId = tenant.TenantId,
        EntitlementId = TimeOrderedId.New(eventTime),
        UnitId = source.UnitId,
        Scope = source.Scope,
        ScopeChildUnitId = source.ScopeChildUnitId,
        ScopeItemClass = source.ScopeItemClass,
        Type = source.Type,
        CoverageFamily = source.CoverageFamily,
        Direction = source.Direction,
        OwnerPartyRef = source.OwnerPartyRef,
        OwnerPartyType = source.OwnerPartyType,
        ObligorEntityId = source.ObligorEntityId,
        PayerPartyRef = source.PayerPartyRef,
        PayerPartyType = source.PayerPartyType,
        PolicyVersionId = source.PolicyVersionId,
        StartTrigger = source.StartTrigger,
        StartBasis = source.StartBasis,
        StartEvidenceRef = source.StartEvidenceRef,
        StartDate = source.StartDate,
        StartTimeZone = source.StartTimeZone,
        PurchaseDate = source.PurchaseDate,
        EndDate = source.EndDate,
        ComputedByRuleVersion = source.ComputedByRuleVersion,
        StartLatest = source.StartLatest,
        DurationValue = source.DurationValue,
        DurationUnit = source.DurationUnit,
        EndOfMonthRule = source.EndOfMonthRule,
        EndInclusive = source.EndInclusive,
        EndAdjustmentDays = source.EndAdjustmentDays,
        DurationBasis = source.DurationBasis,
        Coverage = source.Coverage,
        RegionScope = source.RegionScope,
        Transferable = source.Transferable,
        ConditionAtCreation = source.ConditionAtCreation,
        RefurbishedMatchedRow = source.RefurbishedMatchedRow,
        RefurbishedFloorApplied = source.RefurbishedFloorApplied,
        RefurbishedFloorMonths = source.RefurbishedFloorMonths,
        RefurbishedFloorSource = source.RefurbishedFloorSource,
        RefurbisherPartyRef = source.RefurbisherPartyRef,
        RefurbishedStatementKind = source.RefurbishedStatementKind,
        ParentEntitlementId = source.EntitlementId,
        RootEntitlementId = source.RootEntitlementId ?? source.EntitlementId,
        ReplacementLinkId = link.ReplacementLinkId,
        AccrualCohortId = null,
        RevenueScheduleRef = source.RevenueScheduleRef,
        AssuranceOrService = source.AssuranceOrService,
        Status = EntitlementStatus.Active,
        Confidence = source.Confidence,
        StatutoryExtensionApplied = source.StatutoryExtensionApplied,
        Provenance = EntitlementProvenance.Transferred,
        SourceDocumentType = source.SourceDocumentType,
        SourceDocumentId = source.SourceDocumentId,
        SourceDocumentLine = source.SourceDocumentLine,
        CustomerPoRef = source.CustomerPoRef,
        CreatedEventId = eventId,
        CreatedAt = clock.UtcNow,
    };
}
