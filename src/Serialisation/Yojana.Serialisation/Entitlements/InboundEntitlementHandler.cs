using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Documents;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

/// <summary>
/// Receipt follow-on for supplier cover. The receipt resolves and writes its units first; this separate durable message
/// then creates all applicable entitlements in one batch, so vendor-term latency and volume stay outside posting.
/// </summary>
public static class CreateInboundEntitlementsHandler
{
    public static Task<CreateInboundEntitlementsResult> Handle(CreateInboundEntitlements message, DocumentContext documents, IVendorWarrantyTermsPort vendorTerms,
        PolicyStore store, ITimeZonePort zones, IUnitEventWriter writer, ITenantContext tenant, IClock clock, CancellationToken cancellationToken) =>
        documents.InTransactionAsync(async () =>
        {
            var receipt = await documents.Purchasing.GetReceiptAsync(message.Receipt, cancellationToken)
                ?? throw new CommandRefusedException("document_unavailable", $"Receipt {message.Receipt.Id:N} could not be read through the purchasing port.");
            var ignored = new List<string>();
            var resolved = await Yojana.Serialisation.Documents.Documents.ResolveAllAsync(documents, receipt.Lines, receipt.Document, ignored, cancellationToken);
            var receiptDate = DateOnly.FromDateTime(receipt.PostedAt);
            var termsByItem = new Dictionary<Guid, VendorWarrantyTerm?>();
            foreach (var itemId in resolved.Select(x => x.Line.ItemId).Distinct())
            {
                termsByItem[itemId] = await vendorTerms.TermsForAsync(receipt.VendorPartyRef, itemId, receiptDate, cancellationToken);
            }

            var candidates = resolved.Where(x => termsByItem[x.Line.ItemId] is not null).ToList();
            if (candidates.Count == 0) return new CreateInboundEntitlementsResult(receipt.Document, 0, true);

            var tenantId = tenant.TenantId;
            var unitIds = candidates.Select(x => x.Unit.UnitId).Distinct().ToList();
            var existing = (await documents.Db.Entitlements.AsNoTracking()
                .Where(e => e.TenantId == tenantId && unitIds.Contains(e.UnitId) && e.Direction == EntitlementDirection.Inbound &&
                    e.SourceDocumentType == receipt.Document.Type && e.SourceDocumentId == receipt.Document.Id)
                .Select(e => e.UnitId).ToListAsync(cancellationToken)).ToHashSet();
            candidates = candidates.Where(x => !existing.Contains(x.Unit.UnitId)).DistinctBy(x => x.Unit.UnitId).ToList();
            if (candidates.Count == 0) return new CreateInboundEntitlementsResult(receipt.Document, 0, true);

            var policyIds = candidates.Select(x => termsByItem[x.Line.ItemId]!.PolicyVersionId).Distinct().ToList();
            var policies = await documents.Db.PolicyVersions.AsNoTracking().Where(p => p.TenantId == tenantId && policyIds.Contains(p.PolicyVersionId))
                .ToDictionaryAsync(p => p.PolicyVersionId, cancellationToken);
            var policyTerms = new Dictionary<Guid, PolicyTerms>();
            foreach (var policy in policies.Values) policyTerms[policy.PolicyVersionId] = await store.ReadTermsAsync(policy, cancellationToken);
            var currents = await documents.Db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && unitIds.Contains(c.UnitId)).ToDictionaryAsync(c => c.UnitId, cancellationToken);
            var drafts = new List<UnitEventDraft>(candidates.Count);

            foreach (var candidate in candidates)
            {
                var versionId = termsByItem[candidate.Line.ItemId]!.PolicyVersionId;
                if (!policies.TryGetValue(versionId, out var policy) || policy.Status != PolicyVersionStatus.Published)
                    throw new CommandRefusedException("vendor_policy_not_published", $"Vendor terms name policy version {versionId}, which is not published.");
                if (policy.CoverageFamily != CoverageFamilyCode.Supplier)
                    throw new CommandRefusedException("vendor_policy_family", $"Vendor policy {policy.PolicyCode} must use the supplier_warranty coverage family.");

                var terms = policyTerms[versionId];
                var current = currents[candidate.Unit.UnitId];
                var zone = await CreateEntitlementHandler.ZoneAsync(terms.ReferenceTimeZoneRule, current.LocationId, receipt.VendorPartyRef, zones, cancellationToken);
                var start = TermArithmetic.ResolveStart(terms.Start, new Dictionary<string, DateTime> { [StartTrigger.Receipt] = receipt.PostedAt }, zone);
                var duration = TermArithmetic.ForCondition(terms, candidate.Line.ItemId, current.ConditionGrade, null, null);
                DateOnly? end = start.Start is { } day ? TermArithmetic.EndDate(day, duration.Duration) : null;
                var eventId = TimeOrderedId.New(receipt.PostedAt);
                var entitlement = new Entitlement
                {
                    TenantId = tenantId, EntitlementId = TimeOrderedId.New(receipt.PostedAt), UnitId = candidate.Unit.UnitId, Scope = EntitlementScope.Unit,
                    Type = policy.EntitlementType, CoverageFamily = policy.CoverageFamily, Direction = EntitlementDirection.Inbound, OwnerPartyRef = null,
                    ObligorEntityId = receipt.VendorPartyRef, PayerPartyRef = receipt.VendorPartyRef, PayerPartyType = "vendor", PolicyVersionId = versionId,
                    StartTrigger = terms.Start.Trigger, StartBasis = start.Basis, StartEvidenceRef = $"{receipt.Document.Type}:{receipt.Document.Id:N}", StartDate = start.Start,
                    StartTimeZone = zone, EndDate = end, ComputedByRuleVersion = policy.RuleVersion, DurationValue = duration.Duration.Value, DurationUnit = duration.Duration.Unit,
                    EndOfMonthRule = duration.Duration.EndOfMonth, EndInclusive = duration.Duration.EndInclusive, DurationBasis = terms.DurationBasis,
                    Coverage = JsonSerializer.Serialize(terms.Coverage, LedgerJson.Options), RegionScope = JsonSerializer.Serialize(terms.RegionScope, LedgerJson.Options),
                    Transferable = terms.Transferable, ConditionAtCreation = current.ConditionGrade ?? "new", AccrualCohortId = null, AssuranceOrService = terms.AssuranceOrService,
                    Status = start.Start is null ? EntitlementStatus.PendingEvidence : EntitlementStatus.Active, Confidence = "high", Provenance = EntitlementProvenance.Created,
                    SourceDocumentType = receipt.Document.Type, SourceDocumentId = receipt.Document.Id, SourceDocumentLine = candidate.Line.LineNo,
                    CreatedEventId = eventId, CreatedAt = clock.UtcNow,
                };
                documents.Db.Entitlements.Add(entitlement);
                foreach (var limit in terms.UsageLimits)
                    documents.Db.EntitlementUsageLimits.Add(new EntitlementUsageLimit { TenantId = tenantId, EntitlementId = entitlement.EntitlementId, MeterType = limit.MeterType, LimitValue = limit.Limit, Period = limit.Period });

                drafts.Add(new UnitEventDraft
                {
                    UnitId = entitlement.UnitId, EventTime = receipt.PostedAt, EventType = EntitlementEventType.Created, PresetEventId = eventId,
                    LegalEntityId = receipt.VendorPartyRef, ActorRef = message.Envelope.Source, DocumentType = receipt.Document.Type, DocumentId = receipt.Document.Id,
                    DocumentLine = candidate.Line.LineNo, IdempotencyKey = IdempotencyKey.For(message.Envelope.Source, receipt.Document.Type, receipt.Document.Id,
                        candidate.Line.LineNo, entitlement.UnitId, EntitlementEventType.Created, receipt.PostedAt),
                    Attributes = JsonObjectText.Of(new { entitlement_id = entitlement.EntitlementId, item_id = candidate.Line.ItemId, coverage_family = entitlement.CoverageFamily,
                        direction = entitlement.Direction, entitlement_type = entitlement.Type, policy_code = policy.PolicyCode, policy_version = policy.Version,
                        policy_version_id = versionId, status = entitlement.Status, start_date = entitlement.StartDate, end_date = entitlement.EndDate,
                        accrual_cohort_id = (Guid?)null, assurance_or_service = entitlement.AssuranceOrService, accrual_suppressed = true, provenance = entitlement.Provenance }),
                });
            }

            await documents.Db.SaveChangesAsync(cancellationToken);
            var appended = await writer.AppendAsync(drafts, cancellationToken);
            return new CreateInboundEntitlementsResult(receipt.Document, appended.Written.Count, appended.Written.Count == 0);
        }, cancellationToken);
}
