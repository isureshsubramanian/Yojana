using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Documents;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Corrections;

/// <summary>
/// Case 141: replaces only the serial side of a posted delivery, transfer or receipt. Planning uses fixed-count,
/// set-based reads; execution preloads episodes once, so a 500-serial correction never falls into per-unit queries.
/// </summary>
public static class CorrectDocumentSerialsHandler
{
    private sealed record PostingDocument(DateTime PostedAt, IReadOnlyList<DocumentLine> Lines, bool Invoiced,
        CustodyTarget? SourceFallback, Guid? CustomerPartyRef = null);

    private sealed record Pair(Guid RecordedUnitId, Guid PhysicalUnitId, Guid ItemId, string? RecordedSerial, string? PhysicalSerial,
        CustodyEpisode RecordedCustody, CustodyEpisode? SourceCustody, UnitEvent OriginalEvent, CustodyEpisode? RecordedOwnership);

    private sealed record Plan(PostingDocument Posting, IReadOnlyList<Pair> Pairs, bool WholeListReplacement,
        IReadOnlyDictionary<Guid, Guid> PhysicalByRecorded, CorrectionApprovalDecision Approval);

    public static async Task<CorrectDocumentSerialsResult> Handle(CorrectDocumentSerials command, DocumentContext documents,
        DocumentCorrectionEntitlements entitlements, CorrectionLedger corrections, CorrectionApprovalSettings approvalSettings,
        IStockUnitEventPort stock, IMessageBus bus, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var metadataInput = command.Correction ?? new CorrectionMetadata(command.CommandId, command.Reason,
            InitiatingRole: CorrectionInitiatingRole.WarehouseLead);
        var existing = await documents.Db.Corrections.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenant.TenantId
            && c.CorrectionId == metadataInput.CorrectionId, cancellationToken);
        if (existing is not null)
        {
            if (existing.Command != CorrectionCommand.CorrectDocumentSerials)
            {
                throw new CommandRefusedException("correction_id_in_use", $"Correction id {existing.CorrectionId} has already been used.",
                    "send a new correction id, or replay the original command idempotently");
            }

            var correctedCount = existing.UnitCount / 2;
            return new(existing.CorrectionId, command.Document, correctedCount, correctedCount, 0, 0, false, false, true);
        }

        var plan = await PlanAsync(command.Document, command.RecordedSelectionId, command.PhysicalSelectionId, command.Reason,
            command.CommittedBy, documents, approvalSettings, cancellationToken);
        EnsureApproval(plan.Approval, command.CommittedBy, metadataInput);
        var metadata = await corrections.PrepareAsync(CorrectionCommand.CorrectDocumentSerials, metadataInput, command.CommandId,
            command.Reason, metadataInput.Note, null, command.EventTime, CorrectionInitiatingRole.WarehouseLead, cancellationToken);

        return await documents.InTransactionAsync(async () =>
        {
            await documents.Episodes.PreloadOpenAsync(plan.Pairs.SelectMany(p => new[] { p.RecordedUnitId, p.PhysicalUnitId }).Distinct().ToList(), cancellationToken);
            var drafts = new List<UnitEventDraft>(plan.Pairs.Count * 4);
            foreach (var pair in plan.Pairs)
            {
                var postingDocument = command.Document.Line is null && pair.OriginalEvent.DocumentLine is { } line
                    ? command.Document with { Line = line }
                    : command.Document;
                var source = pair.SourceCustody is { } previous
                    ? new CustodyTarget(previous.LocationKind!, previous.LocationId, previous.PartyRef, previous.PartyType,
                        EpisodeReason.DocumentCorrection, previous.Source ?? EpisodeSource.Delivery, previous.Confidence ?? Confidence.High)
                    : plan.Posting.SourceFallback!;
                var reverseKey = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, pair.RecordedUnitId, UnitEventType.ShipmentReversed, plan.Posting.PostedAt);
                var reversed = await documents.Episodes.PrepareCustodyMoveAsync(pair.RecordedUnitId, source, UnitEventType.ShipmentReversed,
                    reverseKey, plan.Posting.PostedAt, command.ActorRef, command.LegalEntityId, postingDocument,
                    new Dictionary<string, object?> { ["correction_of_document_id"] = command.Document.Id, ["recorded_unit_id"] = pair.RecordedUnitId,
                        ["physical_unit_id"] = pair.PhysicalUnitId }, null, false, cancellationToken);
                drafts.Add(reversed.Draft with { Attributes = CorrectionLedger.Attributes(metadata, reversed.Draft.Attributes!), ReversesEventId = pair.OriginalEvent.EventId });

                if (pair.RecordedOwnership is not null)
                {
                    var closeKey = IdempotencyKey.Derive(reverseKey, "ownership-close");
                    var closed = await documents.Episodes.PrepareOwnershipCloseAsync(pair.RecordedUnitId, EpisodeReason.DocumentCorrection,
                        closeKey, plan.Posting.PostedAt, command.ActorRef, command.LegalEntityId, postingDocument, cancellationToken);
                    drafts.Add(closed.Draft with { Attributes = CorrectionLedger.Attributes(metadata, closed.Draft.Attributes!) });
                }

                var target = new CustodyTarget(pair.RecordedCustody.LocationKind!, pair.RecordedCustody.LocationId,
                    pair.RecordedCustody.PartyRef, pair.RecordedCustody.PartyType, pair.RecordedCustody.Reason ?? EpisodeReason.DocumentCorrection,
                    pair.RecordedCustody.Source ?? EpisodeSource.Delivery, pair.RecordedCustody.Confidence ?? Confidence.High,
                    pair.RecordedCustody.DueBack, pair.RecordedCustody.ExternalOrderRef);
                var shipKey = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, pair.PhysicalUnitId, UnitEventType.Shipped, plan.Posting.PostedAt);
                var shipped = await documents.Episodes.PrepareCustodyMoveAsync(pair.PhysicalUnitId, target, UnitEventType.Shipped,
                    shipKey, plan.Posting.PostedAt, command.ActorRef, command.LegalEntityId, postingDocument,
                    new Dictionary<string, object?> { ["correction_of_document_id"] = command.Document.Id, ["recorded_unit_id"] = pair.RecordedUnitId,
                        ["physical_unit_id"] = pair.PhysicalUnitId }, null, false, cancellationToken);
                drafts.Add(shipped.Draft with { Attributes = CorrectionLedger.Attributes(metadata, shipped.Draft.Attributes!) });

                if (pair.RecordedOwnership is { PartyRef: { } owner, PartyType: { } ownerType } ownership)
                {
                    var opened = await documents.Episodes.PrepareOwnershipOpenAsync(pair.PhysicalUnitId,
                        new OwnershipTarget(owner, ownerType, ownership.Reason ?? EpisodeReason.Sale,
                            ownership.Source ?? EpisodeSource.Delivery, ownership.Confidence ?? Confidence.High,
                            ownership.CustomerPoRef, ownership.CustomerItemRef, ownership.PartyIsGroupMember, ownership.ExternalOrderRef),
                        IdempotencyKey.Derive(shipKey, "ownership-open"), plan.Posting.PostedAt, command.ActorRef,
                        command.LegalEntityId, postingDocument, EpisodeReason.DocumentCorrection, cancellationToken);
                    if (opened.CloseDraft is not null)
                    {
                        drafts.Add(opened.CloseDraft with { Attributes = CorrectionLedger.Attributes(metadata, opened.CloseDraft.Attributes!) });
                    }
                    drafts.Add(opened.Draft with { Attributes = CorrectionLedger.Attributes(metadata, opened.Draft.Attributes!) });
                }
            }

            await documents.Writer.AppendAsync(drafts, cancellationToken);
            var entitlementResult = await entitlements.ApplyAsync(command.CommandId, command.Document, plan.PhysicalByRecorded,
                plan.Posting.PostedAt, command.ActorRef, command.LegalEntityId, metadata, cancellationToken);
            foreach (var item in plan.Pairs.GroupBy(p => p.ItemId))
            {
                await stock.RecordCorrectionForPosting(new(command.Document, item.Key, 0,
                    item.Select(p => p.RecordedUnitId).ToList(), item.Select(p => p.PhysicalUnitId).ToList(), metadata.CorrectionId), cancellationToken);
            }

            await corrections.RecordAsync(CorrectionCommand.CorrectDocumentSerials, metadata, command.ActorRef, command.EventTime,
                plan.Pairs.SelectMany(p => new[] { p.RecordedUnitId, p.PhysicalUnitId }).ToList(), cancellationToken);
            var finance = plan.Approval.ReviewTasks.Count > 0;
            var publishedCorrection = new PublishedCorrection(metadata.CorrectionId, metadata.ReasonCode, metadata.InitiatedVia,
                metadata.CorrectsCorrectionId, (metadata.Approvers ?? []).Select(a => a.UserRef).ToList());
            await bus.PublishAsync(new DocumentSerialsCorrected(
                new(DocumentSerialsCorrected.Schema, DocumentSerialsCorrected.Version, TimeOrderedId.New(command.EventTime),
                    command.EventTime, clock.UtcNow, command.LegalEntityId, command.ActorRef, command.Document),
                publishedCorrection, command.Document, plan.Pairs.Select(p => p.RecordedUnitId).ToList(),
                plan.Pairs.Select(p => p.PhysicalUnitId).ToList(), plan.WholeListReplacement, finance),
                new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
            return new CorrectDocumentSerialsResult(metadata.CorrectionId, command.Document, plan.Pairs.Count, plan.Pairs.Count,
                entitlementResult.Voided, entitlementResult.Created, plan.WholeListReplacement, finance);
        }, cancellationToken);
    }

    public static async Task<CorrectionPreview> Handle(PreviewCorrectDocumentSerials command, DocumentContext documents,
        DocumentCorrectionEntitlements entitlements, CorrectionApprovalSettings approvalSettings, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = command.Correction ?? new CorrectionMetadata(Guid.Empty, command.Reason, InitiatingRole: CorrectionInitiatingRole.WarehouseLead);
            var plan = await PlanAsync(command.Document, command.RecordedSelectionId, command.PhysicalSelectionId, command.Reason,
                command.CommittedBy, documents, approvalSettings, cancellationToken);
            var entitlementPreview = await entitlements.PreviewAsync(command.Document, plan.PhysicalByRecorded, cancellationToken);
            var refusal = ApprovalRefusal(plan.Approval, command.CommittedBy, metadata);
            var units = plan.Pairs.SelectMany(p => new[]
            {
                new UnitOutcome(p.RecordedUnitId, p.RecordedSerial, UnitOutcome.Outcomes.Change, null, "reverse the recorded shipment and return custody to its source"),
                new UnitOutcome(p.PhysicalUnitId, p.PhysicalSerial, UnitOutcome.Outcomes.Change, null, "ship the physical unit at the original document time"),
            }).ToList();
            var events = plan.Pairs.SelectMany(p => new[]
            {
                new CorrectionEventPreview(p.RecordedUnitId, UnitEventType.ShipmentReversed, plan.Posting.PostedAt, "custody restored; ownership closed where the document opened it"),
                new CorrectionEventPreview(p.PhysicalUnitId, UnitEventType.Shipped, plan.Posting.PostedAt, "corrective shipment"),
            }).ToList();
            return new(command.RecordedSelectionId, CorrectionCommand.CorrectDocumentSerials, units.Count,
                units.Count, 0, refusal is null ? 0 : units.Count, units, events, entitlementPreview, [], [], refusal);
        }
        catch (CommandRefusedException ex)
        {
            return new(command.RecordedSelectionId, CorrectionCommand.CorrectDocumentSerials, 0, 0, 0, 1, [], [], [], [], [], ex.Refusal);
        }
    }

    private static async Task<Plan> PlanAsync(DocumentRef document, Guid recordedSelectionId, Guid physicalSelectionId,
        string reason, CorrectionCommitter? committer, DocumentContext ctx,
        CorrectionApprovalSettings approvalSettings, CancellationToken cancellationToken)
    {
        CorrectionRules.ValidateReason(CorrectionCommand.CorrectDocumentSerials, reason);
        var posting = await ReadDocumentAsync(document, ctx, cancellationToken);
        var tenantId = ctx.Tenant.TenantId;
        var selectionIds = new[] { recordedSelectionId, physicalSelectionId };
        var selections = await ctx.Db.SelectionSets.AsNoTracking().Where(s => s.TenantId == tenantId && selectionIds.Contains(s.SelectionId)).ToListAsync(cancellationToken);
        if (selections.Count != 2) throw new CommandRefusedException("selection_unknown", "Both the recorded and physical selection sets must exist.", "resolve both serial lists before previewing the correction");
        var members = await ctx.Db.SelectionSetMembers.AsNoTracking().Where(m => m.TenantId == tenantId && selectionIds.Contains(m.SelectionId))
            .OrderBy(m => m.Serial).ThenBy(m => m.UnitId).ToListAsync(cancellationToken);
        var recorded = members.Where(m => m.SelectionId == recordedSelectionId).ToList();
        var physical = members.Where(m => m.SelectionId == physicalSelectionId).ToList();
        if (recorded.Count == 0 || physical.Count == 0 || recorded.Count != physical.Count)
            throw new CommandRefusedException("document_correction_count_mismatch", $"Recorded count {recorded.Count} does not equal physical count {physical.Count}.", "select the same number of recorded and physical units");
        if (recorded.Select(m => m.UnitId).Intersect(physical.Select(m => m.UnitId)).Any())
            throw new CommandRefusedException("document_correction_selection_overlap", "A unit cannot be both recorded and physical in the same replacement.", "remove unchanged units from both selections");

        var allIds = recorded.Concat(physical).Select(m => m.UnitId).Distinct().ToList();
        var current = await ctx.Db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && allIds.Contains(c.UnitId)).ToDictionaryAsync(c => c.UnitId, cancellationToken);
        if (current.Count != allIds.Count) throw new CommandRefusedException("unit_unknown", "At least one selected unit does not exist in this tenant.");
        foreach (var row in current.Values) EnsureCorrectable(row.UnitId, row.LifecycleStatus);
        var recordedCounts = recorded.GroupBy(m => current[m.UnitId].ItemId).ToDictionary(g => g.Key, g => g.Count());
        var physicalCounts = physical.GroupBy(m => current[m.UnitId].ItemId).ToDictionary(g => g.Key, g => g.Count());
        if (recordedCounts.Count != physicalCounts.Count || recordedCounts.Any(kv => physicalCounts.GetValueOrDefault(kv.Key) != kv.Value))
            throw new CommandRefusedException("document_correction_item_mismatch", "Recorded and physical selections must contain the same count of every item.", "replace each recorded unit with a physical unit of the same item");

        var recordedIds = recorded.Select(m => m.UnitId).ToList();
        var originalEvents = await ctx.Db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && recordedIds.Contains(e.UnitId)
            && e.DocumentType == document.Type && e.DocumentId == document.Id && (document.Line == null || e.DocumentLine == document.Line)
            && (e.EventType == UnitEventType.Shipped || e.EventType == UnitEventType.CustodyChanged || e.EventType == UnitEventType.Commissioned))
            .OrderByDescending(e => e.Seq).ToListAsync(cancellationToken);
        var originalByUnit = originalEvents.GroupBy(e => e.UnitId).ToDictionary(g => g.Key, g => g.First());
        var missingDocument = recordedIds.Where(id => !originalByUnit.ContainsKey(id)).ToList();
        if (missingDocument.Count > 0)
            throw new CommandRefusedException("recorded_unit_not_on_document", $"{missingDocument.Count} recorded unit(s) have no posting fact for the document.", "select only units recorded on this posted document");

        var episodes = await ctx.Db.Episodes.AsNoTracking().Where(e => e.TenantId == tenantId && allIds.Contains(e.UnitId)).ToListAsync(cancellationToken);
        var pairs = new List<Pair>(recorded.Count);
        foreach (var item in recordedCounts.Keys.Order())
        {
            var left = recorded.Where(m => current[m.UnitId].ItemId == item).OrderBy(m => m.Serial).ThenBy(m => m.UnitId).ToList();
            var right = physical.Where(m => current[m.UnitId].ItemId == item).OrderBy(m => m.Serial).ThenBy(m => m.UnitId).ToList();
            for (var i = 0; i < left.Count; i++)
            {
                var recordedCustody = episodes.Where(e => e.UnitId == left[i].UnitId && e.Kind == EpisodeKind.Custody && e.ValidTo == null).SingleOrDefault()
                    ?? throw new CommandRefusedException("recorded_unit_has_no_custody", $"Recorded unit {left[i].UnitId} has no current custody episode.");
                var source = episodes.Where(e => e.UnitId == left[i].UnitId && e.Kind == EpisodeKind.Custody
                    && e.ClosingDocumentType == document.Type && e.ClosingDocumentId == document.Id
                    && (document.Line == null || e.ClosingDocumentLine == document.Line)).OrderByDescending(e => e.ValidTo).FirstOrDefault();
                if (source is null && posting.SourceFallback is null)
                    throw new CommandRefusedException("document_correction_source_unknown", $"The source custody of recorded unit {left[i].UnitId} cannot be reconstructed.", "restore the document's source-location evidence before correcting its serials");
                var expectedSource = source is null ? posting.SourceFallback! : new CustodyTarget(source.LocationKind!, source.LocationId,
                    source.PartyRef, source.PartyType, source.Reason ?? EpisodeReason.DocumentCorrection,
                    source.Source ?? EpisodeSource.Delivery, source.Confidence ?? Confidence.High);
                var physicalCustody = episodes.Where(e => e.UnitId == right[i].UnitId && e.Kind == EpisodeKind.Custody && e.ValidTo == null).SingleOrDefault()
                    ?? throw new CommandRefusedException("physical_unit_has_no_custody", $"Physical unit {right[i].UnitId} has no current custody episode.");
                if (physicalCustody.LocationKind != expectedSource.LocationKind || (expectedSource.LocationId is not null && physicalCustody.LocationId != expectedSource.LocationId))
                    throw new CommandRefusedException("physical_unit_not_at_source", $"Physical unit {right[i].UnitId} is not at the document's source location.", "select a physical unit at the location from which the document posted");
                var ownership = episodes.Where(e => e.UnitId == left[i].UnitId && e.Kind == EpisodeKind.Ownership && e.ValidTo == null
                    && (e.OpeningDocumentId == document.Id || (posting.CustomerPartyRef is not null && e.PartyRef == posting.CustomerPartyRef && e.ValidFrom >= posting.PostedAt))).SingleOrDefault();
                pairs.Add(new(left[i].UnitId, right[i].UnitId, item, left[i].Serial, right[i].Serial,
                    recordedCustody, source, originalByUnit[left[i].UnitId], ownership));
            }
        }

        var documentSerialCount = posting.Lines.Where(l => document.Line == null || l.LineNo == document.Line).Sum(l => l.Serials.Count);
        var policy = await approvalSettings.GetAsync(cancellationToken);
        var approval = CorrectionApprovalRules.Evaluate(policy, CorrectionCommand.CorrectDocumentSerials,
            new(DocumentInvoiced: posting.Invoiced), committer?.Roles ?? []);
        return new(posting, pairs, recorded.Count == documentSerialCount, pairs.ToDictionary(p => p.RecordedUnitId, p => p.PhysicalUnitId), approval);
    }

    private static void EnsureCorrectable(Guid unitId, string lifecycleStatus)
    {
        if (lifecycleStatus is LifecycleStatus.Void or LifecycleStatus.Merged)
            throw new CommandRefusedException("unit_lifecycle_not_correctable", $"Unit {unitId} is {lifecycleStatus}.", "select an active physical-unit record");
    }

    private static CommandRefusal? ApprovalRefusal(CorrectionApprovalDecision decision, CorrectionCommitter? committer, CorrectionMetadata metadata)
    {
        if (decision.Status == CorrectionApprovalStatus.Refused) return decision.Refusal;
        var held = (committer?.Roles ?? []).Concat((metadata.Approvers ?? []).SelectMany(a => a.Roles)).ToHashSet(StringComparer.Ordinal);
        var missing = decision.ApproverRoles.Where(r => !held.Contains(r)).Distinct().ToList();
        return missing.Count == 0 ? null : new("approval_required",
            $"'{CorrectionCommand.CorrectDocumentSerials}' needs approval by {string.Join(" and ", missing)}.",
            $"route the identical preview to {string.Join(" and ", missing)}");
    }

    private static void EnsureApproval(CorrectionApprovalDecision decision, CorrectionCommitter? committer, CorrectionMetadata metadata)
    {
        if (committer is null || string.IsNullOrWhiteSpace(committer.UserRef))
            throw new CommandRefusedException("committer_required", "A correction committer is required.");
        if (ApprovalRefusal(decision, committer, metadata) is { } refusal)
            throw new CommandRefusedException(refusal.Rule, refusal.Message, refusal.UnblockingStep);
    }

    private static async Task<PostingDocument> ReadDocumentAsync(DocumentRef document, DocumentContext ctx, CancellationToken cancellationToken)
    {
        if (await ctx.Sales.GetShipmentAsync(document, cancellationToken) is { } shipment)
            return new(shipment.PostedAt, shipment.Lines, shipment.IsInvoiced, null, shipment.CustomerPartyRef);
        if (await ctx.Inventory.GetTransferAsync(document, cancellationToken) is { } transfer)
            return new(transfer.PostedAt, transfer.Lines, transfer.IsInvoiced,
                new CustodyTarget(transfer.FromLocationKind, transfer.FromLocationId, null, null, EpisodeReason.DocumentCorrection, EpisodeSource.Delivery));
        if (await ctx.Purchasing.GetReceiptAsync(document, cancellationToken) is { } receipt)
            return new(receipt.PostedAt, receipt.Lines, receipt.IsInvoiced,
                new CustodyTarget(LocationKind.Vendor, null, receipt.VendorPartyRef, PartyType.Vendor, EpisodeReason.DocumentCorrection, EpisodeSource.Receipt));
        throw new CommandRefusedException("posted_document_unknown", $"Posted delivery, transfer or receipt {document.Type} {document.Id:N} is unavailable.", "check the document reference and posting state");
    }
}
