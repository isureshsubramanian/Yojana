using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Queries;

namespace Yojana.Serialisation.Claims;

public static class SubmitClaimHandler
{
    public static async Task<SubmitClaimResult> Handle(SubmitClaim command, SerialisationDbContext db, UnitQueries units, ClaimUnitFacts facts,
        ClaimLedger ledger, LegacyUnknownClaimUnitCreator legacyUnits, DocumentNumbers numbers, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.Claims.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.CommandId == command.CommandId, cancellationToken) is { } replay)
        {
            var replayLines = await db.ClaimLines.AsNoTracking().Where(l => l.TenantId == tenantId && l.ClaimId == replay.ClaimId).OrderBy(l => l.Sequence).Select(l => l.ClaimLineId).ToListAsync(cancellationToken);
            return new(replay.ClaimId, replay.UnitId, replay.Status, replayLines, replay.LastEventId, true);
        }

        if (command.Lines.Count == 0) throw new CommandRefusedException("claim_lines_required", "A claim must contain at least one line.");
        if (!ClaimRemedy.All.Contains(command.Remedy)) throw new CommandRefusedException("claim_remedy_invalid", $"'{command.Remedy}' is not a claim remedy.");
        if (string.IsNullOrWhiteSpace(command.ConsumerChoiceEvidenceRef))
        {
            throw new CommandRefusedException("consumer_choice_evidence_required", "The consumer's informed repair-or-replacement choice must be recorded as evidence.");
        }

        var unitId = command.UnitId;
        if (unitId is null && command.Identifier is { } identifier)
        {
            var lookup = await units.LookupAsync(identifier.Value, identifier.IdentifierType, cancellationToken);
            var found = lookup.Hits.Select(h => h.UnitId).Distinct().ToList();
            if (found.Count > 1) throw new CommandRefusedException("identifier_ambiguous", $"Identifier '{identifier.Value}' resolves to {found.Count} units.", "supply the unit id or an item-qualified identifier");
            unitId = found.SingleOrDefault();
            if (unitId == Guid.Empty) unitId = null;
        }

        var claimId = TimeOrderedId.New(command.EventTime);
        if (unitId is null)
        {
            unitId = await legacyUnits.CreateAsync(command, claimId, cancellationToken);
        }

        await facts.ReadAsync(unitId.Value, cancellationToken);

        var asBuilt = await facts.AsBuiltUnitsAsync(unitId.Value, command.ClaimDate, cancellationToken);
        foreach (var line in command.Lines)
        {
            if (!ClaimLineKind.All.Contains(line.Kind)) throw new CommandRefusedException("claim_line_kind_invalid", $"'{line.Kind}' is not a claim line kind.");
            if (line.RequestedAmount < 0) throw new CommandRefusedException("claim_amount_invalid", "Claim amounts cannot be negative.");
            if (string.IsNullOrWhiteSpace(line.CoverageFamily)) throw new CommandRefusedException("coverage_family_required", "Every claim line needs a coverage family.");
            var target = line.UnitId ?? unitId.Value;
            if (!asBuilt.Contains(target)) throw new CommandRefusedException("claim_line_unit_not_in_asbuilt", $"Unit {target} was not in unit {unitId} as-built on {command.ClaimDate:yyyy-MM-dd}.");
            await facts.ReadAsync(target, cancellationToken);
        }

        var eventId = ClaimLedger.EventId(command.EventTime);
        var claim = new Claim
        {
            TenantId = tenantId, ClaimId = claimId, ClaimNumber = await numbers.NextAsync(DocumentNumbers.Claim, command.EventTime, cancellationToken),
            CommandId = command.CommandId, UnitId = unitId.Value, ClaimDate = command.ClaimDate,
            ClaimantPartyRef = command.ClaimantPartyRef, SettlementPartyRef = command.SettlementPartyRef, ServiceEntityId = command.ServiceEntityId,
            ObligorEntityId = command.ObligorEntityId, ServiceCountry = command.ServiceCountry.Trim().ToUpperInvariant(), Currency = command.Currency.Trim().ToUpperInvariant(),
            CoverageSource = ClaimCoverageSource.Chargeable, Remedy = command.Remedy, Status = ClaimStatus.Submitted, SubmittedAt = command.EventTime,
            SubmittedBy = command.ActorRef, LastEventId = eventId,
        };
        db.Claims.Add(claim);
        var claimLines = command.Lines.Select((line, sequence) => new ClaimLine
        {
            TenantId = tenantId, ClaimLineId = TimeOrderedId.New(command.EventTime), ClaimId = claimId, UnitId = line.UnitId ?? unitId.Value,
            Sequence = sequence + 1, Kind = line.Kind, CoverageFamily = line.CoverageFamily, Description = line.Description,
            RequestedAmount = line.RequestedAmount, CoverageSource = ClaimCoverageSource.Chargeable,
            Measurements = JsonSerializer.Serialize(line.Measurements ?? new Dictionary<string, decimal>(), LedgerJson.Options),
        }).ToList();
        db.ClaimLines.AddRange(claimLines);

        var evidence = command.Evidence.Select(e => NewEvidence(tenantId, claimId, e.Kind, e.Reference, e.Description, command)).ToList();
        if (command.ProofOfPurchaseRef is { Length: > 0 } proof) evidence.Add(NewEvidence(tenantId, claimId, ClaimEvidenceKind.ProofOfPurchase, proof, null, command));
        evidence.Add(NewEvidence(tenantId, claimId, ClaimEvidenceKind.ConsumerRemedyChoice, command.ConsumerChoiceEvidenceRef, command.Remedy, command));
        db.ClaimEvidence.AddRange(evidence);

        await ledger.AppendAsync(
        [
            new UnitEventDraft
            {
                UnitId = claim.UnitId, EventTime = command.EventTime, EventType = ClaimEventType.Submitted, PresetEventId = eventId,
                LegalEntityId = claim.ServiceEntityId, ActorRef = command.ActorRef, IdempotencyKey = ClaimLedger.Key(command.CommandId, claim.UnitId, ClaimEventType.Submitted, command.EventTime),
                Attributes = JsonObjectText.Of(new { claim_id = claimId, claim_date = claim.ClaimDate, claimant_party_ref = claim.ClaimantPartyRef, settlement_party_ref = claim.SettlementPartyRef, line_ids = claimLines.Select(l => l.ClaimLineId), remedy = claim.Remedy }),
            },
        ], cancellationToken);
        return new(claimId, claim.UnitId, claim.Status, claimLines.Select(l => l.ClaimLineId).ToList(), eventId, false);
    }

    private static ClaimEvidence NewEvidence(TenantId tenantId, Guid claimId, string kind, string reference, string? description, SubmitClaim command) => new()
    {
        TenantId = tenantId, EvidenceId = TimeOrderedId.New(command.EventTime), ClaimId = claimId, Kind = kind, Reference = reference,
        Description = description, RecordedAt = command.EventTime, ActorRef = command.ActorRef,
    };
}

public static class AdjudicateClaimHandler
{
    public static async Task<AdjudicateClaimResult> Handle(AdjudicateClaim command, SerialisationDbContext db, ClaimLedger ledger, ClaimUnitFacts facts,
        PolicyResolver policies, ISettlementFormulaPort formulas, IItemQueryPort items, IEntitlementRecoveryQuery recoveries, ClaimReviewSettings reviewSettings,
        DocumentNumbers numbers, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var claim = await ledger.LoadAsync(command.ClaimId, cancellationToken);
        var key = ClaimLedger.Key(command.CommandId, claim.UnitId, ClaimEventType.Adjudicated, command.EventTime);
        if (await ledger.ReplayedAsync(key, command.EventTime, cancellationToken) is { } replayed)
        {
            return await ResultAsync(db, tenantId, claim, replayed.EventId, true, cancellationToken);
        }

        var root = await facts.ReadAsync(claim.UnitId, cancellationToken);
        var item = await items.GetAsync(root.ItemId, cancellationToken);
        if (claim.Remedy == ClaimRemedy.Replacement && ClaimAdjudicationRules.IsDiscontinued(item, claim.ClaimDate))
        {
            if (item?.SuccessorItemId is not { } successorItemId)
            {
                throw new CommandRefusedException("replacement_successor_required", "A discontinued item cannot be replaced without a successor item.",
                    "choose repair or goodwill");
            }

            claim.SuccessorItemId = successorItemId;
        }
        else if (item is { SuccessorItemId: { } successorItemId } && ClaimAdjudicationRules.IsDiscontinued(item, claim.ClaimDate))
        {
            claim.SuccessorItemId = successorItemId;
        }

        ClaimLedger.Transition(claim, ClaimEventType.Adjudicating);
        var lines = await db.ClaimLines.Where(l => l.TenantId == tenantId && l.ClaimId == claim.ClaimId).OrderBy(l => l.Sequence).ToListAsync(cancellationToken);
        var rootBlock = ClaimAdjudicationRules.BlockingFlag(root.OverlayFlags);
        var coveredSources = new HashSet<string>(StringComparer.Ordinal);
        var cohorts = new HashSet<Guid>();
        var obligors = new HashSet<Guid>();

        foreach (var line in lines)
        {
            var unit = line.UnitId == root.UnitId ? root : await facts.ReadAsync(line.UnitId, cancellationToken);
            var blocked = rootBlock ?? ClaimAdjudicationRules.BlockingFlag(unit.OverlayFlags);
            if (blocked is not null)
            {
                Deny(line, $"overlay_{blocked}");
                continue;
            }

            if (ClaimAdjudicationRules.ItemDenialReason(item, claim.ClaimDate, line.Kind) is { } itemDenialReason)
            {
                Deny(line, itemDenialReason);
                continue;
            }

            var resolution = await policies.ResolveAsync(new PolicyQuestion(line.UnitId, unit.ItemId, line.CoverageFamily, claim.ClaimDate,
                claim.ClaimantPartyRef, Country: claim.ServiceCountry, CoverageDate: claim.ClaimDate, Direction: EntitlementDirection.Outbound), cancellationToken);
            if (resolution.Level == PolicyLevel.None || resolution.PolicyVersionId is null)
            {
                Deny(line, "no_applicable_coverage");
                continue;
            }

            if (resolution.OutOfRegion)
            {
                Deny(line, "service_country_out_of_region");
                continue;
            }

            if (resolution.EntitlementStatus == EntitlementStatus.Suspended)
            {
                Deny(line, "entitlement_suspended");
                continue;
            }

            var coverage = CoverageFor(resolution.Coverage, line.Kind);
            var measurements = JsonSerializer.Deserialize<Dictionary<string, decimal>>(line.Measurements, LedgerJson.Options) ?? [];
            var calculated = await formulas.CalculateAsync(new SettlementFormulaQuestion(resolution.SettlementFormula ?? "full", line.RequestedAmount, coverage, measurements), cancellationToken);
            line.CoveragePct = coverage;
            line.ApprovedAmount = Math.Min(line.RequestedAmount, calculated.ApprovedAmount);
            line.EntitlementId = resolution.EntitlementId;
            line.PolicyVersionId = resolution.PolicyVersionId;
            line.AccrualCohortId = resolution.AccrualCohortId;
            line.SettlementFormula = resolution.SettlementFormula ?? "full";
            line.ChainLength = resolution.ChainLength;
            line.CoverageSource = Source(resolution.EntitlementType, resolution.Level);
            line.DenialReason = line.ApprovedAmount == 0 ? "coverage_percentage_zero" : line.ApprovedAmount < line.RequestedAmount ? "partially_uncovered" : null;
            if (line.ApprovedAmount > 0)
            {
                coveredSources.Add(line.CoverageSource);
                if (resolution.AccrualCohortId is { } cohort) cohorts.Add(cohort);
                if (resolution.ObligorEntityId is { } obligor) obligors.Add(obligor);
            }
        }

        if (coveredSources.Count > 1) throw new CommandRefusedException("mixed_coverage_sources", "One claim cannot post against more than one coverage source.", "split the lines into one claim per coverage source");
        if (cohorts.Count > 1) throw new CommandRefusedException("multiple_accrual_cohorts", "One claim cannot draw down more than one warranty accrual cohort.", "split the lines by accrual cohort");
        if (obligors.Count > 1) throw new CommandRefusedException("multiple_obligors", "One claim cannot be posted to more than one obligor.", "split the lines by obligor");

        var selectedEntitlementIds = lines.Select(l => l.EntitlementId).OfType<Guid>().Distinct().ToList();
        var lowConfidence = selectedEntitlementIds.Count > 0 && await db.Entitlements.AsNoTracking()
            .AnyAsync(e => e.TenantId == tenantId && selectedEntitlementIds.Contains(e.EntitlementId) && e.Confidence == Confidence.Low, cancellationToken);
        if (lowConfidence)
        {
            var reviewPolicy = await reviewSettings.GetAsync(cancellationToken);
            ClaimAdjudicationRules.RequireLowConfidenceReviewer(command.ReviewedBy, command.ActorRef, reviewPolicy.LowConfidenceReviewerRole);
            claim.AdjudicatedBy = command.ReviewedBy!.UserRef;
            claim.ReviewerRole = reviewPolicy.LowConfidenceReviewerRole;
        }
        else
        {
            if (command.ReviewedBy is { } reviewer)
            {
                // HTTP always supplies the signed-in reviewer. Internal callers that pre-date reviewer evidence remain
                // trusted for ordinary cover; they can never pass the low-confidence branch above without it.
                ClaimAdjudicationRules.RequireStandardAdjudicator(reviewer, command.ActorRef);
                claim.AdjudicatedBy = reviewer.UserRef;
                claim.ReviewerRole = YojanaRole.ServiceManager;
            }
            else
            {
                claim.AdjudicatedBy = command.ActorRef;
                claim.ReviewerRole = null;
            }
        }

        var totalApproved = lines.Sum(l => l.ApprovedAmount);
        var fullyCovered = lines.All(l => l.DenialReason is null && l.ApprovedAmount == l.RequestedAmount);
        var outcome = fullyCovered ? ClaimStatus.Approved : totalApproved > 0 ? ClaimStatus.PartiallyApproved : ClaimStatus.Denied;
        claim.Status = ClaimStateMachine.Next(claim.Status, outcome)
            ?? throw new CommandRefusedException("claim_transition_not_allowed", $"Claim {claim.ClaimId} cannot finish adjudication as {outcome}.");
        claim.CoverageSource = coveredSources.SingleOrDefault() ?? ClaimCoverageSource.Chargeable;
        claim.DenialReason = outcome == ClaimStatus.Denied ? lines.Select(l => l.DenialReason).FirstOrDefault(r => r is not null) ?? "no_applicable_coverage"
            : outcome == ClaimStatus.PartiallyApproved ? "partially_uncovered" : null;
        claim.ChainLength = lines.Max(l => l.ChainLength);
        claim.EntitlementIds = JsonSerializer.Serialize(lines.Select(l => l.EntitlementId).OfType<Guid>().Distinct().Order().ToList(), LedgerJson.Options);
        claim.AdjudicatedAt = command.EventTime;
        if (obligors.Count == 1) claim.ObligorEntityId = obligors.Single();

        var eventId = ClaimLedger.EventId(command.EventTime);
        claim.LastEventId = eventId;
        var drafts = new List<UnitEventDraft>
        {
            new()
            {
                UnitId = claim.UnitId, EventTime = command.EventTime, EventType = ClaimEventType.Adjudicated, PresetEventId = eventId,
                LegalEntityId = claim.ObligorEntityId, ActorRef = command.ActorRef, IdempotencyKey = key,
                Attributes = JsonObjectText.Of(new
                {
                    claim_id = claim.ClaimId, cohort_id = claim.CoverageSource == ClaimCoverageSource.Recall || cohorts.Count == 0 ? (Guid?)null : cohorts.Single(), coverage_source = claim.CoverageSource,
                    approved_amounts_by_line = lines.Select(l => new { claim_line_id = l.ClaimLineId, requested_amount = l.RequestedAmount, approved_amount = l.ApprovedAmount, coverage_pct = l.CoveragePct, coverage_source = l.CoverageSource, entitlement_id = l.EntitlementId }),
                    service_entity_id = claim.ServiceEntityId, obligor_entity_id = claim.ObligorEntityId, claimant_party_ref = claim.ClaimantPartyRef,
                    settlement_party_ref = claim.SettlementPartyRef, status = claim.Status, denial_reason = claim.DenialReason,
                    chain_length = claim.ChainLength, remedy = claim.Remedy, adjudicated_by = claim.AdjudicatedBy, reviewer_role = claim.ReviewerRole, successor_item_id = claim.SuccessorItemId,
                }),
            },
        };

        var recoveryIds = new List<Guid>();
        if (claim.Status is ClaimStatus.Approved or ClaimStatus.PartiallyApproved)
        {
            foreach (var line in lines.Where(l => l.ApprovedAmount > 0))
            {
                foreach (var cover in await recoveries.FindAsync(claim.UnitId, line.UnitId, claim.ClaimDate, cancellationToken))
                {
                    var recoveryId = TimeOrderedId.New(command.EventTime);
                    var recoveryEventId = ClaimLedger.EventId(command.EventTime);
                    db.RecoveryClaims.Add(new RecoveryClaim
                    {
                        TenantId = tenantId, RecoveryClaimId = recoveryId, RecoveryNumber = await numbers.NextAsync(DocumentNumbers.Recovery, command.EventTime, cancellationToken),
                        CustomerClaimId = claim.ClaimId, ClaimLineId = line.ClaimLineId,
                        InboundEntitlementId = cover.EntitlementId, ChildUnitId = cover.UnitId, VendorPartyRef = cover.VendorPartyRef,
                        RequestedAmount = line.ApprovedAmount, Currency = claim.Currency, Status = RecoveryClaimStatus.Raised,
                        RaisedAt = command.EventTime, RaisedEventId = recoveryEventId,
                    });
                    recoveryIds.Add(recoveryId);
                    drafts.Add(new UnitEventDraft
                    {
                        UnitId = cover.UnitId, EventTime = command.EventTime, EventType = ClaimEventType.RecoveryRaised, PresetEventId = recoveryEventId,
                        LegalEntityId = claim.ServiceEntityId, ActorRef = command.ActorRef,
                        IdempotencyKey = ClaimLedger.Key(command.CommandId, recoveryId, ClaimEventType.RecoveryRaised, command.EventTime),
                        Attributes = JsonObjectText.Of(new { recovery_claim_id = recoveryId, customer_claim_id = claim.ClaimId, claim_line_id = line.ClaimLineId, inbound_entitlement_id = cover.EntitlementId, child_unit_id = cover.UnitId, vendor_party_ref = cover.VendorPartyRef, requested_amount = line.ApprovedAmount, currency = claim.Currency }),
                    });
                }
            }
        }

        claim.RecoveryRaised = recoveryIds.Count > 0;
        var uncovered = lines.Where(l => l.RequestedAmount > l.ApprovedAmount).ToList();
        if (uncovered.Count > 0)
        {
            drafts.Add(new UnitEventDraft
            {
                UnitId = claim.UnitId, EventTime = command.EventTime, EventType = ClaimEventType.ChargeableQuoteRequested,
                LegalEntityId = claim.ServiceEntityId, ActorRef = command.ActorRef,
                IdempotencyKey = ClaimLedger.Key(command.CommandId, claim.ClaimId, ClaimEventType.ChargeableQuoteRequested, command.EventTime),
                Attributes = JsonObjectText.Of(new { claim_id = claim.ClaimId, claimant_party_ref = claim.ClaimantPartyRef, settlement_party_ref = claim.SettlementPartyRef, currency = claim.Currency,
                    lines = uncovered.Select(l => new { claim_line_id = l.ClaimLineId, kind = l.Kind, uncovered_amount = l.RequestedAmount - l.ApprovedAmount, reason = l.DenialReason }) }),
            });
        }

        await ledger.AppendAsync(drafts, cancellationToken);
        return new(claim.ClaimId, claim.Status, claim.CoverageSource, claim.DenialReason, claim.ChainLength, lines.Select(ClaimQueries.Decision).ToList(), recoveryIds, eventId, false,
            SuccessorItemId: claim.SuccessorItemId);
    }

    private static void Deny(ClaimLine line, string reason)
    {
        line.CoveragePct = 0;
        line.ApprovedAmount = 0;
        line.CoverageSource = ClaimCoverageSource.Chargeable;
        line.DenialReason = reason;
        line.EntitlementId = null;
        line.PolicyVersionId = null;
        line.AccrualCohortId = null;
        line.ChainLength = 0;
    }

    private static decimal CoverageFor(CoverageTerms? coverage, string kind) => coverage is null ? 0 : kind switch
    {
        ClaimLineKind.Parts => coverage.PartsPct,
        ClaimLineKind.Labour => coverage.LabourPct,
        ClaimLineKind.Travel => coverage.TravelPct,
        _ => 0,
    };

    private static string Source(string? entitlementType, string level) => entitlementType switch
    {
        EntitlementType.RecallCampaign => ClaimCoverageSource.Recall,
        EntitlementType.Goodwill => ClaimCoverageSource.Goodwill,
        EntitlementType.Contract or EntitlementType.Extended => ClaimCoverageSource.Contract,
        _ when level == PolicyLevel.CustomerContract => ClaimCoverageSource.Contract,
        _ => ClaimCoverageSource.Warranty,
    };

    private static async Task<AdjudicateClaimResult> ResultAsync(SerialisationDbContext db, TenantId tenantId, Claim claim, Guid eventId, bool replayed, CancellationToken cancellationToken)
    {
        var lines = await db.ClaimLines.AsNoTracking().Where(l => l.TenantId == tenantId && l.ClaimId == claim.ClaimId).OrderBy(l => l.Sequence).ToListAsync(cancellationToken);
        var recoveryIds = await db.RecoveryClaims.AsNoTracking().Where(r => r.TenantId == tenantId && r.CustomerClaimId == claim.ClaimId).Select(r => r.RecoveryClaimId).ToListAsync(cancellationToken);
        return new(claim.ClaimId, claim.Status, claim.CoverageSource, claim.DenialReason, claim.ChainLength, lines.Select(ClaimQueries.Decision).ToList(), recoveryIds, eventId, replayed,
            SuccessorItemId: claim.SuccessorItemId);
    }
}

public static class ApproveRecoveryClaimHandler
{
    public static async Task<RecoveryClaimChangeResult> Handle(ApproveRecoveryClaim command, SerialisationDbContext db, ClaimLedger ledger, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var recovery = await db.RecoveryClaims.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.RecoveryClaimId == command.RecoveryClaimId, cancellationToken)
            ?? throw new CommandRefusedException("recovery_claim_unknown", $"Recovery claim {command.RecoveryClaimId} does not exist.");
        var key = ClaimLedger.Key(command.CommandId, recovery.RecoveryClaimId, ClaimEventType.RecoveryApproved, command.EventTime);
        if (await ledger.ReplayedAsync(key, command.EventTime, cancellationToken) is { } replayed) return new(recovery.RecoveryClaimId, recovery.Status, recovery.RequestedAmount, recovery.ApprovedAmount, replayed.EventId, true);
        if (recovery.Status != RecoveryClaimStatus.Raised) throw new CommandRefusedException("recovery_claim_transition_not_allowed", $"Recovery claim {recovery.RecoveryClaimId} is {recovery.Status}; only raised claims can be approved.");
        if (command.ApprovedAmount < 0 || command.ApprovedAmount > recovery.RequestedAmount) throw new CommandRefusedException("recovery_amount_invalid", "The approved recovery amount must be between zero and the requested amount.");
        var claim = await ledger.LoadAsync(recovery.CustomerClaimId, cancellationToken);
        var eventId = ClaimLedger.EventId(command.EventTime);
        recovery.ApprovedAmount = command.ApprovedAmount;
        recovery.Status = RecoveryClaimStatus.Approved;
        recovery.ApprovedAt = command.EventTime;
        recovery.ApprovedBy = command.ActorRef;
        recovery.ApprovedEventId = eventId;
        await ledger.AppendAsync(
        [
            new UnitEventDraft
            {
                UnitId = recovery.ChildUnitId, EventTime = command.EventTime, EventType = ClaimEventType.RecoveryApproved, PresetEventId = eventId,
                LegalEntityId = claim.ServiceEntityId, ActorRef = command.ActorRef, IdempotencyKey = key,
                Attributes = JsonObjectText.Of(new { recovery_claim_id = recovery.RecoveryClaimId, vendor_party_ref = recovery.VendorPartyRef, amount = command.ApprovedAmount, currency = recovery.Currency }),
            },
        ], cancellationToken);
        return new(recovery.RecoveryClaimId, recovery.Status, recovery.RequestedAmount, recovery.ApprovedAmount, eventId, false);
    }
}

public static class SettleClaimHandler
{
    public static Task<ClaimChangeResult> Handle(SettleClaim command, ClaimLedger ledger, CancellationToken cancellationToken) =>
        ChangeClaimHandler.ChangeAsync(command.CommandId, command.ClaimId, command.EventTime, command.ActorRef, ClaimEventType.Settled, ledger, cancellationToken);
}

public static class CloseClaimHandler
{
    public static Task<ClaimChangeResult> Handle(CloseClaim command, ClaimLedger ledger, CancellationToken cancellationToken) =>
        ChangeClaimHandler.ChangeAsync(command.CommandId, command.ClaimId, command.EventTime, command.ActorRef, ClaimEventType.Closed, ledger, cancellationToken);
}

internal static class ChangeClaimHandler
{
    public static async Task<ClaimChangeResult> ChangeAsync(Guid commandId, Guid claimId, DateTime eventTime, string actorRef, string eventType, ClaimLedger ledger, CancellationToken cancellationToken)
    {
        var claim = await ledger.LoadAsync(claimId, cancellationToken);
        var key = ClaimLedger.Key(commandId, claim.UnitId, eventType, eventTime);
        if (await ledger.ReplayedAsync(key, eventTime, cancellationToken) is { } replayed) return new(claim.ClaimId, claim.Status, replayed.EventId, true);
        ClaimLedger.Transition(claim, eventType);
        var eventId = ClaimLedger.EventId(eventTime);
        claim.LastEventId = eventId;
        if (eventType == ClaimEventType.Settled) claim.SettledAt = eventTime;
        if (eventType == ClaimEventType.Closed) claim.ClosedAt = eventTime;
        await ledger.AppendAsync(
        [
            new UnitEventDraft
            {
                UnitId = claim.UnitId, EventTime = eventTime, EventType = eventType, PresetEventId = eventId,
                LegalEntityId = claim.ObligorEntityId, ActorRef = actorRef, IdempotencyKey = key,
                Attributes = JsonObjectText.Of(new { claim_id = claim.ClaimId, status = claim.Status }),
            },
        ], cancellationToken);
        return new(claim.ClaimId, claim.Status, eventId, false);
    }
}
