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
        var trigger = Trigger(command);
        var provenance = Provenance(command);
        if (command.DocumentKind == CertificateDocumentKind.ReleaseRecord)
        {
            var payload = await releases.BuildAsync(unit, trigger, command.FormType, issuePresentation, provenance,
                cancellationToken, itemClass);
            return new PayloadBuildResult(CanonicalJson.Serialise(payload), unit.Build?.BuildRecordId, null, itemClass);
        }
        if (command.DocumentKind == CertificateDocumentKind.WarrantyCertificate)
        {
            var (payload, owner) = await warranties.BuildAsync(unit, trigger, issuePresentation, provenance, cancellationToken);
            return new PayloadBuildResult(CanonicalJson.Serialise(payload), unit.Build?.BuildRecordId, owner, itemClass);
        }
        throw new ArgumentException($"Unknown certificate document kind '{command.DocumentKind}'.", nameof(command));
    }

    /// <summary>Item classes for many items in one read; items the item port does not know answer null.</summary>
    public async Task<IReadOnlyDictionary<Guid, string?>> ItemClassesAsync(IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken) =>
        (await items.GetManyAsync(itemIds.Distinct().ToList(), cancellationToken))
            .ToDictionary(pair => pair.Key, pair => pair.Value?.ItemClass);

    /// <summary>
    /// Release-record payloads for many units with one read per port (the migration load's batched certificate path).
    /// Each result is what <see cref="BuildAsync(IssueCertificateVersion, CertificateUnitFact, Guid, CancellationToken)"/>
    /// gives for the same input: trigger, provenance and presentation come from the same helpers, and composition from
    /// <see cref="ReleaseRecordPayloadBuilder.BuildManyAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<PayloadBuildResult>> BuildReleaseRecordsAsync(
        IReadOnlyList<(IssueCertificateVersion Command, CertificateUnitFact Unit, Guid TemplateVersionId, string? ItemClass)> inputs,
        CancellationToken cancellationToken)
    {
        if (inputs.Any(input => input.Command.DocumentKind != CertificateDocumentKind.ReleaseRecord))
            throw new ArgumentException("Only release records are built in batches.", nameof(inputs));
        var presentations = new Dictionary<(Guid, string, string, string, Guid), CertificatePresentation>();
        var builds = new List<ReleaseRecordBuildInput>(inputs.Count);
        foreach (var (command, unit, templateVersionId, itemClass) in inputs)
        {
            var key = (command.IssuerEntityId, command.DocumentKind, command.FormType, command.Language, templateVersionId);
            if (!presentations.TryGetValue(key, out var issuePresentation))
                presentations[key] = issuePresentation = await presentation.ResolveAsync(command.IssuerEntityId,
                    command.DocumentKind, command.FormType, command.Language, templateVersionId, cancellationToken);
            builds.Add(new(unit, Trigger(command), command.FormType, issuePresentation, Provenance(command),
                itemClass));
        }
        var payloads = await releases.BuildManyAsync(builds, cancellationToken);
        return inputs.Select((input, index) => new PayloadBuildResult(CanonicalJson.Serialise(payloads[index]),
            input.Unit.Build?.BuildRecordId, null, input.ItemClass)).ToList();
    }

    private static CertificateTriggerSnapshot Trigger(IssueCertificateVersion command)
    {
        var triggerSchema = command.TriggerSchema ?? TriggerSchema(command);
        var correction = command.DocumentKind == CertificateDocumentKind.ReleaseRecord
            && (command.SupersedeReason == CertificateSupersedeReason.Correction
                || triggerSchema is UnitIdentifierCorrected.Schema or UnitIdentifiersSwapped.Schema
                    or UnitItemCorrected.Schema or UnitAsBuiltCorrected.Schema);
        return new CertificateTriggerSnapshot(triggerSchema,
            command.TriggeringEventId, command.EffectiveAt, command.SupersedeReason,
            correction ? false : null,
            correction ? "Verified record correction; unit condition was not re-verified." : null);
    }

    private static CertificateProvenanceSnapshot Provenance(IssueCertificateVersion command) =>
        new(command.Provenance, command.Unknowns ?? [], command.Evidence ?? []);

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
