using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Rmas;

/// <summary>
/// Consumes the assembly ledger boundary rather than making RecordAssembly understand service policy. Only installs
/// whose document resolves to an RMA repair-and-return or an approved repair claim receive cover.
/// </summary>
public static class ServicePartInstalledHandler
{
    public static async Task Handle(UnitPartInstalled message, SerialisationDbContext db, EntitlementLedger entitlementLedger,
        EpisodeService episodes, IUnitEventWriter writer, ServicePartEntitlementSettings settings,
        AmendmentApprovalSettings amendmentApprovals, IWarrantyClaimsPort claims, IWarrantyCertificatePort certificates,
        ITenantCurrency currency, IJurisdictionRuleResolver jurisdictionRules, IJurisdictionRegionPort regions,
        ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        if (message.Header.Document is not { } document) return;
        var context = await RepairContextAsync(db, tenant.TenantId, message.ParentUnitId, document, cancellationToken);
        if (context is null) return;

        var tenantId = tenant.TenantId;
        var owner = await db.Episodes.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.UnitId == message.ParentUnitId
            && e.Kind == EpisodeKind.Ownership && e.ValidTo == null, cancellationToken);
        if (owner?.PartyType != PartyType.Customer || owner.PartyRef is null) return;

        var part = await db.Current.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.UnitId == message.Header.UnitId, cancellationToken)
            ?? throw new CommandRefusedException("service_part_unknown", $"Installed part unit {message.Header.UnitId} does not exist.");
        var partItemId = message.ItemId == Guid.Empty ? part.ItemId : message.ItemId;
        var installedDate = DateOnly.FromDateTime(message.Header.EventTime);
        var parentEntitlements = await db.Entitlements.Where(e => e.TenantId == tenantId && e.UnitId == message.ParentUnitId
            && e.Direction == EntitlementDirection.Outbound && e.Status != EntitlementStatus.Closed && e.EndDate != null).ToListAsync(cancellationToken);
        if (parentEntitlements.Count == 0) return;

        foreach (var statutory in parentEntitlements.Where(e => e.Type == EntitlementType.Statutory).ToList())
        {
            await ApplyStatutoryExtensionAsync(message, context, owner, statutory, db, entitlementLedger,
                amendmentApprovals, claims, certificates, currency, jurisdictionRules, regions, tenant, cancellationToken);
        }

        await AlignEpisodesAsync(message, document, owner, db, episodes, writer, tenantId, cancellationToken);

        var configured = await settings.GetAsync(cancellationToken);
        var minimumDays = ServicePartEntitlementSettings.MinimumDays(configured, partItemId);
        foreach (var parent in parentEntitlements)
        {
            if (await db.Entitlements.AnyAsync(e => e.TenantId == tenantId && e.SourceEventId == message.Header.EventId
                && e.ServiceParentEntitlementId == parent.EntitlementId, cancellationToken)) continue;
            if (await db.Entitlements.AnyAsync(e => e.TenantId == tenantId && e.UnitId == message.Header.UnitId
                && e.CoverageFamily == parent.CoverageFamily && e.Status != EntitlementStatus.Closed, cancellationToken))
                throw new CommandRefusedException("service_part_family_occupied", $"Installed part {message.Header.UnitId} already has live {parent.CoverageFamily} cover.");

            var version = await db.PolicyVersions.AsNoTracking().SingleAsync(v => v.TenantId == tenantId && v.PolicyVersionId == parent.PolicyVersionId, cancellationToken);
            var minimumEnd = installedDate.AddDays(minimumDays);
            var computedEnd = parent.EndDate!.Value >= minimumEnd ? parent.EndDate.Value : minimumEnd;
            var eventId = TimeOrderedId.New(message.Header.EventTime);
            var entitlement = new Entitlement
            {
                TenantId = tenantId, EntitlementId = TimeOrderedId.New(message.Header.EventTime), UnitId = message.Header.UnitId,
                Scope = EntitlementScope.Unit, Type = parent.Type, CoverageFamily = parent.CoverageFamily, Direction = EntitlementDirection.Outbound,
                OwnerPartyRef = owner.PartyRef, OwnerPartyType = owner.PartyType, ObligorEntityId = parent.ObligorEntityId,
                PayerPartyRef = parent.PayerPartyRef, PayerPartyType = parent.PayerPartyType, PolicyVersionId = parent.PolicyVersionId,
                StartTrigger = StartTrigger.Install, StartBasis = StartTrigger.Install, StartEvidenceRef = message.AssemblyLinkId.ToString("N"),
                StartDate = installedDate, StartTimeZone = parent.StartTimeZone, PurchaseDate = parent.PurchaseDate, EndDate = computedEnd,
                ComputedByRuleVersion = parent.ComputedByRuleVersion, DurationValue = minimumDays, DurationUnit = DurationRule.Days,
                EndOfMonthRule = DurationRule.Clamp, EndInclusive = false, EndAdjustmentDays = computedEnd.DayNumber - minimumEnd.DayNumber,
                DurationBasis = DurationBasis.Time, Coverage = parent.Coverage, RegionScope = parent.RegionScope, Transferable = parent.Transferable,
                ConditionAtCreation = part.ConditionGrade, ServiceParentEntitlementId = parent.EntitlementId,
                InstalledParentUnitId = message.ParentUnitId, InstallationLinkId = message.AssemblyLinkId, SourceEventId = message.Header.EventId,
                AssuranceOrService = parent.AssuranceOrService, Status = EntitlementStatus.Active, Confidence = Confidence.High,
                Provenance = EntitlementProvenance.ServicePart, SourceDocumentType = document.Type, SourceDocumentId = document.Id,
                SourceDocumentLine = document.Line, CreatedEventId = eventId, CreatedAt = clock.UtcNow,
            };
            db.Entitlements.Add(entitlement);
            var usageLimits = await db.EntitlementUsageLimits.AsNoTracking().Where(l => l.TenantId == tenantId && l.EntitlementId == parent.EntitlementId).ToListAsync(cancellationToken);
            foreach (var limit in usageLimits)
                db.EntitlementUsageLimits.Add(new EntitlementUsageLimit { TenantId = tenantId, EntitlementId = entitlement.EntitlementId, MeterType = limit.MeterType, LimitValue = limit.LimitValue, Period = limit.Period });

            var key = IdempotencyKey.Derive(message.Header.EventId.ToString("N"), $"service-part:{parent.EntitlementId:N}");
            await entitlementLedger.AppendAsync(entitlement, EntitlementEventType.Created, key, message.Header.EventTime,
                message.Header.ActorRef, new
                {
                    entitlement_id = entitlement.EntitlementId, item_id = partItemId, coverage_family = entitlement.CoverageFamily,
                    entitlement_type = entitlement.Type, policy_level = "service_parent", policy_code = version.PolicyCode,
                    policy_version = version.Version, policy_version_id = version.PolicyVersionId, status = entitlement.Status,
                    start_basis = entitlement.StartBasis, start_date = entitlement.StartDate, end_date = entitlement.EndDate,
                    time_zone = entitlement.StartTimeZone, accrual_cohort_id = (Guid?)null,
                    assurance_or_service = entitlement.AssuranceOrService, accrual_suppressed = true,
                    provenance = entitlement.Provenance, confidence = entitlement.Confidence, owner_party_ref = entitlement.OwnerPartyRef,
                    rule_version = version.RuleVersion, service_parent_entitlement_id = parent.EntitlementId,
                    installed_parent_unit_id = message.ParentUnitId, installation_link_id = message.AssemblyLinkId,
                    minimum_days = minimumDays,
                }, document, cancellationToken, eventId);
        }
    }

    private static async Task ApplyStatutoryExtensionAsync(UnitPartInstalled message, RepairContext context, CustodyEpisode owner,
        Entitlement entitlement, SerialisationDbContext db, EntitlementLedger ledger, AmendmentApprovalSettings approvals,
        IWarrantyClaimsPort claims, IWarrantyCertificatePort certificates, ITenantCurrency currency,
        IJurisdictionRuleResolver jurisdictionRules, IJurisdictionRegionPort regions, ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var version = await db.PolicyVersions.AsNoTracking().SingleAsync(v => v.TenantId == tenantId && v.PolicyVersionId == entitlement.PolicyVersionId, cancellationToken);
        if (version.ReplacementPolicyId is not { } policyId) return;
        var policy = await db.ReplacementPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.TenantId == tenantId && p.ReplacementPolicyId == policyId, cancellationToken);
        if (policy?.Rule != ReplacementRule.StatutoryExtensionOnce) return;
        var eligibility = await StatutoryExtensionGuard.EvaluateAsync(db, tenantId, entitlement, context.ContractDate,
            context.Country, consumerContract: owner.PartyType == PartyType.Customer, ClaimRemedy.Repair,
            jurisdictionRules, regions, cancellationToken);
        if (!eligibility.Eligible || eligibility.AlreadyApplied) return;

        var newEnd = ReplacementArithmetic.EndDate(entitlement.EndDate!.Value, DateOnly.FromDateTime(message.Header.EventTime),
            new DurationRule(entitlement.DurationValue, entitlement.DurationUnit, entitlement.EndOfMonthRule, entitlement.EndInclusive),
            policy.Rule, policy.MinDays, eligibility.ExtensionMonths);
        entitlement.StatutoryExtensionApplied = true;
        await AmendEntitlementHandler.AmendAsync(new AmendEntitlement(message.Header.EventId, entitlement.EntitlementId,
            AmendmentReason.StatutoryExtension, message.Header.EventTime, message.Header.ActorRef, NewEnd: newEnd,
            SourceDocument: message.Header.Document, CommittedBy: AmendmentCommitter.System,
            StatutoryExtensionMaximum: eligibility.MaximumApplications), db, ledger, approvals, claims,
            certificates, currency, tenant, corrections: null, recordCorrection: false, cancellationToken);
    }

    private static async Task AlignEpisodesAsync(UnitPartInstalled message, DocumentRef document, CustodyEpisode parentOwner,
        SerialisationDbContext db, EpisodeService episodes, IUnitEventWriter writer, TenantId tenantId, CancellationToken cancellationToken)
    {
        await episodes.PreloadOpenAsync([message.Header.UnitId], cancellationToken);
        var drafts = new List<UnitEventDraft>();
        var partOwner = await episodes.OpenAsync(message.Header.UnitId, EpisodeKind.Ownership, cancellationToken);
        if (partOwner?.PartyRef != parentOwner.PartyRef || partOwner?.PartyType != parentOwner.PartyType)
        {
            var ownership = await episodes.PrepareOwnershipOpenAsync(message.Header.UnitId,
                new OwnershipTarget(parentOwner.PartyRef!.Value, parentOwner.PartyType!, EpisodeReason.ServiceInstallation, EpisodeSource.Service),
                IdempotencyKey.Derive(message.Header.EventId.ToString("N"), "service-part-ownership"), message.Header.EventTime,
                message.Header.ActorRef, message.Header.LegalEntityId, document, EpisodeReason.ServiceInstallation, cancellationToken);
            if (ownership.CloseDraft is not null) drafts.Add(ownership.CloseDraft);
            drafts.Add(ownership.Draft);
        }

        var parentCustody = await db.Episodes.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId
            && e.UnitId == message.ParentUnitId && e.Kind == EpisodeKind.Custody && e.ValidTo == null, cancellationToken);
        var partCustody = await episodes.OpenAsync(message.Header.UnitId, EpisodeKind.Custody, cancellationToken);
        if (parentCustody?.LocationKind is { } locationKind
            && (partCustody?.LocationKind != locationKind || partCustody.LocationId != parentCustody.LocationId || partCustody.PartyRef != parentCustody.PartyRef))
        {
            var custody = await episodes.PrepareCustodyMoveAsync(message.Header.UnitId,
                new CustodyTarget(locationKind, parentCustody.LocationId, parentCustody.PartyRef, parentCustody.PartyType,
                    EpisodeReason.ServiceInstallation, EpisodeSource.Service), UnitEventType.CustodyChanged,
                IdempotencyKey.Derive(message.Header.EventId.ToString("N"), "service-part-custody"), message.Header.EventTime,
                message.Header.ActorRef, message.Header.LegalEntityId, document,
                new Dictionary<string, object?> { ["parent_unit_id"] = message.ParentUnitId, ["assembly_link_id"] = message.AssemblyLinkId },
                null, enforceTransitions: partCustody is not null, cancellationToken);
            drafts.Add(custody.Draft);
        }
        await writer.AppendAsync(drafts, cancellationToken);
    }

    private static async Task<RepairContext?> RepairContextAsync(SerialisationDbContext db, TenantId tenantId, Guid parentUnitId,
        DocumentRef document, CancellationToken cancellationToken)
    {
        if (document.Type == CoverageWorkType.Rma)
        {
            var row = await (from rma in db.Rmas.AsNoTracking()
                             join line in db.RmaLines.AsNoTracking() on new { rma.TenantId, rma.RmaId } equals new { line.TenantId, line.RmaId }
                             where rma.TenantId == tenantId && rma.RmaId == document.Id && line.RequestedUnitId == parentUnitId
                                   && line.Disposition == RmaDisposition.RepairAndReturn
                                   && (document.Line == null || line.Sequence == document.Line)
                             select new { rma.ServiceCountry, line.OwnershipEpisodeId }).FirstOrDefaultAsync(cancellationToken);
            if (row is null) return null;
            var contractAt = await db.Episodes.AsNoTracking().Where(e => e.TenantId == tenantId && e.EpisodeId == row.OwnershipEpisodeId)
                .Select(e => (DateTime?)e.ValidFrom).SingleOrDefaultAsync(cancellationToken);
            return contractAt is null ? null : new(DateOnly.FromDateTime(contractAt.Value), row.ServiceCountry);
        }

        if (document.Type == CoverageWorkType.Claim)
        {
            var claim = await db.Claims.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ClaimId == document.Id
                && c.UnitId == parentUnitId && c.Remedy == ClaimRemedy.Repair
                && c.Status != ClaimStatus.Closed && c.Status != ClaimStatus.Denied, cancellationToken);
            if (claim is null) return null;
            var contractAt = await db.Episodes.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == parentUnitId
                    && e.Kind == EpisodeKind.Ownership && e.PartyRef == claim.ClaimantPartyRef)
                .OrderByDescending(e => e.ValidFrom).Select(e => (DateTime?)e.ValidFrom).FirstOrDefaultAsync(cancellationToken);
            return new(contractAt is null ? claim.ClaimDate : DateOnly.FromDateTime(contractAt.Value), claim.ServiceCountry);
        }

        return null;
    }

    private sealed record RepairContext(DateOnly ContractDate, string Country);
}
