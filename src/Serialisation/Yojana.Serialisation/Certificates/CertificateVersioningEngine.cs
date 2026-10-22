using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Certificates;

public sealed class CertificateVersioningEngine(SerialisationDbContext db, ITenantContext tenant, IClock clock,
    ICertificateUnitReadPort units, CertificatePayloadBuilder payloads, CertificateTemplateService templates, DocumentNumbers numbers,
    IUnitEventWriter events, IUserDirectory users, CertificateDatabaseLock databaseLock,
    ReleaseRecordIssuePolicy issuePolicy)
{
    public Task<IssueCertificateVersionResult> IssueAsync(string documentKind, CertificateTriggerContext trigger,
        string reason, CancellationToken cancellationToken) => IssueAsync(new IssueCertificateVersion(
            trigger.EventId, trigger.UnitId, documentKind,
            documentKind == CertificateDocumentKind.ReleaseRecord ? CertificateFormType.GenericRelease : CertificateFormType.WarrantyCertificate,
            reason, trigger.EventTime, "system:certificates", trigger.LegalEntityId, TriggerSchema: trigger.Schema), cancellationToken);

    public async Task<IssueCertificateVersionResult> IssueAsync(IssueCertificateVersion command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (!CertificateDocumentKind.All.Contains(command.DocumentKind))
            return Refused("certificate_document_kind_unknown", $"Unknown document kind '{command.DocumentKind}'.");
        if (!CertificateFormType.All.Contains(command.FormType))
            return Refused("certificate_form_type_unknown", $"Unknown certificate form type '{command.FormType}'.");
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            // TIGHT COUPLING (certificate chain serialization)
            // WHAT: every issue attempt for a (tenant, unit, document kind) takes the same transaction-owned SQL lock.
            // WHY: an empty chain has no database row to lock and SQL Server does not defer the unique version index.
            // WHAT BREAKS IF SEPARATED: concurrent triggers can both choose the same predecessor/version and one is
            // dead-lettered. The matching model comment documents the receipt/index side of this contract. The batched
            // migration path (IssueMigratedReleaseRecordsAsync) takes these same resources through AcquireChainsAsync;
            // renaming the resource on one side only lets a live issue and a migration chunk build the same chain.
            await databaseLock.AcquireChainAsync(command.UnitId, command.DocumentKind, cancellationToken);

            async Task PersistAsync()
            {
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            }

            async Task<IssueCertificateVersionResult> RefuseAsync(string rule, string detail)
            {
                db.CertificateTriggers.Add(Trigger(CertificateTriggerOutcome.Refused, null, rule, detail));
                await PersistAsync();
                return Refused(rule, detail);
            }

            var receipt = await db.CertificateTriggers.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId
                && row.UnitId == command.UnitId && row.DocumentKind == command.DocumentKind
                && row.TriggeringEventId == command.TriggeringEventId, cancellationToken);
            if (receipt is not null)
            {
                if (receipt.Outcome == CertificateTriggerOutcome.Issued && receipt.CertificateId is { } certificateId)
                {
                    var replay = await db.CertificateInstances.AsNoTracking().SingleAsync(row => row.TenantId == tenantId
                        && row.CertificateId == certificateId, cancellationToken);
                    if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                    return new(true, true, replay.CertificateId, replay.Version, replay.TrackingNumber);
                }
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                return receipt.Outcome == CertificateTriggerOutcome.Absorbed
                    ? new(false, true, null, null, null, receipt.RefusalRule, receipt.RefusalDetail, Absorbed: true)
                    : new(false, true, null, null, null, receipt.RefusalRule, receipt.RefusalDetail);
            }

            if (!CertificateProvenance.All.Contains(command.Provenance))
                return await RefuseAsync("certificate_provenance_unknown", $"Unknown certificate provenance '{command.Provenance}'.");
            if (command.Provenance != CertificateProvenance.SystemGenerated
                && (command.Unknowns is not { Count: > 0 } || command.Evidence is not { Count: > 0 }))
                return await RefuseAsync("certificate_provenance_evidence_required", "Migrated and reconstructed certificates require explicit unknowns and evidence.");
            var unit = await units.ReadAsync(command.UnitId, cancellationToken);
            if (unit is null) return await RefuseAsync("unit_unknown", $"Unit {command.UnitId} does not exist.");
            if (unit.LifecycleStatus != LifecycleStatus.Released)
                return await RefuseAsync("certificate_unit_not_released", $"Unit {command.UnitId} is '{unit.LifecycleStatus}'; certificates can be issued only for released units.");
            var itemClass = await payloads.ItemClassAsync(unit, cancellationToken);
            var latest = await db.CertificateInstances.Where(row => row.TenantId == tenantId && row.UnitId == command.UnitId
                    && row.DocumentKind == command.DocumentKind)
                .OrderByDescending(row => row.Version).FirstOrDefaultAsync(cancellationToken);
            var previous = latest?.Status == CertificateStatus.Issued ? latest : null;
            // COUPLING (automatic language inheritance): CertificateSubscriptionHandlers sends an empty language on
            // its internal published-fact hand-off because those events carry no presentation language. It means
            // "continue this chain's explicit language"; separating the sentinel from either side silently changes
            // an automatic version of a non-English certificate to English.
            if (string.IsNullOrWhiteSpace(command.Language))
                command = command with { Language = previous?.Language ?? "en" };
            if (command.DocumentKind == CertificateDocumentKind.ReleaseRecord
                && command.Provenance != CertificateProvenance.Migrated)
            {
                var triggerSchema = command.TriggerSchema ?? "Unit.ReleaseFact";
                if (triggerSchema is CustodyChanged.Schema or OwnershipOpened.Schema
                    && await issuePolicy.SaleFactWasCorrectedAsync(command.UnitId, command.TriggeringEventId,
                        cancellationToken))
                {
                    db.CertificateTriggers.Add(Trigger(CertificateTriggerOutcome.Absorbed, null,
                        "release_record_sale_fact_reversed",
                        "The customer-sale fact that reached the issue point was withdrawn by a document correction."));
                    await PersistAsync();
                    return new(false, false, null, null, null, "release_record_sale_fact_reversed",
                        "The customer-sale fact that reached the issue point was withdrawn by a document correction.",
                        Absorbed: true);
                }
                var decision = await issuePolicy.DecideAsync(unit, itemClass, triggerSchema, previous is not null,
                    cancellationToken);
                if (!decision.Issue)
                {
                    db.CertificateTriggers.Add(Trigger(CertificateTriggerOutcome.Absorbed, null,
                        decision.Reason, decision.Detail));
                    await PersistAsync();
                    return new(false, false, null, null, null, decision.Reason, decision.Detail, Absorbed: true);
                }
            }
            var templateVersionId = await templates.ActiveVersionAsync(command.DocumentKind, command.FormType,
                itemClass, cancellationToken);
            if (templateVersionId is null)
                return await RefuseAsync("certificate_template_unavailable", $"No active {command.FormType} template exists for {command.DocumentKind}.");
            PayloadBuildResult? built;
            try
            {
                built = await payloads.BuildAsync(command, unit, templateVersionId.Value, cancellationToken);
            }
            catch (CertificatePayloadRefusedException exception)
            {
                return await RefuseAsync(exception.Refusal.Rule, exception.Refusal.Message);
            }
            if (built is null) return await RefuseAsync("certificate_payload_unavailable", $"Certificate facts for unit {command.UnitId} are unavailable.");
            var factsHash = CanonicalJson.HashExcludingRootProperties(built.CanonicalPayload, "trigger");
            if (previous?.FactsHash == factsHash)
            {
                const string reason = "certificate_facts_unchanged";
                const string detail = "The trigger reached an existing certificate chain, but the certificate facts have not changed.";
                db.CertificateTriggers.Add(Trigger(CertificateTriggerOutcome.Absorbed, null, reason, detail));
                await PersistAsync();
                return new(false, false, null, null, null, reason, detail, Absorbed: true);
            }
            var version = (latest?.Version ?? 0) + 1;
            if (version > 1 && string.IsNullOrWhiteSpace(command.SupersedeReason))
                return await RefuseAsync("certificate_supersede_reason_required", "A later certificate version requires a supersede reason.");
            var issuedAt = clock.UtcNow;
            var tracking = await numbers.NextAsync(DocumentNumbers.Certificate, issuedAt, cancellationToken);
            var printedName = await PrintedNameAsync(command.ActorRef, cancellationToken);
            var (certificate, signature, issuedDraft) = NewVersion(command, templateVersionId.Value, built, factsHash,
                version, tracking, issuedAt, latest, previous?.CertificateId, printedName);
            db.CertificateInstances.Add(certificate);
            db.CertificateSignatures.Add(signature);
            db.CertificateTriggers.Add(Trigger(CertificateTriggerOutcome.Issued, certificate.CertificateId));
            if (previous is not null) previous.Status = CertificateStatus.Superseded;
            var drafts = new List<UnitEventDraft>();
            if (previous is not null)
            {
                drafts.Add(new UnitEventDraft
                {
                    UnitId = command.UnitId, EventTime = command.EffectiveAt, EventType = CertificateEventType.Superseded,
                    LegalEntityId = command.IssuerEntityId, ActorRef = command.ActorRef,
                    IdempotencyKey = $"cert:sup:{previous.CertificateId:N}:{certificate.CertificateId.ToString("N")[..12]}",
                    Attributes = JsonObjectText.Of(new
                    {
                        certificate_id = previous.CertificateId, superseding_certificate_id = certificate.CertificateId,
                        document_kind = command.DocumentKind, certificate_version = previous.Version,
                        reason = command.SupersedeReason, triggering_event_id = command.TriggeringEventId, superseded_at = issuedAt,
                    }),
                    ProjectCurrentState = false,
                });
            }
            drafts.Add(issuedDraft);
            await events.AppendAsync(drafts, cancellationToken);
            await PersistAsync();
            return new(true, false, certificate.CertificateId, certificate.Version, certificate.TrackingNumber);

            CertificateTrigger Trigger(string outcome, Guid? certificateId = null, string? refusalRule = null, string? refusalDetail = null) =>
                TriggerRow(command, outcome, certificateId, refusalRule, refusalDetail);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Records version-one release records for units a migration load created in the caller's open transaction (OD-55):
    /// the same rules and rows as <see cref="IssueAsync(IssueCertificateVersion, CancellationToken)"/>, with one read per
    /// table for the whole chunk instead of about fifteen round trips per unit. Only migrated release records for units
    /// without a chain or receipt take the batched path; any other command, and any unit that already has a chain or a
    /// receipt, goes through <see cref="IssueAsync(IssueCertificateVersion, CancellationToken)"/> one at a time, so replay,
    /// supersede and absorb behaviour stay in one place. Results are in command order.
    /// </summary>
    public async Task<IReadOnlyList<IssueCertificateVersionResult>> IssueMigratedReleaseRecordsAsync(
        IReadOnlyList<IssueCertificateVersion> commands, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var results = new IssueCertificateVersionResult?[commands.Count];
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var candidates = commands.Select((command, index) => (Command: command with
                {
                    Language = string.IsNullOrWhiteSpace(command.Language) ? "en" : command.Language,
                }, Index: index))
                .Where(row => row.Command.DocumentKind == CertificateDocumentKind.ReleaseRecord
                    && row.Command.FormType == CertificateFormType.GenericRelease
                    && row.Command.Provenance == CertificateProvenance.Migrated
                    && row.Command.Unknowns is { Count: > 0 } && row.Command.Evidence is { Count: > 0 }
                    && row.Command.SupersedeReason is null)
                .ToList();
            if (candidates.Select(row => row.Command.UnitId).Distinct().Count() != candidates.Count)
                candidates = candidates.GroupBy(row => row.Command.UnitId).Where(group => group.Count() == 1)
                    .Select(group => group.Single()).ToList();
            var unitIds = candidates.Select(row => row.Command.UnitId).ToList();
            // The same per-chain locks IssueAsync takes (see its TIGHT COUPLING note), in one round trip.
            await databaseLock.AcquireChainsAsync(unitIds, CertificateDocumentKind.ReleaseRecord, cancellationToken);
            var chained = await db.CertificateInstances.AsNoTracking().Where(row => row.TenantId == tenantId
                    && unitIds.Contains(row.UnitId) && row.DocumentKind == CertificateDocumentKind.ReleaseRecord)
                .Select(row => row.UnitId).Distinct().ToListAsync(cancellationToken);
            var receipted = await db.CertificateTriggers.AsNoTracking().Where(row => row.TenantId == tenantId
                    && unitIds.Contains(row.UnitId) && row.DocumentKind == CertificateDocumentKind.ReleaseRecord)
                .Select(row => new { row.UnitId, row.TriggeringEventId }).ToListAsync(cancellationToken);
            var skip = chained.Concat(receipted.Where(row => candidates.Any(candidate =>
                    candidate.Command.UnitId == row.UnitId && candidate.Command.TriggeringEventId == row.TriggeringEventId))
                .Select(row => row.UnitId)).ToHashSet();
            candidates = candidates.Where(row => !skip.Contains(row.Command.UnitId)).ToList();

            var unitFacts = await units.ReadManyAsync(candidates.Select(row => row.Command.UnitId).ToList(), cancellationToken);
            var itemClasses = await payloads.ItemClassesAsync(unitFacts.Values.Select(unit => unit.ItemId).ToList(),
                cancellationToken);
            var templateVersions = await templates.ActiveVersionsAsync(CertificateDocumentKind.ReleaseRecord,
                CertificateFormType.GenericRelease, itemClasses.Values.Distinct().ToList(), cancellationToken);
            var ready = new List<(IssueCertificateVersion Command, int Index, CertificateUnitFact Unit, Guid TemplateVersionId, string? ItemClass)>();
            foreach (var (command, index) in candidates)
            {
                if (!unitFacts.TryGetValue(command.UnitId, out var unit))
                {
                    results[index] = Refuse(command, "unit_unknown", $"Unit {command.UnitId} does not exist.");
                    continue;
                }
                if (unit.LifecycleStatus != LifecycleStatus.Released)
                {
                    results[index] = Refuse(command, "certificate_unit_not_released",
                        $"Unit {command.UnitId} is '{unit.LifecycleStatus}'; certificates can be issued only for released units.");
                    continue;
                }
                var itemClass = itemClasses.GetValueOrDefault(unit.ItemId);
                if (templateVersions.GetValueOrDefault(itemClass ?? "") is not { } templateVersionId)
                {
                    results[index] = Refuse(command, "certificate_template_unavailable",
                        $"No active {command.FormType} template exists for {command.DocumentKind}.");
                    continue;
                }
                ready.Add((command, index, unit, templateVersionId, itemClass));
            }

            if (ready.Count > 0)
            {
                var built = await payloads.BuildReleaseRecordsAsync(
                    ready.Select(row => (row.Command, row.Unit, row.TemplateVersionId, row.ItemClass)).ToList(), cancellationToken);
                var issuedAt = clock.UtcNow;
                var tracking = await numbers.NextRangeAsync(DocumentNumbers.Certificate, issuedAt, ready.Count, cancellationToken);
                var printedNames = new Dictionary<string, string>(StringComparer.Ordinal);
                var drafts = new List<UnitEventDraft>(ready.Count);
                for (var i = 0; i < ready.Count; i++)
                {
                    var (command, index, _, templateVersionId, _) = ready[i];
                    if (!printedNames.TryGetValue(command.ActorRef, out var printedName))
                        printedNames[command.ActorRef] = printedName = await PrintedNameAsync(command.ActorRef, cancellationToken);
                    var factsHash = CanonicalJson.HashExcludingRootProperties(built[i].CanonicalPayload, "trigger");
                    var (certificate, signature, issuedDraft) = NewVersion(command, templateVersionId, built[i], factsHash,
                        1, tracking[i], issuedAt, null, null, printedName);
                    db.CertificateInstances.Add(certificate);
                    db.CertificateSignatures.Add(signature);
                    db.CertificateTriggers.Add(TriggerRow(command, CertificateTriggerOutcome.Issued, certificate.CertificateId));
                    drafts.Add(issuedDraft);
                    results[index] = new(true, false, certificate.CertificateId, certificate.Version, certificate.TrackingNumber);
                }
                await events.AppendAsync(drafts, cancellationToken);
            }
            await db.SaveChangesAsync(cancellationToken);

            for (var index = 0; index < commands.Count; index++)
                results[index] ??= await IssueAsync(commands[index], cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return results.Select(result => result!).ToList();
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        IssueCertificateVersionResult Refuse(IssueCertificateVersion command, string rule, string detail)
        {
            db.CertificateTriggers.Add(TriggerRow(command, CertificateTriggerOutcome.Refused, null, rule, detail));
            return Refused(rule, detail);
        }
    }

    /// <summary>The instance, its signature and its issued fact: shared by the single and the batched issue paths.</summary>
    private (CertificateInstance Certificate, CertificateSignature Signature, UnitEventDraft Issued) NewVersion(
        IssueCertificateVersion command, Guid templateVersionId, PayloadBuildResult built, string factsHash, int version,
        string tracking, DateTime issuedAt, CertificateInstance? latest, Guid? previousIssuedId, string printedName)
    {
        var hash = CanonicalJson.Hash(built.CanonicalPayload);
        var issuer = ResolveIssuerSnapshot(built.CanonicalPayload);
        var branding = DeserializeSnapshot<CertificateBrandingSnapshot>(built.CanonicalPayload, "branding");
        var certificate = new CertificateInstance
        {
            TenantId = tenant.TenantId, CertificateId = TimeOrderedId.New(issuedAt), UnitId = command.UnitId,
            MigrationLoadId = command.MigrationLoadId,
            DocumentKind = command.DocumentKind, FormType = command.FormType, Version = version, TrackingNumber = tracking,
            TemplateVersionId = templateVersionId, AsBuiltRevisionId = built.AsBuiltRevisionId,
            Payload = built.CanonicalPayload, PayloadHash = hash, FactsHash = factsHash,
            SupersedesCertificateId = latest?.CertificateId,
            SupersedeReason = latest is null ? null : command.SupersedeReason, TriggeringEventId = command.TriggeringEventId,
            IssuerEntityId = command.IssuerEntityId, IssuerSnapshot = CanonicalJson.Serialise(issuer),
            OwnerSnapshot = built.Owner is null ? null : CanonicalJson.Serialise(built.Owner),
            BrandingSnapshot = CanonicalJson.Serialise(branding), Language = command.Language, Provenance = command.Provenance,
            Unknowns = CanonicalJson.Serialise(command.Unknowns ?? []), Evidence = CanonicalJson.Serialise(command.Evidence ?? []),
            IssuedAt = issuedAt, Status = CertificateStatus.Issued,
        };
        var signature = new CertificateSignature
        {
            TenantId = tenant.TenantId, SignatureId = TimeOrderedId.New(issuedAt), CertificateId = certificate.CertificateId,
            SignerIdentity = command.ActorRef, PrintedName = printedName,
            Meaning = command.DocumentKind == CertificateDocumentKind.ReleaseRecord ? CertificateSignatureMeaning.Release : CertificateSignatureMeaning.Approval,
            SignedAt = issuedAt, HashSigned = hash, SignatureMethod = command.ActorRef.StartsWith("system:", StringComparison.Ordinal) ? "system_attestation" : "authenticated_user",
        };
        var issued = new UnitEventDraft
        {
            UnitId = command.UnitId, EventTime = command.EffectiveAt, EventType = CertificateEventType.Issued,
            LegalEntityId = command.IssuerEntityId, ActorRef = command.ActorRef,
            IdempotencyKey = $"cert:issued:{(command.DocumentKind == CertificateDocumentKind.ReleaseRecord ? "rr" : "wc")}:{command.TriggeringEventId:N}",
            Attributes = JsonObjectText.Of(new
            {
                certificate_id = certificate.CertificateId, document_kind = certificate.DocumentKind, form_type = certificate.FormType,
                certificate_version = certificate.Version, tracking_number = certificate.TrackingNumber, payload_hash = certificate.PayloadHash,
                triggering_event_id = command.TriggeringEventId, supersedes_certificate_id = previousIssuedId,
                provenance = certificate.Provenance, migration_load_id = certificate.MigrationLoadId,
                issued_at = certificate.IssuedAt,
            }),
            ProjectCurrentState = false,
        };
        return (certificate, signature, issued);
    }

    private CertificateTrigger TriggerRow(IssueCertificateVersion command, string outcome, Guid? certificateId = null,
        string? refusalRule = null, string? refusalDetail = null) => new()
    {
        TenantId = tenant.TenantId, UnitId = command.UnitId, DocumentKind = command.DocumentKind,
        TriggeringEventId = command.TriggeringEventId, Outcome = outcome, CertificateId = certificateId,
        RefusalRule = refusalRule, RefusalDetail = refusalDetail is { Length: > 500 } ? refusalDetail[..500] : refusalDetail,
        RecordedAt = clock.UtcNow,
    };

    /// <summary>
    /// Retains every immutable version but removes the active release record when its original sale was corrected away.
    /// No replacement version is invented because CorrectDocumentSerials changes no release-record payload fact.
    /// </summary>
    public async Task<bool> WithdrawForSaleReversalAsync(CertificateSaleReversalWork work,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            await databaseLock.AcquireChainAsync(work.UnitId, CertificateDocumentKind.ReleaseRecord,
                cancellationToken);
            var chain = await db.CertificateInstances
                .Where(row => row.TenantId == tenantId && row.UnitId == work.UnitId
                    && row.DocumentKind == CertificateDocumentKind.ReleaseRecord)
                .OrderBy(row => row.Version).ToListAsync(cancellationToken);
            var root = chain.FirstOrDefault();
            var current = chain.LastOrDefault(row => row.Status == CertificateStatus.Issued);
            if (root is null || current is null
                || !await RootWasIssuedForReversedSaleAsync(root, work, cancellationToken))
            {
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                return false;
            }

            current.Status = CertificateStatus.Superseded;
            var withdrawnAt = clock.UtcNow;
            await events.AppendAsync([new UnitEventDraft
            {
                UnitId = work.UnitId,
                EventTime = work.EffectiveAt,
                EventType = CertificateEventType.Withdrawn,
                LegalEntityId = work.LegalEntityId,
                ActorRef = "system:certificates",
                DocumentType = work.DocumentType,
                DocumentId = work.DocumentId,
                DocumentLine = work.DocumentLine,
                IdempotencyKey = $"cert:withdraw:rr:{work.CorrectionEventId:N}",
                Attributes = JsonObjectText.Of(new
                {
                    certificate_id = current.CertificateId,
                    document_kind = current.DocumentKind,
                    certificate_version = current.Version,
                    reason = CertificateSupersedeReason.Correction,
                    triggering_correction_event_id = work.CorrectionEventId,
                    reversed_sale_event_id = work.ReversedSaleEventId,
                    withdrawn_at = withdrawnAt,
                }),
                ProjectCurrentState = false,
            }], cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<bool> RootWasIssuedForReversedSaleAsync(CertificateInstance root,
        CertificateSaleReversalWork work, CancellationToken cancellationToken)
    {
        if (root.TriggeringEventId == work.ReversedSaleEventId) return true;
        if (work.DocumentType is null || work.DocumentId is null) return false;
        return await db.Events.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId
            && row.UnitId == work.UnitId && row.EventId == root.TriggeringEventId
            && row.DocumentType == work.DocumentType && row.DocumentId == work.DocumentId
            && (work.DocumentLine == null || row.DocumentLine == work.DocumentLine), cancellationToken);
    }

    private static CertificateIssuerSnapshot ResolveIssuerSnapshot(string payload)
    {
        return DeserializeSnapshot<CertificateIssuerSnapshot>(payload, "issuer");
    }

    private static T DeserializeSnapshot<T>(string payload, string property)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty(property).Deserialize<T>(CanonicalJson.Options)
            ?? throw new InvalidOperationException($"Certificate payload snapshot '{property}' is invalid.");
    }

    private async Task<string> PrintedNameAsync(string actorRef, CancellationToken cancellationToken)
    {
        if (actorRef.StartsWith("system:", StringComparison.Ordinal)) return "Certificate service";
        var names = await users.DisplayNamesAsync([actorRef], cancellationToken);
        return names.GetValueOrDefault(actorRef) ?? "Authenticated user";
    }

    private static IssueCertificateVersionResult Refused(string rule, string detail) => new(false, false, null, null, null, rule, detail);
}

public static class IssueCertificateVersionHandler
{
    public static Task<IssueCertificateVersionResult> Handle(IssueCertificateVersion command,
        CertificateVersioningEngine engine, CancellationToken cancellationToken) => engine.IssueAsync(command, cancellationToken);
}
