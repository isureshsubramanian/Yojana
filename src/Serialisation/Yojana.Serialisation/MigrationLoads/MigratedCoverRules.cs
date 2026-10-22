using System.Text.Json;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.MigrationLoads;

/// <summary>
/// The shared validation and row preparation boundary for first-load and delta migrated cover. Keeping the rules here
/// prevents those two write paths from accepting different evidence, policy versions or family combinations.
/// </summary>
internal static class MigratedCoverRules
{
    internal sealed record Prepared(
        IReadOnlyList<Entitlement> Entitlements,
        IReadOnlyList<EntitlementEvidence> Evidence,
        IReadOnlyList<EntitlementUsageLimit> UsageLimits,
        IReadOnlyList<UnitEventDraft> Events);

    public static IReadOnlyList<string> Refusals(IReadOnlyList<MigratedEntitlement> entitlements, DateOnly cutover,
        IReadOnlyDictionary<Guid, WarrantyPolicyVersion> policies,
        IReadOnlyDictionary<string, CoverageFamily> families)
    {
        var reasons = new List<string>();
        var states = new List<(string Family, string Status)>();
        foreach (var entitlement in entitlements)
        {
            if (!policies.TryGetValue(entitlement.PolicyVersionId, out var policy))
            {
                reasons.Add("entitlement_policy_unknown");
                continue;
            }
            if (policy.Status != PolicyVersionStatus.Published) reasons.Add("entitlement_policy_not_published");
            if (policy.Version != entitlement.PolicyVersion) reasons.Add("entitlement_policy_version_differs");
            if (policy.CoverageFamily != entitlement.CoverageFamily) reasons.Add("entitlement_policy_family_differs");
            families.TryGetValue(entitlement.CoverageFamily, out var family);
            var addOn = policy.EntitlementType == EntitlementType.Extended;
            if (EntitlementStackingRules.FamilyRefusal(family, addOn) is { } familyReason)
                reasons.Add(familyReason);
            if (entitlement.Direction is not (EntitlementDirection.Outbound or EntitlementDirection.Inbound))
                reasons.Add("entitlement_direction_unknown");
            if (entitlement.EndDate is not null && entitlement.StartDate is null)
                reasons.Add("entitlement_start_required");
            if (entitlement.StartDate is { } start && entitlement.EndDate is { } end && end < start)
                reasons.Add("entitlement_dates_invalid");
            if (entitlement.TerminationDate is { } termination && entitlement.StartDate is { } began
                && termination < began)
                reasons.Add("entitlement_termination_before_start");
            if (entitlement.Evidence.Count == 0) reasons.Add("entitlement_evidence_required");
            if (entitlement.Evidence.Count(row => row.Selected) != 1)
                reasons.Add("entitlement_selected_evidence_required");
            else
            {
                var selected = entitlement.Evidence.Single(row => row.Selected);
                var selectedEnd = selected.TerminationDate is { } terminatedBeforeEnd
                    && (selected.EndDate is null || terminatedBeforeEnd < selected.EndDate)
                    ? terminatedBeforeEnd : selected.EndDate;
                if (selected.SourceKind != entitlement.DateSource || selected.SourceKey != entitlement.SourceKey
                    || selected.StartDate != entitlement.StartDate || selectedEnd != entitlement.EndDate
                    || selected.TerminationDate != entitlement.TerminationDate
                    || selected.Direction != entitlement.Direction)
                    reasons.Add("entitlement_selected_evidence_differs");
            }
            var status = Status(entitlement, cutover);
            if (EntitlementStackingRules.FamilyOccupied(states, entitlement.CoverageFamily)
                && status != EntitlementStatus.Closed)
                reasons.Add("family_occupied");
            states.Add((entitlement.CoverageFamily, status));
        }
        return reasons.Distinct(StringComparer.Ordinal).ToList();
    }

    public static Prepared Prepare(TenantId tenantId, Guid loadId, Guid unitId, Guid legalEntityId,
        Guid? ownerPartyRef, string? ownerPartyType, string? conditionGrade, DateTime unitCreatedAt,
        DateOnly cutover, DateTime now, string actorRef, IReadOnlyList<MigratedEntitlement> inputs,
        IReadOnlyDictionary<Guid, WarrantyPolicyVersion> policies, Func<string, DateTime, string> key)
    {
        var entitlements = new List<Entitlement>();
        var evidenceRows = new List<EntitlementEvidence>();
        var usageRows = new List<EntitlementUsageLimit>();
        var events = new List<UnitEventDraft>();
        foreach (var input in inputs)
        {
            var policy = policies[input.PolicyVersionId];
            var eventAt = input.StartDate is { } startDay
                ? DateTime.SpecifyKind(startDay.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
                : unitCreatedAt;
            if (eventAt < unitCreatedAt) eventAt = unitCreatedAt;
            var entitlementEventId = TimeOrderedId.New(eventAt);
            var terminated = input.TerminationDate is { } termination && termination <= cutover;
            var expired = !terminated && input.EndDate is { } endAtCutover && endAtCutover < cutover;
            var closed = terminated || expired;
            var actualDays = input.StartDate is { } start && input.EndDate is { } end
                ? Math.Max(0, end.DayNumber - start.DayNumber + (policy.EndInclusive ? 1 : 0))
                : policy.DurationValue;
            var entitlement = new Entitlement
            {
                TenantId = tenantId, EntitlementId = TimeOrderedId.New(eventAt), MigrationLoadId = loadId,
                UnitId = unitId, Scope = EntitlementScope.Unit, Type = policy.EntitlementType,
                CoverageFamily = input.CoverageFamily, Direction = input.Direction,
                OwnerPartyRef = input.Direction == EntitlementDirection.Outbound ? ownerPartyRef : null,
                OwnerPartyType = input.Direction == EntitlementDirection.Outbound ? ownerPartyType : null,
                ObligorEntityId = legalEntityId, PolicyVersionId = policy.PolicyVersionId,
                StartTrigger = policy.StartTrigger, StartBasis = input.DateSource, StartEvidenceRef = input.SourceKey,
                PurchaseDate = input.Direction == EntitlementDirection.Outbound ? input.StartDate : null,
                StartDate = input.StartDate, StartTimeZone = "UTC", EndDate = input.EndDate,
                ComputedByRuleVersion = "migrated", DurationValue = actualDays, DurationUnit = DurationRule.Days,
                EndOfMonthRule = policy.EndOfMonthRule, EndInclusive = policy.EndInclusive,
                DurationBasis = policy.DurationBasis, Coverage = policy.Coverage, RegionScope = policy.RegionScope,
                Transferable = policy.Transferable, ConditionAtCreation = conditionGrade,
                AssuranceOrService = policy.AssuranceOrService,
                Status = closed ? EntitlementStatus.Closed
                    : input.StartDate is null ? EntitlementStatus.PendingEvidence : EntitlementStatus.Active,
                ClosedReason = terminated ? EntitlementClosedReason.MigratedTermination
                    : expired ? EntitlementClosedReason.Expired : null,
                ClosedEffective = terminated ? input.TerminationDate : expired ? input.EndDate : null,
                ClosedAt = closed ? now : null, Confidence = "medium", Provenance = EntitlementProvenance.Migrated,
                SourceDocumentType = "migration_cover", SourceDocumentId = input.PlanEntitlementId,
                CreatedEventId = entitlementEventId, CreatedAt = now,
            };
            entitlements.Add(entitlement);
            foreach (var evidence in input.Evidence)
            {
                evidenceRows.Add(new EntitlementEvidence
                {
                    TenantId = tenantId, EvidenceId = TimeOrderedId.New(now), EntitlementId = entitlement.EntitlementId,
                    MigrationLoadId = loadId, SourceKind = evidence.SourceKind, SourceKey = evidence.SourceKey,
                    StartDate = evidence.StartDate, EndDate = evidence.EndDate,
                    TerminationDate = evidence.TerminationDate, Direction = evidence.Direction,
                    Selected = evidence.Selected,
                });
            }
            foreach (var limit in JsonSerializer.Deserialize<List<UsageLimitTerm>>(policy.UsageLimits,
                         LedgerJson.Options) ?? [])
            {
                usageRows.Add(new EntitlementUsageLimit
                {
                    TenantId = tenantId, EntitlementId = entitlement.EntitlementId, MeterType = limit.MeterType,
                    LimitValue = limit.Limit, Period = limit.Period,
                });
            }
            events.Add(new UnitEventDraft
            {
                UnitId = unitId, EventTime = eventAt, EventType = EntitlementEventType.Created,
                PresetEventId = entitlementEventId, LegalEntityId = legalEntityId,
                ActorRef = actorRef, DocumentType = "migration_cover", DocumentId = input.PlanEntitlementId,
                IdempotencyKey = key(EntitlementEventType.Created, eventAt),
                Attributes = JsonObjectText.Of(new
                {
                    load_id = loadId, entitlement_id = entitlement.EntitlementId,
                    coverage_family = entitlement.CoverageFamily, entitlement_type = entitlement.Type,
                    direction = entitlement.Direction, policy_code = policy.PolicyCode,
                    policy_version = policy.Version, policy_version_id = policy.PolicyVersionId,
                    status = entitlement.Status, closed_reason = entitlement.ClosedReason,
                    start_date = entitlement.StartDate, end_date = entitlement.EndDate,
                    termination_date = input.TerminationDate, start_basis = entitlement.StartBasis,
                    owner_party_ref = entitlement.OwnerPartyRef, provenance = entitlement.Provenance,
                    evidence_refs = input.Evidence.Select(row => row.SourceKey).ToList(),
                    trail = new[]
                    {
                        "dates migrated from the reviewed load plan",
                        "policy caps, floors and duration arithmetic were not applied",
                    },
                }),
            });
        }
        return new Prepared(entitlements, evidenceRows, usageRows, events);
    }

    private static string Status(MigratedEntitlement entitlement, DateOnly cutover) =>
        entitlement.TerminationDate is { } terminated && terminated <= cutover
        || entitlement.EndDate is { } expired && expired < cutover
            ? EntitlementStatus.Closed
            : entitlement.StartDate is null ? EntitlementStatus.PendingEvidence : EntitlementStatus.Active;
}
