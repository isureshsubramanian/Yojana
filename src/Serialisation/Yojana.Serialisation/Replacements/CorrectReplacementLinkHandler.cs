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

namespace Yojana.Serialisation.Replacements;

public static class PreviewCorrectReplacementLinkHandler
{
    public static async Task<CorrectionPreview> Handle(PreviewCorrectReplacementLink command,
        SerialisationDbContext db, IUnitConditionQueryPort conditions, CorrectionApprovalSettings approvals,
        IJurisdictionRuleResolver jurisdictionRules, ITenantContext tenant, CancellationToken cancellationToken)
    {
        try
        {
            var plan = await ReplacementLinkCorrectionPlan.LoadAsync(command.RecordedReplacementUnitId,
                command.ActualReplacementUnitId, command.Reason, db, conditions, approvals, jurisdictionRules, tenant, cancellationToken);
            var metadata = command.Correction ?? new CorrectionMetadata(Guid.Empty, command.Reason, command.Note,
                InitiatingRole: CorrectionInitiatingRole.CustomerService);
            var refusal = ReplacementLinkCorrectionPlan.ApprovalRefusal(plan, command.CommittedBy, metadata);
            return ReplacementLinkCorrectionPlan.Preview(plan, refusal);
        }
        catch (CommandRefusedException ex)
        {
            return new(null, CorrectionCommand.CorrectReplacementLink, 0, 0, 0, 1, [], [], [], [], [], ex.Refusal);
        }
    }
}

public static class CorrectReplacementLinkHandler
{
    public static async Task<CorrectReplacementLinkResult> Handle(CorrectReplacementLink command,
        SerialisationDbContext db,
        IUnitConditionQueryPort conditions,
        ReplacementLinkCorrectionEntitlements entitlements,
        CorrectionLedger corrections,
        CorrectionApprovalSettings approvals,
        EpisodeService episodes,
        IUnitEventWriter writer,
        IStockUnitEventPort stock,
        IJurisdictionRuleResolver jurisdictionRules,
        ITenantContext tenant,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.ReplacementLinks.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId
            && row.CorrectionCommandId == command.CommandId, cancellationToken) is { } replay)
            return await ReplayAsync(command, replay, db, tenantId, cancellationToken);

        var supplied = command.Correction ?? new CorrectionMetadata(command.CommandId, command.Reason, command.Note,
            InitiatingRole: CorrectionInitiatingRole.CustomerService);
        var plan = await ReplacementLinkCorrectionPlan.LoadAsync(command.RecordedReplacementUnitId,
            command.ActualReplacementUnitId, command.Reason, db, conditions, approvals, jurisdictionRules, tenant, cancellationToken);
        if (ReplacementLinkCorrectionPlan.ApprovalRefusal(plan, command.CommittedBy, supplied) is { } approvalRefusal)
            throw new CommandRefusedException(approvalRefusal.Rule, approvalRefusal.Message, approvalRefusal.UnblockingStep);
        var metadata = await corrections.PrepareAsync(CorrectionCommand.CorrectReplacementLink, supplied,
            command.CommandId, command.Reason, command.Note, null, command.EventTime,
            CorrectionInitiatingRole.CustomerService, cancellationToken);

        var previous = await db.ReplacementLinks.SingleAsync(row => row.TenantId == tenantId
            && row.ReplacementLinkId == plan.PreviousLink.ReplacementLinkId, cancellationToken);
        var document = await db.WarrantyReplacements.SingleAsync(row => row.TenantId == tenantId
            && row.WarrantyReplacementId == previous.WarrantyReplacementId, cancellationToken);
        var correctedLinkId = TimeOrderedId.New(command.EventTime);
        var correctionDocument = new DocumentRef("warranty_replacement", document.WarrantyReplacementId, 1);

        await episodes.PreloadOpenAsync([plan.RecordedUnit.UnitId, plan.ActualUnit.UnitId], cancellationToken);
        var wrongCustody = await episodes.PrepareCustodyMoveAsync(plan.RecordedUnit.UnitId,
            new CustodyTarget(plan.RecordedPriorCustody.LocationKind!, plan.RecordedPriorCustody.LocationId,
                plan.RecordedPriorCustody.PartyRef, plan.RecordedPriorCustody.PartyType,
                EpisodeReason.DocumentCorrection, plan.RecordedPriorCustody.Source ?? EpisodeSource.Delivery,
                plan.RecordedPriorCustody.Confidence ?? Confidence.High),
            UnitEventType.CustodyChanged,
            IdempotencyKey.ForCommand("serialisation", command.CommandId, plan.RecordedUnit.UnitId,
                UnitEventType.CustodyChanged, plan.ShipmentEventTime),
            plan.ShipmentEventTime, command.ActorRef, command.LegalEntityId, correctionDocument,
            new Dictionary<string, object?> { ["corrected_replacement_link_id"] = correctedLinkId }, null, false,
            cancellationToken);
        var wrongOwnership = await episodes.PrepareOwnershipOpenAsync(plan.RecordedUnit.UnitId,
            new OwnershipTarget(document.LegalEntityId, PartyType.Us, EpisodeReason.DocumentCorrection,
                EpisodeSource.Delivery),
            IdempotencyKey.ForCommand("serialisation", command.CommandId, plan.RecordedUnit.UnitId,
                UnitEventType.OwnershipTransferred, plan.ShipmentEventTime),
            plan.ShipmentEventTime, command.ActorRef, command.LegalEntityId, correctionDocument,
            EpisodeReason.DocumentCorrection, cancellationToken);

        var actualCustody = await episodes.PrepareCustodyMoveAsync(plan.ActualUnit.UnitId,
            new CustodyTarget(plan.RecordedCustody.LocationKind!, plan.RecordedCustody.LocationId,
                plan.RecordedCustody.PartyRef, plan.RecordedCustody.PartyType,
                EpisodeReason.Replacement, plan.RecordedCustody.Source ?? EpisodeSource.Delivery,
                plan.RecordedCustody.Confidence ?? Confidence.High),
            UnitEventType.Shipped,
            IdempotencyKey.ForCommand("serialisation", command.CommandId, plan.ActualUnit.UnitId,
                UnitEventType.Shipped, plan.ShipmentEventTime),
            plan.ShipmentEventTime, command.ActorRef, command.LegalEntityId, correctionDocument,
            new Dictionary<string, object?> { ["corrected_replacement_link_id"] = correctedLinkId }, null, false,
            cancellationToken);
        var actualOwnership = await episodes.PrepareOwnershipOpenAsync(plan.ActualUnit.UnitId,
            new OwnershipTarget(plan.RecordedOwner.PartyRef!.Value, plan.RecordedOwner.PartyType!,
                EpisodeReason.Replacement, plan.RecordedOwner.Source ?? EpisodeSource.Delivery,
                plan.RecordedOwner.Confidence ?? Confidence.High, plan.RecordedOwner.CustomerPoRef,
                plan.RecordedOwner.CustomerItemRef, plan.RecordedOwner.PartyIsGroupMember,
                plan.RecordedOwner.ExternalOrderRef),
            IdempotencyKey.ForCommand("serialisation", command.CommandId, plan.ActualUnit.UnitId,
                UnitEventType.OwnershipTransferred, plan.ShipmentEventTime),
            plan.ShipmentEventTime, command.ActorRef, command.LegalEntityId, correctionDocument,
            EpisodeReason.DocumentCorrection, cancellationToken);

        var entitlementResult = await entitlements.ApplyAsync(command.CommandId, previous, correctedLinkId,
            plan.ActualUnit.UnitId, plan.ReplacementDate, command.EventTime, command.ActorRef, metadata,
            cancellationToken);

        var drafts = new List<UnitEventDraft>();
        AddEpisode(drafts, wrongCustody.Draft, metadata);
        AddEpisode(drafts, wrongOwnership.CloseDraft, metadata);
        AddEpisode(drafts, wrongOwnership.Draft, metadata);
        AddEpisode(drafts, actualCustody.Draft, metadata);
        AddEpisode(drafts, actualOwnership.CloseDraft, metadata);
        AddEpisode(drafts, actualOwnership.Draft, metadata);

        var tag = await RepointAssetTagAsync(command, correctedLinkId, document, plan, db, tenantId, clock.UtcNow,
            metadata, drafts, cancellationToken);
        var correctedEventId = TimeOrderedId.New(command.EventTime);
        drafts.Add(LinkCorrectedDraft(command, document, previous, correctedLinkId, entitlementResult,
            plan, tag is not null, metadata, correctedEventId));
        foreach (var unitId in new[] { plan.RecordedUnit.UnitId, plan.ActualUnit.UnitId })
            drafts.Add(CertificateDraft(command, document, correctedLinkId, unitId, metadata));
        if (tag is not null) drafts.Add(PrintDraft(command, document, correctedLinkId, tag, metadata));
        await writer.AppendAsync(drafts, cancellationToken);

        var corrected = CopyLink(previous, correctedLinkId, command.CommandId, plan, entitlementResult,
            command.ActorRef, clock.UtcNow, correctedEventId);
        db.ReplacementLinks.Add(corrected);
        previous.State = ReplacementLinkState.Corrected;
        previous.CorrectedByLinkId = correctedLinkId;
        await stock.RecordCorrectionForPosting(new(correctionDocument, previous.ReplacementItemId, 0,
            [plan.RecordedUnit.UnitId], [plan.ActualUnit.UnitId], metadata.CorrectionId), cancellationToken);
        await corrections.RecordAsync(CorrectionCommand.CorrectReplacementLink, metadata, command.ActorRef,
            command.EventTime, [plan.RecordedUnit.UnitId, plan.ActualUnit.UnitId], cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return Result(metadata.CorrectionId, previous, corrected, entitlementResult, tag is not null,
            correctedEventId, false);
    }

    private static void AddEpisode(List<UnitEventDraft> drafts, UnitEventDraft? draft, CorrectionMetadata metadata)
    {
        if (draft is not null) drafts.Add(draft with { Attributes = CorrectionLedger.Attributes(metadata, draft.Attributes!) });
    }

    private static async Task<UnitIdentifier?> RepointAssetTagAsync(CorrectReplacementLink command,
        Guid correctedLinkId, WarrantyReplacement document, ReplacementLinkCorrectionPlan.Plan plan,
        SerialisationDbContext db, TenantId tenantId, DateTime recordedAt, CorrectionMetadata metadata,
        List<UnitEventDraft> drafts, CancellationToken cancellationToken)
    {
        var old = await db.Identifiers.FirstOrDefaultAsync(row => row.TenantId == tenantId
            && row.UnitId == plan.RecordedUnit.UnitId && row.Type == IdentifierType.CustomerAssetTag
            && row.ValidTo == null, cancellationToken);
        if (old is null) return null;
        var eventId = TimeOrderedId.New(command.EventTime);
        var replacement = new UnitIdentifier
        {
            TenantId = tenantId,
            IdentifierId = TimeOrderedId.New(command.EventTime),
            UnitId = plan.ActualUnit.UnitId,
            Type = old.Type,
            Scheme = old.Scheme,
            IssuerPartyRef = old.IssuerPartyRef,
            EnterpriseIdentifier = old.EnterpriseIdentifier,
            Qualifier = old.Qualifier,
            RawValue = old.RawValue,
            NormalisedValue = old.NormalisedValue,
            ScopeKey = old.ScopeKey,
            ValidFrom = plan.ShipmentEventTime,
            SupersedesIdentifierId = old.IdentifierId,
            CorrectionReason = command.Reason,
            ActorRef = command.ActorRef,
            RecordedTime = recordedAt,
            EventId = eventId,
        };
        old.ValidTo = plan.ShipmentEventTime;
        old.SupersededByIdentifierId = replacement.IdentifierId;
        old.CorrectionReason = command.Reason;
        await db.SaveChangesAsync(cancellationToken);
        db.Identifiers.Add(replacement);
        drafts.Add(new UnitEventDraft
        {
            UnitId = plan.RecordedUnit.UnitId,
            EventTime = command.EventTime,
            EventType = ReplacementEventType.AssetTagRepointed,
            LegalEntityId = command.LegalEntityId,
            ActorRef = command.ActorRef,
            DocumentType = "warranty_replacement",
            DocumentId = document.WarrantyReplacementId,
            IdempotencyKey = IdempotencyKey.ForCommand("serialisation", command.CommandId,
                plan.RecordedUnit.UnitId, ReplacementEventType.AssetTagRepointed, command.EventTime),
            Attributes = CorrectionLedger.Attributes(metadata, new
            {
                previous_replacement_link_id = plan.PreviousLink.ReplacementLinkId,
                corrected_replacement_link_id = correctedLinkId,
                replacement_unit_id = plan.ActualUnit.UnitId,
                identifier_id = old.IdentifierId,
                replacement_identifier_id = replacement.IdentifierId,
            }),
        });
        return replacement;
    }

    private static UnitEventDraft LinkCorrectedDraft(CorrectReplacementLink command,
        WarrantyReplacement document, ReplacementLink previous, Guid correctedLinkId,
        ReplacementLinkCorrectionEntitlements.Result entitlements, ReplacementLinkCorrectionPlan.Plan plan,
        bool tag, CorrectionMetadata metadata, Guid eventId) => new()
    {
        UnitId = plan.ActualUnit.UnitId,
        EventTime = command.EventTime,
        EventType = ReplacementEventType.LinkCorrected,
        PresetEventId = eventId,
        LegalEntityId = command.LegalEntityId,
        ActorRef = command.ActorRef,
        DocumentType = "warranty_replacement",
        DocumentId = document.WarrantyReplacementId,
        DocumentLine = 1,
        IdempotencyKey = IdempotencyKey.ForCommand("serialisation", command.CommandId,
            plan.ActualUnit.UnitId, ReplacementEventType.LinkCorrected, command.EventTime),
        Attributes = CorrectionLedger.Attributes(metadata, new
        {
            warranty_replacement_id = document.WarrantyReplacementId,
            previous_replacement_link_id = previous.ReplacementLinkId,
            corrected_replacement_link_id = correctedLinkId,
            original_unit_id = previous.OriginalUnitId,
            recorded_replacement_unit_id = previous.ReplacementUnitId,
            actual_replacement_unit_id = plan.ActualUnit.UnitId,
            voided_entitlement_id = entitlements.VoidedEntitlementId,
            target_entitlement_id = entitlements.Transfer.TargetEntitlementId,
            replacement_reason = previous.ReplacementReason,
            policy_applied = entitlements.Transfer.PolicyApplied,
            computed_end = entitlements.Transfer.ComputedEnd,
            asset_tag_repointed = tag,
        }),
    };

    private static UnitEventDraft CertificateDraft(CorrectReplacementLink command,
        WarrantyReplacement document, Guid correctedLinkId, Guid unitId, CorrectionMetadata metadata) => new()
    {
        UnitId = unitId,
        EventTime = command.EventTime,
        EventType = ReplacementEventType.CertificateReissueRequested,
        LegalEntityId = command.LegalEntityId,
        ActorRef = command.ActorRef,
        DocumentType = "warranty_replacement",
        DocumentId = document.WarrantyReplacementId,
        DocumentLine = 1,
        IdempotencyKey = IdempotencyKey.ForCommand("serialisation", command.CommandId, unitId,
            ReplacementEventType.CertificateReissueRequested, command.EventTime),
        Attributes = CorrectionLedger.Attributes(metadata, new
        {
            replacement_link_id = correctedLinkId,
            document_kinds = ReplacementCertificateDocumentKind.All,
            reason = "replacement_link_corrected",
        }),
    };

    private static UnitEventDraft PrintDraft(CorrectReplacementLink command, WarrantyReplacement document,
        Guid correctedLinkId, UnitIdentifier tag, CorrectionMetadata metadata) => new()
    {
        UnitId = tag.UnitId,
        EventTime = command.EventTime,
        EventType = ReplacementEventType.AssetTagPrintRequested,
        LegalEntityId = command.LegalEntityId,
        ActorRef = command.ActorRef,
        DocumentType = "warranty_replacement",
        DocumentId = document.WarrantyReplacementId,
        DocumentLine = 1,
        IdempotencyKey = IdempotencyKey.ForCommand("serialisation", command.CommandId, tag.UnitId,
            ReplacementEventType.AssetTagPrintRequested, command.EventTime),
        Attributes = CorrectionLedger.Attributes(metadata, new
        {
            warranty_replacement_id = document.WarrantyReplacementId,
            replacement_link_id = correctedLinkId,
            identifier_id = tag.IdentifierId,
            asset_tag = tag.NormalisedValue,
        }),
    };

    private static ReplacementLink CopyLink(ReplacementLink previous, Guid correctedLinkId, Guid commandId,
        ReplacementLinkCorrectionPlan.Plan plan, ReplacementLinkCorrectionEntitlements.Result entitlements,
        string actorRef, DateTime recordedAt, Guid eventId) => new()
    {
        TenantId = previous.TenantId,
        ReplacementLinkId = correctedLinkId,
        WarrantyReplacementId = previous.WarrantyReplacementId,
        PreviousReplacementLinkId = previous.ReplacementLinkId,
        CorrectionCommandId = commandId,
        Direction = previous.Direction,
        ReplacementReason = previous.ReplacementReason,
        State = previous.State,
        SourceKind = previous.SourceKind,
        SourceId = previous.SourceId,
        SourceNumber = previous.SourceNumber,
        OriginalUnitId = previous.OriginalUnitId,
        ReplacementUnitId = plan.ActualUnit.UnitId,
        OriginalItemId = previous.OriginalItemId,
        ReplacementItemId = plan.ActualUnit.ItemId,
        SourceEntitlementId = entitlements.RestoredSourceEntitlementId,
        TargetEntitlementId = entitlements.Transfer.TargetEntitlementId,
        PolicyApplied = entitlements.Transfer.PolicyApplied,
        ComputedEnd = entitlements.Transfer.ComputedEnd,
        PricePolicy = previous.PricePolicy,
        DifferenceAmount = previous.DifferenceAmount,
        Currency = previous.Currency,
        ReturnDue = previous.ReturnDue,
        NonReturnPriceBasis = previous.NonReturnPriceBasis,
        ReplacementConditionAllowed = previous.ReplacementConditionAllowed,
        ReplacementCondition = plan.ActualCondition,
        ConsentRefurbished = previous.ConsentRefurbished,
        CreatedAt = recordedAt,
        CreatedBy = actorRef,
        ShippedAt = recordedAt,
        ShippedBy = actorRef,
        ShippedEventId = eventId,
    };

    private static async Task<CorrectReplacementLinkResult> ReplayAsync(CorrectReplacementLink command,
        ReplacementLink corrected, SerialisationDbContext db, TenantId tenantId, CancellationToken cancellationToken)
    {
        var previous = await db.ReplacementLinks.AsNoTracking().SingleAsync(row => row.TenantId == tenantId
            && row.CorrectedByLinkId == corrected.ReplacementLinkId, cancellationToken);
        var correction = await db.Corrections.AsNoTracking().SingleAsync(row => row.TenantId == tenantId
            && row.CorrectionId == (command.Correction == null ? command.CommandId : command.Correction.CorrectionId),
            cancellationToken);
        if (previous.ReplacementUnitId != command.RecordedReplacementUnitId
            || corrected.ReplacementUnitId != command.ActualReplacementUnitId)
            throw new CommandRefusedException("command_id_reused",
                "This command id already corrected a different replacement pair.", "send a new command id");
        if (previous.TargetEntitlementId is null || corrected.SourceEntitlementId is null || corrected.TargetEntitlementId is null
            || corrected.PolicyApplied is null || corrected.ComputedEnd is null || corrected.ShippedEventId is null)
            throw new CommandRefusedException("replacement_correction_replay_inconsistent",
                "The corrected replacement link is missing its recorded result.");
        var tag = await db.Identifiers.AsNoTracking().AnyAsync(row => row.TenantId == tenantId
            && row.UnitId == corrected.ReplacementUnitId && row.Type == IdentifierType.CustomerAssetTag
            && row.ValidTo == null && row.CorrectionReason == command.Reason, cancellationToken);
        var entitlementResult = new ReplacementLinkCorrectionEntitlements.Result(previous.TargetEntitlementId.Value,
            corrected.SourceEntitlementId.Value,
            new(corrected.SourceEntitlementId.Value, corrected.TargetEntitlementId.Value, Guid.Empty,
                corrected.PolicyApplied, corrected.ComputedEnd.Value, 0, corrected.ShippedEventId.Value, true), Guid.Empty);
        return Result(correction.CorrectionId, previous, corrected, entitlementResult, tag,
            corrected.ShippedEventId.Value, true);
    }

    private static CorrectReplacementLinkResult Result(Guid correctionId, ReplacementLink previous,
        ReplacementLink corrected, ReplacementLinkCorrectionEntitlements.Result entitlements, bool tag,
        Guid eventId, bool replayed) => new(correctionId, previous.ReplacementLinkId, corrected.ReplacementLinkId,
        previous.ReplacementUnitId, corrected.ReplacementUnitId, entitlements.VoidedEntitlementId,
        entitlements.RestoredSourceEntitlementId, entitlements.Transfer.TargetEntitlementId,
        corrected.ReplacementReason, entitlements.Transfer.PolicyApplied, entitlements.Transfer.ComputedEnd,
        tag, eventId, replayed);
}

internal static class ReplacementLinkCorrectionPlan
{
    internal sealed record UnitFact(Guid UnitId, Guid ItemId, string? Serial, string? LocationKind,
        Guid? OwnerPartyRef);

    internal sealed record Plan(ReplacementLink PreviousLink, WarrantyReplacement Document,
        UnitFact RecordedUnit, UnitFact ActualUnit, CustodyEpisode RecordedOwner, CustodyEpisode RecordedCustody,
        CustodyEpisode RecordedPriorCustody, DateTime ShipmentEventTime, DateOnly ReplacementDate,
        string ActualCondition, bool HasAssetTag, CorrectionApprovalPolicy ApprovalPolicy);

    internal static async Task<Plan> LoadAsync(Guid recordedUnitId, Guid actualUnitId, string reason,
        SerialisationDbContext db, IUnitConditionQueryPort conditions, CorrectionApprovalSettings approvals,
        IJurisdictionRuleResolver jurisdictionRules, ITenantContext tenant, CancellationToken cancellationToken)
    {
        CorrectionRules.ValidateReason(CorrectionCommand.CorrectReplacementLink, reason);
        if (recordedUnitId == actualUnitId)
            throw new CommandRefusedException("replacement_unit_unchanged",
                "The recorded and actual replacement units are the same.", "scan the physical replacement unit");
        var tenantId = tenant.TenantId;
        var links = await db.ReplacementLinks.AsNoTracking().Where(row => row.TenantId == tenantId
                && row.ReplacementUnitId == recordedUnitId && row.State != ReplacementLinkState.Corrected
                && row.State != ReplacementLinkState.Cancelled && row.ShippedEventId != null)
            .OrderByDescending(row => row.CreatedAt).Take(2).ToListAsync(cancellationToken);
        if (links.Count == 0)
            throw new CommandRefusedException("replacement_link_unknown",
                "The recorded unit has no shipped replacement link that can be corrected.");
        if (links.Count > 1)
            throw new CommandRefusedException("replacement_link_ambiguous",
                "The recorded unit belongs to more than one correctable replacement link.",
                "open the replacement document and correct its named link");
        var previous = links[0];
        if (actualUnitId == previous.OriginalUnitId)
            throw new CommandRefusedException("replacement_is_original",
                "The actual replacement unit cannot be the unit that was replaced.");
        if (await db.ReplacementLinks.AsNoTracking().AnyAsync(row => row.TenantId == tenantId
            && row.ReplacementUnitId == actualUnitId && row.State != ReplacementLinkState.Corrected
            && row.State != ReplacementLinkState.Cancelled, cancellationToken))
            throw new CommandRefusedException("actual_replacement_linked",
                "The actual unit already belongs to another replacement link.");

        var document = await db.WarrantyReplacements.AsNoTracking().SingleAsync(row => row.TenantId == tenantId
            && row.WarrantyReplacementId == previous.WarrantyReplacementId, cancellationToken);
        var units = await db.Current.AsNoTracking().Where(row => row.TenantId == tenantId
                && (row.UnitId == recordedUnitId || row.UnitId == actualUnitId))
            .Select(row => new UnitFact(row.UnitId, row.ItemId, null, row.LocationKind, row.OwnerPartyRef))
            .ToListAsync(cancellationToken);
        if (units.Count != 2) throw new CommandRefusedException("replacement_unit_unknown",
            "The recorded or actual replacement unit does not exist.");
        var identifiers = await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenantId
                && (row.UnitId == recordedUnitId || row.UnitId == actualUnitId)
                && row.ValidTo == null && row.Type == IdentifierType.InternalSerial)
            .Select(row => new { row.UnitId, row.RawValue }).ToListAsync(cancellationToken);
        units = units.Select(unit => unit with
        {
            Serial = identifiers.FirstOrDefault(identifier => identifier.UnitId == unit.UnitId)?.RawValue,
        }).ToList();
        var recorded = units.Single(unit => unit.UnitId == recordedUnitId);
        var actual = units.Single(unit => unit.UnitId == actualUnitId);
        if (actual.ItemId != previous.ReplacementItemId)
            throw new CommandRefusedException("actual_replacement_item_mismatch",
                "The actual unit is not the item posted on the replacement document.",
                "correct the replacement document before correcting its serial link");
        var actualOwner = await db.Episodes.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId
            && row.UnitId == actualUnitId && row.Kind == EpisodeKind.Ownership && row.ValidTo == null,
            cancellationToken);
        if (actual.LocationKind is not (LocationKind.Warehouse or LocationKind.Bin)
            || actualOwner is not null && actualOwner.PartyType != PartyType.Us)
            throw new CommandRefusedException("actual_replacement_not_company_stock",
                "The actual replacement unit is not recorded as unowned company stock in a warehouse or bin.",
                "correct its current custody or ownership before correcting the replacement link");

        var episodes = await db.Episodes.AsNoTracking().Where(row => row.TenantId == tenantId
            && row.UnitId == recordedUnitId).ToListAsync(cancellationToken);
        var owner = episodes.SingleOrDefault(row => row.Kind == EpisodeKind.Ownership && row.ValidTo == null)
            ?? throw new CommandRefusedException("recorded_replacement_owner_missing",
                "The recorded replacement unit has no current customer ownership episode.");
        if (owner.PartyRef is null || owner.PartyType == PartyType.Us)
            throw new CommandRefusedException("recorded_replacement_not_customer_owned",
                "The recorded replacement unit is not currently owned by the replacement customer.");
        var custody = episodes.SingleOrDefault(row => row.Kind == EpisodeKind.Custody && row.ValidTo == null)
            ?? throw new CommandRefusedException("recorded_replacement_custody_missing",
                "The recorded replacement unit has no current custody episode.");
        if (custody.LocationKind != LocationKind.CustomerSite)
            throw new CommandRefusedException("recorded_replacement_not_at_customer",
                "The recorded replacement unit is not currently recorded at the customer site.");
        var priorCustody = episodes.Where(row => row.Kind == EpisodeKind.Custody && row.ValidTo == custody.ValidFrom
                && row.LocationKind is LocationKind.Warehouse or LocationKind.Bin)
            .OrderByDescending(row => row.ValidFrom).FirstOrDefault()
            ?? throw new CommandRefusedException("recorded_replacement_prior_stock_missing",
                "The recorded replacement unit has no prior warehouse or bin custody to restore.");
        if (await db.Identifiers.AsNoTracking().AnyAsync(row => row.TenantId == tenantId
            && row.UnitId == actualUnitId && row.Type == IdentifierType.CustomerAssetTag && row.ValidTo == null,
            cancellationToken))
            throw new CommandRefusedException("actual_replacement_asset_tag_occupied",
                "The actual replacement unit already has a customer asset tag.");

        var shipment = await db.Events.AsNoTracking().SingleAsync(row => row.TenantId == tenantId
            && row.UnitId == recordedUnitId && row.EventId == previous.ShippedEventId!.Value, cancellationToken);
        var sourceEntitlement = await db.Entitlements.AsNoTracking().SingleAsync(row => row.TenantId == tenantId
            && row.EntitlementId == previous.SourceEntitlementId, cancellationToken);
        var replacementDate = sourceEntitlement.ClosedEffective ?? DateOnly.FromDateTime(shipment.EventTime);
        var condition = await conditions.LatestGradeAsync(actualUnitId, cancellationToken);
        WarrantyReplacementRules.ValidateCondition(previous.ReplacementReason, previous.ReplacementConditionAllowed, condition);
        if (document.ConsumerContract && ConditionGrades.IsRefurbished(condition) && !previous.ConsentRefurbished
            && await WarrantyReplacementRules.RequiresConsentAsync(jurisdictionRules, document.ServiceCountry,
                sourceEntitlement.StartDate ?? replacementDate, cancellationToken))
            throw new CommandRefusedException("refurbished_consent_required", "The applicable jurisdiction rule requires recorded consent before refurbished stock can ship.");
        var hasAssetTag = await db.Identifiers.AsNoTracking().AnyAsync(row => row.TenantId == tenantId
            && row.UnitId == recordedUnitId && row.Type == IdentifierType.CustomerAssetTag && row.ValidTo == null,
            cancellationToken);
        return new(previous, document, recorded, actual, owner, custody, priorCustody, shipment.EventTime,
            replacementDate, condition, hasAssetTag, await approvals.GetAsync(cancellationToken));
    }

    internal static CommandRefusal? ApprovalRefusal(Plan plan, CorrectionCommitter? committer,
        CorrectionMetadata metadata)
    {
        if (committer is null || string.IsNullOrWhiteSpace(committer.UserRef))
            return new("committer_required", "A replacement-link correction committer is required.");
        var facts = new CorrectionApprovalFacts(UnitHasShipped: true, UnitAppearsOnExternalDocument: true,
            OwningPartyChanges: true, UnitSold: true, UnitHasLiveEntitlement: true);
        if (CorrectionApprovalRules.ApprovalsSatisfied(plan.ApprovalPolicy,
            CorrectionCommand.CorrectReplacementLink, facts, committer, metadata.Approvers ?? [])) return null;
        return CorrectionApprovalRules.Evaluate(plan.ApprovalPolicy, CorrectionCommand.CorrectReplacementLink,
            facts, committer.Roles).Refusal
            ?? new CommandRefusal("approval_required", "The replacement-link correction needs service-manager approval.",
                "route the identical preview to a service manager");
    }

    internal static CorrectionPreview Preview(Plan plan, CommandRefusal? refusal)
    {
        var units = new[]
        {
            new UnitOutcome(plan.RecordedUnit.UnitId, plan.RecordedUnit.Serial, UnitOutcome.Outcomes.Change,
                null, "restore company ownership and prior stock custody"),
            new UnitOutcome(plan.ActualUnit.UnitId, plan.ActualUnit.Serial, UnitOutcome.Outcomes.Change,
                null, "record customer ownership and customer-site custody"),
        };
        var events = new[]
        {
            new CorrectionEventPreview(plan.RecordedUnit.UnitId, UnitEventType.CustodyChanged,
                plan.ShipmentEventTime, "restore the prior stock location"),
            new CorrectionEventPreview(plan.RecordedUnit.UnitId, UnitEventType.OwnershipTransferred,
                plan.ShipmentEventTime, "restore company ownership"),
            new CorrectionEventPreview(plan.ActualUnit.UnitId, UnitEventType.Shipped,
                plan.ShipmentEventTime, "record the unit physically supplied"),
            new CorrectionEventPreview(plan.ActualUnit.UnitId, UnitEventType.OwnershipTransferred,
                plan.ShipmentEventTime, "record the replacement customer"),
            new CorrectionEventPreview(plan.ActualUnit.UnitId, ReplacementEventType.LinkCorrected,
                plan.ShipmentEventTime, "retain the old link as corrected and point it to a new link"),
            new CorrectionEventPreview(plan.RecordedUnit.UnitId, ReplacementEventType.CertificateReissueRequested,
                plan.ShipmentEventTime, "release record and warranty certificate"),
            new CorrectionEventPreview(plan.ActualUnit.UnitId, ReplacementEventType.CertificateReissueRequested,
                plan.ShipmentEventTime, "release record and warranty certificate"),
        };
        var entitlements = new[]
        {
            new CorrectionEntitlementPreview(plan.RecordedUnit.UnitId, "reverse_transfer",
                plan.PreviousLink.TargetEntitlementId, $"void {EntitlementClosedReason.VoidedDocumentCorrection}"),
            new CorrectionEntitlementPreview(plan.ActualUnit.UnitId, "transfer", null,
                $"{plan.PreviousLink.PolicyApplied} · {plan.PreviousLink.ReplacementReason} · end {plan.PreviousLink.ComputedEnd:yyyy-MM-dd}"),
        };
        var certificates = units.Select(unit => new CorrectionCertificatePreview(unit.UnitId,
            "release_record_and_warranty_certificate", "reissue", "replacement_link_corrected")).ToList();
        var print = plan.HasAssetTag
            ? new[] { new CorrectionPrintJobPreview(plan.ActualUnit.UnitId, "customer_asset_tag", "replacement_link_corrected") }
            : [];
        return new(null, CorrectionCommand.CorrectReplacementLink, units.Length, units.Length, 0,
            refusal is null ? 0 : units.Length, units, events, entitlements, certificates, print, refusal);
    }
}

