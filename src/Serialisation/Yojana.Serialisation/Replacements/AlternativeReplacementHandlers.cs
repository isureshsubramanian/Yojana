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

public static class ConvertLoanToReplacementHandler
{
    public static async Task<ConvertLoanToReplacementResult> Handle(ConvertLoanToReplacement command, SerialisationDbContext db,
        IServiceDocumentQueryPort serviceDocuments, UnitResolver units, IUnitConditionQueryPort conditions, EpisodeService episodes,
        EntitlementLedger entitlementLedger, CorrectionLedger corrections, DocumentNumbers numbers, IUnitEventWriter writer,
        ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.WarrantyReplacements.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.CommandId == command.CommandId, cancellationToken) is { } replay)
            return await ReplayLoanAsync(db, tenantId, replay.WarrantyReplacementId, cancellationToken);
        ValidateCommon(command.CommandId, command.ReplacementReason, command.ReplacementConditionAllowed, command.ServiceCountry);
        var issue = await serviceDocuments.GetLoanerIssueAsync(command.LoanerIssue, cancellationToken)
            ?? throw new CommandRefusedException("loaner_issue_unknown", $"Loaner issue {command.LoanerIssue.Id} is unavailable through the service document port.");
        if (command.ReplacementDate > DateOnly.FromDateTime(command.EventTime)) throw new CommandRefusedException("replacement_date_in_future", "The replacement date cannot be after its business event time.");
        var issuedUnits = new HashSet<Guid>();
        foreach (var line in issue.Lines)
            foreach (var serial in line.Serials)
                if (await units.ResolveAsync(serial, line.ItemId, cancellationToken) is { } resolved) issuedUnits.Add(resolved.UnitId);
        if (!issuedUnits.Contains(command.LoanerUnitId)) throw new CommandRefusedException("loaner_unit_not_on_issue", "The selected loaner unit is not on the named service loaner issue.");

        var source = await db.Entitlements.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.EntitlementId == command.SourceEntitlementId, cancellationToken)
            ?? throw new CommandRefusedException("entitlement_unknown", $"Entitlement {command.SourceEntitlementId} does not exist.");
        if (source.UnitId != command.OriginalUnitId || source.Direction != EntitlementDirection.Outbound || source.Status != EntitlementStatus.Active)
            throw new CommandRefusedException("loan_replacement_source_invalid", "The source must be active outbound cover on the original unit.");
        if (source.OwnerPartyRef != issue.CustomerPartyRef) throw new CommandRefusedException("loan_customer_mismatch", "The loaner issue customer does not own the source entitlement.");
        if (command.OriginalUnitId == command.LoanerUnitId) throw new CommandRefusedException("replacement_is_original", "A replacement must be a different unit.");

        var loanCustody = await episodes.OpenAsync(command.LoanerUnitId, EpisodeKind.Custody, cancellationToken);
        if (loanCustody is not { LocationKind: LocationKind.CustomerSite, PartyRef: { } } || loanCustody.PartyRef != issue.CustomerPartyRef || loanCustody.Reason != EpisodeReason.Loan)
            throw new CommandRefusedException("loaner_not_with_customer", "The loaner must be held by the issue customer under an open loan custody episode.");
        var originalOwner = await episodes.OpenAsync(command.OriginalUnitId, EpisodeKind.Ownership, cancellationToken);
        if (originalOwner?.PartyRef != issue.CustomerPartyRef) throw new CommandRefusedException("original_owner_mismatch", "The loan customer is not the current owner of the original unit.");
        var loanerOwner = await episodes.OpenAsync(command.LoanerUnitId, EpisodeKind.Ownership, cancellationToken);
        if (loanerOwner is not null && loanerOwner.PartyType != PartyType.Us)
            throw new CommandRefusedException("loaner_owner_mismatch", "The loaner is not owned by the service entity.");

        var current = await db.Current.AsNoTracking().Where(row => row.TenantId == tenantId && (row.UnitId == command.OriginalUnitId || row.UnitId == command.LoanerUnitId))
            .Select(row => new { row.UnitId, row.ItemId }).ToListAsync(cancellationToken);
        var original = current.SingleOrDefault(row => row.UnitId == command.OriginalUnitId) ?? throw new CommandRefusedException("original_unit_unknown", "The original unit does not exist.");
        var loaner = current.SingleOrDefault(row => row.UnitId == command.LoanerUnitId) ?? throw new CommandRefusedException("loaner_unit_unknown", "The loaner unit does not exist.");
        var grade = await conditions.LatestGradeAsync(command.LoanerUnitId, cancellationToken);
        WarrantyReplacementRules.ValidateCondition(command.ReplacementReason, command.ReplacementConditionAllowed, grade,
            command.ServiceCountry, command.ConsumerContract, command.ConsentRefurbished);
        var createShape = new CreateWarrantyReplacement(command.CommandId, ReplacementSourceKind.Loan, command.LoanerIssue.Id,
            source.EntitlementId, command.LoanerUnitId, command.ReplacementReason, ReplacementPricePolicy.Zero, 0m, "XXX",
            command.ServiceCountry, command.ConsumerContract, command.ConsentRefurbished, command.EventTime, command.LegalEntityId,
            command.ActorRef, command.ReplacementConditionAllowed, command.ReplacementPolicyCode);
        var policy = await CreateWarrantyReplacementHandler.ResolveReplacementPolicyAsync(createShape, source, db, tenantId, cancellationToken);

        var documentId = TimeOrderedId.New(command.EventTime);
        var linkId = TimeOrderedId.New(command.EventTime);
        var number = await numbers.NextAsync(DocumentNumbers.WarrantyReplacement, command.EventTime, cancellationToken);
        var sourceNumber = $"loan:{command.LoanerIssue.Id:N}";
        var document = NewDocument(tenantId, documentId, command.CommandId, number, ReplacementSourceKind.Loan, command.LoanerIssue.Id,
            sourceNumber, command.LegalEntityId, command.ServiceCountry, command.ConsumerContract,
            policy.Code, command.EventTime, command.ActorRef);
        var link = NewLink(tenantId, linkId, documentId, ReplacementDirection.CustomerFacing, command.ReplacementReason,
            ReplacementSourceKind.Loan, command.LoanerIssue.Id, document.SourceNumber, command.OriginalUnitId, command.LoanerUnitId,
            original.ItemId, loaner.ItemId, source, issue.CustomerPartyRef, command.ReplacementConditionAllowed, command.ConsentRefurbished,
            command.EventTime, command.ActorRef, "XXX");
        db.WarrantyReplacements.Add(document);
        db.ReplacementLinks.Add(link);
        AddLine(db, tenantId, documentId, linkId, loaner.ItemId, "XXX", command.EventTime);

        var docRef = new DocumentRef("warranty_replacement", documentId, 1);
        var originalOwnership = await episodes.PrepareOwnershipOpenAsync(command.OriginalUnitId,
            new OwnershipTarget(command.LegalEntityId, PartyType.Us, EpisodeReason.Replacement, EpisodeSource.Delivery),
            IdempotencyKey.Derive(command.CommandId.ToString("N"), "original-ownership"), command.EventTime, command.ActorRef,
            command.LegalEntityId, docRef, EpisodeReason.Replacement, cancellationToken);
        var loanerOwnership = await episodes.PrepareOwnershipOpenAsync(command.LoanerUnitId,
            new OwnershipTarget(issue.CustomerPartyRef, PartyType.Customer, EpisodeReason.Replacement, EpisodeSource.Delivery),
            IdempotencyKey.Derive(command.CommandId.ToString("N"), "loaner-ownership"), command.EventTime, command.ActorRef,
            command.LegalEntityId, docRef, EpisodeReason.Replacement, cancellationToken);
        var loanerCustody = await episodes.PrepareCustodyMoveAsync(command.LoanerUnitId,
            new CustodyTarget(LocationKind.CustomerSite, loanCustody.LocationId, issue.CustomerPartyRef, PartyType.Customer,
                EpisodeReason.Replacement, EpisodeSource.Delivery), UnitEventType.CustodyChanged,
            IdempotencyKey.Derive(command.CommandId.ToString("N"), "loaner-custody"), command.EventTime, command.ActorRef,
            command.LegalEntityId, docRef, new Dictionary<string, object?> { ["replacement_link_id"] = linkId }, null, true, cancellationToken);
        var transfer = await TransferEntitlementHandler.Handle(new TransferEntitlement(command.CommandId, source.EntitlementId,
            command.LoanerUnitId, linkId, command.ReplacementDate, command.EventTime, command.ActorRef, policy.Code,
            Country: command.ServiceCountry, ConsumerContract: command.ConsumerContract), db, entitlementLedger, tenant, clock, corrections, cancellationToken);

        var eventId = TimeOrderedId.New(command.EventTime);
        var drafts = new List<UnitEventDraft>();
        if (originalOwnership.CloseDraft is not null) drafts.Add(originalOwnership.CloseDraft);
        drafts.Add(originalOwnership.Draft);
        if (loanerOwnership.CloseDraft is not null) drafts.Add(loanerOwnership.CloseDraft);
        drafts.Add(loanerOwnership.Draft);
        drafts.Add(loanerCustody.Draft);
        await RepointAssetTagAsync(db, tenantId, command.OriginalUnitId, command.LoanerUnitId, linkId, documentId,
            command.CommandId, command.EventTime, command.ActorRef, command.LegalEntityId, drafts, cancellationToken);
        drafts.Add(new UnitEventDraft
        {
            UnitId = command.LoanerUnitId, EventTime = command.EventTime, EventType = ReplacementEventType.Shipped, PresetEventId = eventId,
            LegalEntityId = command.LegalEntityId, ActorRef = command.ActorRef, DocumentType = docRef.Type, DocumentId = docRef.Id, DocumentLine = docRef.Line,
            IdempotencyKey = IdempotencyKey.ForCommand("serialisation", command.CommandId, command.LoanerUnitId, ReplacementEventType.Shipped, command.EventTime),
            Attributes = JsonObjectText.Of(new { warranty_replacement_id = documentId, replacement_number = number, replacement_link_id = linkId,
                original_unit_id = command.OriginalUnitId, replacement_unit_id = command.LoanerUnitId, original_item_id = original.ItemId,
                replacement_item_id = loaner.ItemId, source_entitlement_id = source.EntitlementId, target_entitlement_id = transfer.TargetEntitlementId,
                source_kind = ReplacementSourceKind.Loan, source_id = command.LoanerIssue.Id, source_number = document.SourceNumber,
                reason = command.ReplacementReason, price_policy = ReplacementPricePolicy.Zero, difference_amount = 0m, currency = "XXX",
                policy_applied = transfer.PolicyApplied, computed_end = transfer.ComputedEnd, replacement_condition = grade,
                accrual_suppressed = true, restocking_fee_amount = 0m, loan_conversion = true }),
        });
        await writer.AppendAsync(drafts, cancellationToken);
        Finalise(document, link, command.CommandId, command.ActorRef, clock.UtcNow, eventId, transfer, grade, ReplacementLinkState.ClosedReturned);
        return new(documentId, number, linkId, transfer.TargetEntitlementId, transfer.PolicyApplied, transfer.ComputedEnd, eventId, false);
    }

    private static async Task<ConvertLoanToReplacementResult> ReplayLoanAsync(SerialisationDbContext db, TenantId tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        var data = await (from document in db.WarrantyReplacements.AsNoTracking()
                          join link in db.ReplacementLinks.AsNoTracking() on new { document.TenantId, document.WarrantyReplacementId } equals new { link.TenantId, link.WarrantyReplacementId }
                          where document.TenantId == tenantId && document.WarrantyReplacementId == documentId
                          select new { document, link }).SingleAsync(cancellationToken);
        return new(documentId, data.document.ReplacementNumber, data.link.ReplacementLinkId, data.link.TargetEntitlementId!.Value,
            data.link.PolicyApplied!, data.link.ComputedEnd!.Value, data.link.ShippedEventId!.Value, true);
    }

    internal static WarrantyReplacement NewDocument(TenantId tenantId, Guid documentId, Guid commandId, string number, string sourceKind,
        Guid sourceId, string sourceNumber, Guid legalEntityId, string country, bool consumer, string policyCode, DateTime at, string actor) => new()
    {
        TenantId = tenantId, WarrantyReplacementId = documentId, CommandId = commandId, ReplacementNumber = number,
        SourceKind = sourceKind, SourceId = sourceId, SourceNumber = sourceNumber, LegalEntityId = legalEntityId,
        ServiceCountry = country.ToUpperInvariant(), ConsumerContract = consumer, Status = WarrantyReplacementStatus.Shipped,
        ReplacementPolicyCode = policyCode, RestockingFeeAmount = 0m, CreatedAt = at, CreatedBy = actor,
        LastModifiedAt = at, LastModifiedBy = actor,
    };

    internal static ReplacementLink NewLink(TenantId tenantId, Guid linkId, Guid documentId, string direction, string reason,
        string sourceKind, Guid sourceId, string sourceNumber, Guid originalUnitId, Guid replacementUnitId, Guid originalItemId,
        Guid replacementItemId, Entitlement source, Guid counterparty, string conditionAllowed, bool consent, DateTime at, string actor,
        string currency) => new()
    {
        TenantId = tenantId, ReplacementLinkId = linkId, WarrantyReplacementId = documentId, PreviousReplacementLinkId = source.ReplacementLinkId,
        Direction = direction, ReplacementReason = reason, State = ReplacementLinkState.ClosedReturned, SourceKind = sourceKind,
        SourceId = sourceId, SourceNumber = sourceNumber, OriginalUnitId = originalUnitId, ReplacementUnitId = replacementUnitId,
        OriginalItemId = originalItemId, ReplacementItemId = replacementItemId, SourceEntitlementId = source.EntitlementId,
        CounterpartyPartyRef = counterparty, PricePolicy = ReplacementPricePolicy.Zero, DifferenceAmount = 0m, Currency = currency,
        ReplacementConditionAllowed = conditionAllowed, ConsentRefurbished = consent, CreatedAt = at, CreatedBy = actor,
    };

    internal static void AddLine(SerialisationDbContext db, TenantId tenantId, Guid documentId, Guid linkId, Guid itemId, string currency, DateTime at) =>
        db.WarrantyReplacementLines.Add(new WarrantyReplacementLine
        {
            TenantId = tenantId, LineId = TimeOrderedId.New(at), WarrantyReplacementId = documentId, ReplacementLinkId = linkId,
            Sequence = 1, Kind = WarrantyReplacementLineKind.Replacement, ItemId = itemId, Description = "Warranty replacement unit",
            Quantity = 1m, UnitAmount = 0m, LineAmount = 0m, Currency = currency,
        });

    internal static void Finalise(WarrantyReplacement document, ReplacementLink link, Guid commandId, string actor, DateTime recordedAt,
        Guid eventId, TransferEntitlementResult transfer, string grade, string state)
    {
        document.Status = WarrantyReplacementStatus.Shipped;
        document.ShipCommandId = commandId;
        document.ShippedAt = recordedAt;
        document.ShippedBy = actor;
        document.LastModifiedAt = recordedAt;
        document.LastModifiedBy = actor;
        link.State = state;
        link.TargetEntitlementId = transfer.TargetEntitlementId;
        link.PolicyApplied = transfer.PolicyApplied;
        link.ComputedEnd = transfer.ComputedEnd;
        link.ReplacementCondition = grade;
        link.ShippedAt = recordedAt;
        link.ShippedBy = actor;
        link.ShippedEventId = eventId;
    }

    private static async Task RepointAssetTagAsync(SerialisationDbContext db, TenantId tenantId, Guid originalUnitId, Guid replacementUnitId,
        Guid linkId, Guid documentId, Guid commandId, DateTime at, string actor, Guid legalEntityId, List<UnitEventDraft> drafts,
        CancellationToken cancellationToken)
    {
        var tags = await db.Identifiers.Where(row => row.TenantId == tenantId && row.Type == IdentifierType.CustomerAssetTag && row.ValidTo == null
            && (row.UnitId == originalUnitId || row.UnitId == replacementUnitId)).ToListAsync(cancellationToken);
        var old = tags.SingleOrDefault(row => row.UnitId == originalUnitId);
        if (old is null) return;
        if (tags.Any(row => row.UnitId == replacementUnitId)) throw new CommandRefusedException("replacement_asset_tag_occupied", "The replacement unit already has a customer asset tag.");
        if (at < old.ValidFrom) throw new CommandRefusedException("asset_tag_event_out_of_order", "The asset tag cannot be re-pointed before it was assigned.");
        var eventId = TimeOrderedId.New(at);
        var replacement = new UnitIdentifier
        {
            TenantId = tenantId, IdentifierId = TimeOrderedId.New(at), UnitId = replacementUnitId, Type = old.Type, Scheme = old.Scheme,
            IssuerPartyRef = old.IssuerPartyRef, EnterpriseIdentifier = old.EnterpriseIdentifier, Qualifier = old.Qualifier,
            RawValue = old.RawValue, NormalisedValue = old.NormalisedValue, ScopeKey = old.ScopeKey, ValidFrom = at,
            SupersedesIdentifierId = old.IdentifierId, ActorRef = actor, RecordedTime = at, EventId = eventId,
        };
        old.ValidTo = at;
        old.SupersededByIdentifierId = replacement.IdentifierId;
        old.CorrectionReason = "replacement";
        db.Identifiers.Add(replacement);
        drafts.Add(new UnitEventDraft
        {
            UnitId = originalUnitId, EventTime = at, EventType = ReplacementEventType.AssetTagRepointed, LegalEntityId = legalEntityId,
            ActorRef = actor, DocumentType = "warranty_replacement", DocumentId = documentId,
            IdempotencyKey = IdempotencyKey.ForCommand("serialisation", commandId, originalUnitId, ReplacementEventType.AssetTagRepointed, at),
            Attributes = JsonObjectText.Of(new { replacement_link_id = linkId, replacement_unit_id = replacementUnitId,
                identifier_id = old.IdentifierId, replacement_identifier_id = replacement.IdentifierId }),
        });
        drafts.Add(new UnitEventDraft
        {
            UnitId = replacementUnitId, EventTime = at, EventType = ReplacementEventType.AssetTagPrintRequested, PresetEventId = eventId,
            LegalEntityId = legalEntityId, ActorRef = actor, DocumentType = "warranty_replacement", DocumentId = documentId,
            IdempotencyKey = IdempotencyKey.ForCommand("serialisation", commandId, replacementUnitId, ReplacementEventType.AssetTagPrintRequested, at),
            Attributes = JsonObjectText.Of(new { warranty_replacement_id = documentId, replacement_link_id = linkId,
                identifier_id = replacement.IdentifierId, asset_tag = replacement.NormalisedValue }),
        });
    }

    internal static void ValidateCommon(Guid commandId, string reason, string conditionAllowed, string country)
    {
        if (commandId == Guid.Empty) throw new CommandRefusedException("command_id_required", "A command id is required.");
        if (!ReplacementReason.All.Contains(reason)) throw new CommandRefusedException("replacement_reason_unknown", $"'{reason}' is not a replacement reason.");
        if (!ReplacementConditionAllowed.All.Contains(conditionAllowed)) throw new CommandRefusedException("replacement_condition_allowed_unknown", $"'{conditionAllowed}' is not a condition allowance.");
        if (country.Length != 2 || !country.All(char.IsAsciiLetter)) throw new CommandRefusedException("service_country_invalid", "Service country must be a two-letter code.");
    }
}

public static class ReceiveVendorReplacementHandler
{
    public static async Task<ReceiveVendorReplacementResult> Handle(ReceiveVendorReplacement command, SerialisationDbContext db,
        IUnitConditionQueryPort conditions, EpisodeService episodes, EntitlementLedger entitlementLedger, CorrectionLedger corrections,
        DocumentNumbers numbers, IUnitEventWriter writer, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.WarrantyReplacements.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.CommandId == command.CommandId, cancellationToken) is { } replay)
            return await ReplayVendorAsync(db, tenantId, replay.WarrantyReplacementId, cancellationToken);
        ConvertLoanToReplacementHandler.ValidateCommon(command.CommandId, command.ReplacementReason, command.ReplacementConditionAllowed, "XX");
        if (command.ReplacementDate > DateOnly.FromDateTime(command.ReceivedAt)) throw new CommandRefusedException("replacement_date_in_future", "The replacement date cannot be after receipt.");
        var data = await (from line in db.RmaLines
                          join rma in db.Rmas on new { line.TenantId, line.RmaId } equals new { rma.TenantId, rma.RmaId }
                          where line.TenantId == tenantId && line.RmaLineId == command.RmaLineId
                          select new { line, rma }).SingleOrDefaultAsync(cancellationToken)
            ?? throw new CommandRefusedException("rma_line_unknown", $"RMA line {command.RmaLineId} does not exist.");
        if (data.line.Disposition != RmaDisposition.ReturnToVendor || data.line.ReceivedUnitId is not { } originalUnitId)
            throw new CommandRefusedException("vendor_return_required", "A vendor replacement requires a received RMA line dispositioned return-to-vendor.");
        var vendorOwnership = await episodes.OpenAsync(originalUnitId, EpisodeKind.Ownership, cancellationToken);
        if (vendorOwnership is not { PartyRef: { } vendorPartyRef, PartyType: PartyType.Vendor })
            throw new CommandRefusedException("vendor_ownership_required", "The returned unit is not currently owned by a vendor.");
        var source = await db.Entitlements.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.EntitlementId == command.SourceEntitlementId, cancellationToken)
            ?? throw new CommandRefusedException("entitlement_unknown", $"Entitlement {command.SourceEntitlementId} does not exist.");
        if (source.UnitId != originalUnitId || source.Direction != EntitlementDirection.Inbound || source.Status != EntitlementStatus.Active)
            throw new CommandRefusedException("vendor_entitlement_invalid", "The source must be active inbound supplier cover on the returned unit.");
        if (source.ObligorEntityId != vendorPartyRef) throw new CommandRefusedException("vendor_entitlement_counterparty_mismatch", "The inbound entitlement is owed by a different vendor.");

        var current = await db.Current.AsNoTracking().Where(row => row.TenantId == tenantId && (row.UnitId == originalUnitId || row.UnitId == command.ReplacementUnitId))
            .Select(row => new { row.UnitId, row.ItemId }).ToListAsync(cancellationToken);
        var original = current.SingleOrDefault(row => row.UnitId == originalUnitId) ?? throw new CommandRefusedException("original_unit_unknown", "The returned unit does not exist.");
        var replacement = current.SingleOrDefault(row => row.UnitId == command.ReplacementUnitId) ?? throw new CommandRefusedException("replacement_unit_unknown", "The vendor replacement unit does not exist.");
        if (originalUnitId == command.ReplacementUnitId) throw new CommandRefusedException("replacement_is_original", "A replacement must be a different unit.");
        var grade = await conditions.LatestGradeAsync(command.ReplacementUnitId, cancellationToken);
        WarrantyReplacementRules.ValidateCondition(command.ReplacementReason, command.ReplacementConditionAllowed, grade, "XX", false, false);
        var createShape = new CreateWarrantyReplacement(command.CommandId, ReplacementSourceKind.Rma, data.rma.RmaId, source.EntitlementId,
            command.ReplacementUnitId, command.ReplacementReason, ReplacementPricePolicy.Zero, 0m, data.rma.Currency, data.rma.ServiceCountry,
            false, false, command.ReceivedAt, command.LegalEntityId, command.ActorRef, command.ReplacementConditionAllowed, command.ReplacementPolicyCode);
        var policy = await CreateWarrantyReplacementHandler.ResolveReplacementPolicyAsync(createShape, source, db, tenantId, cancellationToken);
        var documentId = TimeOrderedId.New(command.ReceivedAt);
        var linkId = TimeOrderedId.New(command.ReceivedAt);
        var number = await numbers.NextAsync(DocumentNumbers.WarrantyReplacement, command.ReceivedAt, cancellationToken);
        var document = ConvertLoanToReplacementHandler.NewDocument(tenantId, documentId, command.CommandId, number, ReplacementSourceKind.Rma,
            data.rma.RmaId, data.rma.RmaNumber!, command.LegalEntityId, data.rma.ServiceCountry, false, policy.Code, command.ReceivedAt, command.ActorRef);
        var link = ConvertLoanToReplacementHandler.NewLink(tenantId, linkId, documentId, ReplacementDirection.VendorFacing,
            command.ReplacementReason, ReplacementSourceKind.Rma, data.rma.RmaId, data.rma.RmaNumber!, originalUnitId,
            command.ReplacementUnitId, original.ItemId, replacement.ItemId, source, vendorPartyRef, command.ReplacementConditionAllowed,
            false, command.ReceivedAt, command.ActorRef, data.rma.Currency);
        db.WarrantyReplacements.Add(document);
        db.ReplacementLinks.Add(link);
        ConvertLoanToReplacementHandler.AddLine(db, tenantId, documentId, linkId, replacement.ItemId, data.rma.Currency, command.ReceivedAt);

        var docRef = new DocumentRef("warranty_replacement", documentId, 1);
        var custody = await episodes.PrepareCustodyMoveAsync(command.ReplacementUnitId,
            new CustodyTarget(LocationKind.Warehouse, command.ReceivingLocationId, Reason: EpisodeReason.Replacement, Source: EpisodeSource.Receipt),
            UnitEventType.CustodyChanged, IdempotencyKey.Derive(command.CommandId.ToString("N"), "vendor-receipt-custody"), command.ReceivedAt,
            command.ActorRef, command.LegalEntityId, docRef, new Dictionary<string, object?> { ["vendor_party_ref"] = vendorPartyRef, ["replacement_link_id"] = linkId }, null, true, cancellationToken);
        var ownership = await episodes.PrepareOwnershipOpenAsync(command.ReplacementUnitId,
            new OwnershipTarget(command.LegalEntityId, PartyType.Us, EpisodeReason.Replacement, EpisodeSource.Receipt),
            IdempotencyKey.Derive(command.CommandId.ToString("N"), "vendor-receipt-ownership"), command.ReceivedAt,
            command.ActorRef, command.LegalEntityId, docRef, EpisodeReason.Replacement, cancellationToken);
        var transfer = await TransferEntitlementHandler.HandleInboundAsync(new TransferEntitlement(command.CommandId, source.EntitlementId,
            command.ReplacementUnitId, linkId, command.ReplacementDate, command.ReceivedAt, command.ActorRef, policy.Code),
            db, entitlementLedger, tenant, clock, corrections, cancellationToken);
        var eventId = TimeOrderedId.New(command.ReceivedAt);
        var drafts = new List<UnitEventDraft> { custody.Draft };
        if (ownership.CloseDraft is not null) drafts.Add(ownership.CloseDraft);
        drafts.Add(ownership.Draft);
        drafts.Add(new UnitEventDraft
        {
            UnitId = command.ReplacementUnitId, EventTime = command.ReceivedAt, EventType = ReplacementEventType.VendorReceived, PresetEventId = eventId,
            LegalEntityId = command.LegalEntityId, ActorRef = command.ActorRef, DocumentType = docRef.Type, DocumentId = docRef.Id, DocumentLine = docRef.Line,
            IdempotencyKey = IdempotencyKey.ForCommand("serialisation", command.CommandId, command.ReplacementUnitId, ReplacementEventType.VendorReceived, command.ReceivedAt),
            Attributes = JsonObjectText.Of(new { warranty_replacement_id = documentId, replacement_link_id = linkId, rma_id = data.rma.RmaId,
                rma_line_id = command.RmaLineId, original_unit_id = originalUnitId, replacement_unit_id = command.ReplacementUnitId,
                vendor_party_ref = vendorPartyRef, source_entitlement_id = source.EntitlementId, target_entitlement_id = transfer.TargetEntitlementId,
                policy_applied = transfer.PolicyApplied, computed_end = transfer.ComputedEnd, replacement_condition = grade }),
        });
        await writer.AppendAsync(drafts, cancellationToken);
        ConvertLoanToReplacementHandler.Finalise(document, link, command.CommandId, command.ActorRef, clock.UtcNow, eventId, transfer, grade, ReplacementLinkState.ClosedReturned);
        return new(documentId, number, linkId, transfer.TargetEntitlementId, transfer.PolicyApplied, transfer.ComputedEnd, eventId, false);
    }

    private static async Task<ReceiveVendorReplacementResult> ReplayVendorAsync(SerialisationDbContext db, TenantId tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        var data = await (from document in db.WarrantyReplacements.AsNoTracking()
                          join link in db.ReplacementLinks.AsNoTracking() on new { document.TenantId, document.WarrantyReplacementId } equals new { link.TenantId, link.WarrantyReplacementId }
                          where document.TenantId == tenantId && document.WarrantyReplacementId == documentId
                          select new { document, link }).SingleAsync(cancellationToken);
        return new(documentId, data.document.ReplacementNumber, data.link.ReplacementLinkId, data.link.TargetEntitlementId!.Value,
            data.link.PolicyApplied!, data.link.ComputedEnd!.Value, data.link.ShippedEventId!.Value, true);
    }
}
