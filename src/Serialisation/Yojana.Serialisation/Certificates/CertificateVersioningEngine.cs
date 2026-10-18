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
    IUnitEventWriter events, IUserDirectory users, CertificateDatabaseLock databaseLock)
{
    public Task<IssueCertificateVersionResult> IssueAsync(string documentKind, CertificateTriggerContext trigger,
        string reason, CancellationToken cancellationToken) => IssueAsync(new IssueCertificateVersion(
            trigger.EventId, trigger.UnitId, documentKind,
            documentKind == CertificateDocumentKind.ReleaseRecord ? CertificateFormType.GenericRelease : CertificateFormType.Custom,
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
            // dead-lettered. The matching model comment documents the receipt/index side of this contract.
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
                    ? new(false, true, null, null, null, Absorbed: true)
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
            if (command.DocumentKind == CertificateDocumentKind.ReleaseRecord && built.AsBuiltRevisionId is null)
                return await RefuseAsync("certificate_build_record_missing", $"Released unit {command.UnitId} has no BuildRecord.");
            var previous = await db.CertificateInstances.Where(row => row.TenantId == tenantId && row.UnitId == command.UnitId
                    && row.DocumentKind == command.DocumentKind && row.Status == CertificateStatus.Issued)
                .OrderByDescending(row => row.Version).FirstOrDefaultAsync(cancellationToken);
            var factsHash = CanonicalJson.HashExcludingRootProperties(built.CanonicalPayload, "trigger");
            if (previous?.FactsHash == factsHash)
            {
                db.CertificateTriggers.Add(Trigger(CertificateTriggerOutcome.Absorbed));
                await PersistAsync();
                return new(false, false, null, null, null, Absorbed: true);
            }
            var version = (previous?.Version ?? 0) + 1;
            if (version > 1 && string.IsNullOrWhiteSpace(command.SupersedeReason))
                return await RefuseAsync("certificate_supersede_reason_required", "A later certificate version requires a supersede reason.");
            var issuedAt = clock.UtcNow;
            var tracking = await numbers.NextAsync(DocumentNumbers.Certificate, issuedAt, cancellationToken);
            var hash = CanonicalJson.Hash(built.CanonicalPayload);
            var issuer = ResolveIssuerSnapshot(built.CanonicalPayload);
            var branding = DeserializeSnapshot<CertificateBrandingSnapshot>(built.CanonicalPayload, "branding");
            var certificate = new CertificateInstance
            {
                TenantId = tenantId, CertificateId = TimeOrderedId.New(issuedAt), UnitId = command.UnitId,
                DocumentKind = command.DocumentKind, FormType = command.FormType, Version = version, TrackingNumber = tracking,
                TemplateVersionId = templateVersionId.Value, AsBuiltRevisionId = built.AsBuiltRevisionId,
                Payload = built.CanonicalPayload, PayloadHash = hash, FactsHash = factsHash,
                SupersedesCertificateId = previous?.CertificateId,
                SupersedeReason = previous is null ? null : command.SupersedeReason, TriggeringEventId = command.TriggeringEventId,
                IssuerEntityId = command.IssuerEntityId, IssuerSnapshot = CanonicalJson.Serialise(issuer),
                OwnerSnapshot = built.Owner is null ? null : CanonicalJson.Serialise(built.Owner),
                BrandingSnapshot = CanonicalJson.Serialise(branding), Language = command.Language, Provenance = command.Provenance,
                Unknowns = CanonicalJson.Serialise(command.Unknowns ?? []), Evidence = CanonicalJson.Serialise(command.Evidence ?? []),
                IssuedAt = issuedAt, Status = CertificateStatus.Issued,
            };
            var printedName = await PrintedNameAsync(command.ActorRef, cancellationToken);
            db.CertificateInstances.Add(certificate);
            db.CertificateSignatures.Add(new CertificateSignature
            {
                TenantId = tenantId, SignatureId = TimeOrderedId.New(issuedAt), CertificateId = certificate.CertificateId,
                SignerIdentity = command.ActorRef, PrintedName = printedName,
                Meaning = command.DocumentKind == CertificateDocumentKind.ReleaseRecord ? CertificateSignatureMeaning.Release : CertificateSignatureMeaning.Approval,
                SignedAt = issuedAt, HashSigned = hash, SignatureMethod = command.ActorRef.StartsWith("system:", StringComparison.Ordinal) ? "system_attestation" : "authenticated_user",
            });
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
                });
            }
            drafts.Add(new UnitEventDraft
            {
                UnitId = command.UnitId, EventTime = command.EffectiveAt, EventType = CertificateEventType.Issued,
                LegalEntityId = command.IssuerEntityId, ActorRef = command.ActorRef,
                IdempotencyKey = $"cert:issued:{(command.DocumentKind == CertificateDocumentKind.ReleaseRecord ? "rr" : "wc")}:{command.TriggeringEventId:N}",
                Attributes = JsonObjectText.Of(new
                {
                    certificate_id = certificate.CertificateId, document_kind = certificate.DocumentKind, form_type = certificate.FormType,
                    certificate_version = certificate.Version, tracking_number = certificate.TrackingNumber, payload_hash = certificate.PayloadHash,
                    triggering_event_id = command.TriggeringEventId, supersedes_certificate_id = previous?.CertificateId,
                    provenance = certificate.Provenance, issued_at = certificate.IssuedAt,
                }),
            });
            await events.AppendAsync(drafts, cancellationToken);
            await PersistAsync();
            return new(true, false, certificate.CertificateId, certificate.Version, certificate.TrackingNumber);

            CertificateTrigger Trigger(string outcome, Guid? certificateId = null, string? refusalRule = null, string? refusalDetail = null) => new()
            {
                TenantId = tenantId, UnitId = command.UnitId, DocumentKind = command.DocumentKind,
                TriggeringEventId = command.TriggeringEventId, Outcome = outcome, CertificateId = certificateId,
                RefusalRule = refusalRule, RefusalDetail = refusalDetail is { Length: > 500 } ? refusalDetail[..500] : refusalDetail,
                RecordedAt = clock.UtcNow,
            };
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
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
