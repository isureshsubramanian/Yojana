using System.Globalization;
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

namespace Yojana.Serialisation.Entitlements;

// Entitlement commands (P2-T05). Each writes the entitlement rows and one ledger event on the unit in the handler's
// transaction; unit_current's coverage columns follow through the event's delta (EntitlementLedger). Dates are never
// typed: the start comes from evidence through the policy's chain and cap, the end from TermArithmetic.

public static class CreateEntitlementHandler
{
    // Coupled to the factory registration of RefurbishedGoodsSettings in SerialisationModule: Wolverine must not see the
    // PlatformDbContext behind ITenantSettings in this handler graph, or its SerialisationDbContext transaction is refused.
    public static async Task<CreateEntitlementResult> Handle(CreateEntitlement command, SerialisationDbContext db, EntitlementLedger ledger, PolicyResolver resolver, PolicyStore store, ITimeZonePort zones, RefurbishedGoodsSettings refurbishedSettings, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
        => await CreateAsync(command, db, ledger, resolver, store, zones, refurbishedSettings, tenant, clock, null, [], false, null, null, null, cancellationToken);

    internal static async Task<CreateEntitlementResult> CreateAsync(CreateEntitlement command, SerialisationDbContext db, EntitlementLedger ledger, PolicyResolver resolver, PolicyStore store, ITimeZonePort zones, RefurbishedGoodsSettings refurbishedSettings, ITenantContext tenant, IClock clock,
        string? startBasisOverride, IReadOnlyList<string> evidenceRefs, bool financeReviewRequired, string? reasonCode, string? financeReviewRef, CorrectionMetadata? correction, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var key = EntitlementLedger.Key(command.CommandId, command.UnitId, EntitlementEventType.Created, command.EventTime);
        if (await ledger.ReplayedAsync(key, command.EventTime, cancellationToken) is { } replayed)
        {
            var existing = await db.Entitlements.AsNoTracking().FirstAsync(e => e.TenantId == tenantId && e.CreatedEventId == replayed.EventId, cancellationToken);
            return new CreateEntitlementResult(existing.EntitlementId, existing.Status, "replayed", null, null, existing.StartDate, existing.EndDate, existing.StartBasis, false, existing.RefurbishedFloorApplied, existing.StartTimeZone, existing.AccrualCohortId, [], Disclosure(existing));
        }

        var unit = await db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && c.UnitId == command.UnitId).Select(c => new { c.ItemId, c.LocationId, c.ConditionGrade }).FirstOrDefaultAsync(cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Unit {command.UnitId} does not exist.");
        if (await db.Entitlements.AnyAsync(e => e.TenantId == tenantId && e.UnitId == command.UnitId && e.CoverageFamily == command.CoverageFamily && e.Status != EntitlementStatus.Closed, cancellationToken))
        {
            throw new CommandRefusedException("family_occupied", $"Unit {command.UnitId} already has a live {command.CoverageFamily} entitlement.", "supersede it (SupersedeEntitlement) or close it first; a family holds one live entitlement");
        }

        WarrantyPolicyVersion version;
        string level;
        IReadOnlyList<string> trail;
        if (command.PolicyVersionId is { } named)
        {
            version = await db.PolicyVersions.AsNoTracking().FirstOrDefaultAsync(v => v.TenantId == tenantId && v.PolicyVersionId == named, cancellationToken)
                ?? throw new CommandRefusedException("policy_version_unknown", $"Policy version {named} does not exist.");
            level = "named";
            trail = [$"named policy version {version.PolicyCode} v{version.Version}"];
        }
        else
        {
            var resolution = await resolver.ResolveAsync(new PolicyQuestion(command.UnitId, unit.ItemId, command.CoverageFamily, command.ContractDate, command.OwnerPartyRef, unit.ConditionGrade ?? command.ConditionGrade, command.Channel, command.Country), cancellationToken);
            trail = resolution.Trail;
            if (resolution.Level == PolicyLevel.ParentEntitlement)
            {
                throw new CommandRefusedException("covered_by_parent", $"Unit {command.UnitId} is covered by its parent's entitlement {resolution.EntitlementId}.", "name a policy version to create the child's own entitlement anyway");
            }

            if (resolution.PolicyVersionId is not { } resolved || resolution.Level == PolicyLevel.None)
            {
                throw new CommandRefusedException("no_policy", $"No {command.CoverageFamily} policy applies to unit {command.UnitId} for contracts on {command.ContractDate:yyyy-MM-dd}: {string.Join("; ", resolution.Trail)}", "give the item a policy, or name a policy version");
            }

            version = await db.PolicyVersions.AsNoTracking().FirstAsync(v => v.TenantId == tenantId && v.PolicyVersionId == resolved, cancellationToken);
            level = resolution.Level;
        }

        if (version.Status == PolicyVersionStatus.Draft) throw new CommandRefusedException("policy_not_published", $"{version.PolicyCode} v{version.Version} is a draft.");
        if (version.CoverageFamily != command.CoverageFamily) throw new CommandRefusedException("policy_family_differs", $"{version.PolicyCode} is a {version.CoverageFamily} policy, not {command.CoverageFamily}.");

        var terms = await store.ReadTermsAsync(version, cancellationToken);
        var grade = unit.ConditionGrade ?? command.ConditionGrade ?? ConditionGrades.New;
        RefurbishedEntitlementDisclosure? disclosure = null;
        EffectiveDuration duration;
        if (grade != ConditionGrades.New)
        {
            if (command.Channel is null || command.Country is null) throw new CommandRefusedException("refurbished_sale_context_missing", "A non-new sale needs its channel and country.", "supply the sale channel and ISO country");
            disclosure = RefurbishedGoodsPolicyEngine.Decide(unit.ItemId, grade, command.Channel, command.Country, command.BuyerKind, terms.Refurbished, await refurbishedSettings.GetAsync(cancellationToken), command.RefurbisherPartyRef);
            duration = new EffectiveDuration(terms.Duration with { Value = disclosure.TermMonths, Unit = DurationRule.Months }, disclosure.FloorApplied, disclosure.MatchedRow);
        }
        else duration = new EffectiveDuration(terms.Duration, false, null);
        var zone = await ZoneAsync(terms.ReferenceTimeZoneRule, command.SiteLocationId ?? unit.LocationId, command.ObligorEntityId, zones, cancellationToken);
        var start = TermArithmetic.ResolveStart(terms.Start, command.Evidence, zone);
        DateOnly? startLatest = terms.Start.CapDays is { } cap && command.Evidence.TryGetValue(StartTrigger.Invoice, out var invoice) ? TermArithmetic.LocalDay(invoice, zone).AddDays(cap) : null;
        DateOnly? end = start.Start is { } s ? TermArithmetic.EndDate(s, duration.Duration) : null;

        Guid? cohortId = null;
        if (terms.AssuranceOrService == AssuranceOrService.Assurance && command.Provenance is not EntitlementProvenance.Transferred)
        {
            // An assurance warranty is accrued once, into the cohort of its obligor, item, sale month and tag (case 71: an inherited one carries none).
            var period = command.ContractDate.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var cohort = await db.AccrualCohorts.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ObligorEntityId == command.ObligorEntityId && c.ItemId == unit.ItemId && c.Period == period && c.AssuranceOrService == AssuranceOrService.Assurance, cancellationToken);
            if (cohort is null)
            {
                cohort = new AccrualCohort { TenantId = tenantId, CohortId = TimeOrderedId.New(command.EventTime), ObligorEntityId = command.ObligorEntityId, ItemId = unit.ItemId, Period = period, AssuranceOrService = AssuranceOrService.Assurance, CreatedAt = clock.UtcNow };
                db.AccrualCohorts.Add(cohort);
            }

            cohortId = cohort.CohortId;
        }

        var eventId = TimeOrderedId.New(command.EventTime);
        var entitlement = new Entitlement
        {
            TenantId = tenantId, EntitlementId = TimeOrderedId.New(command.EventTime), UnitId = command.UnitId, Scope = EntitlementScope.Unit, Type = version.EntitlementType, CoverageFamily = command.CoverageFamily,
            Direction = EntitlementDirection.Outbound, OwnerPartyRef = command.OwnerPartyRef, OwnerPartyType = command.OwnerPartyType, ObligorEntityId = command.ObligorEntityId, PayerPartyRef = command.PayerPartyRef,
            PayerPartyType = command.PayerPartyType, PolicyVersionId = version.PolicyVersionId, StartTrigger = terms.Start.Trigger, StartBasis = start.Start is null ? null : startBasisOverride ?? start.Basis, StartEvidenceRef = start.Basis is null ? null : command.StartEvidenceRef,
            StartDate = start.Start, StartTimeZone = zone, EndDate = end, ComputedByRuleVersion = version.RuleVersion, StartLatest = startLatest, DurationValue = duration.Duration.Value, DurationUnit = duration.Duration.Unit,
            EndOfMonthRule = duration.Duration.EndOfMonth, EndInclusive = duration.Duration.EndInclusive, DurationBasis = terms.DurationBasis, Coverage = JsonSerializer.Serialize(terms.Coverage, LedgerJson.Options),
            RegionScope = JsonSerializer.Serialize(terms.RegionScope, LedgerJson.Options), Transferable = terms.Transferable, ConditionAtCreation = grade, AccrualCohortId = cohortId,
            RefurbishedMatchedRow = disclosure is null ? null : JsonSerializer.Serialize(disclosure.MatchedRow, LedgerJson.Options), RefurbishedFloorApplied = disclosure?.FloorApplied ?? false,
            RefurbishedFloorMonths = disclosure?.FloorMonths, RefurbishedFloorSource = disclosure?.FloorSource, RefurbisherPartyRef = disclosure?.RefurbisherPartyRef, RefurbishedStatementKind = disclosure?.StatementKind,
            AssuranceOrService = terms.AssuranceOrService, Status = start.Start is null ? EntitlementStatus.PendingEvidence : EntitlementStatus.Active, Confidence = command.Confidence, Provenance = command.Provenance,
            SourceDocumentType = command.SourceDocument?.Type, SourceDocumentId = command.SourceDocument?.Id, SourceDocumentLine = command.SourceDocument?.Line, CustomerPoRef = command.CustomerPoRef,
            CreatedEventId = eventId, CreatedAt = clock.UtcNow,
        };
        db.Entitlements.Add(entitlement);
        foreach (var limit in terms.UsageLimits)
        {
            db.EntitlementUsageLimits.Add(new EntitlementUsageLimit { TenantId = tenantId, EntitlementId = entitlement.EntitlementId, MeterType = limit.MeterType, LimitValue = limit.Limit, Period = limit.Period });
        }

        await ledger.AppendAsync(entitlement, EntitlementEventType.Created, key, command.EventTime, command.ActorRef, new
        {
            entitlement_id = entitlement.EntitlementId, item_id = unit.ItemId, coverage_family = entitlement.CoverageFamily, entitlement_type = entitlement.Type, policy_level = level, policy_code = version.PolicyCode, policy_version = version.Version, policy_version_id = version.PolicyVersionId,
            status = entitlement.Status, start_basis = entitlement.StartBasis, start = start.Explanation, start_date = entitlement.StartDate, end_date = entitlement.EndDate, cap_applied = start.CapApplied, floor_applied = duration.FloorApplied,
            matched_refurbished_row = disclosure?.MatchedRow, term_months = disclosure?.TermMonths, floor_months = disclosure?.FloorMonths, floor_source = disclosure?.FloorSource, refurbisher_party_ref = disclosure?.RefurbisherPartyRef, statement_kind = disclosure?.StatementKind,
            time_zone = zone, accrual_cohort_id = cohortId, assurance_or_service = entitlement.AssuranceOrService,
            accrual_suppressed = entitlement.AssuranceOrService == AssuranceOrService.Assurance && cohortId is null, provenance = command.Provenance, confidence = command.Confidence,
            owner_party_ref = command.OwnerPartyRef, rule_version = version.RuleVersion, trail,
            evidence_refs = evidenceRefs, finance_review_required = financeReviewRequired, reason_code = reasonCode, finance_review_ref = financeReviewRef,
        }, command.SourceDocument, cancellationToken, eventId, correction);
        return new CreateEntitlementResult(entitlement.EntitlementId, entitlement.Status, level, version.PolicyCode, version.Version, entitlement.StartDate, entitlement.EndDate, entitlement.StartBasis, start.CapApplied, duration.FloorApplied, zone, cohortId, trail, disclosure);
    }

    internal static RefurbishedEntitlementDisclosure? Disclosure(Entitlement e) => e.RefurbishedMatchedRow is null || e.RefurbishedStatementKind is null
        ? null
        : new(e.DurationValue, e.RefurbishedFloorApplied, e.RefurbishedFloorMonths, e.RefurbishedFloorSource, e.RefurbisherPartyRef, e.RefurbishedStatementKind,
            JsonSerializer.Deserialize<RefurbishedTerm>(e.RefurbishedMatchedRow, LedgerJson.Options)!);

    /// <summary>The reference zone by the policy's rule, falling back to the obligor's zone and then UTC, so a start is never read in an unknown calendar.</summary>
    internal static async Task<string> ZoneAsync(string rule, Guid? site, Guid obligor, ITimeZonePort zones, CancellationToken cancellationToken)
    {
        var candidates = new List<string?>();
        if (rule == PolicyTerms.TimeZoneCustomerSite && site is { } location) candidates.Add(await zones.ForLocationAsync(location, cancellationToken));
        if (rule != PolicyTerms.TimeZoneUtc) candidates.Add(await zones.ForLegalEntityAsync(obligor, cancellationToken));
        return candidates.FirstOrDefault(z => z is not null && TermArithmetic.IsKnownZone(z)) ?? "UTC";
    }
}

public static class RebaseEntitlementStartHandler
{
    public static async Task<EntitlementChangeResult> Handle(RebaseEntitlementStart command, SerialisationDbContext db, EntitlementLedger ledger, PolicyStore store, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var entitlement = await ledger.LoadAsync(command.EntitlementId, cancellationToken);
        var pending = entitlement.Status == EntitlementStatus.PendingEvidence;
        var eventType = pending ? EntitlementEventType.EvidenceAttached : EntitlementEventType.Rebased;
        var key = EntitlementLedger.Key(command.CommandId, entitlement.UnitId, eventType, command.EventTime);
        if (await ledger.ReplayedAsync(key, command.EventTime, cancellationToken) is { } replayed) return Result(entitlement, replayed.EventId, true);

        var tenantId = tenant.TenantId;
        var version = await db.PolicyVersions.AsNoTracking().FirstAsync(v => v.TenantId == tenantId && v.PolicyVersionId == entitlement.PolicyVersionId, cancellationToken);
        var chain = (await store.ReadTermsAsync(version, cancellationToken)).Start;
        var steps = new[] { new StartFallback(chain.Trigger) }.Concat(chain.Fallbacks).ToList();
        var rank = steps.FindIndex(f => f.Trigger == command.Trigger);
        if (rank < 0) throw new CommandRefusedException("trigger_not_in_policy", $"{version.PolicyCode} v{version.Version} starts from {string.Join(" → ", steps.Select(f => f.Trigger))}; '{command.Trigger}' is not in the chain.");
        var currentRank = entitlement.StartBasis is { } basis ? steps.FindIndex(f => f.Trigger == basis) : int.MaxValue;
        if (!pending && rank > currentRank)
        {
            throw new CommandRefusedException("weaker_evidence", $"The start rests on {entitlement.StartBasis}, which the policy ranks above {command.Trigger}.", "rebase only with evidence of the same or a stronger trigger");
        }

        EntitlementLedger.Transition(entitlement, eventType);
        var newStart = TermArithmetic.LocalDay(command.EvidenceAt, entitlement.StartTimeZone).AddDays(steps[rank].OffsetDays);
        var capApplied = entitlement.StartLatest is { } latest && newStart > latest;
        if (capApplied) newStart = entitlement.StartLatest!.Value;
        if (!pending && newStart == entitlement.StartDate) throw new CommandRefusedException("no_change", $"The start is already {newStart:yyyy-MM-dd}.");
        var newEnd = TermArithmetic.EndDate(newStart, new DurationRule(entitlement.DurationValue, entitlement.DurationUnit, entitlement.EndOfMonthRule, entitlement.EndInclusive)).AddDays(entitlement.EndAdjustmentDays);
        var eventId = TimeOrderedId.New(command.EventTime);
        db.EntitlementRebases.Add(new EntitlementRebase
        {
            TenantId = tenantId, RebaseId = TimeOrderedId.New(command.EventTime), EntitlementId = entitlement.EntitlementId, OldStart = entitlement.StartDate, NewStart = newStart, OldEnd = entitlement.EndDate, NewEnd = newEnd,
            EvidenceRef = command.EvidenceRef, PolicyCapApplied = capApplied, ActorRef = command.ActorRef, At = command.EventTime, EventId = eventId,
        });
        var before = new { start = entitlement.StartDate, end = entitlement.EndDate, basis = entitlement.StartBasis };
        entitlement.StartDate = newStart;
        entitlement.EndDate = newEnd;
        entitlement.StartBasis = command.Trigger;
        entitlement.StartEvidenceRef = command.EvidenceRef;
        await ledger.AppendAsync(entitlement, eventType, key, command.EventTime, command.ActorRef, new { entitlement_id = entitlement.EntitlementId, before, after = new { start = newStart, end = newEnd, basis = command.Trigger }, evidence_ref = command.EvidenceRef, cap_applied = capApplied }, null, cancellationToken, eventId);
        return Result(entitlement, eventId, false);
    }

    internal static EntitlementChangeResult Result(Entitlement e, Guid eventId, bool replayed) => new(e.EntitlementId, e.Status, e.ClosedReason, e.StartDate, e.EndDate, eventId, replayed);
}

public static class SuspendEntitlementHandler
{
    public static async Task<EntitlementChangeResult> Handle(SuspendEntitlement command, SerialisationDbContext db, EntitlementLedger ledger, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var entitlement = await ledger.LoadAsync(command.EntitlementId, cancellationToken);
        var key = EntitlementLedger.Key(command.CommandId, entitlement.UnitId, EntitlementEventType.Suspended, command.EventTime);
        if (await ledger.ReplayedAsync(key, command.EventTime, cancellationToken) is { } replayed) return RebaseEntitlementStartHandler.Result(entitlement, replayed.EventId, true);
        if (string.IsNullOrWhiteSpace(command.Reason)) throw new CommandRefusedException("reason_required", "A suspension names its reason.");
        if (command.From < entitlement.StartDate || command.From > entitlement.EndDate)
        {
            throw new CommandRefusedException("outside_term", $"A suspension from {command.From:yyyy-MM-dd} is outside the term {entitlement.StartDate:yyyy-MM-dd} to {entitlement.EndDate:yyyy-MM-dd}.");
        }

        EntitlementLedger.Transition(entitlement, EntitlementEventType.Suspended);
        var eventId = TimeOrderedId.New(command.EventTime);
        db.EntitlementSuspensions.Add(new EntitlementSuspension { TenantId = tenant.TenantId, SuspensionId = TimeOrderedId.New(command.EventTime), EntitlementId = entitlement.EntitlementId, FromDate = command.From, Reason = command.Reason, ClockPaused = command.ClockPaused, ActorRef = command.ActorRef, EventId = eventId });
        await ledger.AppendAsync(entitlement, EntitlementEventType.Suspended, key, command.EventTime, command.ActorRef, new { entitlement_id = entitlement.EntitlementId, from = command.From, reason = command.Reason, clock_paused = command.ClockPaused }, null, cancellationToken, eventId);
        return RebaseEntitlementStartHandler.Result(entitlement, eventId, false);
    }
}

public static class ResumeEntitlementHandler
{
    public static async Task<EntitlementChangeResult> Handle(ResumeEntitlement command, SerialisationDbContext db, EntitlementLedger ledger, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var entitlement = await ledger.LoadAsync(command.EntitlementId, cancellationToken);
        var key = EntitlementLedger.Key(command.CommandId, entitlement.UnitId, EntitlementEventType.Resumed, command.EventTime);
        if (await ledger.ReplayedAsync(key, command.EventTime, cancellationToken) is { } replayed) return RebaseEntitlementStartHandler.Result(entitlement, replayed.EventId, true);
        EntitlementLedger.Transition(entitlement, EntitlementEventType.Resumed);
        var open = await db.EntitlementSuspensions.FirstAsync(s => s.TenantId == tenantId && s.EntitlementId == entitlement.EntitlementId && s.ToDate == null, cancellationToken);
        if (command.On < open.FromDate) throw new CommandRefusedException("resume_before_suspend", $"The suspension began {open.FromDate:yyyy-MM-dd}; it cannot end on {command.On:yyyy-MM-dd}.");
        // Case 62: only a paused clock moves the end, by exactly the paused days.
        var paused = open.ClockPaused ? command.On.DayNumber - open.FromDate.DayNumber : 0;
        var oldEnd = entitlement.EndDate;
        if (paused > 0 && entitlement.EndDate is { } end)
        {
            entitlement.EndDate = end.AddDays(paused);
            entitlement.EndAdjustmentDays += paused;
        }
        var eventId = TimeOrderedId.New(command.EventTime);
        open.ToDate = command.On;
        open.ResumedEventId = eventId;
        await ledger.AppendAsync(entitlement, EntitlementEventType.Resumed, key, command.EventTime, command.ActorRef, new { entitlement_id = entitlement.EntitlementId, on = command.On, paused_days = paused, end_before = oldEnd, end_after = entitlement.EndDate }, null, cancellationToken, eventId);
        return RebaseEntitlementStartHandler.Result(entitlement, eventId, false);
    }
}

public static class TransferEntitlementHandler
{
    private static readonly DateOnly EuEffectiveDate = new(2026, 7, 31);

    public static async Task<TransferEntitlementResult> Handle(TransferEntitlement command, SerialisationDbContext db, EntitlementLedger ledger, ITenantContext tenant, IClock clock, CorrectionLedger corrections, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var source = await ledger.LoadAsync(command.SourceEntitlementId, cancellationToken);
        if (source.Direction != EntitlementDirection.Outbound) throw new CommandRefusedException("outbound_entitlement_required", "Replacement transfer applies only to customer cover.");
        var transferKey = EntitlementLedger.Key(command.CommandId, command.TargetUnitId, EntitlementEventType.Transferred, command.EventTime);
        if (await ledger.ReplayedAsync(transferKey, command.EventTime, cancellationToken) is { } replayed)
        {
            var existing = await db.Entitlements.AsNoTracking().SingleAsync(e => e.TenantId == tenantId && e.CreatedEventId == replayed.EventId, cancellationToken);
            using var replayAttributes = JsonDocument.Parse(replayed.Attributes ?? "{}");
            var replayPolicy = replayAttributes.RootElement.TryGetProperty("policy_applied", out var policyApplied) ? policyApplied.GetString() ?? "" : "";
            var replayRootId = existing.RootEntitlementId ?? source.EntitlementId;
            var replayChainLength = await ChainLengthAsync(db, tenantId, replayRootId, cancellationToken) - 1;
            return new(source.EntitlementId, existing.EntitlementId, replayRootId, replayPolicy, existing.EndDate!.Value, replayChainLength, replayed.EventId, true);
        }

        EntitlementLedger.Transition(source, EntitlementEventType.Closed);
        if (source.EndDate is not { } sourceEnd || source.StartDate is null) throw new CommandRefusedException("term_unresolved", "A replacement can only inherit an entitlement whose start and end are resolved.");
        if (!await db.Current.AsNoTracking().AnyAsync(c => c.TenantId == tenantId && c.UnitId == command.TargetUnitId, cancellationToken))
            throw new CommandRefusedException("target_unit_unknown", $"Unit {command.TargetUnitId} does not exist.");
        if (source.UnitId == command.TargetUnitId) throw new CommandRefusedException("target_is_source", "A replacement entitlement must move to a different unit.");
        if (await db.Entitlements.AnyAsync(e => e.TenantId == tenantId && e.UnitId == command.TargetUnitId && e.CoverageFamily == source.CoverageFamily && e.Status != EntitlementStatus.Closed, cancellationToken))
            throw new CommandRefusedException("family_occupied", $"Unit {command.TargetUnitId} already has a live {source.CoverageFamily} entitlement.");

        var version = await db.PolicyVersions.AsNoTracking().SingleAsync(v => v.TenantId == tenantId && v.PolicyVersionId == source.PolicyVersionId, cancellationToken);
        var policy = command.ReplacementPolicyCode is { } code
            ? await db.ReplacementPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Code == code, cancellationToken)
            : version.ReplacementPolicyId is { } policyId
                ? await db.ReplacementPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ReplacementPolicyId == policyId, cancellationToken)
                : null;
        if (policy is null) throw new CommandRefusedException("replacement_policy_unknown", command.ReplacementPolicyCode is null ? "The source policy has no replacement policy." : $"Replacement policy '{command.ReplacementPolicyCode}' does not exist.");

        var metadata = await corrections.PrepareAsync(CorrectionCommand.TransferEntitlement, command.Correction, command.CommandId, EntitlementTransferReason.Replacement, null,
            null, command.EventTime, CorrectionInitiatingRole.ServiceTechnician, cancellationToken);

        var rootId = source.RootEntitlementId ?? source.EntitlementId;
        if (policy.Rule == ReplacementRule.StatutoryExtensionOnce)
        {
            var scope = JsonSerializer.Deserialize<List<string>>(source.RegionScope, LedgerJson.Options) ?? [];
            if (source.Type != EntitlementType.Statutory || command.Remedy != "repair" || !command.ConsumerContract || command.ContractDate is not { } contractDate || contractDate < EuEffectiveDate
                || command.Country is not { } country || !Regions.Contains("EU", country) || !Regions.InScope(scope, country))
                throw new CommandRefusedException("statutory_extension_not_applicable", "The one-time statutory extension requires a live statutory entitlement and a repair remedy for an EU consumer contract dated on or after 2026-07-31 within its region scope.");
            if (await db.Entitlements.AnyAsync(e => e.TenantId == tenantId && (e.EntitlementId == rootId || e.RootEntitlementId == rootId) && e.StatutoryExtensionApplied, cancellationToken))
                throw new CommandRefusedException("statutory_extension_already_applied", $"Entitlement chain {rootId} has already received its statutory extension.");
        }

        var duration = new DurationRule(source.DurationValue, source.DurationUnit, source.EndOfMonthRule, source.EndInclusive);
        var computedEnd = ReplacementArithmetic.EndDate(sourceEnd, command.ReplacementDate, duration, policy.Rule, policy.MinDays, policy.ExtensionMonths);
        var eventId = TimeOrderedId.New(command.EventTime);
        var target = new Entitlement
        {
            TenantId = tenantId, EntitlementId = TimeOrderedId.New(command.EventTime), UnitId = command.TargetUnitId, Scope = source.Scope, ScopeChildUnitId = source.ScopeChildUnitId, ScopeItemClass = source.ScopeItemClass,
            Type = source.Type, CoverageFamily = source.CoverageFamily, Direction = source.Direction, OwnerPartyRef = source.OwnerPartyRef, OwnerPartyType = source.OwnerPartyType, ObligorEntityId = source.ObligorEntityId,
            PayerPartyRef = source.PayerPartyRef, PayerPartyType = source.PayerPartyType, PolicyVersionId = source.PolicyVersionId, StartTrigger = source.StartTrigger, StartBasis = source.StartBasis,
            StartEvidenceRef = source.StartEvidenceRef, StartDate = source.StartDate, StartTimeZone = source.StartTimeZone, EndDate = computedEnd, ComputedByRuleVersion = source.ComputedByRuleVersion,
            StartLatest = source.StartLatest, DurationValue = source.DurationValue, DurationUnit = source.DurationUnit, EndOfMonthRule = source.EndOfMonthRule, EndInclusive = source.EndInclusive,
            EndAdjustmentDays = computedEnd.DayNumber - TermArithmetic.EndDate(source.StartDate.Value, duration).DayNumber, DurationBasis = source.DurationBasis, Coverage = source.Coverage, RegionScope = source.RegionScope, Transferable = source.Transferable, ConditionAtCreation = source.ConditionAtCreation,
            ParentEntitlementId = source.EntitlementId, RootEntitlementId = rootId, ReplacementLinkId = command.ReplacementLinkId, AccrualCohortId = null, RevenueScheduleRef = source.RevenueScheduleRef,
            AssuranceOrService = source.AssuranceOrService, Status = EntitlementStatus.Active, Confidence = source.Confidence, StatutoryExtensionApplied = policy.Rule == ReplacementRule.StatutoryExtensionOnce,
            Provenance = EntitlementProvenance.Transferred, SourceDocumentType = source.SourceDocumentType, SourceDocumentId = source.SourceDocumentId, SourceDocumentLine = source.SourceDocumentLine,
            CustomerPoRef = source.CustomerPoRef, CreatedEventId = eventId, CreatedAt = clock.UtcNow,
        };
        db.Entitlements.Add(target);
        var limits = await db.EntitlementUsageLimits.AsNoTracking().Where(l => l.TenantId == tenantId && l.EntitlementId == source.EntitlementId).ToListAsync(cancellationToken);
        foreach (var limit in limits) db.EntitlementUsageLimits.Add(new EntitlementUsageLimit { TenantId = tenantId, EntitlementId = target.EntitlementId, MeterType = limit.MeterType, LimitValue = limit.LimitValue, Period = limit.Period });

        source.ClosedReason = EntitlementClosedReason.Replaced;
        source.ClosedEffective = command.ReplacementDate;
        source.ClosedAt = clock.UtcNow;
        source.SuccessorUnitId = target.UnitId;
        source.SuccessorEntitlementId = target.EntitlementId;
        var closeKey = EntitlementLedger.Key(command.CommandId, source.UnitId, EntitlementEventType.Closed, command.EventTime);
        await ledger.AppendAsync(source, EntitlementEventType.Closed, closeKey, command.EventTime, command.ActorRef, new
        {
            entitlement_id = source.EntitlementId, coverage_family = source.CoverageFamily, reason = EntitlementClosedReason.Replaced, effective = command.ReplacementDate,
            successor_entitlement_id = target.EntitlementId, successor_unit_id = target.UnitId,
        }, null, cancellationToken, correction: metadata);

        var chainLength = await ChainLengthAsync(db, tenantId, rootId, cancellationToken) - 1;
        await ledger.AppendAsync(target, EntitlementEventType.Transferred, transferKey, command.EventTime, command.ActorRef, new
        {
            source_entitlement_id = source.EntitlementId, target_entitlement_id = target.EntitlementId, source_unit_id = source.UnitId, target_unit_id = target.UnitId,
            replacement_link_id = command.ReplacementLinkId, policy_applied = policy.Code, computed_end = computedEnd, root_entitlement_id = rootId, chain_length = chainLength, accrual_suppressed = true,
        }, null, cancellationToken, eventId, metadata);
        await corrections.RecordAsync(CorrectionCommand.TransferEntitlement, metadata, command.ActorRef, command.EventTime, [source.UnitId, target.UnitId], cancellationToken);
        return new(source.EntitlementId, target.EntitlementId, rootId, policy.Code, computedEnd, chainLength, eventId, false);
    }

    // A superseding row (P2-T09) shares the root but replaces cover on the same unit; it is not a replacement, so it is
    // not counted. Coupled to SupersedeEntitlementHandler, which keeps the root on the successor.
    private static Task<int> ChainLengthAsync(SerialisationDbContext db, Yojana.Platform.Contracts.Tenancy.TenantId tenantId, Guid rootId, CancellationToken cancellationToken) =>
        db.Entitlements.CountAsync(e => e.TenantId == tenantId && (e.EntitlementId == rootId || e.RootEntitlementId == rootId) && e.SupersedesEntitlementId == null, cancellationToken);
}

public static class VoidEntitlementHandler
{
    public static async Task<EntitlementChangeResult> Handle(VoidEntitlement command, EntitlementLedger ledger, IClock clock, CorrectionLedger corrections, CancellationToken cancellationToken)
    {
        var entitlement = await ledger.LoadAsync(command.EntitlementId, cancellationToken);
        var key = EntitlementLedger.Key(command.CommandId, entitlement.UnitId, EntitlementEventType.Voided, command.EventTime);
        if (await ledger.ReplayedAsync(key, command.EventTime, cancellationToken) is { } replayed) return RebaseEntitlementStartHandler.Result(entitlement, replayed.EventId, true);
        if (!EntitlementStateMachine.VoidReasons.Contains(command.ClosedReason)) throw new CommandRefusedException("void_reason_unknown", $"'{command.ClosedReason}' is not one of {string.Join(", ", EntitlementStateMachine.VoidReasons)}.");
        if (string.IsNullOrWhiteSpace(command.Explanation) || command.EvidenceRefs.Count == 0)
        {
            throw new CommandRefusedException("evidence_required", "A void names why and the evidence for it.", "attach the evidence (document, inspection report) and an explanation");
        }

        var supplied = command.Correction ?? new CorrectionMetadata(command.CommandId, command.ClosedReason, command.Explanation, command.EvidenceRefs);
        var metadata = await corrections.PrepareAsync(CorrectionCommand.VoidEntitlement, supplied, command.CommandId, command.ClosedReason, command.Explanation,
            null, command.EventTime, CorrectionInitiatingRole.CustomerService, cancellationToken);

        EntitlementLedger.Transition(entitlement, EntitlementEventType.Voided);
        entitlement.ClosedReason = command.ClosedReason;
        entitlement.ClosedEffective = command.Effective;
        entitlement.ClosedAt = clock.UtcNow;
        var eventId = await ledger.AppendAsync(entitlement, EntitlementEventType.Voided, key, command.EventTime, command.ActorRef, new { entitlement_id = entitlement.EntitlementId, coverage_family = entitlement.CoverageFamily, reason = command.ClosedReason, explanation = command.Explanation, evidence = command.EvidenceRefs, effective = command.Effective }, null, cancellationToken, correction: metadata);
        await corrections.RecordAsync(CorrectionCommand.VoidEntitlement, metadata, command.ActorRef, command.EventTime, [entitlement.UnitId], cancellationToken);
        return RebaseEntitlementStartHandler.Result(entitlement, eventId, false);
    }
}

public static class CloseEntitlementHandler
{
    public static async Task<EntitlementChangeResult> Handle(CloseEntitlement command, EntitlementLedger ledger, IClock clock, CorrectionLedger corrections, CancellationToken cancellationToken)
    {
        var entitlement = await ledger.LoadAsync(command.EntitlementId, cancellationToken);
        var key = EntitlementLedger.Key(command.CommandId, entitlement.UnitId, EntitlementEventType.Closed, command.EventTime);
        if (await ledger.ReplayedAsync(key, command.EventTime, cancellationToken) is { } replayed) return RebaseEntitlementStartHandler.Result(entitlement, replayed.EventId, true);
        if (!EntitlementStateMachine.CloseReasons.Contains(command.ClosedReason))
        {
            throw new CommandRefusedException("close_reason_not_here", $"'{command.ClosedReason}' is not closed through CloseEntitlement.", "transfers, supersessions and voids close through their own commands");
        }

        if (command.ClosedReason == EntitlementClosedReason.Expired && (entitlement.EndDate is not { } end || command.Effective <= end))
        {
            throw new CommandRefusedException("not_expired", $"Entitlement {entitlement.EntitlementId} runs to {entitlement.EndDate:yyyy-MM-dd}; it cannot close as expired on {command.Effective:yyyy-MM-dd}.");
        }

        var metadata = command.Correction is null ? null : await corrections.PrepareAsync(
            CorrectionCommand.CloseEntitlement, command.Correction, command.CommandId, command.ClosedReason, command.Note,
            null, command.EventTime, CorrectionInitiatingRole.CustomerService, cancellationToken);

        EntitlementLedger.Transition(entitlement, EntitlementEventType.Closed);
        entitlement.ClosedReason = command.ClosedReason;
        entitlement.ClosedEffective = command.Effective;
        entitlement.ClosedAt = clock.UtcNow;
        var eventId = await ledger.AppendAsync(entitlement, EntitlementEventType.Closed, key, command.EventTime, command.ActorRef, new { entitlement_id = entitlement.EntitlementId, coverage_family = entitlement.CoverageFamily, reason = command.ClosedReason, effective = command.Effective, note = command.Note }, command.SourceDocument, cancellationToken, correction: metadata);
        if (metadata is not null)
            await corrections.RecordAsync(CorrectionCommand.CloseEntitlement, metadata, command.ActorRef, command.EventTime, [entitlement.UnitId], cancellationToken);
        return RebaseEntitlementStartHandler.Result(entitlement, eventId, false);
    }
}
