using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Certificates;

/// <summary>Composes certificate-owned presentation with facts read only through owner-module ports.</summary>
public sealed class CertificatePayloadBuilder(ICertificateUnitReadPort units, IItemQueryPort items, ICertificatePresentationPort presentation,
    ReleaseRecordPayloadBuilder releases, WarrantyCertificatePayloadBuilder warranties)
{
    public async Task<PayloadBuildResult?> BuildAsync(IssueCertificateVersion command, Guid templateVersionId,
        CancellationToken cancellationToken)
    {
        var unit = await units.ReadAsync(command.UnitId, cancellationToken);
        if (unit is null) return null;
        return await BuildAsync(command, unit, templateVersionId, cancellationToken);
    }

    public async Task<string?> ItemClassAsync(CertificateUnitFact unit, CancellationToken cancellationToken) =>
        (await items.GetAsync(unit.ItemId, cancellationToken))?.ItemClass;

    public async Task<PayloadBuildResult> BuildAsync(IssueCertificateVersion command, CertificateUnitFact unit,
        Guid templateVersionId, CancellationToken cancellationToken)
    {
        var itemClass = await ItemClassAsync(unit, cancellationToken);
        var issuePresentation = await presentation.ResolveAsync(command.IssuerEntityId, command.DocumentKind,
            command.FormType, command.Language, templateVersionId, cancellationToken);
        var triggerSchema = command.TriggerSchema ?? TriggerSchema(command);
        var correction = command.DocumentKind == CertificateDocumentKind.ReleaseRecord
            && (command.SupersedeReason == CertificateSupersedeReason.Correction
                || triggerSchema is UnitIdentifierCorrected.Schema or UnitIdentifiersSwapped.Schema
                    or UnitItemCorrected.Schema or UnitAsBuiltCorrected.Schema);
        var trigger = new CertificateTriggerSnapshot(triggerSchema,
            command.TriggeringEventId, command.EffectiveAt, command.SupersedeReason,
            correction ? false : null,
            correction ? "Verified record correction; unit condition was not re-verified." : null);
        var provenance = new CertificateProvenanceSnapshot(command.Provenance, command.Unknowns ?? [], command.Evidence ?? []);
        if (command.DocumentKind == CertificateDocumentKind.ReleaseRecord)
        {
            var payload = await releases.BuildAsync(unit, trigger, command.FormType, issuePresentation, provenance, cancellationToken);
            return new PayloadBuildResult(CanonicalJson.Serialise(payload), unit.Build?.BuildRecordId, null, itemClass);
        }
        if (command.DocumentKind == CertificateDocumentKind.WarrantyCertificate)
        {
            var (payload, owner) = await warranties.BuildAsync(unit, trigger, issuePresentation, provenance, cancellationToken);
            return new PayloadBuildResult(CanonicalJson.Serialise(payload), unit.Build?.BuildRecordId, owner, itemClass);
        }
        throw new ArgumentException($"Unknown certificate document kind '{command.DocumentKind}'.", nameof(command));
    }

    private static string TriggerSchema(IssueCertificateVersion command) => command.SupersedeReason switch
    {
        CertificateSupersedeReason.EntitlementChange => "Entitlement.Change",
        CertificateSupersedeReason.IdentifierChange => "Unit.IdentifierChange",
        CertificateSupersedeReason.Refurbishment => "Refurbishment.Released",
        _ => command.DocumentKind == CertificateDocumentKind.ReleaseRecord ? "Unit.ReleaseFact" : "Warranty.Change",
    };
}

public sealed record PayloadBuildResult(string CanonicalPayload, Guid? AsBuiltRevisionId, CertificateOwnerSnapshot? Owner,
    string? ItemClass);
