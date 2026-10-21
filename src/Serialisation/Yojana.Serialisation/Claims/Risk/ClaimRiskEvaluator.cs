using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Claims.Risk;

public sealed record ClaimRiskLineEvaluation(ClaimLine Line, ClaimLineOutcome Outcome);

/// <summary>Evaluates deterministic risk facts and records each claim/rule at most once; reviewer decisions are never reopened.</summary>
public sealed class ClaimRiskEvaluator(SerialisationDbContext db, ClaimRiskRulesSettings settings, ITenantContext tenant)
{
    private static readonly string[] DuplicateOpenStatuses =
        [ClaimStatus.Submitted, ClaimStatus.Adjudicating, ClaimStatus.Approved, ClaimStatus.PartiallyApproved];
    private static readonly string[] CustomerPartyTypes =
        [PartyType.Customer, PartyType.EndUser, PartyType.MarketplaceBuyer];

    public async Task<IReadOnlyList<ClaimRiskFlag>> EvaluateAsync(Claim claim,
        IReadOnlyList<ClaimRiskLineEvaluation> lines, string evaluation, DateTime evaluatedAt,
        bool includeUncommittedCurrentClaim, CancellationToken cancellationToken)
    {
        if (!ClaimRiskEvaluation.All.Contains(evaluation, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown claim-risk evaluation '{evaluation}'.", nameof(evaluation));

        var policy = await settings.GetAsync(cancellationToken);
        ClaimRiskRulesSettings.Validate(policy);
        var hits = new List<RiskHit>();
        var incident = claim.OccurredDate ?? claim.ClaimDate;
        var tenantId = tenant.TenantId;

        if (policy.RecentCoverPurchase.Enabled)
        {
            var families = lines.Select(row => row.Line.CoverageFamily).Distinct().ToList();
            var stacking = await db.CoverageFamilies.AsNoTracking()
                .Where(row => row.TenantId == tenantId && families.Contains(row.FamilyCode) && row.Stacking)
                .Select(row => row.FamilyCode).ToListAsync(cancellationToken);
            var matches = lines.Where(row => row.Outcome.DenialReason != ClaimDenialReason.ClaimWaitingPeriod
                    && (stacking.Contains(row.Line.CoverageFamily)
                        || row.Outcome.Resolution?.EntitlementType == EntitlementType.Extended)
                    && row.Outcome.Resolution?.PurchaseDate is { })
                .Select(row => new
                {
                    row.Line.ClaimLineId,
                    row.Line.CoverageFamily,
                    row.Outcome.EntitlementId,
                    PurchaseDate = row.Outcome.Resolution!.PurchaseDate!.Value,
                    DaysAfterPurchase = incident.DayNumber - row.Outcome.Resolution!.PurchaseDate!.Value.DayNumber,
                })
                .Where(row => row.DaysAfterPurchase is >= 0 && row.DaysAfterPurchase <= policy.RecentCoverPurchase.Days)
                .OrderBy(row => row.DaysAfterPurchase).ToList();
            if (matches.Count > 0)
                hits.Add(Hit(ClaimRiskRuleCode.RecentCoverPurchase,
                    new { days = policy.RecentCoverPurchase.Days },
                    new { incident_date = incident, lines = matches }));
        }

        if (policy.RepeatUnitClaims.Enabled)
        {
            var since = incident.AddDays(-policy.RepeatUnitClaims.Days);
            var earlier = await db.Claims.AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.ClaimId != claim.ClaimId && row.UnitId == claim.UnitId
                    && (row.OccurredDate ?? row.ClaimDate) >= since && (row.OccurredDate ?? row.ClaimDate) < incident)
                .OrderByDescending(row => row.OccurredDate ?? row.ClaimDate).ThenBy(row => row.ClaimId)
                .Select(row => new { row.ClaimId, row.ClaimNumber, IncidentDate = row.OccurredDate ?? row.ClaimDate, row.Status })
                .ToListAsync(cancellationToken);
            if (earlier.Count >= policy.RepeatUnitClaims.MinimumEarlierClaims)
                hits.Add(Hit(ClaimRiskRuleCode.RepeatUnitClaims,
                    new { minimum_earlier_claims = policy.RepeatUnitClaims.MinimumEarlierClaims, days = policy.RepeatUnitClaims.Days },
                    new { incident_date = incident, earlier_claim_count = earlier.Count, earlier_claims = earlier }));
        }

        if (policy.RepeatFault.Enabled && !string.IsNullOrWhiteSpace(claim.ReportedCauseCode))
        {
            var since = incident.AddDays(-policy.RepeatFault.Days);
            var earlier = await db.Claims.AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.ClaimId != claim.ClaimId && row.UnitId == claim.UnitId
                    && row.ReportedCauseCode == claim.ReportedCauseCode
                    && (row.OccurredDate ?? row.ClaimDate) >= since && (row.OccurredDate ?? row.ClaimDate) < incident)
                .OrderByDescending(row => row.OccurredDate ?? row.ClaimDate).ThenBy(row => row.ClaimId)
                .Select(row => new { row.ClaimId, row.ClaimNumber, IncidentDate = row.OccurredDate ?? row.ClaimDate })
                .ToListAsync(cancellationToken);
            if (earlier.Count > 0)
                hits.Add(Hit(ClaimRiskRuleCode.RepeatFault,
                    new { days = policy.RepeatFault.Days },
                    new { incident_date = incident, reported_cause_code = claim.ReportedCauseCode, earlier_claims = earlier }));
        }

        if (policy.ClaimantRate.Enabled)
        {
            var partyTypes = await db.Episodes.AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.PartyRef == claim.ClaimantPartyRef)
                .Select(row => row.PartyType).Distinct().ToListAsync(cancellationToken);
            var currentOwner = await db.Current.AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.UnitId == claim.UnitId)
                .Select(row => row.OwnerPartyRef).SingleOrDefaultAsync(cancellationToken);
            var customer = currentOwner == claim.ClaimantPartyRef
                || partyTypes.Any(kind => kind is not null && CustomerPartyTypes.Contains(kind));
            if (customer)
            {
                var since = incident.AddDays(-policy.ClaimantRate.Days);
                var partyClaims = await db.Claims.AsNoTracking().CountAsync(row => row.TenantId == tenantId
                    && row.ClaimantPartyRef == claim.ClaimantPartyRef
                    && (row.OccurredDate ?? row.ClaimDate) >= since && (row.OccurredDate ?? row.ClaimDate) <= incident,
                    cancellationToken);
                var tenantClaims = await db.Claims.AsNoTracking().CountAsync(row => row.TenantId == tenantId
                    && (row.OccurredDate ?? row.ClaimDate) >= since && (row.OccurredDate ?? row.ClaimDate) <= incident,
                    cancellationToken);
                if (includeUncommittedCurrentClaim)
                {
                    partyClaims++;
                    tenantClaims++;
                }

                var partyUnits = await db.Current.AsNoTracking().CountAsync(row => row.TenantId == tenantId
                    && row.OwnerPartyRef == claim.ClaimantPartyRef, cancellationToken);
                var tenantUnits = await db.Current.AsNoTracking().CountAsync(row => row.TenantId == tenantId
                    && row.OwnerPartyRef != null, cancellationToken);
                if (partyClaims >= policy.ClaimantRate.MinimumClaims && partyUnits >= policy.ClaimantRate.MinimumUnits
                    && tenantClaims > 0 && tenantUnits > 0)
                {
                    var partyRate = decimal.Divide(partyClaims, partyUnits);
                    var tenantRate = decimal.Divide(tenantClaims, tenantUnits);
                    if (partyRate > policy.ClaimantRate.RateMultiplier * tenantRate)
                        hits.Add(Hit(ClaimRiskRuleCode.ClaimantRate,
                            new
                            {
                                days = policy.ClaimantRate.Days,
                                rate_multiplier = policy.ClaimantRate.RateMultiplier,
                                minimum_claims = policy.ClaimantRate.MinimumClaims,
                                minimum_units = policy.ClaimantRate.MinimumUnits,
                            },
                            new
                            {
                                party_kind = "customer", claimant_party_ref = claim.ClaimantPartyRef,
                                party_claims = partyClaims, party_units = partyUnits, party_rate = partyRate,
                                tenant_claims = tenantClaims, tenant_units = tenantUnits, tenant_rate = tenantRate,
                            }));
                }
            }
        }

        var incidentEnd = incident.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        if (policy.ChannelMismatch.Enabled)
        {
            var unit = await db.Current.AsNoTracking().Where(row => row.TenantId == tenantId && row.UnitId == claim.UnitId)
                .Select(row => new { row.OwnerPartyRef, row.CustodyPartyRef }).SingleAsync(cancellationToken);
            var inChain = unit.OwnerPartyRef == claim.ClaimantPartyRef || unit.CustodyPartyRef == claim.ClaimantPartyRef
                || await db.Episodes.AsNoTracking().AnyAsync(row => row.TenantId == tenantId && row.UnitId == claim.UnitId
                    && row.PartyRef == claim.ClaimantPartyRef && row.ValidFrom < incidentEnd, cancellationToken);
            if (!inChain)
                hits.Add(Hit(ClaimRiskRuleCode.ChannelMismatch, new { },
                    new { claimant_party_ref = claim.ClaimantPartyRef, current_owner_party_ref = unit.OwnerPartyRef, current_custody_party_ref = unit.CustodyPartyRef }));
        }

        if (policy.RecentOwnershipChange.Enabled)
        {
            var since = incident.AddDays(-policy.RecentOwnershipChange.Days).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var episode = await db.Episodes.AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.UnitId == claim.UnitId && row.Kind == EpisodeKind.Ownership
                    && row.ValidFrom >= since && row.ValidFrom < incidentEnd)
                .OrderByDescending(row => row.ValidFrom).ThenByDescending(row => row.EpisodeId)
                .Select(row => new { row.EpisodeId, row.PartyRef, row.PartyType, OpenedAt = row.ValidFrom })
                .FirstOrDefaultAsync(cancellationToken);
            if (episode is not null)
                hits.Add(Hit(ClaimRiskRuleCode.RecentOwnershipChange,
                    new { days = policy.RecentOwnershipChange.Days }, new { incident_date = incident, ownership = episode }));
        }

        if (policy.PartNotReturned.Enabled)
        {
            var claimLinks = await (from link in db.ReplacementLinks.AsNoTracking()
                join source in db.Claims.AsNoTracking()
                    on new { link.TenantId, ClaimId = link.SourceId } equals new { source.TenantId, source.ClaimId }
                where link.TenantId == tenantId && link.SourceKind == ReplacementSourceKind.Claim
                    && link.State == ReplacementLinkState.AwaitingReturn && link.ReturnDue < incident
                    && link.OriginalUnitId != claim.UnitId && source.UnitId == claim.UnitId && source.ClaimId != claim.ClaimId
                select new { link.ReplacementLinkId, link.OriginalUnitId, link.ReturnDue, link.SourceKind, link.SourceNumber })
                .ToListAsync(cancellationToken);
            var rmaLinks = await (from link in db.ReplacementLinks.AsNoTracking()
                join line in db.RmaLines.AsNoTracking()
                    on new { link.TenantId, RmaId = link.SourceId } equals new { line.TenantId, line.RmaId }
                where link.TenantId == tenantId && link.SourceKind == ReplacementSourceKind.Rma
                    && link.State == ReplacementLinkState.AwaitingReturn && link.ReturnDue < incident
                    && line.ParentUnitId == claim.UnitId && (line.RequestedUnitId == link.OriginalUnitId || line.AuthorisedUnitId == link.OriginalUnitId)
                select new { link.ReplacementLinkId, link.OriginalUnitId, link.ReturnDue, link.SourceKind, link.SourceNumber })
                .ToListAsync(cancellationToken);
            var links = claimLinks.Concat(rmaLinks).DistinctBy(row => row.ReplacementLinkId).ToList();
            if (links.Count > 0)
            {
                var serials = await UnitSerials.DisplayAsync(db, tenantId, links.Select(row => row.OriginalUnitId).Distinct().ToList(), cancellationToken);
                hits.Add(Hit(ClaimRiskRuleCode.PartNotReturned, new { overdue_before_incident = true },
                    new
                    {
                        incident_date = incident,
                        outstanding_returns = links.Select(row => new
                        {
                            row.ReplacementLinkId, row.SourceKind, row.SourceNumber, row.ReturnDue,
                            part_unit_id = row.OriginalUnitId, part_serial = serials.GetValueOrDefault(row.OriginalUnitId),
                        }),
                    }));
            }
        }

        if (policy.DuplicateOpenClaim.Enabled)
        {
            var currentUnitIds = lines.Select(row => row.Line.UnitId).Append(claim.UnitId).Distinct().ToList();
            var otherClaims = await db.Claims.AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.ClaimId != claim.ClaimId && DuplicateOpenStatuses.Contains(row.Status)
                    && (currentUnitIds.Contains(row.UnitId) || db.ClaimLines.Any(line => line.TenantId == tenantId
                        && line.ClaimId == row.ClaimId && currentUnitIds.Contains(line.UnitId))))
                .OrderBy(row => row.ClaimNumber)
                .Select(row => new { row.ClaimId, row.ClaimNumber, row.UnitId, row.Status })
                .ToListAsync(cancellationToken);
            if (otherClaims.Count > 0)
            {
                var ids = otherClaims.Select(row => row.ClaimId).ToList();
                var otherLineUnits = await db.ClaimLines.AsNoTracking()
                    .Where(row => row.TenantId == tenantId && ids.Contains(row.ClaimId) && currentUnitIds.Contains(row.UnitId))
                    .Select(row => new { row.ClaimId, row.UnitId }).Distinct().ToListAsync(cancellationToken);
                var matchingUnits = otherClaims.Where(row => currentUnitIds.Contains(row.UnitId)).Select(row => row.UnitId)
                    .Concat(otherLineUnits.Select(row => row.UnitId)).Distinct().ToList();
                var serials = await UnitSerials.DisplayAsync(db, tenantId, matchingUnits, cancellationToken);
                var duplicates = otherClaims.Select(row => new
                {
                    row.ClaimId, row.ClaimNumber, row.Status,
                    serials = otherLineUnits.Where(line => line.ClaimId == row.ClaimId).Select(line => line.UnitId)
                        .Append(row.UnitId).Where(currentUnitIds.Contains).Distinct()
                        .Select(unitId => new { unit_id = unitId, serial = serials.GetValueOrDefault(unitId), kind = unitId == claim.UnitId ? "unit" : "part" }),
                }).ToList();
                hits.Add(Hit(ClaimRiskRuleCode.DuplicateOpenClaim, new { open_statuses = DuplicateOpenStatuses },
                    new { claims = duplicates }));
            }
        }

        var existing = await db.ClaimRiskFlags.Where(row => row.TenantId == tenantId && row.ClaimId == claim.ClaimId)
            .Select(row => row.RuleCode).ToListAsync(cancellationToken);
        var added = hits.Where(hit => !existing.Contains(hit.RuleCode, StringComparer.Ordinal)).Select(hit => new ClaimRiskFlag
        {
            TenantId = tenantId,
            FlagId = TimeOrderedId.New(evaluatedAt),
            ClaimId = claim.ClaimId,
            RuleCode = hit.RuleCode,
            ThresholdSnapshot = JsonSerializer.Serialize(hit.Thresholds, LedgerJson.Options),
            Evidence = JsonSerializer.Serialize(hit.Evidence, LedgerJson.Options),
            RaisedAt = evaluatedAt,
            RaisedByEvaluation = evaluation,
            Status = ClaimRiskFlagStatus.Open,
        }).ToList();
        db.ClaimRiskFlags.AddRange(added);
        return added;
    }

    private static RiskHit Hit(string ruleCode, object thresholds, object evidence) => new(ruleCode, thresholds, evidence);
    private sealed record RiskHit(string RuleCode, object Thresholds, object Evidence);
}
