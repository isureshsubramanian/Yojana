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
/// Case 143: changes the effective ownership source by compensating facts, never by editing historical UnitEvents.
/// Selection, episodes, related events and entitlement state are read in sets, independent of the selected unit count.
/// </summary>
public static class CorrectSourceDocumentHandler
{
    private sealed record PlannedUnit(Guid UnitId, string? Serial, CustodyEpisode Episode, IReadOnlyList<Guid> RelatedEventIds);
    private sealed record Plan(OwnershipSourceDocument Previous, OwnershipSourceDocument Corrected,
        IReadOnlyList<PlannedUnit> Units, Guid? PreviousOwnerPartyRef, bool OwnerChanged, CorrectionApprovalPolicy Policy,
        CorrectionApprovalFacts ApprovalFacts, IReadOnlyList<CorrectionEntitlementPreview> EntitlementChanges);

    public static async Task<CorrectSourceDocumentResult> Handle(CorrectSourceDocument command, DocumentContext documents,
        IOwnershipSourceDocumentQueryPort ownershipSources, SourceDocumentCorrectionEntitlements entitlements,
        CorrectionLedger corrections, CorrectionApprovalSettings approvalSettings, IMessageBus bus, ITenantContext tenant,
        IClock clock, CancellationToken cancellationToken)
    {
        var supplied = command.Correction ?? new CorrectionMetadata(command.CommandId, command.Reason,
            command.Note, InitiatingRole: CorrectionInitiatingRole.CustomerService);
        var existing = await documents.Db.Corrections.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenant.TenantId
            && c.CorrectionId == supplied.CorrectionId, cancellationToken);
        if (existing is not null)
        {
            if (existing.Command != CorrectionCommand.CorrectSourceDocument)
                throw new CommandRefusedException("correction_id_in_use", $"Correction id {existing.CorrectionId} has already been used.",
                    "send a new correction id, or replay the original command idempotently");
            return new(existing.CorrectionId, existing.UnitCount, 0, 0, 0, 0, false, true);
        }

        var plan = await PlanAsync(command.SelectionId, command.PreviousDocument, command.CorrectedDocument,
            command.Reason, ownershipSources, documents, entitlements, approvalSettings, cancellationToken);
        EnsureApproval(plan, command.CommittedBy, supplied);
        var metadata = await corrections.PrepareAsync(CorrectionCommand.CorrectSourceDocument, supplied, command.CommandId,
            command.Reason, command.Note, null, command.EventTime, CorrectionInitiatingRole.CustomerService, cancellationToken);

        return await documents.InTransactionAsync(async () =>
        {
            var unitIds = plan.Units.Select(u => u.UnitId).ToList();
            await documents.Episodes.PreloadOpenAsync(unitIds, cancellationToken);
            var drafts = new List<UnitEventDraft>(unitIds.Count * (plan.OwnerChanged ? 3 : 1));
            var correctionEventIds = new Dictionary<Guid, Guid>();
            foreach (var unit in plan.Units)
            {
                var target = new OwnershipTarget(plan.Corrected.OwnerPartyRef, plan.Corrected.OwnerPartyType,
                    unit.Episode.Reason ?? EpisodeReason.Sale, unit.Episode.Source ?? EpisodeSource.Delivery,
                    unit.Episode.Confidence ?? Confidence.High, plan.Corrected.CustomerPoRef, unit.Episode.CustomerItemRef,
                    unit.Episode.PartyIsGroupMember, unit.Episode.ExternalOrderRef);
                var prepared = await documents.Episodes.PrepareOwnershipSourceDocumentCorrectionAsync(unit.UnitId, target,
                    command.CorrectedDocument, unit.RelatedEventIds,
                    IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, unit.UnitId,
                        UnitEventType.OwnershipSourceDocumentCorrected, unit.Episode.ValidFrom),
                    unit.Episode.ValidFrom, command.ActorRef, command.LegalEntityId, cancellationToken);
                if (prepared.CloseDraft is not null)
                    drafts.Add(prepared.CloseDraft with { Attributes = CorrectionLedger.Attributes(metadata, prepared.CloseDraft.Attributes!) });
                if (prepared.OpenDraft is not null)
                    drafts.Add(prepared.OpenDraft with { Attributes = CorrectionLedger.Attributes(metadata, prepared.OpenDraft.Attributes!) });
                var correctionDraft = prepared.CorrectionDraft with
                {
                    Attributes = CorrectionLedger.Attributes(metadata, prepared.CorrectionDraft.Attributes!),
                };
                drafts.Add(correctionDraft);
                correctionEventIds[unit.UnitId] = correctionDraft.PresetEventId!.Value;
            }

            var entitlementResult = await entitlements.ApplyAsync(command.CommandId, unitIds, plan.Previous, plan.Corrected,
                command.ActorRef, command.LegalEntityId, metadata, cancellationToken);
            drafts.AddRange(entitlementResult.Drafts);
            await documents.Writer.AppendAsync(drafts, cancellationToken);
            await corrections.RecordAsync(CorrectionCommand.CorrectSourceDocument, metadata, command.ActorRef,
                command.EventTime, unitIds, cancellationToken);

            var publishedCorrection = new PublishedCorrection(metadata.CorrectionId, metadata.ReasonCode,
                metadata.InitiatedVia, metadata.CorrectsCorrectionId,
                (metadata.Approvers ?? []).Select(a => a.UserRef).ToList());
            await bus.PublishAsync(new OwnershipSourceDocumentCorrected(
                    new(OwnershipSourceDocumentCorrected.Schema, OwnershipSourceDocumentCorrected.Version,
                        TimeOrderedId.New(command.EventTime), command.EventTime, clock.UtcNow, command.LegalEntityId,
                        command.ActorRef, command.CorrectedDocument),
                    publishedCorrection, unitIds, command.PreviousDocument, command.CorrectedDocument,
                    plan.OwnerChanged, plan.PreviousOwnerPartyRef, plan.Corrected.OwnerPartyRef),
                new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
            foreach (var unitId in unitIds)
            {
                await bus.PublishAsync(new LabelReprintJobRequested(TimeOrderedId.New(command.EventTime), unitId,
                        metadata.CorrectionId, correctionEventIds[unitId], command.CorrectedDocument,
                        EntitlementClosedReason.SourceDocumentCorrected, command.EventTime, command.LegalEntityId,
                        command.ActorRef),
                    new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
            }

            return new CorrectSourceDocumentResult(metadata.CorrectionId, unitIds.Count, plan.Units.Sum(u => u.RelatedEventIds.Count),
                entitlementResult.Superseded, entitlementResult.Created, unitIds.Count, plan.OwnerChanged);
        }, cancellationToken);
    }

    public static async Task<CorrectionPreview> Handle(PreviewCorrectSourceDocument command, DocumentContext documents,
        IOwnershipSourceDocumentQueryPort ownershipSources, SourceDocumentCorrectionEntitlements entitlements,
        CorrectionApprovalSettings approvalSettings, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = command.Correction ?? new CorrectionMetadata(Guid.Empty, command.Reason, command.Note,
                InitiatingRole: CorrectionInitiatingRole.CustomerService);
            var plan = await PlanAsync(command.SelectionId, command.PreviousDocument, command.CorrectedDocument,
                command.Reason, ownershipSources, documents, entitlements, approvalSettings, cancellationToken);
            var refusal = ApprovalRefusal(plan, command.CommittedBy, metadata);
            var units = plan.Units.Select(unit => new UnitOutcome(unit.UnitId, unit.Serial, UnitOutcome.Outcomes.Change,
                null, plan.OwnerChanged
                    ? "close the wrong-owner episode, reopen it for the corrected owner and re-derive cover"
                    : "correct the episode source and re-derive cover")).ToList();
            var events = plan.Units.SelectMany(unit => plan.OwnerChanged
                ? new[]
                {
                    new CorrectionEventPreview(unit.UnitId, UnitEventType.OwnershipClosed, unit.Episode.ValidFrom, "close the wrong-owner episode"),
                    new CorrectionEventPreview(unit.UnitId, UnitEventType.OwnershipTransferred, unit.Episode.ValidFrom, "open ownership for the corrected party"),
                    new CorrectionEventPreview(unit.UnitId, UnitEventType.OwnershipSourceDocumentCorrected, unit.Episode.ValidFrom,
                        $"compensates {unit.RelatedEventIds.Count} immutable document fact(s)"),
                }
                : new[]
                {
                    new CorrectionEventPreview(unit.UnitId, UnitEventType.OwnershipSourceDocumentCorrected, unit.Episode.ValidFrom,
                        $"compensates {unit.RelatedEventIds.Count} immutable document fact(s)"),
                }).ToList();
            return new CorrectionPreview(
                SelectionId: command.SelectionId,
                Command: CorrectionCommand.CorrectSourceDocument,
                UnitCount: units.Count,
                Change: units.Count,
                Skip: 0,
                Block: refusal is null ? 0 : units.Count,
                Units: units,
                UnitEvents: events,
                EntitlementChanges: plan.EntitlementChanges,
                CertificateVersions: units.Select(u => new CorrectionCertificatePreview(u.UnitId, "warranty_certificate", "reissue", EntitlementClosedReason.SourceDocumentCorrected)).ToList(),
                PrintJobs: units.Select(u => new CorrectionPrintJobPreview(u.UnitId, "label_reprint", EntitlementClosedReason.SourceDocumentCorrected)).ToList(),
                Refusal: refusal);
        }
        catch (CommandRefusedException ex)
        {
            return new CorrectionPreview(
                SelectionId: command.SelectionId,
                Command: CorrectionCommand.CorrectSourceDocument,
                UnitCount: 0,
                Change: 0,
                Skip: 0,
                Block: 1,
                Units: [],
                UnitEvents: [],
                EntitlementChanges: [],
                CertificateVersions: [],
                PrintJobs: [],
                Refusal: ex.Refusal);
        }
    }

    private static async Task<Plan> PlanAsync(Guid selectionId, DocumentRef previousDocument,
        DocumentRef correctedDocument, string reason, IOwnershipSourceDocumentQueryPort ownershipSources,
        DocumentContext documents, SourceDocumentCorrectionEntitlements entitlements,
        CorrectionApprovalSettings approvalSettings, CancellationToken cancellationToken)
    {
        CorrectionRules.ValidateReason(CorrectionCommand.CorrectSourceDocument, reason);
        if (SameDocument(previousDocument, correctedDocument))
            throw new CommandRefusedException("source_document_unchanged", "The previous and corrected source documents are the same.",
                "select the document that should replace the current source");
        var previous = await ownershipSources.GetOwnershipSourceAsync(previousDocument, cancellationToken)
            ?? throw new CommandRefusedException("previous_source_document_unknown", $"Ownership source {previousDocument.Type} {previousDocument.Id:N} is unavailable.");
        var corrected = await ownershipSources.GetOwnershipSourceAsync(correctedDocument, cancellationToken)
            ?? throw new CommandRefusedException("corrected_source_document_unknown", $"Ownership source {correctedDocument.Type} {correctedDocument.Id:N} is unavailable.",
                "check the document reference and that Sales exposes its immutable correction view");

        var tenantId = documents.Tenant.TenantId;
        if (!await documents.Db.SelectionSets.AsNoTracking().AnyAsync(s => s.TenantId == tenantId && s.SelectionId == selectionId, cancellationToken))
            throw new CommandRefusedException("selection_unknown", $"Selection set {selectionId} does not exist.");
        var members = await documents.Db.SelectionSetMembers.AsNoTracking().Where(m => m.TenantId == tenantId
            && m.SelectionId == selectionId).OrderBy(m => m.Serial).ThenBy(m => m.UnitId).ToListAsync(cancellationToken);
        if (members.Count == 0) throw new CommandRefusedException("selection_empty", "The source-document correction selection is empty.");
        var unitIds = members.Select(m => m.UnitId).Distinct().ToList();
        if (unitIds.Count != members.Count) throw new CommandRefusedException("selection_duplicate_unit", "The selection contains a unit more than once.");

        var current = await documents.Db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && unitIds.Contains(c.UnitId))
            .Select(c => new { c.UnitId, c.LifecycleStatus }).ToListAsync(cancellationToken);
        if (current.Count != unitIds.Count) throw new CommandRefusedException("unit_unknown", "At least one selected unit does not exist.");
        if (current.FirstOrDefault(c => c.LifecycleStatus is LifecycleStatus.Void or LifecycleStatus.Merged) is { } invalid)
            throw new CommandRefusedException("unit_lifecycle_not_correctable", $"Unit {invalid.UnitId} is {invalid.LifecycleStatus}.");

        var episodes = await documents.Db.Episodes.AsNoTracking().Where(e => e.TenantId == tenantId
            && unitIds.Contains(e.UnitId) && e.Kind == EpisodeKind.Ownership && e.ValidTo == null).ToListAsync(cancellationToken);
        if (episodes.Count != unitIds.Count)
            throw new CommandRefusedException("no_open_ownership", "Every selected unit must have one open ownership episode.");
        var mismatch = episodes.FirstOrDefault(e => e.OpeningDocumentType != previousDocument.Type
            || e.OpeningDocumentId != previousDocument.Id || (previousDocument.Line is not null && e.OpeningDocumentLine != previousDocument.Line));
        if (mismatch is not null)
            throw new CommandRefusedException("ownership_source_mismatch", $"Unit {mismatch.UnitId} is not open from the stated previous document.",
                "preview a selection whose open ownership episodes share that source document");
        var previousOwners = episodes.Select(e => (e.PartyRef, e.PartyType)).Distinct().ToList();
        if (previousOwners.Count != 1)
            throw new CommandRefusedException("previous_owner_not_uniform", "The selection contains more than one current owning party.",
                "correct one owning party per command so approval and the published summary remain unambiguous");

        var eventRows = await documents.Db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && unitIds.Contains(e.UnitId)
            && e.DocumentType == previousDocument.Type && e.DocumentId == previousDocument.Id
            && (previousDocument.Line == null || e.DocumentLine == previousDocument.Line))
            .Select(e => new { e.UnitId, e.EventId }).ToListAsync(cancellationToken);
        var archivedRows = await documents.Db.ArchivedEvents.AsNoTracking().Where(e => e.TenantId == tenantId && unitIds.Contains(e.UnitId)
            && e.DocumentType == previousDocument.Type && e.DocumentId == previousDocument.Id
            && (previousDocument.Line == null || e.DocumentLine == previousDocument.Line))
            .Select(e => new { e.UnitId, e.EventId }).ToListAsync(cancellationToken);
        eventRows.AddRange(archivedRows);
        var eventsByUnit = eventRows.GroupBy(e => e.UnitId).ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(e => e.EventId).Distinct().ToList());
        var episodeByUnit = episodes.ToDictionary(e => e.UnitId);
        var units = members.Select(m => new PlannedUnit(m.UnitId, m.Serial, episodeByUnit[m.UnitId],
            eventsByUnit.GetValueOrDefault(m.UnitId, []))).ToList();
        var ownerChanged = episodes.Any(e => e.PartyRef != corrected.OwnerPartyRef || e.PartyType != corrected.OwnerPartyType);
        var policy = await approvalSettings.GetAsync(cancellationToken);
        var facts = new CorrectionApprovalFacts(OwningPartyChanges: ownerChanged);
        var entitlementChanges = await entitlements.PreviewAsync(unitIds, previous, corrected, cancellationToken);
        return new(previous, corrected, units, previousOwners[0].PartyRef, ownerChanged, policy, facts, entitlementChanges);
    }

    private static CommandRefusal? ApprovalRefusal(Plan plan, CorrectionCommitter? committer, CorrectionMetadata metadata)
    {
        if (committer is null || string.IsNullOrWhiteSpace(committer.UserRef))
            return new("committer_required", "A correction committer is required.");
        if (CorrectionApprovalRules.ApprovalsSatisfied(plan.Policy, CorrectionCommand.CorrectSourceDocument,
                plan.ApprovalFacts, committer, metadata.Approvers ?? [])) return null;
        return CorrectionApprovalRules.Evaluate(plan.Policy, CorrectionCommand.CorrectSourceDocument,
            plan.ApprovalFacts, committer.Roles).Refusal
            ?? new CommandRefusal("approval_required", "The source-document correction does not have every required approval.",
                "route the identical preview to the required approvers");
    }

    private static void EnsureApproval(Plan plan, CorrectionCommitter? committer, CorrectionMetadata metadata)
    {
        if (ApprovalRefusal(plan, committer, metadata) is { } refusal)
            throw new CommandRefusedException(refusal.Rule, refusal.Message, refusal.UnblockingStep);
    }

    private static bool SameDocument(DocumentRef left, DocumentRef right) =>
        left.Type == right.Type && left.Id == right.Id && left.Line == right.Line;
}
