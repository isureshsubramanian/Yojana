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

/// <summary>
/// P2-T09 per-unit operation. Closing before adding the successor is deliberate: the filtered unique index enforces one
/// live entitlement per unit and family at every save point, including when P2-T11 calls this repeatedly in one unit of work.
/// The successor stands in the old row's place: same root (so a chain's statutory extension stays applied once) and the
/// old row's parent, linked to it by <c>supersedes_entitlement_id</c>. It is not a link of the replacement chain, which
/// TransferEntitlementHandler counts without superseding rows.
/// </summary>
public static class SupersedeEntitlementHandler
{
    public static async Task<SupersedeEntitlementResult> Handle(SupersedeEntitlement command, SerialisationDbContext db, EntitlementLedger ledger, PolicyStore store,
        IWarrantyClaimsPort claims, IWarrantyCertificatePort certificates, ITenantContext tenant, IClock clock, CorrectionLedger corrections, CancellationToken cancellationToken) =>
        await SupersedeAsync(command, db, ledger, store, claims, certificates, tenant, clock, corrections, true, cancellationToken);

    internal static async Task<SupersedeEntitlementResult> SupersedeAsync(SupersedeEntitlement command, SerialisationDbContext db, EntitlementLedger ledger, PolicyStore store,
        IWarrantyClaimsPort claims, IWarrantyCertificatePort certificates, ITenantContext tenant, IClock clock, CorrectionLedger? corrections, bool recordCorrection, CancellationToken cancellationToken, decimal? salePrice = null, string? saleCurrency = null)
    {
        var tenantId = tenant.TenantId;
        var old = await ledger.LoadAsync(command.EntitlementId, cancellationToken);
        var key = EntitlementLedger.Key(command.CommandId, old.UnitId, EntitlementEventType.Superseded, command.EventTime);
        if (await ledger.ReplayedAsync(key, command.EventTime, cancellationToken) is { } replayed)
        {
            var replaySuccessor = await db.Entitlements.AsNoTracking().SingleAsync(e => e.TenantId == tenantId && e.CreatedEventId == replayed.EventId, cancellationToken);
            using var json = JsonDocument.Parse(replayed.Attributes ?? "{}");
            var a = json.RootElement;
            return new(old.EntitlementId, replaySuccessor.EntitlementId, replaySuccessor.RootEntitlementId ?? old.EntitlementId, replaySuccessor.StartDate!.Value, replaySuccessor.EndDate!.Value,
                a.GetProperty("reduction").GetBoolean(), a.GetProperty("approval_level").GetString() ?? "", a.GetProperty("approved_by").GetString() ?? "", replayed.EventId, true);
        }

        // A suspended entitlement has an open suspension row that a close would leave dangling: resume it first.
        if (old.Status == EntitlementStatus.Suspended)
            throw new CommandRefusedException("entitlement_suspended", "A suspended entitlement is not superseded.", "resume it, then supersede");
        if (old.Status != EntitlementStatus.Active)
            throw new CommandRefusedException("entitlement_not_in_force", "Only an active entitlement with a resolved term may be superseded.");
        if (old.StartDate is not { } oldStart || old.EndDate is not { } oldEnd)
            throw new CommandRefusedException("term_unresolved", "Only an entitlement with a resolved start and end may be superseded.");
        if (command.EffectiveDate <= oldStart || command.EffectiveDate > oldEnd)
            throw new CommandRefusedException("effective_date_out_of_range", $"The effective date must be after {oldStart:yyyy-MM-dd} and no later than {oldEnd:yyyy-MM-dd}; the old row must retain at least its first covered day.");
        if (command.NewPolicyVersionId is null && string.IsNullOrWhiteSpace(command.NewPolicyCode))
            throw new CommandRefusedException("policy_required", "Name the published policy version or policy code that replaces the old cover.");

        var version = command.NewPolicyVersionId is { } id
            ? await db.PolicyVersions.AsNoTracking().FirstOrDefaultAsync(v => v.TenantId == tenantId && v.PolicyVersionId == id, cancellationToken)
            : await db.PolicyVersions.AsNoTracking().Where(v => v.TenantId == tenantId && v.PolicyCode == command.NewPolicyCode && v.Status == PolicyVersionStatus.Published && v.AppliesToContractsFrom <= command.EffectiveDate)
                .OrderByDescending(v => v.Version).FirstOrDefaultAsync(cancellationToken);
        if (version is null) throw new CommandRefusedException("policy_version_unknown", "The replacement policy version does not exist or no published version applies on the effective date.");
        if (version.Status != PolicyVersionStatus.Published) throw new CommandRefusedException("policy_not_published", $"{version.PolicyCode} v{version.Version} is not published.");
        if (version.CoverageFamily != old.CoverageFamily) throw new CommandRefusedException("policy_family_differs", $"{version.PolicyCode} is in {version.CoverageFamily}, not {old.CoverageFamily}.");
        if (version.PolicyVersionId == old.PolicyVersionId) throw new CommandRefusedException("no_change", "The replacement policy version is already on the entitlement.");

        var terms = await store.ReadTermsAsync(version, cancellationToken);
        var duration = TermArithmetic.ForCondition(terms, await db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && c.UnitId == old.UnitId).Select(c => c.ItemId).SingleAsync(cancellationToken), old.ConditionAtCreation, null, null).Duration;
        var newEnd = TermArithmetic.EndDate(command.EffectiveDate, duration);
        var oldCoverage = JsonSerializer.Deserialize<CoverageTerms>(old.Coverage, LedgerJson.Options) ?? new CoverageTerms(0, 0, 0, "unknown");
        var reduction = SupersessionRules.CoverageReduced(oldEnd, oldCoverage, newEnd, terms.Coverage);
        var certificateIssued = reduction && await certificates.CertificateIssuedAsync(old.EntitlementId, cancellationToken);
        var required = SupersessionRules.Required(command.ReasonCode, reduction, certificateIssued);
        var committer = command.CommittedBy ?? throw new CommandRefusedException("committer_required", "A supersession names who commits it and the roles they hold.");
        if (!AmendmentRules.Satisfies(committer.Roles, required.Role)) throw new CommandRefusedException("approval_required", $"A supersession is committed by {required.Role}.");
        if (required.SourceDocument && command.SourceDocument is null) throw new CommandRefusedException("source_document_required", "A purchased upgrade cites its source document.");
        if (required.CustomerNotification && string.IsNullOrWhiteSpace(command.CustomerNotificationRef)) throw new CommandRefusedException("customer_notification_required", "The customer holds a certificate for the cover this supersession reduces.");
        if (reduction && await claims.HasOpenClaimsAsync(old.UnitId, old.EntitlementId, cancellationToken)) throw new CommandRefusedException("open_claims", "Cover is not reduced while claims against it are open.");

        CorrectionMetadata? metadata = null;
        if (recordCorrection)
        {
            metadata = await corrections!.PrepareAsync(CorrectionCommand.SupersedeEntitlement, command.Correction, command.CommandId, command.ReasonCode, null,
                committer.UserRef, command.EventTime, CorrectionInitiatingRole.CustomerService, cancellationToken);
        }

        EntitlementLedger.Transition(old, EntitlementEventType.Closed);
        old.EndDate = command.EffectiveDate.AddDays(-1);
        old.EndAdjustmentDays = old.EndDate.Value.DayNumber - TermArithmetic.EndDate(oldStart, new DurationRule(old.DurationValue, old.DurationUnit, old.EndOfMonthRule, old.EndInclusive)).DayNumber;
        old.ClosedReason = EntitlementClosedReason.ReplacedByChange;
        old.ClosedEffective = command.EffectiveDate;
        old.ClosedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var eventId = TimeOrderedId.New(command.EventTime);
        var root = old.RootEntitlementId ?? old.EntitlementId;
        var successor = new Entitlement
        {
            TenantId = tenantId, EntitlementId = TimeOrderedId.New(command.EventTime), UnitId = old.UnitId, Scope = old.Scope, ScopeChildUnitId = old.ScopeChildUnitId, ScopeItemClass = old.ScopeItemClass,
            Type = version.EntitlementType, CoverageFamily = old.CoverageFamily, Direction = old.Direction, OwnerPartyRef = old.OwnerPartyRef, OwnerPartyType = old.OwnerPartyType, ObligorEntityId = old.ObligorEntityId,
            PayerPartyRef = old.PayerPartyRef, PayerPartyType = old.PayerPartyType, PolicyVersionId = version.PolicyVersionId, StartTrigger = old.StartTrigger, StartBasis = "supersession_effective_date",
            StartEvidenceRef = command.SourceDocument is null ? null : $"{command.SourceDocument.Type}:{command.SourceDocument.Id}", StartDate = command.EffectiveDate, StartTimeZone = old.StartTimeZone, EndDate = newEnd,
            ComputedByRuleVersion = version.RuleVersion, DurationValue = duration.Value, DurationUnit = duration.Unit, EndOfMonthRule = duration.EndOfMonth, EndInclusive = duration.EndInclusive,
            DurationBasis = terms.DurationBasis, Coverage = JsonSerializer.Serialize(terms.Coverage, LedgerJson.Options), RegionScope = JsonSerializer.Serialize(terms.RegionScope, LedgerJson.Options), Transferable = terms.Transferable,
            ConditionAtCreation = old.ConditionAtCreation, ParentEntitlementId = old.ParentEntitlementId, RootEntitlementId = root, SupersedesEntitlementId = old.EntitlementId, AccrualCohortId = old.AccrualCohortId,
            RevenueScheduleRef = old.RevenueScheduleRef, AssuranceOrService = terms.AssuranceOrService, Status = EntitlementStatus.Active, Confidence = old.Confidence, Provenance = EntitlementProvenance.Superseding,
            SourceDocumentType = command.SourceDocument?.Type ?? old.SourceDocumentType, SourceDocumentId = command.SourceDocument?.Id ?? old.SourceDocumentId, SourceDocumentLine = command.SourceDocument?.Line ?? old.SourceDocumentLine,
            SalePrice = salePrice, SaleCurrency = saleCurrency, CustomerPoRef = old.CustomerPoRef, CreatedEventId = eventId, CreatedAt = clock.UtcNow,
        };
        old.SuccessorEntitlementId = successor.EntitlementId;
        db.Entitlements.Add(successor);
        foreach (var limit in terms.UsageLimits) db.EntitlementUsageLimits.Add(new EntitlementUsageLimit { TenantId = tenantId, EntitlementId = successor.EntitlementId, MeterType = limit.MeterType, LimitValue = limit.Limit, Period = limit.Period });
        await ledger.AppendAsync(successor, EntitlementEventType.Superseded, key, command.EventTime, command.ActorRef, new
        {
            superseded_entitlement_id = old.EntitlementId, new_entitlement_id = successor.EntitlementId, coverage_family = old.CoverageFamily, effective_date = command.EffectiveDate,
            old_policy_version_id = old.PolicyVersionId, new_policy_version_id = version.PolicyVersionId, reason_code = command.ReasonCode, reduction,
            price = salePrice, currency = saleCurrency, approval_level = required.Role, approved_by = committer.UserRef, customer_notification_ref = command.CustomerNotificationRef, accrual_cohort_id = successor.AccrualCohortId,
        }, command.SourceDocument, cancellationToken, eventId, metadata);
        if (metadata is not null)
            await corrections!.RecordAsync(CorrectionCommand.SupersedeEntitlement, metadata, command.ActorRef, command.EventTime, [old.UnitId], cancellationToken);
        return new(old.EntitlementId, successor.EntitlementId, root, command.EffectiveDate, newEnd, reduction, required.Role, committer.UserRef, eventId, false);
    }
}
