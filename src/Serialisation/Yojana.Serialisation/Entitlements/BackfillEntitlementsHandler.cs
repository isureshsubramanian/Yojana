using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Entitlements;

/// <summary>Pure validation shared by the direct list command and P2-T11's selection-set adapter.</summary>
public static class BackfillRules
{
    public static string? CommandProblem(BackfillEntitlements command)
    {
        if (command.UnitIds.Count == 0) return "units_required";
        if (command.UnitIds.Count != command.UnitIds.Distinct().Count()) return "units_repeated";
        if (!BackfillReason.All.Contains(command.ReasonCode)) return "reason_unknown";
        if (command.EvidenceRefs.Count == 0 || command.EvidenceRefs.Any(string.IsNullOrWhiteSpace)) return "evidence_required";
        if (command.CommittedBy is null || !AmendmentRules.Satisfies(command.CommittedBy.Roles, YojanaRole.ServiceManager)) return "approval_required";
        return null;
    }

    public static bool FinanceReviewRequired(string assuranceOrService) => assuranceOrService == AssuranceOrService.Assurance;
}

/// <summary>
/// P2-T10: validates every unit before writing any of them, then creates all accepted base entitlements in Wolverine's
/// single handler transaction. Backfilled ledger facts publish only Entitlement.Backfilled: publishing Created as well
/// would tell Finance to accrue the same cohort twice.
/// </summary>
public static class BackfillEntitlementsHandler
{
    private sealed record Plan(Guid UnitId, CustodyEpisode Ownership, string StartBasis, DateOnly ContractDate, Guid PolicyVersionId, bool FinanceReviewRequired);

    public static async Task<BackfillEntitlementsResult> Handle(BackfillEntitlements command, SerialisationDbContext db, EntitlementLedger ledger, PolicyResolver resolver, PolicyStore store,
        ITimeZonePort zones, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        if (BackfillRules.CommandProblem(command) is { } problem)
        {
            throw problem switch
            {
                "reason_unknown" => new CommandRefusedException(problem, $"'{command.ReasonCode}' is not one of {string.Join(", ", BackfillReason.All)}."),
                "approval_required" => new CommandRefusedException(problem, "BackfillEntitlements is committed by a service manager.", "route it to a service manager to commit"),
                "evidence_required" => new CommandRefusedException(problem, "A backfill names the evidence used for the whole list."),
                _ => new CommandRefusedException(problem, "A backfill needs a non-empty list of distinct units."),
            };
        }

        var tenantId = tenant.TenantId;
        var outcomes = new List<BackfillUnitResult>();
        var plans = new List<Plan>();
        foreach (var unitId in command.UnitIds)
        {
            var key = EntitlementLedger.Key(command.CommandId, unitId, EntitlementEventType.Created, command.EventTime);
            if (await ledger.ReplayedAsync(key, command.EventTime, cancellationToken) is { } replayed)
            {
                var existing = await db.Entitlements.AsNoTracking().SingleAsync(e => e.TenantId == tenantId && e.CreatedEventId == replayed.EventId, cancellationToken);
                outcomes.Add(new(unitId, "created", null, existing.EntitlementId, existing.StartDate, existing.EndDate));
                continue;
            }

            var unit = await db.Units.AsNoTracking().Where(u => u.TenantId == tenantId && u.UnitId == unitId).Select(u => new { u.UnitId, u.Origin }).FirstOrDefaultAsync(cancellationToken);
            if (unit is null) { outcomes.Add(Skipped(unitId, "unit_unknown")); continue; }
            if (await db.Entitlements.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.UnitId == unitId && e.CoverageFamily == CoverageFamilyCode.Base, cancellationToken))
            {
                outcomes.Add(Skipped(unitId, "already_has_base"));
                continue;
            }

            var ownership = await db.Episodes.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.UnitId == unitId && e.Kind == EpisodeKind.Ownership && e.PartyType == PartyType.Customer)
                .OrderByDescending(e => e.ValidFrom).FirstOrDefaultAsync(cancellationToken);
            if (ownership is null) { outcomes.Add(Skipped(unitId, "not_sold")); continue; }
            if (ownership.OpeningDocumentId is null || ownership.OpeningDocumentType is null)
            {
                outcomes.Add(Skipped(unitId, "no_start_evidence"));
                continue;
            }

            var itemId = await db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && c.UnitId == unitId).Select(c => c.ItemId).SingleAsync(cancellationToken);
            var contractDate = DateOnly.FromDateTime(ownership.ValidFrom);
            var resolution = await resolver.ResolveAsync(new PolicyQuestion(unitId, itemId, CoverageFamilyCode.Base, contractDate, null, null, null, command.Country), cancellationToken);
            if (resolution.Level != PolicyLevel.ProductPolicy || resolution.PolicyVersionId is not { } policyVersionId)
            {
                outcomes.Add(Skipped(unitId, "no_policy"));
                continue;
            }

            var version = await db.PolicyVersions.AsNoTracking().SingleAsync(v => v.TenantId == tenantId && v.PolicyVersionId == policyVersionId, cancellationToken);
            var terms = await store.ReadTermsAsync(version, cancellationToken);
            var supportsDelivery = new[] { terms.Start.Trigger }.Concat(terms.Start.Fallbacks.Select(f => f.Trigger)).Contains(StartTrigger.Deliver);
            if (!supportsDelivery) { outcomes.Add(Skipped(unitId, "no_start_evidence")); continue; }
            var basis = unit.Origin == UnitOrigin.VendorAsn ? BackfillStartBasis.VendorShipConfirmation : BackfillStartBasis.DeliveryDocument;
            plans.Add(new(unitId, ownership, basis, contractDate, policyVersionId, BackfillRules.FinanceReviewRequired(terms.AssuranceOrService)));
        }

        var invalid = outcomes.Where(o => o.Reason is not null).ToList();
        if (invalid.Count > 0 && !command.SkipInvalid)
        {
            throw new CommandRefusedException("backfill_validation_failed", $"Backfill refused before writing: {string.Join("; ", invalid.Select(i => $"{i.UnitId}: {i.Reason}"))}.", "correct the invalid units, or explicitly set skip_invalid to create only the valid ones");
        }

        if (plans.Any(p => p.FinanceReviewRequired) && string.IsNullOrWhiteSpace(command.FinanceReviewRef))
        {
            throw new CommandRefusedException("finance_review_required", "At least one backfilled assurance warranty joins or creates an accrual cohort.", "record the finance review reference before committing the list");
        }

        foreach (var plan in plans)
        {
            var evidenceAt = DateTime.SpecifyKind(plan.Ownership.ValidFrom, DateTimeKind.Utc);
            var creation = new CreateEntitlement(command.CommandId, plan.UnitId, CoverageFamilyCode.Base, plan.ContractDate, command.ObligorEntityId, command.EventTime, command.ActorRef,
                new Dictionary<string, DateTime> { [StartTrigger.Deliver] = evidenceAt }, plan.PolicyVersionId, plan.Ownership.PartyRef, plan.Ownership.PartyType,
                Country: command.Country, SiteLocationId: command.SiteLocationId, StartEvidenceRef: command.EvidenceRefs[0], SourceDocument: command.SourceDocument,
                CustomerPoRef: plan.Ownership.CustomerPoRef, Provenance: EntitlementProvenance.Backfilled);
            var created = await CreateEntitlementHandler.CreateAsync(creation, db, ledger, resolver, store, zones, tenant, clock, plan.StartBasis, command.EvidenceRefs, plan.FinanceReviewRequired, command.ReasonCode, command.FinanceReviewRef, cancellationToken);
            outcomes.Add(new(plan.UnitId, "created", null, created.EntitlementId, created.StartDate, created.EndDate));
        }

        var ordered = command.UnitIds.Select(id => outcomes.Single(o => o.UnitId == id)).ToList();
        return new(ordered, plans.Count == 0 && ordered.Count > 0 && ordered.All(o => o.EntitlementId is not null));
    }

    private static BackfillUnitResult Skipped(Guid unitId, string reason) => new(unitId, "skipped", reason, null, null, null);
}
