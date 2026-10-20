using System.Text.Json;
using System.Text.RegularExpressions;
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
/// <c>AttachExtendedWarranty</c> (P2-T07, ADR-0001 "Extended warranty", "Coverage families and stacking"): a separately
/// priced plan in an add-on family, stacked beside the base. One ledger fact, <c>entitlement.extended_sold</c>, creates
/// it and carries price and revenue schedule, so <see cref="PublishedEventMapper"/> publishes <c>Entitlement.ExtendedSold</c>
/// (deferred revenue) and never <c>Entitlement.Created</c> (an accrual): a plan is not an assurance warranty.
/// </summary>
public static partial class AttachExtendedWarrantyHandler
{
    public static async Task<AttachExtendedWarrantyResult> Handle(AttachExtendedWarranty command, SerialisationDbContext db, EntitlementLedger ledger, PolicyResolver resolver, PolicyStore store, ITimeZonePort zones, ITenantContext tenant, IClock clock, CorrectionLedger corrections, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var key = EntitlementLedger.Key(command.CommandId, command.UnitId, EntitlementEventType.ExtendedSold, command.EventTime);
        if (await ledger.ReplayedAsync(key, command.EventTime, cancellationToken) is { } replayed)
        {
            var existing = await db.Entitlements.AsNoTracking().SingleAsync(e => e.TenantId == tenantId && e.CreatedEventId == replayed.EventId, cancellationToken);
            var existingVersion = await db.PolicyVersions.AsNoTracking().SingleAsync(v => v.TenantId == tenantId && v.PolicyVersionId == existing.PolicyVersionId, cancellationToken);
            using var attributes = JsonDocument.Parse(replayed.Attributes ?? "{}");
            var recorded = attributes.RootElement.TryGetProperty("revenue_schedule", out var s) ? s.Deserialize<List<EntitlementRevenueSchedulePeriod>>(LedgerJson.Options) ?? [] : [];
            Guid? baseId = attributes.RootElement.TryGetProperty("base_entitlement_id", out var b) && b.ValueKind == JsonValueKind.String ? b.GetGuid() : null;
            return new(existing.EntitlementId, existingVersion.PolicyCode, existingVersion.Version, existing.StartBasis ?? "", existing.StartDate!.Value, existing.EndDate!.Value, baseId, recorded, replayed.EventId, true);
        }

        if (command.Price <= 0) throw new CommandRefusedException("price_not_positive", "A separately priced plan needs a price above zero.");
        if (!CurrencyCode().IsMatch(command.Currency)) throw new CommandRefusedException("currency_invalid", $"'{command.Currency}' is not an ISO 4217 code.");

        var unit = await db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && c.UnitId == command.UnitId).Select(c => new { c.ItemId, c.LocationId }).FirstOrDefaultAsync(cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Unit {command.UnitId} does not exist.");
        var family = await db.CoverageFamilies.AsNoTracking().FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FamilyCode == command.CoverageFamily, cancellationToken)
            ?? throw new CommandRefusedException("family_unknown", $"Coverage family '{command.CoverageFamily}' is not defined for this tenant.");
        if (!family.Active) throw new CommandRefusedException("family_inactive", $"Coverage family '{command.CoverageFamily}' is inactive.");
        if (!family.Stacking)
        {
            throw new CommandRefusedException("family_not_add_on", $"'{command.CoverageFamily}' does not stack beside the base; a plan is sold in an add-on family.", "mark the family as stacking, or change coverage within it with SupersedeEntitlement");
        }

        if (await db.Entitlements.AnyAsync(e => e.TenantId == tenantId && e.UnitId == command.UnitId && e.CoverageFamily == command.CoverageFamily && e.Status != EntitlementStatus.Closed, cancellationToken))
        {
            throw new CommandRefusedException("family_occupied", $"Unit {command.UnitId} already has a live {command.CoverageFamily} entitlement.", "supersede it (SupersedeEntitlement) or close it first; a family holds one live entitlement");
        }

        WarrantyPolicyVersion version;
        if (command.PolicyVersionId is { } named)
        {
            version = await db.PolicyVersions.AsNoTracking().FirstOrDefaultAsync(v => v.TenantId == tenantId && v.PolicyVersionId == named, cancellationToken)
                ?? throw new CommandRefusedException("policy_version_unknown", $"Policy version {named} does not exist.");
        }
        else
        {
            var resolution = await resolver.ResolveAsync(new PolicyQuestion(command.UnitId, unit.ItemId, command.CoverageFamily, command.ContractDate, command.OwnerPartyRef, null, null, command.Country), cancellationToken);
            if (resolution.PolicyVersionId is not { } resolved || resolution.Level is PolicyLevel.None or PolicyLevel.UnitEntitlement or PolicyLevel.ParentEntitlement)
            {
                throw new CommandRefusedException("no_policy", $"No {command.CoverageFamily} plan applies to unit {command.UnitId} for contracts on {command.ContractDate:yyyy-MM-dd}: {string.Join("; ", resolution.Trail)}", "give the item a plan policy, or name a policy version");
            }

            version = await db.PolicyVersions.AsNoTracking().FirstAsync(v => v.TenantId == tenantId && v.PolicyVersionId == resolved, cancellationToken);
        }

        if (version.Status == PolicyVersionStatus.Draft) throw new CommandRefusedException("policy_not_published", $"{version.PolicyCode} v{version.Version} is a draft.");
        if (version.CoverageFamily != command.CoverageFamily) throw new CommandRefusedException("policy_family_differs", $"{version.PolicyCode} is a {version.CoverageFamily} policy, not {command.CoverageFamily}.");
        var terms = await store.ReadTermsAsync(version, cancellationToken);
        if (terms.AssuranceOrService != AssuranceOrService.Service) throw new CommandRefusedException("policy_not_separately_priced", $"{version.PolicyCode} is an assurance warranty; it comes with the sale (CreateEntitlement), not as a plan.");
        if (terms.Start.Trigger is not (StartTrigger.PlanPurchase or StartTrigger.BaseExpiry))
        {
            throw new CommandRefusedException("start_trigger_not_plan", $"{version.PolicyCode} starts at '{terms.Start.Trigger}'; a plan starts at plan_purchase or base_expiry.");
        }

        var zone = await CreateEntitlementHandler.ZoneAsync(terms.ReferenceTimeZoneRule, command.SiteLocationId ?? unit.LocationId, command.ObligorEntityId, zones, cancellationToken);
        var purchaseDay = TermArithmetic.LocalDay(command.PurchasedAt, zone);
        BaseCoverage? baseCoverage = null;
        if (terms.Eligibility?.BaseFamily is { } baseFamily)
        {
            baseCoverage = await db.Entitlements.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.UnitId == command.UnitId && e.CoverageFamily == baseFamily && e.Status != EntitlementStatus.Closed)
                .Select(e => new BaseCoverage(e.EntitlementId, e.Status, e.StartDate, e.EndDate)).FirstOrDefaultAsync(cancellationToken);
        }

        if (ExtendedPlanArithmetic.Ineligible(terms.Eligibility, baseCoverage, purchaseDay) is { } refusal) throw new CommandRefusedException(refusal.Rule, refusal.Message);
        var start = ExtendedPlanArithmetic.Start(terms.Start.Trigger, purchaseDay, baseCoverage)
            ?? throw new CommandRefusedException("base_not_resolved", $"The plan starts when the {terms.Eligibility?.BaseFamily} entitlement expires, and its end is not resolved yet.", "attach the base's start evidence first");
        var end = TermArithmetic.EndDate(start, terms.Duration);
        var schedule = ExtendedPlanArithmetic.RevenueSchedule(start, end, command.Price);
        var metadata = command.Correction is null ? null : await corrections.PrepareAsync(
            CorrectionCommand.AttachExtendedWarranty, command.Correction, command.CommandId, command.Correction.ReasonCode, null,
            null, command.EventTime, CorrectionInitiatingRole.CustomerService, cancellationToken);

        var eventId = TimeOrderedId.New(command.EventTime);
        var entitlement = new Entitlement
        {
            TenantId = tenantId, EntitlementId = TimeOrderedId.New(command.EventTime), UnitId = command.UnitId, Scope = EntitlementScope.Unit, Type = version.EntitlementType, CoverageFamily = command.CoverageFamily,
            Direction = EntitlementDirection.Outbound, OwnerPartyRef = command.OwnerPartyRef, OwnerPartyType = command.OwnerPartyType, ObligorEntityId = command.ObligorEntityId, PayerPartyRef = command.PayerPartyRef,
            PayerPartyType = command.PayerPartyType, PolicyVersionId = version.PolicyVersionId, StartTrigger = terms.Start.Trigger, StartBasis = terms.Start.Trigger, StartEvidenceRef = $"{command.SourceDocument.Type}:{command.SourceDocument.Id}",
            StartDate = start, StartTimeZone = zone, PurchaseDate = purchaseDay, EndDate = end, ComputedByRuleVersion = version.RuleVersion, DurationValue = terms.Duration.Value, DurationUnit = terms.Duration.Unit,
            EndOfMonthRule = terms.Duration.EndOfMonth, EndInclusive = terms.Duration.EndInclusive, DurationBasis = terms.DurationBasis, Coverage = JsonSerializer.Serialize(terms.Coverage, LedgerJson.Options),
            RegionScope = JsonSerializer.Serialize(terms.RegionScope, LedgerJson.Options), Transferable = terms.Transferable, ConditionAtCreation = null, AccrualCohortId = null,
            AssuranceOrService = AssuranceOrService.Service, Status = EntitlementStatus.Active, Confidence = "high", Provenance = EntitlementProvenance.Created,
            SourceDocumentType = command.SourceDocument.Type, SourceDocumentId = command.SourceDocument.Id, SourceDocumentLine = command.SourceDocument.Line, CustomerPoRef = command.CustomerPoRef,
            SalePrice = command.Price, SaleCurrency = command.Currency, CreatedEventId = eventId, CreatedAt = clock.UtcNow,
        };
        db.Entitlements.Add(entitlement);
        foreach (var limit in terms.UsageLimits)
        {
            db.EntitlementUsageLimits.Add(new EntitlementUsageLimit { TenantId = tenantId, EntitlementId = entitlement.EntitlementId, MeterType = limit.MeterType, LimitValue = limit.Limit, Period = limit.Period });
        }

        await ledger.AppendAsync(entitlement, EntitlementEventType.ExtendedSold, key, command.EventTime, command.ActorRef, new
        {
            entitlement_id = entitlement.EntitlementId, item_id = unit.ItemId, coverage_family = entitlement.CoverageFamily, entitlement_type = entitlement.Type, policy_code = version.PolicyCode,
            policy_version = version.Version, policy_version_id = version.PolicyVersionId, base_entitlement_id = baseCoverage?.EntitlementId, eligibility = terms.Eligibility, start_basis = terms.Start.Trigger,
            purchase_day = purchaseDay, start_date = start, end_date = end, term_months = ExtendedPlanArithmetic.TermMonths(terms.Duration), price = command.Price, currency = command.Currency,
            revenue_schedule = schedule, payer_party_ref = command.PayerPartyRef, owner_party_ref = command.OwnerPartyRef, time_zone = zone, assurance_or_service = AssuranceOrService.Service,
            service_contract_regulated = terms.ServiceContractRegulated, rule_version = version.RuleVersion,
        }, command.SourceDocument, cancellationToken, eventId, metadata);
        if (metadata is not null)
            await corrections.RecordAsync(CorrectionCommand.AttachExtendedWarranty, metadata, command.ActorRef, command.EventTime, [command.UnitId], cancellationToken);
        return new(entitlement.EntitlementId, version.PolicyCode, version.Version, terms.Start.Trigger, start, end, baseCoverage?.EntitlementId, schedule, eventId, false);
    }

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyCode();
}
