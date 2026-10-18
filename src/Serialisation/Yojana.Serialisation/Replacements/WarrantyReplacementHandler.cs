using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Replacements;

public static class CreateWarrantyReplacementHandler
{
    public static async Task<CreateWarrantyReplacementResult> Handle(
        CreateWarrantyReplacement command,
        SerialisationDbContext db,
        IReplacementSourceQueryPort sources,
        IReturnReasonQueryPort returnReasons,
        PolicyResolver policies,
        DocumentNumbers numbers,
        ITenantContext tenant,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.WarrantyReplacements.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId && row.CommandId == command.CommandId, cancellationToken) is { } replay)
            return new(await WarrantyReplacementQueries.DetailAsync(db, tenantId, replay.WarrantyReplacementId, cancellationToken), true);

        Validate(command);
        var source = await sources.ResolveAsync(command.SourceKind, command.SourceId, command.SourceEntitlementId, cancellationToken)
            ?? throw new CommandRefusedException("replacement_source_unknown", $"{command.SourceKind} source {command.SourceId} does not resolve to entitlement {command.SourceEntitlementId}.",
                command.SourceKind == ReplacementSourceKind.Rma ? "wire the RMA replacement-source port, or select an existing RMA" : "select the adjudicated entitlement recorded on the claim");
        var entitlement = await db.Entitlements.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId && row.EntitlementId == source.SourceEntitlementId, cancellationToken)
            ?? throw new CommandRefusedException("entitlement_unknown", $"Entitlement {source.SourceEntitlementId} does not exist.");
        if (entitlement.UnitId != source.OriginalUnitId) throw new CommandRefusedException("replacement_source_mismatch", "The source entitlement belongs to another unit.");
        if (entitlement.Direction != EntitlementDirection.Outbound) throw new CommandRefusedException("outbound_entitlement_required", "A customer replacement requires outbound cover.");
        if (entitlement.Status != EntitlementStatus.Active) throw new CommandRefusedException("entitlement_not_active", $"Entitlement {entitlement.EntitlementId} is {entitlement.Status}.");
        if (command.ReturnDue is not null && entitlement.OwnerPartyRef is { } advanceParty
            && await db.PartyReplacementHolds.AnyAsync(row => row.TenantId == tenantId && row.PartyRef == advanceParty && row.LiftedAt == null, cancellationToken))
            throw new CommandRefusedException("advance_replacement_hold", "This party has an open replacement hold and cannot receive another advance replacement.", "resolve the overdue return or lift the hold with a recorded reason");

        var current = await db.Current.AsNoTracking().Where(row => row.TenantId == tenantId && (row.UnitId == source.OriginalUnitId || row.UnitId == command.ReplacementUnitId))
            .Select(row => new { row.UnitId, row.ItemId, row.OwnerPartyRef }).ToListAsync(cancellationToken);
        var original = current.SingleOrDefault(row => row.UnitId == source.OriginalUnitId)
            ?? throw new CommandRefusedException("original_unit_unknown", $"Original unit {source.OriginalUnitId} does not exist.");
        var replacement = current.SingleOrDefault(row => row.UnitId == command.ReplacementUnitId)
            ?? throw new CommandRefusedException("replacement_unit_unknown", $"Replacement unit {command.ReplacementUnitId} does not exist.");
        if (source.OriginalUnitId == command.ReplacementUnitId) throw new CommandRefusedException("replacement_is_original", "A replacement must be a different unit.");
        if (await db.ReplacementLinks.AnyAsync(row => row.TenantId == tenantId && row.ReplacementUnitId == command.ReplacementUnitId
            && (row.State == ReplacementLinkState.Proposed || row.State == ReplacementLinkState.AwaitingReturn), cancellationToken))
            throw new CommandRefusedException("replacement_unit_reserved", $"Unit {command.ReplacementUnitId} already belongs to an open replacement link.");

        var resolved = await policies.ResolveAsync(new PolicyQuestion(source.OriginalUnitId, original.ItemId, entitlement.CoverageFamily,
            source.CoverageDate, entitlement.OwnerPartyRef, Country: command.ServiceCountry, CoverageDate: source.CoverageDate, Direction: EntitlementDirection.Outbound), cancellationToken);
        if (resolved.EntitlementId != entitlement.EntitlementId)
            throw new CommandRefusedException("source_entitlement_not_applicable", "The selected entitlement is not the policy stack's applicable cover for the source date.", "select the entitlement adjudicated for this source");

        var replacementPolicy = await ResolveReplacementPolicyAsync(command, entitlement, db, tenantId, cancellationToken);
        // Owner decision 2026-10-18: only a goodwill replacement may carry a restocking fee; for every other reason the
        // return reason's fee rule is not consulted at all, so a fee-bearing reason never blocks a warranty replacement.
        var fee = command.ReplacementReason == ReplacementReason.Goodwill
            ? await returnReasons.QuoteAsync(command.ReturnReasonCode ?? source.ReturnReasonCode, command.RestockingFeeBasis, cancellationToken)
            : new RestockingFeeQuote(RestockFeeRule.None, 0m);
        if (fee.Amount < 0) throw new CommandRefusedException("restocking_fee_invalid", "A restocking fee cannot be negative.");

        var now = clock.UtcNow;
        var documentId = TimeOrderedId.New(command.EventTime);
        var linkId = TimeOrderedId.New(command.EventTime);
        var number = await numbers.NextAsync(DocumentNumbers.WarrantyReplacement, command.EventTime, cancellationToken);
        var conditionAllowed = command.ReplacementReason == ReplacementReason.DeadOnArrival ? ReplacementConditionAllowed.New : command.ReplacementConditionAllowed;
        var document = new WarrantyReplacement
        {
            TenantId = tenantId, WarrantyReplacementId = documentId, CommandId = command.CommandId, ReplacementNumber = number,
            SourceKind = source.Kind, SourceId = source.Id, SourceNumber = source.Number, OriginalInvoiceType = source.OriginalSale?.Type,
            OriginalInvoiceId = source.OriginalSale?.Id, OriginalInvoiceLine = source.OriginalSale?.Line, LegalEntityId = command.LegalEntityId,
            ServiceCountry = command.ServiceCountry.ToUpperInvariant(), ConsumerContract = command.ConsumerContract, Status = WarrantyReplacementStatus.Proposed,
            ReplacementPolicyCode = replacementPolicy.Code, ReturnReasonCode = command.ReturnReasonCode ?? source.ReturnReasonCode,
            RestockingFeeRule = fee.Rule, RestockingFeeAmount = fee.Amount, CreatedAt = now, CreatedBy = command.ActorRef,
            LastModifiedAt = now, LastModifiedBy = command.ActorRef,
        };
        var link = new ReplacementLink
        {
            TenantId = tenantId, ReplacementLinkId = linkId, WarrantyReplacementId = documentId, PreviousReplacementLinkId = entitlement.ReplacementLinkId,
            Direction = ReplacementDirection.CustomerFacing, ReplacementReason = command.ReplacementReason, State = ReplacementLinkState.Proposed,
            SourceKind = source.Kind, SourceId = source.Id, SourceNumber = source.Number, OriginalUnitId = source.OriginalUnitId,
            ReplacementUnitId = command.ReplacementUnitId, OriginalItemId = original.ItemId, ReplacementItemId = replacement.ItemId,
            SourceEntitlementId = entitlement.EntitlementId, CounterpartyPartyRef = entitlement.OwnerPartyRef,
            PricePolicy = command.PricePolicy, DifferenceAmount = command.DifferenceAmount,
            Currency = command.Currency.ToUpperInvariant(), ReturnDue = command.ReturnDue, NonReturnPriceBasis = command.NonReturnPriceBasis,
            NonReturnAmount = command.NonReturnAmount,
            ReplacementConditionAllowed = conditionAllowed, ConsentRefurbished = command.ConsentRefurbished, CreatedAt = now, CreatedBy = command.ActorRef,
        };
        db.WarrantyReplacements.Add(document);
        db.ReplacementLinks.Add(link);
        db.WarrantyReplacementLines.Add(new WarrantyReplacementLine
        {
            TenantId = tenantId, LineId = TimeOrderedId.New(command.EventTime), WarrantyReplacementId = documentId, ReplacementLinkId = linkId,
            Sequence = 1, Kind = WarrantyReplacementLineKind.Replacement, ItemId = replacement.ItemId, Description = "Warranty replacement unit",
            Quantity = 1m, UnitAmount = Math.Abs(command.DifferenceAmount), LineAmount = Math.Abs(command.DifferenceAmount), Currency = link.Currency,
        });
        if (fee.Amount > 0)
        {
            db.WarrantyReplacementLines.Add(new WarrantyReplacementLine
            {
                TenantId = tenantId, LineId = TimeOrderedId.New(command.EventTime), WarrantyReplacementId = documentId, Sequence = 2,
                Kind = WarrantyReplacementLineKind.ServiceRevenue, Description = "Restocking service fee", Quantity = 1m,
                UnitAmount = fee.Amount, LineAmount = fee.Amount, Currency = link.Currency, RevenueRule = fee.Rule,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        return new(await WarrantyReplacementQueries.DetailAsync(db, tenantId, documentId, cancellationToken), false);
    }

    internal static async Task<ReplacementPolicy> ResolveReplacementPolicyAsync(CreateWarrantyReplacement command, Entitlement entitlement,
        SerialisationDbContext db, TenantId tenantId, CancellationToken cancellationToken)
    {
        var version = await db.PolicyVersions.AsNoTracking().SingleAsync(row => row.TenantId == tenantId && row.PolicyVersionId == entitlement.PolicyVersionId, cancellationToken);
        if (command.ReplacementReason == ReplacementReason.WarrantyDefect && command.ReplacementPolicyCode is not null)
            throw new CommandRefusedException("replacement_policy_override_not_allowed", "A warranty-defect replacement uses the source warranty's configured replacement policy.");
        if (command.ReplacementReason is ReplacementReason.Goodwill or ReplacementReason.Recall && string.IsNullOrWhiteSpace(command.ReplacementPolicyCode))
            throw new CommandRefusedException("replacement_policy_required", "Goodwill and recall replacements name the approved policy.");

        ReplacementPolicy? policy;
        if (!string.IsNullOrWhiteSpace(command.ReplacementPolicyCode))
            policy = await db.ReplacementPolicies.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId && row.Code == command.ReplacementPolicyCode, cancellationToken);
        else if (version.ReplacementPolicyId is { } policyId)
            policy = await db.ReplacementPolicies.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId && row.ReplacementPolicyId == policyId, cancellationToken);
        else policy = null;
        if (policy is null) throw new CommandRefusedException("replacement_policy_unknown", "The replacement policy could not be resolved.");
        if (command.ReplacementReason is ReplacementReason.SellerError or ReplacementReason.DeadOnArrival && policy.Rule != ReplacementRule.CarryRemaining)
            throw new CommandRefusedException("original_term_policy_required", "Seller-error and DOA replacements require a carry_remaining policy so the original start and term remain unchanged.");
        return policy;
    }

    private static void Validate(CreateWarrantyReplacement command)
    {
        if (command.CommandId == Guid.Empty) throw new CommandRefusedException("command_id_required", "A command id is required.");
        if (!ReplacementSourceKind.All.Contains(command.SourceKind)) throw new CommandRefusedException("replacement_source_kind_unknown", $"'{command.SourceKind}' is not a replacement source kind.");
        if (!ReplacementReason.All.Contains(command.ReplacementReason)) throw new CommandRefusedException("replacement_reason_unknown", $"'{command.ReplacementReason}' is not a replacement reason.");
        if (!ReplacementPricePolicy.All.Contains(command.PricePolicy)) throw new CommandRefusedException("price_policy_unknown", $"'{command.PricePolicy}' is not a price policy.");
        if (command.PricePolicy == ReplacementPricePolicy.Zero && command.DifferenceAmount != 0) throw new CommandRefusedException("zero_price_difference", "A zero-price replacement has no difference amount.");
        if (command.PricePolicy == ReplacementPricePolicy.Full && command.DifferenceAmount < 0) throw new CommandRefusedException("full_price_negative", "A full-price replacement amount cannot be negative.");
        if (!ReplacementConditionAllowed.All.Contains(command.ReplacementConditionAllowed)) throw new CommandRefusedException("replacement_condition_allowed_unknown", $"'{command.ReplacementConditionAllowed}' is not a condition allowance.");
        if (command.Currency.Length != 3 || !command.Currency.All(char.IsAsciiLetter)) throw new CommandRefusedException("currency_invalid", "Currency must be a three-letter code.");
        if (command.ServiceCountry.Length != 2 || !command.ServiceCountry.All(char.IsAsciiLetter)) throw new CommandRefusedException("service_country_invalid", "Service country must be a two-letter code.");
        if (command.RestockingFeeBasis < 0) throw new CommandRefusedException("restocking_fee_basis_invalid", "The restocking fee basis cannot be negative.");
        if (command.NonReturnAmount is < 0) throw new CommandRefusedException("non_return_amount_invalid", "The non-return amount cannot be negative.");
        if (command.ReturnDue is not null && string.IsNullOrWhiteSpace(command.NonReturnPriceBasis)) throw new CommandRefusedException("non_return_price_basis_required", "An advance replacement with a return due date needs its non-return price basis.");
        if (command.NonReturnPriceBasis is not null && !NonReturnPriceBasis.All.Contains(command.NonReturnPriceBasis)) throw new CommandRefusedException("non_return_price_basis_unknown", $"'{command.NonReturnPriceBasis}' is not one of {string.Join(", ", NonReturnPriceBasis.All)}.");
        if (command.ReturnDue is { } due && due < DateOnly.FromDateTime(command.EventTime)) throw new CommandRefusedException("return_due_before_creation", "The return due date cannot be before the document date.");
    }
}

public sealed class ShipWarrantyReplacementHandler
{
    public static async Task<ShipWarrantyReplacementResult> Handle(
        ShipWarrantyReplacement command,
        SerialisationDbContext db,
        IUnitConditionQueryPort conditions,
        IStockUnitEventPort stock,
        IUnitEventWriter writer,
        EpisodeService episodes,
        EntitlementLedger entitlementLedger,
        CorrectionLedger corrections,
        ITenantContext tenant,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var document = await db.WarrantyReplacements.FirstOrDefaultAsync(row => row.TenantId == tenantId && row.WarrantyReplacementId == command.WarrantyReplacementId, cancellationToken)
            ?? throw new CommandRefusedException("warranty_replacement_unknown", $"Warranty replacement {command.WarrantyReplacementId} does not exist.");
        var link = await db.ReplacementLinks.SingleAsync(row => row.TenantId == tenantId && row.WarrantyReplacementId == document.WarrantyReplacementId, cancellationToken);
        if (document.ShipCommandId == command.CommandId) return await ReplayAsync(document, link, db, tenantId, cancellationToken);
        if (document.Status != WarrantyReplacementStatus.Proposed || link.State != ReplacementLinkState.Proposed)
            throw new CommandRefusedException("warranty_replacement_not_proposed", $"Warranty replacement {document.ReplacementNumber} is already {document.Status}.");
        if (command.ReplacementDate > DateOnly.FromDateTime(command.EventTime)) throw new CommandRefusedException("replacement_date_in_future", "The replacement date cannot be after its business event time.");

        var grade = await conditions.LatestGradeAsync(link.ReplacementUnitId, cancellationToken);
        WarrantyReplacementRules.ValidateCondition(link.ReplacementReason, link.ReplacementConditionAllowed, grade,
            document.ServiceCountry, document.ConsumerContract, link.ConsentRefurbished);
        var source = await db.Entitlements.AsNoTracking().SingleAsync(row => row.TenantId == tenantId && row.EntitlementId == link.SourceEntitlementId, cancellationToken);
        if (source.Status != EntitlementStatus.Active) throw new CommandRefusedException("source_entitlement_not_active", $"Source entitlement {source.EntitlementId} is {source.Status}.");
        await ValidateAssetTagAsync(db, tenantId, link, command.EventTime, cancellationToken);
        var originalOwner = await episodes.OpenAsync(link.OriginalUnitId, EpisodeKind.Ownership, cancellationToken);
        var ownerPartyRef = source.OwnerPartyRef ?? originalOwner?.PartyRef
            ?? throw new CommandRefusedException("replacement_customer_missing", "The source entitlement and original unit do not identify the replacement customer.");
        var ownerPartyType = source.OwnerPartyType ?? originalOwner?.PartyType
            ?? throw new CommandRefusedException("replacement_customer_missing", "The source entitlement and original unit do not identify the replacement customer.");
        var replacementOwner = await episodes.OpenAsync(link.ReplacementUnitId, EpisodeKind.Ownership, cancellationToken);
        if (replacementOwner is not null && replacementOwner.PartyType != PartyType.Us
            && (replacementOwner.PartyRef != ownerPartyRef || replacementOwner.PartyType != ownerPartyType))
            throw new CommandRefusedException("replacement_owner_mismatch", "The replacement unit is already owned by a different customer.");

        var replacementDocument = new DocumentRef("warranty_replacement", document.WarrantyReplacementId, 1);
        var shipKey = IdempotencyKey.ForCommand("serialisation", command.CommandId, link.ReplacementUnitId, ReplacementEventType.Shipped, command.EventTime);
        var custody = await episodes.PrepareCustodyMoveAsync(link.ReplacementUnitId,
            new CustodyTarget(LocationKind.CustomerSite, PartyRef: ownerPartyRef, PartyType: ownerPartyType,
                Reason: EpisodeReason.Replacement, Source: EpisodeSource.Delivery),
            UnitEventType.Shipped, IdempotencyKey.Derive(shipKey, "custody"), command.EventTime, command.ActorRef,
            document.LegalEntityId, replacementDocument, null, null, false, cancellationToken);
        var ownership = await episodes.PrepareOwnershipOpenAsync(link.ReplacementUnitId,
            new OwnershipTarget(ownerPartyRef, ownerPartyType, EpisodeReason.Replacement, EpisodeSource.Delivery),
            IdempotencyKey.Derive(shipKey, "ownership"), command.EventTime, command.ActorRef, document.LegalEntityId,
            replacementDocument, null, cancellationToken);

        var transfer = await TransferEntitlementHandler.Handle(new TransferEntitlement(command.CommandId, link.SourceEntitlementId,
            link.ReplacementUnitId, link.ReplacementLinkId, command.ReplacementDate, command.EventTime, command.ActorRef,
            document.ReplacementPolicyCode, Country: document.ServiceCountry, ConsumerContract: document.ConsumerContract),
            db, entitlementLedger, tenant, clock, corrections, cancellationToken);
        var stockResult = await stock.IssueWarrantyReplacementAsync(new WarrantyReplacementStockIssue(command.CommandId,
            document.WarrantyReplacementId, document.ReplacementNumber, link.ReplacementLinkId, link.ReplacementUnitId,
            link.ReplacementItemId, command.EventTime, command.ActorRef, document.LegalEntityId), cancellationToken);

        var shipEventId = TimeOrderedId.New(command.EventTime);
        var drafts = new List<UnitEventDraft> { custody.Draft };
        if (ownership.CloseDraft is not null) drafts.Add(ownership.CloseDraft);
        drafts.Add(ownership.Draft);
        var tag = await RepointAssetTagAsync(db, tenantId, link, command, clock.UtcNow, drafts, cancellationToken);
        // COUPLED to PublishedEventMapper's Replacement.Shipped branch: both units need the internal fact, while the
        // published document event must leave only from the replacement unit or consumers process one shipment twice.
        drafts.Add(ShipmentDraft(document, link, transfer, grade, stockResult.PostingReference, command,
            link.OriginalUnitId, TimeOrderedId.New(command.EventTime)));
        drafts.Add(ShipmentDraft(document, link, transfer, grade, stockResult.PostingReference, command,
            link.ReplacementUnitId, shipEventId));
        if (tag is not null) drafts.Add(PrintDraft(document, link, tag, command));
        await writer.AppendAsync(drafts, cancellationToken);

        document.Status = WarrantyReplacementStatus.Shipped;
        document.ShipCommandId = command.CommandId;
        document.ShippedAt = clock.UtcNow;
        document.ShippedBy = command.ActorRef;
        document.StockPostingReference = stockResult.PostingReference;
        document.LastModifiedAt = clock.UtcNow;
        document.LastModifiedBy = command.ActorRef;
        var alreadyReturned = link.SourceKind == ReplacementSourceKind.Rma
            && await db.ReturnReceipts.AsNoTracking().AnyAsync(row => row.TenantId == tenantId && row.RmaId == link.SourceId
                && row.ReceivedUnitId == link.OriginalUnitId && row.Outcome == ReturnOutcome.Accepted, cancellationToken);
        link.State = link.ReturnDue is null || alreadyReturned ? ReplacementLinkState.ClosedReturned : ReplacementLinkState.AwaitingReturn;
        link.TargetEntitlementId = transfer.TargetEntitlementId;
        link.PolicyApplied = transfer.PolicyApplied;
        link.ComputedEnd = transfer.ComputedEnd;
        link.ReplacementCondition = grade;
        link.ShippedAt = clock.UtcNow;
        link.ShippedBy = command.ActorRef;
        link.ShippedEventId = shipEventId;
        await db.SaveChangesAsync(cancellationToken);
        return Result(document, link, transfer.TargetEntitlementId, transfer.PolicyApplied, transfer.ComputedEnd, grade,
            stockResult.PostingReference, tag is not null, shipEventId, false);
    }

    private static async Task ValidateAssetTagAsync(SerialisationDbContext db, TenantId tenantId, ReplacementLink link, DateTime eventTime, CancellationToken cancellationToken)
    {
        var tags = await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenantId && row.Type == IdentifierType.CustomerAssetTag && row.ValidTo == null
            && (row.UnitId == link.OriginalUnitId || row.UnitId == link.ReplacementUnitId)).ToListAsync(cancellationToken);
        var original = tags.SingleOrDefault(row => row.UnitId == link.OriginalUnitId);
        if (tags.Any(row => row.UnitId == link.ReplacementUnitId)) throw new CommandRefusedException("replacement_asset_tag_occupied", "The replacement unit already has a customer asset tag.");
        if (original is not null && eventTime < original.ValidFrom) throw new CommandRefusedException("asset_tag_event_out_of_order", "The asset tag cannot be re-pointed before it was assigned.");
    }

    private static async Task<UnitIdentifier?> RepointAssetTagAsync(SerialisationDbContext db, TenantId tenantId, ReplacementLink link,
        ShipWarrantyReplacement command, DateTime recordedAt, List<UnitEventDraft> drafts, CancellationToken cancellationToken)
    {
        var old = await db.Identifiers.FirstOrDefaultAsync(row => row.TenantId == tenantId && row.UnitId == link.OriginalUnitId
            && row.Type == IdentifierType.CustomerAssetTag && row.ValidTo == null, cancellationToken);
        if (old is null) return null;
        var eventId = TimeOrderedId.New(command.EventTime);
        var replacement = new UnitIdentifier
        {
            TenantId = tenantId, IdentifierId = TimeOrderedId.New(command.EventTime), UnitId = link.ReplacementUnitId, Type = old.Type,
            Scheme = old.Scheme, IssuerPartyRef = old.IssuerPartyRef, EnterpriseIdentifier = old.EnterpriseIdentifier, Qualifier = old.Qualifier,
            RawValue = old.RawValue, NormalisedValue = old.NormalisedValue, ScopeKey = old.ScopeKey, ValidFrom = command.EventTime,
            SupersedesIdentifierId = old.IdentifierId, ActorRef = command.ActorRef, RecordedTime = recordedAt, EventId = eventId,
        };
        old.ValidTo = command.EventTime;
        old.SupersededByIdentifierId = replacement.IdentifierId;
        old.CorrectionReason = "replacement";
        await db.SaveChangesAsync(cancellationToken);
        db.Identifiers.Add(replacement);
        drafts.Add(new UnitEventDraft
        {
            UnitId = link.OriginalUnitId, EventTime = command.EventTime, EventType = ReplacementEventType.AssetTagRepointed,
            LegalEntityId = db.WarrantyReplacements.Local.Single(row => row.WarrantyReplacementId == link.WarrantyReplacementId).LegalEntityId,
            ActorRef = command.ActorRef, DocumentType = "warranty_replacement", DocumentId = link.WarrantyReplacementId,
            IdempotencyKey = IdempotencyKey.ForCommand("serialisation", command.CommandId, link.OriginalUnitId, ReplacementEventType.AssetTagRepointed, command.EventTime),
            Attributes = JsonObjectText.Of(new { replacement_link_id = link.ReplacementLinkId, replacement_unit_id = link.ReplacementUnitId, identifier_id = old.IdentifierId, replacement_identifier_id = replacement.IdentifierId }),
        });
        return replacement;
    }

    private static UnitEventDraft ShipmentDraft(WarrantyReplacement document, ReplacementLink link, TransferEntitlementResult transfer,
        string grade, string postingReference, ShipWarrantyReplacement command, Guid unitId, Guid eventId) => new()
    {
        UnitId = unitId, EventTime = command.EventTime, EventType = ReplacementEventType.Shipped, PresetEventId = eventId,
        LegalEntityId = document.LegalEntityId, ActorRef = command.ActorRef, DocumentType = "warranty_replacement", DocumentId = document.WarrantyReplacementId,
        IdempotencyKey = IdempotencyKey.ForCommand("serialisation", command.CommandId, unitId, ReplacementEventType.Shipped, command.EventTime),
        Attributes = JsonObjectText.Of(new
        {
            warranty_replacement_id = document.WarrantyReplacementId, replacement_number = document.ReplacementNumber,
            replacement_link_id = link.ReplacementLinkId, original_unit_id = link.OriginalUnitId, replacement_unit_id = link.ReplacementUnitId,
            original_item_id = link.OriginalItemId, replacement_item_id = link.ReplacementItemId, source_entitlement_id = link.SourceEntitlementId,
            target_entitlement_id = transfer.TargetEntitlementId, source_kind = link.SourceKind, source_id = link.SourceId, source_number = link.SourceNumber,
            reason = link.ReplacementReason, price_policy = link.PricePolicy, difference_amount = link.DifferenceAmount, currency = link.Currency,
            policy_applied = transfer.PolicyApplied, computed_end = transfer.ComputedEnd, replacement_condition = grade,
            accrual_suppressed = true, restocking_fee_amount = document.RestockingFeeAmount, stock_posting_reference = postingReference,
        }),
    };

    private static UnitEventDraft PrintDraft(WarrantyReplacement document, ReplacementLink link, UnitIdentifier tag, ShipWarrantyReplacement command) => new()
    {
        UnitId = link.ReplacementUnitId, EventTime = command.EventTime, EventType = ReplacementEventType.AssetTagPrintRequested, PresetEventId = tag.EventId,
        LegalEntityId = document.LegalEntityId, ActorRef = command.ActorRef, DocumentType = "warranty_replacement", DocumentId = document.WarrantyReplacementId,
        IdempotencyKey = IdempotencyKey.ForCommand("serialisation", command.CommandId, link.ReplacementUnitId, ReplacementEventType.AssetTagPrintRequested, command.EventTime),
        Attributes = JsonObjectText.Of(new { warranty_replacement_id = document.WarrantyReplacementId, replacement_link_id = link.ReplacementLinkId, identifier_id = tag.IdentifierId, asset_tag = tag.NormalisedValue }),
    };

    private static async Task<ShipWarrantyReplacementResult> ReplayAsync(WarrantyReplacement document, ReplacementLink link,
        SerialisationDbContext db, TenantId tenantId, CancellationToken cancellationToken)
    {
        if (link.TargetEntitlementId is null || link.PolicyApplied is null || link.ComputedEnd is null || link.ReplacementCondition is null
            || document.StockPostingReference is null || link.ShippedEventId is null)
            throw new CommandRefusedException("replacement_replay_inconsistent", "The shipped replacement is missing its recorded result.");
        var tag = await db.Identifiers.AsNoTracking().AnyAsync(row => row.TenantId == tenantId && row.UnitId == link.ReplacementUnitId
            && row.Type == IdentifierType.CustomerAssetTag && row.SupersedesIdentifierId != null && row.ValidTo == null, cancellationToken);
        return Result(document, link, link.TargetEntitlementId.Value, link.PolicyApplied, link.ComputedEnd.Value, link.ReplacementCondition,
            document.StockPostingReference, tag, link.ShippedEventId.Value, true);
    }

    private static ShipWarrantyReplacementResult Result(WarrantyReplacement document, ReplacementLink link, Guid targetEntitlementId,
        string policyApplied, DateOnly computedEnd, string grade, string postingReference, bool tag, Guid eventId, bool replayed) =>
        new(document.WarrantyReplacementId, document.ReplacementNumber, link.ReplacementLinkId, link.State, targetEntitlementId,
            policyApplied, computedEnd, grade, postingReference, tag, true, eventId, replayed);
}

public static class WarrantyReplacementRules
{
    public static void ValidateCondition(string reason, string allowed, string actual, string country, bool consumerContract, bool consentRefurbished)
    {
        var refurbished = ConditionGrades.IsRefurbished(actual);
        if (reason == ReplacementReason.DeadOnArrival && actual != ConditionGrades.New)
            throw new CommandRefusedException("doa_requires_new", "A dead-on-arrival replacement must ship a new unit.");
        if (allowed == ReplacementConditionAllowed.New && actual != ConditionGrades.New)
            throw new CommandRefusedException("replacement_condition_not_allowed", $"Condition '{actual}' is not allowed; this replacement requires new stock.");
        if (allowed == ReplacementConditionAllowed.NewOrRefurbished && actual != ConditionGrades.New && !refurbished)
            throw new CommandRefusedException("replacement_condition_not_allowed", $"Condition '{actual}' is not new or refurbished.");
        if (consumerContract && Regions.Contains("EU", country) && refurbished && !consentRefurbished)
            throw new CommandRefusedException("refurbished_consent_required", "An EU consumer replacement needs recorded consent before refurbished stock can ship.");
    }
}

public static class ReplacementEventType
{
    public const string Shipped = "replacement.shipped";
    public const string AssetTagRepointed = "replacement.asset_tag_repointed";
    public const string AssetTagPrintRequested = "replacement.asset_tag_print_requested";
    public const string ReturnOverdue = "replacement.return_overdue";
    public const string ReturnDueExtended = "replacement.return_due_extended";
    public const string NonReturnBilled = "replacement.non_return_billed";
    public const string LoanConverted = "replacement.loan_converted";
    public const string VendorReceived = "replacement.vendor_received";
    public const string LinkCorrected = "replacement.link_corrected";
    public const string CertificateReissueRequested = "certificate.reissue_requested";
}
