using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Certificates;

/// <summary>
/// Selection issue/reprint orchestration. Selection resolution stays in MOD-11; this service consumes only the stored
/// member contract and snapshots certificate outputs. Queries are set-based, and rendering is the sole per-document call.
/// </summary>
public sealed class CertificateSelectionService(
    SerialisationDbContext db,
    ITenantContext tenant,
    IClock clock,
    IItemQueryPort itemProfiles,
    IItemNamePort itemNames,
    AsBuiltCompleteness completeness,
    CertificateTemplateService templates,
    CertificateVersioningEngine versions,
    ReleaseRecordIssuePointSettings issuePoints,
    ReleaseRecordIssuePolicy issuePolicy,
    ICertificateRenderer renderer,
    ICertificateBundleRenderer bundleRenderer,
    IUnitEventWriter events,
    IDbContextOutbox<SerialisationDbContext> outbox,
    CertificateRunOptions options,
    ICertificateRunChunkObserver chunks)
{
    private const int MaxPage = 500;
    internal const int ChunkSize = 100;

    public async Task<CertificateSelectionPreview> PreviewAsync(CertificateSelectionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request.DocumentKind, request.FormType, request.Language);
        if (request.IssuerEntityId == Guid.Empty)
            throw new CommandRefusedException("issuer_entity_required", "An issuing legal entity is required.");
        var all = await EvaluateAsync(request.SelectionId, request.DocumentKind, request.FormType, cancellationToken);
        var ordered = all.OrderBy(row => row.Outcome == CertificateSelectionOutcome.NotIssuable ? 0 : row.Outcome == CertificateSelectionOutcome.ReprintCopy ? 1 : 2)
            .ThenBy(row => row.Serial, StringComparer.Ordinal).ToList();
        var filtered = ordered.Where(row => string.IsNullOrWhiteSpace(request.Outcome) || row.Outcome == request.Outcome)
            .Where(row => string.IsNullOrWhiteSpace(request.Find) || Matches(row, request.Find)).ToList();
        var skip = Math.Max(0, request.Skip);
        var take = Math.Clamp(request.Take, 1, MaxPage);
        return new CertificateSelectionPreview(request.SelectionId, Kpis(all, request.IncludeBundle),
            filtered.Skip(skip).Take(take).ToList(), filtered.Count, skip, take);
    }

    public Task<CertificateRunResult> IssueAsync(IssueCertificate command, CancellationToken cancellationToken) =>
        ExecuteAsync(command.CommandId, command.SelectionId, command.DocumentKind, command.FormType,
            command.IssuerEntityId, command.Language, command.ReprintReason, command.IncludeBundle,
            command.ActorRef, reprintOnly: false, cancellationToken);

    public Task<CertificateRunResult> ReprintAsync(ReprintCertificate command, CancellationToken cancellationToken) =>
        ExecuteAsync(command.CommandId, command.SelectionId, command.DocumentKind, CertificateFormType.Custom,
            Guid.Empty, "en", command.Reason, command.IncludeBundle, command.ActorRef,
            reprintOnly: true, cancellationToken);

    private async Task<CertificateRunResult> ExecuteAsync(Guid commandId, Guid selectionId, string documentKind,
        string formType, Guid issuerEntityId, string language, string reason, bool includeBundle, string actorRef,
        bool reprintOnly, CancellationToken cancellationToken)
    {
        if (commandId == Guid.Empty) throw new CommandRefusedException("command_id_required", "A command id is required.");
        if (string.IsNullOrWhiteSpace(actorRef)) throw new CommandRefusedException("actor_required", "The authenticated actor is required.");
        if (string.IsNullOrWhiteSpace(reason)) throw new CommandRefusedException("reprint_reason_required", "One reprint reason is required for the run.");
        Validate(documentKind, formType, language, reprintOnly);
        if (!reprintOnly && issuerEntityId == Guid.Empty)
            throw new CommandRefusedException("issuer_entity_required", "An issuing legal entity is required.");
        var requestHash = Hash(CanonicalJson.Serialise(new
        {
            selection_id = selectionId,
            document_kind = documentKind,
            form_type = formType,
            issuer_entity_id = issuerEntityId,
            language,
            reason = reason.Trim(),
            include_bundle = includeBundle,
            reprint_only = reprintOnly,
        }));
        var existing = await db.CertificateRuns.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.CommandId == commandId, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                throw new CommandRefusedException("command_id_conflict", "This command id was already used with different certificate-run inputs.");
            if (existing.CompletedAt is not null)
                return await MaterialiseAsync(commandId, includeBundle, replayed: true, cancellationToken);
        }

        var selectionCount = await SelectionCountAsync(selectionId, cancellationToken);
        if (selectionCount > options.MaximumSynchronousUnits)
            throw new CommandRefusedException("certificate_selection_too_large",
                $"The selection resolves to {selectionCount:N0} units; the synchronous certificate-run limit is {options.MaximumSynchronousUnits:N0}.",
                $"split the selection into sets of no more than {options.MaximumSynchronousUnits:N0} units");

        if (existing is null)
        {
            db.CertificateRuns.Add(new CertificateRun
            {
                TenantId = tenant.TenantId,
                CommandId = commandId,
                SelectionId = selectionId,
                RequestHash = requestHash,
                CreatedAt = clock.UtcNow,
                CreatedBy = actorRef,
            });
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        var evaluated = await EvaluateAsync(selectionId, documentKind, formType, cancellationToken);
        if (reprintOnly)
        {
            evaluated = evaluated.Select(row => row.Outcome == CertificateSelectionOutcome.ReprintCopy ? row : row with
            {
                Outcome = CertificateSelectionOutcome.NotIssuable,
                Reason = "certificate_not_issued",
                Detail = "No current issued certificate exists for this unit.",
                UnblockingStep = "issue version 1 first",
            }).ToList();
        }

        var completedUnitIds = await db.CertificateRunUnits.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.CommandId == commandId)
            .Select(row => row.UnitId).ToListAsync(cancellationToken);
        var pending = evaluated.Where(row => !completedUnitIds.Contains(row.UnitId))
            .OrderBy(row => row.Serial, StringComparer.Ordinal).ThenBy(row => row.UnitId).ToList();
        var chunkNumber = completedUnitIds.Count / ChunkSize;
        // TIGHT COUPLING (certificate run transaction boundary)
        // WHAT: this loop owns 100-unit transactions and the handlers below opt out of Wolverine's whole-message transaction.
        // WHY: a certificate chain lock is transaction-owned; no message boundary exists inside one synchronous command.
        // WHAT BREAKS IF SEPARATED: restoring automatic handler transactions holds every unit lock until the entire selection ends,
        // while removing these transactions can commit a certificate without its per-unit progress receipt.
        foreach (var chunk in pending.Chunk(ChunkSize))
        {
            chunkNumber++;
            await chunks.StartingAsync(commandId, chunkNumber, chunk.Length, cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await ProcessChunkAsync(commandId, documentKind, formType, issuerEntityId, language, reason,
                    actorRef, chunk, cancellationToken);
                await outbox.SaveChangesAndFlushMessagesAsync(MultiFlushMode.AllowMultiples, cancellationToken);
                if (db.Database.CurrentTransaction is not null)
                    await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                if (db.Database.CurrentTransaction is not null)
                    await transaction.RollbackAsync(cancellationToken);
                throw;
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var receipt = await db.CertificateRuns.SingleAsync(row => row.TenantId == tenant.TenantId
                && row.CommandId == commandId, cancellationToken);
            receipt.CompletedAt ??= clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        db.ChangeTracker.Clear();
        return await MaterialiseAsync(commandId, includeBundle, replayed: existing is not null, cancellationToken);
    }

    private async Task ProcessChunkAsync(Guid commandId, string documentKind, string formType, Guid issuerEntityId,
        string language, string reason, string actorRef, IReadOnlyCollection<CertificateSelectionUnit> chunk,
        CancellationToken cancellationToken)
    {
        var unitIds = chunk.Select(row => row.UnitId).ToList();
        var releaseTimes = await db.BuildRecords.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && unitIds.Contains(row.UnitId))
            .GroupBy(row => row.UnitId)
            .Select(group => new { UnitId = group.Key, ReleasedAt = group.Max(row => row.ReleasedAt) })
            .ToDictionaryAsync(row => row.UnitId, row => row.ReleasedAt, cancellationToken);
        var saleTimes = await issuePolicy.CustomerSaleTimesAsync(unitIds, cancellationToken);
        var createdEvents = await db.Units.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && unitIds.Contains(row.UnitId))
            .Select(row => new { row.UnitId, row.CreatedEventTime }).ToListAsync(cancellationToken);
        var commissionedTimes = createdEvents.ToDictionary(row => row.UnitId, row => row.CreatedEventTime);
        var itemIds = chunk.Select(row => row.ItemId).OfType<Guid>().Distinct().ToList();
        var profiles = await itemProfiles.GetManyAsync(itemIds, cancellationToken);
        var issueSettings = await issuePoints.GetAsync(cancellationToken);
        var certificateIds = chunk.Select(row => row.CertificateId).OfType<Guid>().Distinct().ToList();
        var copyCertificates = certificateIds.Count == 0
            ? new Dictionary<Guid, CertificateInstance>()
            : await db.CertificateInstances.Where(row => row.TenantId == tenant.TenantId
                    && certificateIds.Contains(row.CertificateId))
                .ToDictionaryAsync(row => row.CertificateId, cancellationToken);
        var reprintIds = chunk.Select(row => Deterministic(commandId, row.UnitId, "reprint")).ToList();
        List<Guid> logged = reprintIds.Count == 0
            ? []
            : await db.ReprintLogs.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                    && reprintIds.Contains(row.ReprintId))
                .Select(row => row.ReprintId).ToListAsync(cancellationToken);
        var loggedSet = logged.ToHashSet();

        foreach (var row in chunk)
        {
            if (row.Outcome == CertificateSelectionOutcome.NotIssuable)
            {
                db.CertificateRunUnits.Add(Receipt(commandId, row, CertificateRunUnitOutcome.Skipped,
                    null, null, null, copy: false));
                continue;
            }

            CertificateInstance certificate;
            var copy = row.Outcome == CertificateSelectionOutcome.ReprintCopy;
            if (!copy)
            {
                var triggerId = Deterministic(commandId, row.UnitId, "issue");
                var itemClass = row.ItemId is { } itemId ? profiles.GetValueOrDefault(itemId)?.ItemClass : null;
                var issuePoint = ReleaseRecordIssuePointSettings.Resolve(issueSettings, itemClass);
                var effectiveAt = issuePoint switch
                {
                    ReleaseRecordIssuePoint.AtFirstShipment => saleTimes.GetValueOrDefault(row.UnitId),
                    ReleaseRecordIssuePoint.AtBuildRelease when releaseTimes.TryGetValue(row.UnitId, out var releasedAt) => releasedAt,
                    ReleaseRecordIssuePoint.AtBuildRelease => saleTimes.GetValueOrDefault(row.UnitId),
                    _ => clock.UtcNow,
                };
                if (effectiveAt == default) effectiveAt = commissionedTimes[row.UnitId];
                var issued = await versions.IssueAsync(new IssueCertificateVersion(triggerId, row.UnitId,
                    documentKind, formType, null, effectiveAt, actorRef, issuerEntityId, Language: language,
                    TriggerSchema: ReleaseRecordIssuePolicy.SelectionTrigger), cancellationToken);
                if (!issued.Issued || issued.CertificateId is null)
                {
                    db.CertificateRunUnits.Add(Receipt(commandId, row with
                    {
                        Reason = issued.RefusalRule ?? "certificate_issue_refused",
                        Detail = issued.RefusalDetail,
                    }, CertificateRunUnitOutcome.Failed, null, null, null, copy: false));
                    continue;
                }
                certificate = db.CertificateInstances.Local.FirstOrDefault(item => item.CertificateId == issued.CertificateId.Value)
                    ?? await db.CertificateInstances.SingleAsync(item => item.TenantId == tenant.TenantId
                        && item.CertificateId == issued.CertificateId.Value, cancellationToken);
            }
            else
            {
                certificate = copyCertificates[row.CertificateId!.Value];
                var reprintId = Deterministic(commandId, row.UnitId, "reprint");
                if (!loggedSet.Contains(reprintId))
                {
                    var at = clock.UtcNow;
                    db.ReprintLogs.Add(new ReprintLog
                    {
                        TenantId = tenant.TenantId,
                        ReprintId = reprintId,
                        CertificateId = certificate.CertificateId,
                        Version = certificate.Version,
                        RequestedBy = actorRef,
                        At = at,
                    });
                    await events.AppendAsync([new UnitEventDraft
                    {
                        UnitId = row.UnitId, EventTime = at, EventType = CertificateEventType.Reprinted,
                        LegalEntityId = certificate.IssuerEntityId, ActorRef = actorRef,
                        IdempotencyKey = $"cert:reprint:{reprintId:N}",
                        Attributes = JsonObjectText.Of(new
                        {
                            reprint_id = reprintId, certificate_id = certificate.CertificateId,
                            certificate_version = certificate.Version, tracking_number = certificate.TrackingNumber,
                            reason = reason.Trim(), copy = true, reprinted_at = at,
                        }),
                        ProjectCurrentState = false,
                    }], cancellationToken);
                }
            }
            db.CertificateRunUnits.Add(Receipt(commandId, row, copy
                    ? CertificateRunUnitOutcome.Copied : CertificateRunUnitOutcome.Issued,
                certificate.CertificateId, certificate.Version, certificate.TrackingNumber, copy));
        }
    }

    private async Task<List<CertificateSelectionUnit>> EvaluateAsync(Guid selectionId, string documentKind,
        string formType, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (!await db.SelectionSets.AsNoTracking().AnyAsync(row => row.TenantId == tenantId && row.SelectionId == selectionId, cancellationToken))
            throw new CommandRefusedException("selection_unknown", $"Selection {selectionId} does not exist.");
        await templates.EnsureStartersAsync(cancellationToken);
        var result = new List<CertificateSelectionUnit>();
        for (var skip = 0; ; skip += MaxPage)
        {
            var members = await db.SelectionSetMembers.AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.SelectionId == selectionId)
                .OrderBy(row => row.Serial).ThenBy(row => row.UnitId)
                .Skip(skip).Take(MaxPage).Select(row => new SelectionMember(row.UnitId, row.Serial))
                .ToListAsync(cancellationToken);
            if (members.Count == 0) break;
            result.AddRange(await EvaluateBatchAsync(members, documentKind, formType, cancellationToken));
        }
        return result;
    }

    private async Task<List<CertificateSelectionUnit>> EvaluateBatchAsync(IReadOnlyList<SelectionMember> members,
        string documentKind, string formType, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var unitIds = members.Select(row => row.UnitId).ToList();
        var units = await db.Units.AsNoTracking().Where(row => row.TenantId == tenantId && unitIds.Contains(row.UnitId))
            .Select(row => new { row.UnitId, row.LifecycleStatus, row.CreatedEventTime }).ToDictionaryAsync(row => row.UnitId, cancellationToken);
        var assignments = await db.ItemAssignments.AsNoTracking().Where(row => row.TenantId == tenantId
                && unitIds.Contains(row.UnitId) && row.ValidTo == null)
            .Select(row => new { row.UnitId, row.ItemId }).ToDictionaryAsync(row => row.UnitId, cancellationToken);
        var buildRows = await db.BuildRecords.AsNoTracking().Where(row => row.TenantId == tenantId && unitIds.Contains(row.UnitId))
            .Select(row => new { row.UnitId, row.Version, row.Revision, row.ReleasedAt }).ToListAsync(cancellationToken);
        var builds = buildRows.GroupBy(row => row.UnitId).ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.Version).First());
        var certificateRows = await db.CertificateInstances.AsNoTracking().Where(row => row.TenantId == tenantId
                && unitIds.Contains(row.UnitId) && row.DocumentKind == documentKind && row.Status == CertificateStatus.Issued)
            .Select(row => new { row.UnitId, row.CertificateId, row.Version, row.TrackingNumber }).ToListAsync(cancellationToken);
        var certificates = certificateRows.GroupBy(row => row.UnitId).ToDictionary(group => group.Key,
            group => group.OrderByDescending(row => row.Version).First());
        var itemIds = assignments.Values.Select(row => row.ItemId).Distinct().ToList();
        var labels = await itemNames.NamesAsync(itemIds, cancellationToken);
        var profiles = await itemProfiles.GetManyAsync(itemIds, cancellationToken);
        var activeTemplates = await templates.ActiveVersionsAsync(documentKind, formType,
            profiles.Values.Select(row => row?.ItemClass).Distinct().ToList(), cancellationToken);
        var issueSettings = documentKind == CertificateDocumentKind.ReleaseRecord
            ? await issuePoints.GetAsync(cancellationToken) : null;
        var customerSaleUnits = documentKind == CertificateDocumentKind.ReleaseRecord
            ? (await issuePolicy.CustomerSaleTimesAsync(unitIds, cancellationToken)).Keys.ToHashSet()
            : [];
        var commissioned = documentKind == CertificateDocumentKind.ReleaseRecord
            ? units.ToDictionary(row => row.Key, row => row.Value.CreatedEventTime)
            : [];

        var completenessByUnit = new Dictionary<Guid, UnitCompleteness>();
        if (documentKind == CertificateDocumentKind.ReleaseRecord)
        {
            var requests = assignments.Values.Where(row => builds.ContainsKey(row.UnitId) || commissioned.ContainsKey(row.UnitId))
                .Select(row => (row.UnitId, row.ItemId, DateOnly.FromDateTime(builds.TryGetValue(row.UnitId, out var build)
                    ? build.ReleasedAt : commissioned[row.UnitId]))).ToList();
            var reports = await completeness.QueryAsync(requests, cancellationToken);
            foreach (var row in reports) completenessByUnit[row.UnitId] = row;
        }

        var result = new List<CertificateSelectionUnit>(members.Count);
        foreach (var member in members)
        {
            if (!units.TryGetValue(member.UnitId, out var unit))
            {
                result.Add(Row(member.UnitId, member.Serial, null, null, null, null,
                    CertificateSelectionOutcome.NotIssuable, "unit_unknown", "The selected unit no longer exists.", "resolve the selection again"));
                continue;
            }
            assignments.TryGetValue(member.UnitId, out var assignment);
            var itemId = assignment?.ItemId;
            labels.TryGetValue(itemId ?? Guid.Empty, out var label);
            builds.TryGetValue(member.UnitId, out var build);
            if (certificates.TryGetValue(member.UnitId, out var certificate))
            {
                result.Add(new CertificateSelectionUnit(member.UnitId, member.Serial, itemId, label?.Code, label?.Name,
                    build?.Revision, certificate.CertificateId, certificate.Version, certificate.TrackingNumber,
                    CertificateSelectionOutcome.ReprintCopy, null, "Current issued version will be marked Copy.", null));
                continue;
            }
            if (unit.LifecycleStatus != LifecycleStatus.Released)
            {
                result.Add(Row(member.UnitId, member.Serial, itemId, label, build?.Revision,
                    CertificateSelectionOutcome.NotIssuable, "not_released", $"Lifecycle is {unit.LifecycleStatus}.", "release the unit first"));
                continue;
            }
            if (documentKind == CertificateDocumentKind.ReleaseRecord && issueSettings is not null)
            {
                var selectedClass = itemId is { } selectedItemId ? profiles.GetValueOrDefault(selectedItemId)?.ItemClass : null;
                var issuePoint = ReleaseRecordIssuePointSettings.Resolve(issueSettings, selectedClass);
                var reached = issuePoint == ReleaseRecordIssuePoint.OnRequest
                    || issuePoint == ReleaseRecordIssuePoint.AtBuildRelease && build is not null
                    || customerSaleUnits.Contains(member.UnitId);
                if (!reached)
                {
                    result.Add(Row(member.UnitId, member.Serial, itemId, label, build?.Revision,
                        CertificateSelectionOutcome.NotIssuable, "release_record_issue_point_not_reached",
                        issuePoint == ReleaseRecordIssuePoint.AtFirstShipment || build is null
                            ? "The unit has not yet shipped to a customer."
                            : "The configured release-record issue point has not been reached.",
                        issuePoint == ReleaseRecordIssuePoint.OnRequest ? "request the release record" : "ship the unit to a customer first"));
                    continue;
                }
            }
            if (completenessByUnit.TryGetValue(member.UnitId, out var report) && report.MissingRequiredComponentSerials > 0)
            {
                var missing = string.Join(", ", report.Slots.Where(slot => slot.Outcome == SlotStatus.Missing).Select(slot => slot.Slot));
                result.Add(Row(member.UnitId, member.Serial, itemId, label, build?.Revision,
                    CertificateSelectionOutcome.NotIssuable, "missing_required_component",
                    $"Required component slot(s) {missing} are MISSING.", "record the component or its approved exception"));
                continue;
            }
            var itemClass = itemId is { } id ? profiles.GetValueOrDefault(id)?.ItemClass : null;
            if (!activeTemplates.TryGetValue(itemClass ?? "", out var active) || active is null)
            {
                result.Add(Row(member.UnitId, member.Serial, itemId, label, build?.Revision,
                    CertificateSelectionOutcome.NotIssuable, "active_template_missing",
                    $"No active {formType} template applies to this unit.", "activate a matching certificate template"));
                continue;
            }
            result.Add(Row(member.UnitId, member.Serial, itemId, label, build?.Revision,
                CertificateSelectionOutcome.IssueVersion1, null, "Version 1 will be signed as released as conforming.", null));
        }
        return result;
    }

    private async Task<CertificateRunResult> MaterialiseAsync(Guid commandId, bool includeBundle, bool replayed,
        CancellationToken cancellationToken)
    {
        var stored = await db.CertificateRunUnits.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.CommandId == commandId)
            .OrderBy(row => row.Serial).ThenBy(row => row.UnitId).ToListAsync(cancellationToken);
        var ids = stored.Select(row => row.CertificateId).OfType<Guid>().Distinct().ToList();
        var certificates = ids.Count == 0 ? [] : await db.CertificateInstances.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && ids.Contains(row.CertificateId)).ToListAsync(cancellationToken);
        var byId = certificates.ToDictionary(row => row.CertificateId);
        var signatures = ids.Count == 0 ? [] : await db.CertificateSignatures.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && ids.Contains(row.CertificateId))
            .OrderBy(row => row.SignedAt).ToListAsync(cancellationToken);
        var signatureByCertificate = signatures.GroupBy(row => row.CertificateId).ToDictionary(group => group.Key, group => group.Last());
        var predecessorIds = certificates.Select(row => row.SupersedesCertificateId).OfType<Guid>().Distinct().ToList();
        var predecessorTracking = predecessorIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.CertificateInstances.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                    && predecessorIds.Contains(row.CertificateId))
                .ToDictionaryAsync(row => row.CertificateId, row => row.TrackingNumber, cancellationToken);
        var rendered = new List<RenderedDocument>();
        foreach (var row in stored.Where(row => row.CertificateId is not null).OrderBy(row => row.Serial, StringComparer.Ordinal))
        {
            var certificate = byId[row.CertificateId!.Value];
            signatureByCertificate.TryGetValue(certificate.CertificateId, out var signature);
            var ordinary = CertificateQueries.RenderRequest(certificate, signature,
                certificate.SupersedesCertificateId is { } predecessorId
                    ? predecessorTracking.GetValueOrDefault(predecessorId) : null);
            var request = ordinary with { Copy = row.Copy };
            var render = await renderer.RenderAsync(request, cancellationToken);
            var extension = render.MediaType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase) ? "pdf" : "html";
            var serial = row.Serial ?? certificate.UnitId.ToString("N");
            var fileName = $"{Safe(serial)}-{certificate.TrackingNumber}-v{certificate.Version}{(row.Copy ? "-copy" : "")}.{extension}";
            rendered.Add(new RenderedDocument(row.UnitId, serial, certificate, fileName, request, render));
        }
        var archive = await ArchiveAsync(rendered, stored.Where(row => row.CertificateId is null).ToList(),
            includeBundle, cancellationToken);
        var rows = stored.Select(ToSelectionUnit).ToList();
        return new CertificateRunResult(commandId, replayed, Kpis(rows, includeBundle), rendered.Select(row =>
            new CertificateOutputEntry(row.UnitId, row.Serial, row.Certificate.CertificateId, row.Certificate.Version,
                row.Certificate.TrackingNumber, row.FileName, row.Render.MediaType,
                stored.Single(item => item.UnitId == row.UnitId).Copy)).ToList(),
            rows.Where(row => row.Outcome == CertificateSelectionOutcome.NotIssuable).ToList(), archive,
            "application/zip", $"certificates-{commandId:N}.zip");
    }

    private async Task<byte[]> ArchiveAsync(IReadOnlyList<RenderedDocument> documents,
        IReadOnlyList<CertificateRunUnit> skipped, bool includeBundle, CancellationToken cancellationToken)
    {
        var archiveTimestamp = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var document in documents)
            {
                var entry = archive.CreateEntry(document.FileName, CompressionLevel.Fastest);
                entry.LastWriteTime = archiveTimestamp;
                using var stream = entry.Open();
                stream.Write(document.Render.Bytes);
            }
            var cover = Cover(skipped);
            WriteText(archive, "cover.html", cover, archiveTimestamp);
            if (includeBundle && documents.Count > 0)
            {
                var bundle = await bundleRenderer.RenderBundleAsync(documents.Select(row => row.Request).ToList(), cancellationToken);
                var extension = bundle.MediaType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase) ? "pdf" : "html";
                var entry = archive.CreateEntry($"bundle.{extension}", CompressionLevel.Fastest);
                entry.LastWriteTime = archiveTimestamp;
                using var stream = entry.Open();
                await stream.WriteAsync(bundle.Bytes, cancellationToken);
            }
        }
        return output.ToArray();
    }

    private static string Cover(IReadOnlyList<CertificateRunUnit> skipped)
    {
        var rows = skipped.Count == 0 ? "<p>No units were skipped.</p>" : "<ul>" + string.Join("", skipped.Select(row =>
            $"<li>{WebUtility.HtmlEncode(row.Serial ?? row.UnitId.ToString("N"))}: {WebUtility.HtmlEncode(row.Detail ?? row.Rule ?? "not issuable")}</li>")) + "</ul>";
        return $"<section><h1>Certificate run cover</h1><h2>Skipped units</h2>{rows}</section>";
    }

    private static void WriteText(ZipArchive archive, string name, string value, DateTimeOffset archiveTimestamp)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        entry.LastWriteTime = archiveTimestamp;
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(value);
    }

    private static CertificateSelectionUnit Row(Guid unitId, string? serial, Guid? itemId, ItemLabel? item,
        string? revision, string outcome, string? reason, string? detail, string? unblockingStep) =>
        Row(unitId, serial, itemId, item?.Code, item?.Name, revision, outcome, reason, detail, unblockingStep);

    private static CertificateSelectionUnit Row(Guid unitId, string? serial, Guid? itemId, string? itemCode,
        string? itemName, string? revision, string outcome, string? reason, string? detail, string? unblockingStep) =>
        new(unitId, serial, itemId, itemCode, itemName, revision, null, null, null, outcome, reason, detail, unblockingStep);

    private static CertificateSelectionKpis Kpis(List<CertificateSelectionUnit> rows, bool bundle) => new(
        rows.Count,
        rows.Count(row => row.Outcome == CertificateSelectionOutcome.IssueVersion1),
        rows.Count(row => row.Outcome == CertificateSelectionOutcome.ReprintCopy),
        rows.Count(row => row.Outcome == CertificateSelectionOutcome.NotIssuable),
        rows.Count(row => row.Outcome != CertificateSelectionOutcome.NotIssuable), bundle);

    private static bool Matches(CertificateSelectionUnit row, string find) =>
        new[] { row.Serial, row.ItemCode, row.ItemName, row.TrackingNumber, row.Reason, row.Detail }
            .Any(value => value?.Contains(find.Trim(), StringComparison.OrdinalIgnoreCase) == true);

    private static void Validate(string documentKind, string formType, string language, bool reprintOnly = false)
    {
        if (!CertificateDocumentKind.All.Contains(documentKind))
            throw new CommandRefusedException("certificate_document_kind_unknown", $"Unknown document kind '{documentKind}'.");
        if (!reprintOnly && !CertificateFormType.All.Contains(formType))
            throw new CommandRefusedException("certificate_form_type_unknown", $"Unknown certificate form type '{formType}'.");
        if (!reprintOnly && string.IsNullOrWhiteSpace(language))
            throw new CommandRefusedException("certificate_language_required", "Certificate language is required.");
    }

    private static Guid Deterministic(Guid commandId, Guid unitId, string purpose) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{commandId:N}|{unitId:N}|certificate-selection|{purpose}")).AsSpan(0, 16));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Safe(string value) => string.Concat(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
    private CertificateRunUnit Receipt(Guid commandId, CertificateSelectionUnit row, string outcome,
        Guid? certificateId, int? version, string? trackingNumber, bool copy) => new()
    {
        TenantId = tenant.TenantId,
        CommandId = commandId,
        UnitId = row.UnitId,
        Serial = row.Serial,
        ItemId = row.ItemId,
        ItemCode = row.ItemCode,
        ItemName = row.ItemName,
        Revision = row.Revision,
        CertificateId = certificateId,
        CertificateVersion = version,
        TrackingNumber = trackingNumber,
        Outcome = outcome,
        Rule = row.Reason,
        Detail = row.Detail,
        UnblockingStep = row.UnblockingStep,
        Copy = copy,
        ProcessedAt = clock.UtcNow,
    };

    private static CertificateSelectionUnit ToSelectionUnit(CertificateRunUnit row) => new(row.UnitId, row.Serial, row.ItemId,
        row.ItemCode, row.ItemName, row.Revision, row.CertificateId, row.CertificateVersion, row.TrackingNumber,
        row.Outcome switch
        {
            CertificateRunUnitOutcome.Issued => CertificateSelectionOutcome.IssueVersion1,
            CertificateRunUnitOutcome.Copied => CertificateSelectionOutcome.ReprintCopy,
            _ => CertificateSelectionOutcome.NotIssuable,
        }, row.Rule, row.Detail, row.UnblockingStep);

    private async Task<int> SelectionCountAsync(Guid selectionId, CancellationToken cancellationToken)
    {
        if (!await db.SelectionSets.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId
            && row.SelectionId == selectionId, cancellationToken))
            throw new CommandRefusedException("selection_unknown", $"Selection {selectionId} does not exist.");
        return await db.SelectionSetMembers.AsNoTracking().CountAsync(row => row.TenantId == tenant.TenantId
            && row.SelectionId == selectionId, cancellationToken);
    }

    private sealed record SelectionMember(Guid UnitId, string? Serial);
    private sealed record RenderedDocument(Guid UnitId, string Serial, CertificateInstance Certificate,
        string FileName, CertificateRenderRequest Request, CertificateRenderResult Render);
}

// TIGHT COUPLING (certificate run transaction boundary)
// WHAT: these two handlers must remain NonTransactional while CertificateSelectionService owns chunk transactions.
// WHY: Wolverine otherwise wraps the complete selection in one transaction outside the service's chunk commits.
// WHAT BREAKS IF SEPARATED: all chain locks and outbox rows remain open until the last unit, recreating the long transaction.
[NonTransactional]
public static class IssueCertificateHandler
{
    public static Task<CertificateRunResult> Handle(IssueCertificate command, CertificateSelectionService service,
        CancellationToken cancellationToken) => service.IssueAsync(command, cancellationToken);
}

[NonTransactional]
public static class ReprintCertificateHandler
{
    public static Task<CertificateRunResult> Handle(ReprintCertificate command, CertificateSelectionService service,
        CancellationToken cancellationToken) => service.ReprintAsync(command, cancellationToken);
}
