using System.Globalization;
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
/// Re-derives cover from the corrected document's warranty lines in one set-based unit of work. Policy selection,
/// jurisdiction checks, calendar arithmetic, refurbished floors and extended-plan arithmetic are the existing P2
/// implementations; this service only supplies a bulk persistence path so 500 units do not execute 500 read queries.
/// </summary>
public sealed class SourceDocumentCorrectionEntitlements(
    SerialisationDbContext db,
    PolicyStore store,
    ITimeZonePort zones,
    RefurbishedGoodsSettings refurbishedSettings,
    WarrantyReferenceSettings warrantyReference,
    ITenantContext tenant,
    IClock clock)
{
    // Coupled to the factory registration in SerialisationModule: RefurbishedGoodsSettings and WarrantyReferenceSettings
    // own ITenantSettings. A constructor registration exposes PlatformDbContext to Wolverine beside this module context.
    private sealed record UnitInput(Guid UnitId, Guid ItemId, Guid? LocationId, string? ConditionGrade);
    private sealed record LinePlan(InvoiceDocument Invoice, InvoiceLine Line, WarrantyPolicyVersion Version, PolicyTerms Terms, CoverageFamily Family);
    private sealed record Created(Entitlement Entitlement, LinePlan Line, IReadOnlyList<EntitlementRevenueSchedulePeriod>? RevenueSchedule, Guid? BaseEntitlementId,
        string StartExplanation, bool CapApplied, RefurbishedEntitlementDisclosure? Disclosure);

    public sealed record ApplyResult(int Superseded, int Created, IReadOnlyList<UnitEventDraft> Drafts,
        IReadOnlyList<CorrectionEntitlementPreview> Preview);

    public async Task<IReadOnlyList<CorrectionEntitlementPreview>> PreviewAsync(IReadOnlyCollection<Guid> unitIds,
        OwnershipSourceDocument previous, OwnershipSourceDocument corrected, CancellationToken cancellationToken)
    {
        var plan = await LinesAsync(corrected, cancellationToken);
        EnsureWarrantyLines(plan);
        var rows = await LiveAsync(unitIds, cancellationToken);
        var previousDocuments = SourceDocuments(previous);
        var changes = new List<CorrectionEntitlementPreview>();
        foreach (var unitId in unitIds.Order())
        {
            var old = rows.Where(e => e.UnitId == unitId && IsFrom(e, previousDocuments)).OrderBy(e => e.CoverageFamily).ToList();
            foreach (var entitlement in old)
            {
                var replacement = plan.FirstOrDefault(line => line.Version.CoverageFamily == entitlement.CoverageFamily);
                changes.Add(new(unitId, replacement is null ? "close" : "supersede", entitlement.EntitlementId,
                    $"{entitlement.CoverageFamily} · {EntitlementClosedReason.SourceDocumentCorrected}"));
            }
            foreach (var line in plan)
            {
                changes.Add(new(unitId, "create", null, $"{line.Version.CoverageFamily} · {line.Version.PolicyCode} v{line.Version.Version}"));
            }
        }
        return changes;
    }

    public async Task<ApplyResult> ApplyAsync(Guid commandId, IReadOnlyCollection<Guid> unitIds,
        OwnershipSourceDocument previous, OwnershipSourceDocument corrected, string actorRef, Guid legalEntityId,
        CorrectionMetadata metadata, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var ids = unitIds.Distinct().ToList();
        var units = await db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && ids.Contains(c.UnitId))
            .Select(c => new UnitInput(c.UnitId, c.ItemId, c.LocationId, c.ConditionGrade)).ToDictionaryAsync(c => c.UnitId, cancellationToken);
        if (units.Count != ids.Count) throw new CommandRefusedException("unit_unknown", "At least one selected unit has no current projection.");

        var lines = await LinesAsync(corrected, cancellationToken);
        EnsureWarrantyLines(lines);

        var live = await LiveAsync(ids, cancellationToken);
        var previousDocuments = SourceDocuments(previous);
        var wrong = live.Where(e => IsFrom(e, previousDocuments)).ToList();
        if (wrong.FirstOrDefault(e => e.Status == EntitlementStatus.Suspended) is { } suspended)
            throw new CommandRefusedException("source_entitlement_suspended", $"Entitlement {suspended.EntitlementId} is suspended.",
                "resume it, correct the source document, then suspend the corrected entitlement if still required");

        var targetFamilies = lines.Select(l => l.Version.CoverageFamily).ToHashSet(StringComparer.Ordinal);
        var wrongIds = wrong.Select(e => e.EntitlementId).ToHashSet();
        var conflict = live.FirstOrDefault(e => targetFamilies.Contains(e.CoverageFamily) && !wrongIds.Contains(e.EntitlementId));
        if (conflict is not null)
            throw new CommandRefusedException("correct_source_entitlement_conflict",
                $"Unit {conflict.UnitId} already has live {conflict.CoverageFamily} cover from another source.",
                "close or correct that entitlement before correcting the source document");

        var initialState = live.GroupBy(e => e.UnitId).ToDictionary(g => g.Key,
            g => g.Select(e => new EntitlementCoverageEntry(e.EntitlementId, e.Status, e.EndDate, e.Direction)).ToList());
        foreach (var id in ids) initialState.TryAdd(id, []);

        foreach (var old in wrong)
        {
            EntitlementLedger.Transition(old, EntitlementEventType.Closed);
            old.ClosedReason = EntitlementClosedReason.SourceDocumentCorrected;
            var replacement = lines.SingleOrDefault(line => line.Version.CoverageFamily == old.CoverageFamily);
            old.ClosedEffective = DateOnly.FromDateTime(replacement?.Invoice.PostedAt ?? corrected.EffectiveAt);
            old.ClosedAt = clock.UtcNow;
        }
        // The filtered unique index permits one live row per family. Persist the closures before adding successors;
        // both saves remain inside the command's transaction and roll back together on any later refusal.
        await db.SaveChangesAsync(cancellationToken);

        var settings = await refurbishedSettings.GetAsync(cancellationToken);
        var cohorts = await CohortsAsync(units.Values, lines, cancellationToken);
        var created = new List<Created>(ids.Count * lines.Count);
        foreach (var unit in units.Values.OrderBy(u => u.UnitId))
        {
            var byFamily = new Dictionary<string, Entitlement>(StringComparer.Ordinal);
            foreach (var retained in live.Where(e => e.UnitId == unit.UnitId && !wrongIds.Contains(e.EntitlementId)))
                byFamily[retained.CoverageFamily] = retained;

            foreach (var line in lines.OrderBy(l => l.Terms.AssuranceOrService == AssuranceOrService.Service ? 1 : 0).ThenBy(l => l.Version.CoverageFamily))
            {
                var predecessor = wrong.SingleOrDefault(e => e.UnitId == unit.UnitId && e.CoverageFamily == line.Version.CoverageFamily);
                var next = line.Terms.AssuranceOrService == AssuranceOrService.Service
                    ? await CreateServiceAsync(unit, line, corrected, predecessor, byFamily, cancellationToken)
                    : await CreateAssuranceAsync(unit, line, corrected, predecessor, settings, cohorts, cancellationToken);
                byFamily[next.Entitlement.CoverageFamily] = next.Entitlement;
                created.Add(next);
                db.Entitlements.Add(next.Entitlement);
                foreach (var limit in line.Terms.UsageLimits)
                    db.EntitlementUsageLimits.Add(new EntitlementUsageLimit { TenantId = tenantId, EntitlementId = next.Entitlement.EntitlementId,
                        MeterType = limit.MeterType, LimitValue = limit.Limit, Period = limit.Period });
                if (predecessor is not null)
                {
                    predecessor.SuccessorEntitlementId = next.Entitlement.EntitlementId;
                }
            }
        }

        var state = initialState.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        var drafts = new List<UnitEventDraft>(wrong.Count + created.Count);
        var preview = new List<CorrectionEntitlementPreview>(wrong.Count + created.Count);
        foreach (var unitId in ids.Order())
        {
            foreach (var old in wrong.Where(e => e.UnitId == unitId).OrderBy(e => e.CoverageFamily))
            {
                var successor = created.FirstOrDefault(c => c.Entitlement.UnitId == unitId && c.Entitlement.CoverageFamily == old.CoverageFamily);
                var before = EntitlementCoverage.From(state[unitId]);
                var index = state[unitId].FindIndex(e => e.EntitlementId == old.EntitlementId);
                state[unitId][index] = state[unitId][index] with { Status = EntitlementStatus.Closed };
                var after = EntitlementCoverage.From(state[unitId]);
                var at = successor?.Line.Invoice.PostedAt ?? corrected.EffectiveAt;
                var document = successor is null ? corrected.Document : successor.Line.Invoice.Document with { Line = successor.Line.Line.LineNo };
                var eventType = successor is null ? EntitlementEventType.Closed : EntitlementEventType.Superseded;
                var attributes = successor is null
                    ? new { entitlement_id = old.EntitlementId, coverage_family = old.CoverageFamily,
                        reason = EntitlementClosedReason.SourceDocumentCorrected, effective = old.ClosedEffective }
                    : (object)new { superseded_entitlement_id = old.EntitlementId, new_entitlement_id = successor.Entitlement.EntitlementId,
                        coverage_family = old.CoverageFamily, effective_date = old.ClosedEffective, old_policy_version_id = old.PolicyVersionId,
                        new_policy_version_id = successor.Entitlement.PolicyVersionId, reason_code = EntitlementClosedReason.SourceDocumentCorrected };
                drafts.Add(Event(commandId, unitId, old.ObligorEntityId, eventType, old.EntitlementId, at, actorRef, legalEntityId,
                    document, metadata, before, after, TimeOrderedId.New(at), attributes));
                preview.Add(new(unitId, successor is null ? "close" : "supersede", old.EntitlementId,
                    $"{old.CoverageFamily} · {EntitlementClosedReason.SourceDocumentCorrected}"));
            }

            foreach (var next in created.Where(c => c.Entitlement.UnitId == unitId).OrderBy(c => c.Entitlement.CoverageFamily))
            {
                var entitlement = next.Entitlement;
                var before = EntitlementCoverage.From(state[unitId]);
                state[unitId].Add(new(entitlement.EntitlementId, entitlement.Status, entitlement.EndDate, entitlement.Direction));
                var after = EntitlementCoverage.From(state[unitId]);
                var document = next.Line.Invoice.Document with { Line = next.Line.Line.LineNo };
                var eventType = next.RevenueSchedule is null ? EntitlementEventType.Created : EntitlementEventType.ExtendedSold;
                var attributes = next.RevenueSchedule is null ? CreatedAttributes(unitId, units[unitId].ItemId, next) : ExtendedAttributes(units[unitId].ItemId, next);
                drafts.Add(Event(commandId, unitId, entitlement.ObligorEntityId, eventType, entitlement.EntitlementId,
                    next.Line.Invoice.PostedAt, actorRef, legalEntityId, document, metadata, before, after, entitlement.CreatedEventId, attributes));
                preview.Add(new(unitId, "create", entitlement.EntitlementId,
                    $"{entitlement.CoverageFamily} · {next.Line.Version.PolicyCode} v{next.Line.Version.Version}"));
            }
        }

        var successorKeys = created.Select(next => (next.Entitlement.UnitId, next.Entitlement.CoverageFamily)).ToHashSet();
        var superseded = wrong.Count(old => successorKeys.Contains((old.UnitId, old.CoverageFamily)));
        return new(superseded, created.Count, drafts, preview);
    }

    private async Task<List<LinePlan>> LinesAsync(OwnershipSourceDocument source, CancellationToken cancellationToken)
    {
        var lines = new List<LinePlan>();
        foreach (var invoice in source.WarrantyDocuments.OrderBy(i => i.PostedAt))
        {
            foreach (var line in invoice.Lines.Where(l => l.ItemType == "warranty_service"))
            {
                var resolved = await SalesInvoicePostedHandler.ResolveLineAsync(invoice, line, db, store, warrantyReference, tenant, cancellationToken);
                var family = await db.CoverageFamilies.AsNoTracking().SingleOrDefaultAsync(f => f.TenantId == tenant.TenantId
                    && f.FamilyCode == resolved.Version.CoverageFamily, cancellationToken)
                    ?? throw new CommandRefusedException("family_unknown", $"Coverage family '{resolved.Version.CoverageFamily}' is not defined.");
                if (!family.Active) throw new CommandRefusedException("family_inactive", $"Coverage family '{family.FamilyCode}' is inactive.");
                lines.Add(new(invoice, line, resolved.Version, resolved.Terms, family));
            }
        }
        var duplicate = lines.GroupBy(l => l.Version.CoverageFamily, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new CommandRefusedException("correct_source_family_ambiguous",
                $"The corrected source has {duplicate.Count()} warranty lines in family '{duplicate.Key}'.",
                "identify one effective warranty line per coverage family before correcting the source");
        foreach (var line in lines.Where(l => l.Terms.AssuranceOrService == AssuranceOrService.Service))
        {
            if (!line.Family.Stacking) throw new CommandRefusedException("family_not_add_on", $"'{line.Family.FamilyCode}' does not stack beside base cover.");
            if (line.Terms.Start.Trigger is not (StartTrigger.PlanPurchase or StartTrigger.BaseExpiry))
                throw new CommandRefusedException("start_trigger_not_plan", $"{line.Version.PolicyCode} starts at '{line.Terms.Start.Trigger}'.");
            if (line.Line.Price <= 0) throw new CommandRefusedException("price_not_positive", "A separately priced plan needs a price above zero.");
            if (line.Line.Currency.Length != 3 || line.Line.Currency.Any(c => c is < 'A' or > 'Z'))
                throw new CommandRefusedException("currency_invalid", $"'{line.Line.Currency}' is not an ISO 4217 code.");
        }
        return lines;
    }

    private static void EnsureWarrantyLines(IReadOnlyCollection<LinePlan> lines)
    {
        if (lines.Count == 0)
            throw new CommandRefusedException("correct_source_has_no_warranty_lines", "The corrected source document has no warranty-service lines.",
                "post or link the warranty lines to the corrected order or delivery before correcting its source");
    }

    private Task<List<Entitlement>> LiveAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken) =>
        db.Entitlements.Where(e => e.TenantId == tenant.TenantId && unitIds.Contains(e.UnitId)
            && e.Direction == EntitlementDirection.Outbound && e.Status != EntitlementStatus.Closed).ToListAsync(cancellationToken);

    private static HashSet<(string Type, Guid Id)> SourceDocuments(OwnershipSourceDocument source) =>
        source.WarrantyDocuments.Select(i => (i.Document.Type, i.Document.Id)).Append((source.Document.Type, source.Document.Id)).ToHashSet();

    private static bool IsFrom(Entitlement entitlement, HashSet<(string Type, Guid Id)> documents) =>
        entitlement.SourceDocumentType is { } type && entitlement.SourceDocumentId is { } id && documents.Contains((type, id));

    private async Task<Dictionary<(Guid Obligor, Guid Item, string Period), AccrualCohort>> CohortsAsync(
        IEnumerable<UnitInput> units, IReadOnlyCollection<LinePlan> lines, CancellationToken cancellationToken)
    {
        var assurance = lines.Where(l => l.Terms.AssuranceOrService == AssuranceOrService.Assurance).ToList();
        if (assurance.Count == 0) return [];
        var obligors = assurance.Select(l => l.Invoice.ObligorEntityId).Distinct().ToList();
        var items = units.Select(u => u.ItemId).Distinct().ToList();
        var periods = assurance.Select(l => DateOnly.FromDateTime(l.Invoice.PostedAt).ToString("yyyy-MM", CultureInfo.InvariantCulture)).Distinct().ToList();
        var rows = await db.AccrualCohorts.Where(c => c.TenantId == tenant.TenantId && obligors.Contains(c.ObligorEntityId)
            && items.Contains(c.ItemId) && periods.Contains(c.Period) && c.AssuranceOrService == AssuranceOrService.Assurance).ToListAsync(cancellationToken);
        return rows.ToDictionary(c => (c.ObligorEntityId, c.ItemId, c.Period));
    }

    private async Task<Created> CreateAssuranceAsync(UnitInput unit, LinePlan line, OwnershipSourceDocument source,
        Entitlement? predecessor, RefurbishedGoodsPolicy settings,
        Dictionary<(Guid Obligor, Guid Item, string Period), AccrualCohort> cohorts, CancellationToken cancellationToken)
    {
        var grade = unit.ConditionGrade ?? "new";
        RefurbishedEntitlementDisclosure? disclosure = null;
        EffectiveDuration duration;
        if (grade == "new") duration = new(line.Terms.Duration, false, null);
        else
        {
            if (line.Invoice.Channel is null || line.Invoice.Country is null)
                throw new CommandRefusedException("refurbished_sale_context_missing", "A non-new sale needs its channel and country.",
                    "supply the sale channel and ISO country on the corrected invoice view");
            disclosure = RefurbishedGoodsPolicyEngine.Decide(unit.ItemId, grade, line.Invoice.Channel, line.Invoice.Country,
                RefurbishedBuyerKind.Consumer, line.Terms.Refurbished, settings, null);
            duration = new(line.Terms.Duration with { Value = disclosure.TermMonths, Unit = DurationRule.Months }, disclosure.FloorApplied, disclosure.MatchedRow);
        }
        var zone = await CreateEntitlementHandler.ZoneAsync(line.Terms.ReferenceTimeZoneRule,
            line.Invoice.SiteLocationId ?? unit.LocationId, line.Invoice.ObligorEntityId, zones, cancellationToken);
        var evidence = new Dictionary<string, DateTime>
        {
            [StartTrigger.Invoice] = line.Invoice.PostedAt,
            [StartTrigger.PlanPurchase] = line.Invoice.PostedAt,
        };
        var start = TermArithmetic.ResolveStart(line.Terms.Start, evidence, zone);
        var startLatest = line.Terms.Start.CapDays is { } cap ? TermArithmetic.LocalDay(line.Invoice.PostedAt, zone).AddDays(cap) : (DateOnly?)null;
        var period = DateOnly.FromDateTime(line.Invoice.PostedAt).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var cohortKey = (line.Invoice.ObligorEntityId, unit.ItemId, period);
        if (!cohorts.TryGetValue(cohortKey, out var cohort))
        {
            cohort = new AccrualCohort { TenantId = tenant.TenantId, CohortId = TimeOrderedId.New(line.Invoice.PostedAt),
                ObligorEntityId = line.Invoice.ObligorEntityId, ItemId = unit.ItemId, Period = period,
                AssuranceOrService = AssuranceOrService.Assurance, CreatedAt = clock.UtcNow };
            cohorts[cohortKey] = cohort;
            db.AccrualCohorts.Add(cohort);
        }
        var eventId = TimeOrderedId.New(line.Invoice.PostedAt);
        var entitlement = Common(unit, line, source, predecessor, eventId, zone);
        entitlement.StartBasis = start.Basis;
        entitlement.StartEvidenceRef = start.Basis is null ? null : $"{line.Invoice.Document.Type}:{line.Invoice.Document.Id:N}";
        entitlement.StartDate = start.Start;
        entitlement.EndDate = start.Start is { } day ? TermArithmetic.EndDate(day, duration.Duration) : null;
        entitlement.StartLatest = startLatest;
        entitlement.DurationValue = duration.Duration.Value;
        entitlement.DurationUnit = duration.Duration.Unit;
        entitlement.EndOfMonthRule = duration.Duration.EndOfMonth;
        entitlement.EndInclusive = duration.Duration.EndInclusive;
        entitlement.ConditionAtCreation = grade;
        entitlement.AccrualCohortId = cohort.CohortId;
        entitlement.Status = start.Start is null ? EntitlementStatus.PendingEvidence : EntitlementStatus.Active;
        entitlement.RefurbishedMatchedRow = disclosure is null ? null : JsonSerializer.Serialize(disclosure.MatchedRow, LedgerJson.Options);
        entitlement.RefurbishedFloorApplied = disclosure?.FloorApplied ?? false;
        entitlement.RefurbishedFloorMonths = disclosure?.FloorMonths;
        entitlement.RefurbishedFloorSource = disclosure?.FloorSource;
        entitlement.RefurbisherPartyRef = disclosure?.RefurbisherPartyRef;
        entitlement.RefurbishedStatementKind = disclosure?.StatementKind;
        return new(entitlement, line, null, null, start.Explanation, start.CapApplied, disclosure);
    }

    private async Task<Created> CreateServiceAsync(UnitInput unit, LinePlan line, OwnershipSourceDocument source,
        Entitlement? predecessor, Dictionary<string, Entitlement> byFamily, CancellationToken cancellationToken)
    {
        var zone = await CreateEntitlementHandler.ZoneAsync(line.Terms.ReferenceTimeZoneRule,
            line.Invoice.SiteLocationId ?? unit.LocationId, line.Invoice.ObligorEntityId, zones, cancellationToken);
        var purchaseDay = TermArithmetic.LocalDay(line.Invoice.PostedAt, zone);
        BaseCoverage? baseCoverage = null;
        if (line.Terms.Eligibility?.BaseFamily is { } family && byFamily.TryGetValue(family, out var baseEntitlement))
            baseCoverage = new(baseEntitlement.EntitlementId, baseEntitlement.Status, baseEntitlement.StartDate, baseEntitlement.EndDate);
        if (ExtendedPlanArithmetic.Ineligible(line.Terms.Eligibility, baseCoverage, purchaseDay) is { } refusal)
            throw new CommandRefusedException(refusal.Rule, refusal.Message);
        var start = ExtendedPlanArithmetic.Start(line.Terms.Start.Trigger, purchaseDay, baseCoverage)
            ?? throw new CommandRefusedException("base_not_resolved", "The plan starts at base expiry, but the base end is unresolved.");
        var end = TermArithmetic.EndDate(start, line.Terms.Duration);
        var price = line.Line.Price / Math.Max(1, line.Line.Quantity);
        var schedule = ExtendedPlanArithmetic.RevenueSchedule(start, end, price);
        var eventId = TimeOrderedId.New(line.Invoice.PostedAt);
        var entitlement = Common(unit, line, source, predecessor, eventId, zone);
        entitlement.StartBasis = line.Terms.Start.Trigger;
        entitlement.StartEvidenceRef = $"{line.Invoice.Document.Type}:{line.Invoice.Document.Id:N}";
        entitlement.StartDate = start;
        entitlement.EndDate = end;
        return new(entitlement, line, schedule, baseCoverage?.EntitlementId, line.Terms.Start.Trigger, false, null);
    }

    private Entitlement Common(UnitInput unit, LinePlan line, OwnershipSourceDocument source, Entitlement? predecessor,
        Guid eventId, string zone) => new()
    {
        TenantId = tenant.TenantId,
        EntitlementId = TimeOrderedId.New(line.Invoice.PostedAt),
        UnitId = unit.UnitId,
        Scope = EntitlementScope.Unit,
        Type = line.Version.EntitlementType,
        CoverageFamily = line.Version.CoverageFamily,
        Direction = EntitlementDirection.Outbound,
        OwnerPartyRef = source.OwnerPartyRef,
        OwnerPartyType = source.OwnerPartyType,
        ObligorEntityId = line.Invoice.ObligorEntityId,
        PayerPartyRef = source.OwnerPartyRef,
        PayerPartyType = source.OwnerPartyType,
        PolicyVersionId = line.Version.PolicyVersionId,
        StartTrigger = line.Terms.Start.Trigger,
        StartTimeZone = zone,
        ComputedByRuleVersion = line.Version.RuleVersion,
        DurationValue = line.Terms.Duration.Value,
        DurationUnit = line.Terms.Duration.Unit,
        EndOfMonthRule = line.Terms.Duration.EndOfMonth,
        EndInclusive = line.Terms.Duration.EndInclusive,
        DurationBasis = line.Terms.DurationBasis,
        Coverage = JsonSerializer.Serialize(line.Terms.Coverage, LedgerJson.Options),
        RegionScope = JsonSerializer.Serialize(line.Terms.RegionScope, LedgerJson.Options),
        Transferable = line.Terms.Transferable,
        ParentEntitlementId = predecessor?.ParentEntitlementId,
        RootEntitlementId = predecessor is null ? null : predecessor.RootEntitlementId ?? predecessor.EntitlementId,
        SupersedesEntitlementId = predecessor?.EntitlementId,
        AssuranceOrService = line.Terms.AssuranceOrService,
        Status = EntitlementStatus.Active,
        Confidence = Confidence.High,
        Provenance = predecessor is null ? EntitlementProvenance.Created : EntitlementProvenance.Superseding,
        SourceDocumentType = line.Invoice.Document.Type,
        SourceDocumentId = line.Invoice.Document.Id,
        SourceDocumentLine = line.Line.LineNo,
        CustomerPoRef = line.Invoice.CustomerPoRef,
        CreatedEventId = eventId,
        CreatedAt = clock.UtcNow,
    };

    private UnitEventDraft Event(Guid commandId, Guid unitId, Guid obligorEntityId, string eventType, Guid entitlementId,
        DateTime at, string actorRef, Guid legalEntityId, DocumentRef document, CorrectionMetadata metadata,
        EntitlementCoverage before, EntitlementCoverage after, Guid eventId, object attributes)
    {
        var node = JsonSerializer.SerializeToNode(attributes, LedgerJson.Options)!.AsObject();
        node["coverage_before"] = JsonSerializer.SerializeToNode(new
        {
            live_entitlement_ids = before.LiveEntitlementIds,
            warranty_end_max = before.WarrantyEndMax,
        }, LedgerJson.Options);
        return new UnitEventDraft
        {
            UnitId = unitId,
            EventTime = at,
            EventType = eventType,
            PresetEventId = eventId,
            LegalEntityId = obligorEntityId == Guid.Empty ? legalEntityId : obligorEntityId,
            ActorRef = actorRef,
            DocumentType = document.Type,
            DocumentId = document.Id,
            DocumentLine = document.Line,
            IdempotencyKey = IdempotencyKey.Derive(EntitlementLedger.Key(commandId, unitId, eventType, at), entitlementId.ToString("N")),
            Attributes = CorrectionLedger.Attributes(metadata, node),
            Delta = new CurrentStateDelta { LiveEntitlementIds = after.LiveEntitlementIds,
                WarrantyEndMax = new Optional<DateOnly?>(after.WarrantyEndMax) },
        };
    }

    private static object CreatedAttributes(Guid unitId, Guid itemId, Created created) => new
    {
        entitlement_id = created.Entitlement.EntitlementId,
        item_id = itemId,
        coverage_family = created.Entitlement.CoverageFamily,
        entitlement_type = created.Entitlement.Type,
        policy_level = "warranty_line",
        policy_code = created.Line.Version.PolicyCode,
        policy_version = created.Line.Version.Version,
        policy_version_id = created.Line.Version.PolicyVersionId,
        status = created.Entitlement.Status,
        start_basis = created.Entitlement.StartBasis,
        start = created.StartExplanation,
        start_date = created.Entitlement.StartDate,
        end_date = created.Entitlement.EndDate,
        cap_applied = created.CapApplied,
        floor_applied = created.Entitlement.RefurbishedFloorApplied,
        matched_refurbished_row = created.Disclosure?.MatchedRow,
        term_months = created.Disclosure?.TermMonths,
        floor_months = created.Disclosure?.FloorMonths,
        floor_source = created.Disclosure?.FloorSource,
        refurbisher_party_ref = created.Disclosure?.RefurbisherPartyRef,
        statement_kind = created.Disclosure?.StatementKind,
        time_zone = created.Entitlement.StartTimeZone,
        accrual_cohort_id = created.Entitlement.AccrualCohortId,
        assurance_or_service = created.Entitlement.AssuranceOrService,
        accrual_suppressed = false,
        provenance = created.Entitlement.Provenance,
        owner_party_ref = created.Entitlement.OwnerPartyRef,
        rule_version = created.Line.Version.RuleVersion,
        source_document_corrected = true,
    };

    private static object ExtendedAttributes(Guid itemId, Created created)
    {
        var price = created.Line.Line.Price / Math.Max(1, created.Line.Line.Quantity);
        return new
        {
            entitlement_id = created.Entitlement.EntitlementId,
            item_id = itemId,
            coverage_family = created.Entitlement.CoverageFamily,
            entitlement_type = created.Entitlement.Type,
            policy_code = created.Line.Version.PolicyCode,
            policy_version = created.Line.Version.Version,
            policy_version_id = created.Line.Version.PolicyVersionId,
            base_entitlement_id = created.BaseEntitlementId,
            eligibility = created.Line.Terms.Eligibility,
            start_basis = created.Entitlement.StartBasis,
            purchase_day = DateOnly.FromDateTime(created.Line.Invoice.PostedAt),
            start_date = created.Entitlement.StartDate,
            end_date = created.Entitlement.EndDate,
            term_months = ExtendedPlanArithmetic.TermMonths(created.Line.Terms.Duration),
            price,
            currency = created.Line.Line.Currency,
            revenue_schedule = created.RevenueSchedule,
            payer_party_ref = created.Entitlement.PayerPartyRef,
            owner_party_ref = created.Entitlement.OwnerPartyRef,
            time_zone = created.Entitlement.StartTimeZone,
            assurance_or_service = AssuranceOrService.Service,
            service_contract_regulated = created.Line.Terms.ServiceContractRegulated,
            rule_version = created.Line.Version.RuleVersion,
            source_document_corrected = true,
        };
    }
}
