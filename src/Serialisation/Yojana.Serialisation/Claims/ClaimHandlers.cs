using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Claims.Risk;
using Yojana.Serialisation.Currency;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Documents;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Queries;

namespace Yojana.Serialisation.Claims;

public static class SubmitClaimHandler
{
    public static async Task<SubmitClaimResult> Handle(SubmitClaim command, SerialisationDbContext db, UnitQueries units, ClaimUnitFacts facts,
        ClaimLedger ledger, LegacyUnknownClaimUnitCreator legacyUnits, DocumentNumbers numbers, ICampaignCoverPort campaigns,
        ITenantCurrency currencies, ITenantContext tenant, IClock clock, ClaimCauseCatalogue causes, IItemQueryPort items,
        ClaimLineDecider decider, ClaimRiskEvaluator risk, CancellationToken cancellationToken)
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

        // ITenantCurrency reaches PlatformDbContext; it is safe in this handler only because PlatformSetup registers it
        // through a factory that Wolverine's code generation does not look inside.
        await causes.ValidateReportedAsync(command.ReportedCauseCode, cancellationToken);
        if (command.ReportedDescription?.Length > 4000)
            throw new CommandRefusedException("claim_description_too_long", "The customer description is at most 4,000 characters.");
        await HomeCurrencyGuard.RequireAsync(currencies, command.Currency, cancellationToken);

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

        var root = await facts.ReadAsync(unitId.Value, cancellationToken);

        if (command.CampaignId is { } campaignId)
        {
            var campaign = await campaigns.FindAsync(campaignId, unitId.Value, command.ClaimDate, cancellationToken)
                ?? throw new CommandRefusedException("campaign_not_found", $"Campaign {campaignId:N} does not exist.");
            if (campaign.Status != CampaignStatus.Active)
                throw new CommandRefusedException("campaign_not_active", $"Campaign {campaign.Code} is {campaign.Status}; claims can name only active campaigns.");
            if (!campaign.Selected)
                throw new CommandRefusedException("unit_not_in_campaign", $"The claim unit is not selected into campaign {campaign.Code}.");
        }

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
            CommandId = command.CommandId, UnitId = unitId.Value, CampaignId = command.CampaignId, ClaimDate = command.ClaimDate,
            ReportedCauseCode = command.ReportedCauseCode, ReportedDescription = command.ReportedDescription, OccurredDate = command.OccurredDate,
            ClaimantPartyRef = command.ClaimantPartyRef, SettlementPartyRef = command.SettlementPartyRef, ServiceEntityId = command.ServiceEntityId,
            ObligorEntityId = command.ObligorEntityId, ServiceCountry = command.ServiceCountry.Trim().ToUpperInvariant(), Currency = command.Currency.Trim().ToUpperInvariant(),
            CoverageSource = ClaimCoverageSource.Chargeable, Remedy = command.Remedy, Status = ClaimStatus.Submitted, SubmittedAt = command.EventTime,
            SubmittedBy = command.ActorRef, LastEventId = eventId,
            LastModifiedAt = clock.UtcNow, LastModifiedBy = command.ActorRef,
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

        var namedCampaign = claim.CampaignId is { } selectedCampaign
            ? await campaigns.FindAsync(selectedCampaign, claim.UnitId, claim.ClaimDate, cancellationToken)
            : null;
        var liveCampaign = namedCampaign is { Selected: true } && namedCampaign.CoversOn(claim.ClaimDate) ? namedCampaign : null;
        var item = await items.GetAsync(root.ItemId, cancellationToken);
        var riskLines = new List<ClaimRiskLineEvaluation>(claimLines.Count);
        foreach (var line in claimLines)
            riskLines.Add(new(line, await decider.DecideAsync(claim, root, item, line, liveCampaign, cancellationToken)));
        await risk.EvaluateAsync(claim, riskLines, ClaimRiskEvaluation.Submission, command.EventTime,
            includeUncommittedCurrentClaim: true, cancellationToken);

        await ledger.AppendAsync(
        [
            new UnitEventDraft
            {
                UnitId = claim.UnitId, EventTime = command.EventTime, EventType = ClaimEventType.Submitted, PresetEventId = eventId,
                LegalEntityId = claim.ServiceEntityId, ActorRef = command.ActorRef, IdempotencyKey = ClaimLedger.Key(command.CommandId, claim.UnitId, ClaimEventType.Submitted, command.EventTime),
                Attributes = JsonObjectText.Of(new { claim_id = claimId, claim_number = claim.ClaimNumber,
                    status = claim.Status, campaign_id = claim.CampaignId, claim_date = claim.ClaimDate,
                    claimant_party_ref = claim.ClaimantPartyRef, settlement_party_ref = claim.SettlementPartyRef,
                    line_ids = claimLines.Select(l => l.ClaimLineId), remedy = claim.Remedy }),
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
        ClaimLineDecider decider, IItemQueryPort items, IEntitlementRecoveryQuery recoveries, ClaimReviewSettings reviewSettings,
        RecoverySettings recoverySettings, DocumentNumbers numbers, ICampaignCoverPort campaigns, ClaimRiskEvaluator risk,
        ITenantCurrency currency, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var claim = await ledger.LoadAsync(command.ClaimId, cancellationToken);
        await HomeCurrencyGuard.RequireAsync(currency, claim.Currency, cancellationToken);
        var key = ClaimLedger.Key(command.CommandId, claim.UnitId, ClaimEventType.Adjudicated, command.EventTime);
        if (await ledger.ReplayedAsync(key, command.EventTime, cancellationToken) is { } replayed)
        {
            return await ResultAsync(db, tenantId, claim, replayed.EventId, true, cancellationToken);
        }

        var root = await facts.ReadAsync(claim.UnitId, cancellationToken);
        var namedCampaign = claim.CampaignId is { } campaignId
            ? await campaigns.FindAsync(campaignId, claim.UnitId, claim.ClaimDate, cancellationToken)
            : null;
        var liveCampaign = namedCampaign is { Selected: true } && namedCampaign.CoversOn(claim.ClaimDate) ? namedCampaign : null;
        var item = await items.GetAsync(root.ItemId, cancellationToken);
        Guid? successorItemId = null;
        if (claim.Remedy == ClaimRemedy.Replacement && ClaimAdjudicationRules.IsDiscontinued(item, claim.ClaimDate))
        {
            if (item?.SuccessorItemId is not { } successor)
            {
                throw new CommandRefusedException("replacement_successor_required", "A discontinued item cannot be replaced without a successor item.",
                    "choose repair or goodwill");
            }

            successorItemId = successor;
        }
        else if (item is { SuccessorItemId: { } successor } && ClaimAdjudicationRules.IsDiscontinued(item, claim.ClaimDate))
        {
            successorItemId = successor;
        }

        var lines = await db.ClaimLines.Where(l => l.TenantId == tenantId && l.ClaimId == claim.ClaimId).OrderBy(l => l.Sequence).ToListAsync(cancellationToken);
        var evaluatedLines = new List<ClaimRiskLineEvaluation>(lines.Count);
        foreach (var line in lines)
            evaluatedLines.Add(new(line, await decider.DecideAsync(claim, root, item, line, liveCampaign, cancellationToken)));
        var raised = await risk.EvaluateAsync(claim, evaluatedLines, ClaimRiskEvaluation.Adjudication, command.EventTime,
            includeUncommittedCurrentClaim: false, cancellationToken);

        var coveredSources = new HashSet<string>(StringComparer.Ordinal);
        var cohorts = new HashSet<Guid>();
        var obligors = new HashSet<Guid>();
        if (command.DenialReason is null)
        {
            foreach (var evaluated in evaluatedLines.Where(row => row.Outcome.ApprovedAmount > 0))
            {
                coveredSources.Add(evaluated.Outcome.CoverageSource);
                if (evaluated.Outcome.AccrualCohortId is { } cohort) cohorts.Add(cohort);
                if (evaluated.Outcome.ObligorEntityId is { } obligor) obligors.Add(obligor);
            }
        }

        if (coveredSources.Count > 1) throw new CommandRefusedException("mixed_coverage_sources", "One claim cannot post against more than one coverage source.", "split the lines into one claim per coverage source");
        if (cohorts.Count > 1) throw new CommandRefusedException("multiple_accrual_cohorts", "One claim cannot draw down more than one warranty accrual cohort.", "split the lines by accrual cohort");
        if (obligors.Count > 1) throw new CommandRefusedException("multiple_obligors", "One claim cannot be posted to more than one obligor.", "split the lines by obligor");

        var totalApproved = command.DenialReason is null ? evaluatedLines.Sum(row => row.Outcome.ApprovedAmount) : 0;
        var fullyCovered = command.DenialReason is null && evaluatedLines.All(row => row.Outcome.DenialReason is null
            && row.Outcome.ApprovedAmount == row.Line.RequestedAmount);
        var outcome = fullyCovered ? ClaimStatus.Approved : totalApproved > 0 ? ClaimStatus.PartiallyApproved : ClaimStatus.Denied;
        if (outcome is ClaimStatus.Approved or ClaimStatus.PartiallyApproved)
        {
            var openRules = await db.ClaimRiskFlags.AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.ClaimId == claim.ClaimId && row.Status == ClaimRiskFlagStatus.Open)
                .Select(row => row.RuleCode).ToListAsync(cancellationToken);
            openRules = openRules.Concat(raised.Where(row => row.Status == ClaimRiskFlagStatus.Open).Select(row => row.RuleCode))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (openRules.Count > 0)
            {
                var lineDecisions = evaluatedLines.Select(row => Decision(row.Line, row.Outcome)).ToList();
                var denialReason = outcome == ClaimStatus.PartiallyApproved ? "partially_uncovered" : null;
                return new(claim.ClaimId, outcome, coveredSources.SingleOrDefault() ?? ClaimCoverageSource.Chargeable,
                    denialReason, evaluatedLines.Max(row => row.Outcome.ChainLength), lineDecisions, [], Guid.Empty, false,
                    successorItemId, "claim_risk_review_pending", openRules);
            }
        }

        ClaimLedger.Transition(claim, ClaimEventType.Adjudicating);
        // The reserve belongs to the campaign's cover: a claim that falls back to the policy stack posts to warranty, not to the campaign reserve.
        claim.ReserveTag = liveCampaign?.ReserveTag;
        claim.CampaignCoverReason = claim.CampaignId is null || liveCampaign is not null
            ? null
            : namedCampaign?.NotCoveredReason(claim.ClaimDate) ?? ClaimCampaignCoverReason.Closed;
        claim.SuccessorItemId = successorItemId;
        if (command.DenialReason is { } manualReason)
        {
            // A finding by the adjudicator (unauthorised parts, misuse, no fault found) denies the whole claim (MOD-07
            // invariant 1); risk flags never turn this finding into an automatic denial and do not hold it.
            if (!ClaimManualDenialReason.All.Contains(manualReason)) throw new CommandRefusedException("claim_denial_reason_invalid", $"'{manualReason}' is not a claim denial reason.");
            if (string.IsNullOrWhiteSpace(command.DenialNote)) throw new CommandRefusedException("claim_denial_note_required", "A claim denied by the adjudicator needs a note saying what was found.");
            foreach (var line in lines) ClaimLineDecider.Apply(line, ClaimLineOutcome.Denied(manualReason));
            db.ClaimEvidence.Add(new ClaimEvidence
            {
                TenantId = tenantId, EvidenceId = TimeOrderedId.New(command.EventTime), ClaimId = claim.ClaimId, Kind = ClaimEvidenceKind.AdjudicationNote,
                Reference = manualReason, Description = command.DenialNote.Trim(), RecordedAt = command.EventTime, ActorRef = command.ActorRef,
            });
        }
        else
        {
            foreach (var evaluated in evaluatedLines) ClaimLineDecider.Apply(evaluated.Line, evaluated.Outcome);
        }

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
        claim.LastModifiedAt = clock.UtcNow;
        claim.LastModifiedBy = command.ActorRef;
        var drafts = new List<UnitEventDraft>
        {
            new()
            {
                UnitId = claim.UnitId, EventTime = command.EventTime, EventType = ClaimEventType.Adjudicated, PresetEventId = eventId,
                LegalEntityId = claim.ObligorEntityId, ActorRef = command.ActorRef, IdempotencyKey = key,
                Attributes = JsonObjectText.Of(new
                {
                    claim_id = claim.ClaimId, cohort_id = liveCampaign is not null || claim.CoverageSource == ClaimCoverageSource.Recall || cohorts.Count == 0 ? (Guid?)null : cohorts.Single(), coverage_source = claim.CoverageSource,
                    approved_amounts_by_line = lines.Select(l => new { claim_line_id = l.ClaimLineId, requested_amount = l.RequestedAmount, approved_amount = l.ApprovedAmount, coverage_pct = l.CoveragePct, coverage_source = l.CoverageSource, entitlement_id = l.EntitlementId }),
                    service_entity_id = claim.ServiceEntityId, obligor_entity_id = claim.ObligorEntityId, claimant_party_ref = claim.ClaimantPartyRef,
                    settlement_party_ref = claim.SettlementPartyRef, currency = claim.Currency, status = claim.Status, denial_reason = claim.DenialReason,
                    chain_length = claim.ChainLength, remedy = claim.Remedy, adjudicated_by = claim.AdjudicatedBy, reviewer_role = claim.ReviewerRole, successor_item_id = claim.SuccessorItemId,
                    campaign_id = claim.CampaignId, campaign_code = namedCampaign?.Code, reserve_tag = claim.ReserveTag, campaign_cover_reason = claim.CampaignCoverReason,
                }),
            },
        };

        var recoveryIds = new List<Guid>();
        string? notRaisedReason = null;
        if (claim.Status is ClaimStatus.Approved or ClaimStatus.PartiallyApproved)
        {
            var recoveryPolicy = await recoverySettings.GetAsync(cancellationToken);
            // Only a claim the campaign actually covers follows the campaign rule; one that fell back is a warranty claim.
            notRaisedReason = liveCampaign is not null
                ? recoveryPolicy.CampaignClaims == RecoveryCampaignClaims.NoRecovery ? RecoveryNotRaisedReason.CampaignClaim : null
                : claim.CoverageSource == ClaimCoverageSource.Recall ? RecoveryNotRaisedReason.RecallClaim : null;
            if (notRaisedReason is null)
            {
                foreach (var line in lines.Where(l => l.ApprovedAmount > 0))
                {
                    var decision = RecoverySelection.Select(recoveryPolicy, claim.UnitId, line, await recoveries.FindAsync(claim.UnitId, line.UnitId, claim.ClaimDate, cancellationToken));
                    notRaisedReason ??= decision.NotRaisedReason;
                    foreach (var cover in decision.Covers)
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
                            LastModifiedAt = clock.UtcNow, LastModifiedBy = command.ActorRef,
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
        }

        claim.RecoveryRaised = recoveryIds.Count > 0;
        claim.RecoveryNotRaisedReason = claim.RecoveryRaised ? null : notRaisedReason;
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

    private static ClaimLineDecision Decision(ClaimLine line, ClaimLineOutcome outcome) =>
        new(line.ClaimLineId, line.UnitId, line.CoverageFamily, line.RequestedAmount, outcome.CoveragePct,
            outcome.ApprovedAmount, outcome.CoverageSource, outcome.EntitlementId, outcome.DenialReason,
            outcome.ChainLength, outcome.WaitingPeriodPurchaseDate, outcome.FirstClaimableDate,
            outcome.WaitingPeriodNotAppliedReason);

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
    public static async Task<RecoveryClaimChangeResult> Handle(ApproveRecoveryClaim command, SerialisationDbContext db, ClaimLedger ledger,
        ITenantCurrency currency, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var recovery = await db.RecoveryClaims.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.RecoveryClaimId == command.RecoveryClaimId, cancellationToken)
            ?? throw new CommandRefusedException("recovery_claim_unknown", $"Recovery claim {command.RecoveryClaimId} does not exist.");
        var key = ClaimLedger.Key(command.CommandId, recovery.RecoveryClaimId, ClaimEventType.RecoveryApproved, command.EventTime);
        if (await ledger.ReplayedAsync(key, command.EventTime, cancellationToken) is { } replayed) return new(recovery.RecoveryClaimId, recovery.Status, recovery.RequestedAmount, recovery.ApprovedAmount, replayed.EventId, true, recovery.VendorReference, recovery.VendorNote);
        await HomeCurrencyGuard.RequireAsync(currency, recovery.Currency, cancellationToken);
        if (recovery.Status != RecoveryClaimStatus.Raised) throw new CommandRefusedException("recovery_claim_transition_not_allowed", $"Recovery claim {recovery.RecoveryClaimId} is {recovery.Status}; only raised claims can be approved.");
        if (command.ApprovedAmount < 0 || command.ApprovedAmount > recovery.RequestedAmount) throw new CommandRefusedException("recovery_amount_invalid", "The approved recovery amount must be between zero and the requested amount.");
        var vendorReference = string.IsNullOrWhiteSpace(command.VendorReference) ? null : command.VendorReference.Trim();
        var vendorNote = string.IsNullOrWhiteSpace(command.VendorNote) ? null : command.VendorNote.Trim();
        if (vendorReference?.Length > 100) throw new CommandRefusedException("vendor_reference_too_long", "The vendor reference must be 100 characters or fewer.");
        if (vendorNote?.Length > 500) throw new CommandRefusedException("vendor_note_too_long", "The vendor note must be 500 characters or fewer.");
        var claim = await ledger.LoadAsync(recovery.CustomerClaimId, cancellationToken);
        var eventId = ClaimLedger.EventId(command.EventTime);
        recovery.ApprovedAmount = command.ApprovedAmount;
        recovery.Status = RecoveryClaimStatus.Approved;
        recovery.ApprovedAt = command.EventTime;
        recovery.ApprovedBy = command.ActorRef;
        recovery.VendorReference = vendorReference;
        recovery.VendorNote = vendorNote;
        recovery.ApprovedEventId = eventId;
        recovery.LastModifiedAt = clock.UtcNow;
        recovery.LastModifiedBy = command.ActorRef;
        await ledger.AppendAsync(
        [
            new UnitEventDraft
            {
                UnitId = recovery.ChildUnitId, EventTime = command.EventTime, EventType = ClaimEventType.RecoveryApproved, PresetEventId = eventId,
                LegalEntityId = claim.ServiceEntityId, ActorRef = command.ActorRef, IdempotencyKey = key,
                Attributes = JsonObjectText.Of(new { recovery_claim_id = recovery.RecoveryClaimId, vendor_party_ref = recovery.VendorPartyRef, amount = command.ApprovedAmount, currency = recovery.Currency }),
            },
        ], cancellationToken);
        return new(recovery.RecoveryClaimId, recovery.Status, recovery.RequestedAmount, recovery.ApprovedAmount, eventId, false, recovery.VendorReference, recovery.VendorNote);
    }
}

public static class SettleClaimHandler
{
    public static Task<ClaimChangeResult> Handle(SettleClaim command, ClaimLedger ledger, IClock clock, CancellationToken cancellationToken) =>
        ChangeClaimHandler.ChangeAsync(command.CommandId, command.ClaimId, command.EventTime, command.ActorRef, ClaimEventType.Settled, ledger, clock, cancellationToken);
}

public static class CloseClaimHandler
{
    public static Task<ClaimChangeResult> Handle(CloseClaim command, ClaimLedger ledger, IClock clock, CancellationToken cancellationToken) =>
        ChangeClaimHandler.ChangeAsync(command.CommandId, command.ClaimId, command.EventTime, command.ActorRef, ClaimEventType.Closed, ledger, clock, cancellationToken);
}

internal static class ChangeClaimHandler
{
    public static async Task<ClaimChangeResult> ChangeAsync(Guid commandId, Guid claimId, DateTime eventTime, string actorRef,
        string eventType, ClaimLedger ledger, IClock clock, CancellationToken cancellationToken)
    {
        var claim = await ledger.LoadAsync(claimId, cancellationToken);
        var key = ClaimLedger.Key(commandId, claim.UnitId, eventType, eventTime);
        if (await ledger.ReplayedAsync(key, eventTime, cancellationToken) is { } replayed) return new(claim.ClaimId, claim.Status, replayed.EventId, true);
        ClaimLedger.Transition(claim, eventType);
        var eventId = ClaimLedger.EventId(eventTime);
        claim.LastEventId = eventId;
        claim.LastModifiedAt = clock.UtcNow;
        claim.LastModifiedBy = actorRef;
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

public static class RequestClaimEvidenceHandler
{
    public static async Task<ClaimChangeResult> Handle(RequestClaimEvidence command, SerialisationDbContext db, ClaimLedger ledger,
        ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var claim = await ledger.LoadAsync(command.ClaimId, cancellationToken);
        var key = ClaimLedger.Key(command.CommandId, claim.ClaimId, ClaimEventType.EvidenceRequested, command.EventTime);
        if (await ledger.ReplayedAsync(key, command.EventTime, cancellationToken) is { } replayed) return new(claim.ClaimId, claim.Status, replayed.EventId, true);
        DocumentVersion.RequireExpected(command.ExpectedVersion, claim.RowVersion, claim.LastModifiedBy, claim.LastModifiedAt);
        if (claim.Status is not (ClaimStatus.Submitted or ClaimStatus.Adjudicating))
        {
            throw new CommandRefusedException("claim_evidence_request_not_allowed", $"Claim {claim.ClaimNumber} is {claim.Status}; evidence can be asked for only before it is decided.");
        }

        if (string.IsNullOrWhiteSpace(command.Note)) throw new CommandRefusedException("claim_evidence_note_required", "Say what evidence is needed.");
        db.ClaimEvidence.Add(new ClaimEvidence
        {
            TenantId = tenant.TenantId, EvidenceId = TimeOrderedId.New(command.EventTime), ClaimId = claim.ClaimId, Kind = ClaimEvidenceKind.EvidenceRequested,
            Reference = claim.ClaimNumber, Description = command.Note.Trim(), RecordedAt = command.EventTime, ActorRef = command.ActorRef,
        });
        var eventId = ClaimLedger.EventId(command.EventTime);
        claim.LastModifiedAt = clock.UtcNow;
        claim.LastModifiedBy = command.ActorRef;
        claim.LastEventId = eventId;
        await ledger.AppendAsync(
        [
            new UnitEventDraft
            {
                UnitId = claim.UnitId, EventTime = command.EventTime, EventType = ClaimEventType.EvidenceRequested, PresetEventId = eventId,
                LegalEntityId = claim.ServiceEntityId, ActorRef = command.ActorRef, IdempotencyKey = key,
                Attributes = JsonObjectText.Of(new { claim_id = claim.ClaimId, note = command.Note.Trim() }),
            },
        ], cancellationToken);
        return new(claim.ClaimId, claim.Status, eventId, false);
    }
}
