using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Certificates;

public static class ReleaseCertificateSubscriptionHandler
{
    public static Task Handle(UnitReleased message, CertificateVersioningEngine engine, CancellationToken ct) =>
        Issue(engine, message.Header, CertificateSupersedeReason.Reassembly, ct);

    public static Task Handle(UnitAsBuiltCorrected message, CertificateVersioningEngine engine, CancellationToken ct) =>
        Issue(engine, message.Header, CertificateSupersedeReason.Correction, ct);

    public static Task Handle(UnitIdentifierCorrected message, CertificateVersioningEngine engine, CancellationToken ct) =>
        Issue(engine, message.Header, CertificateSupersedeReason.IdentifierChange, ct);

    public static Task Handle(UnitIdentifiersSwapped message, CertificateVersioningEngine engine, CancellationToken ct) =>
        Issue(engine, message.Header, CertificateSupersedeReason.IdentifierChange, ct);

    public static Task Handle(UnitItemCorrected message, CertificateVersioningEngine engine, CancellationToken ct) =>
        Issue(engine, message.Header, CertificateSupersedeReason.Correction, ct);

    public static Task Handle(UnitGraded message, CertificateVersioningEngine engine, CancellationToken ct) =>
        Issue(engine, message.Header, CertificateSupersedeReason.Correction, ct);

    public static Task Handle(RefurbishmentReleased message, CertificateVersioningEngine engine, CancellationToken ct) =>
        Issue(engine, message.Header with { UnitId = message.ReleasedUnitId }, CertificateSupersedeReason.Refurbishment, ct);

    public static Task Handle(UnitMerged message, CertificateVersioningEngine engine, CancellationToken ct) =>
        message.Header.UnitId == message.SurvivorUnitId
            ? Issue(engine, message.Header, CertificateSupersedeReason.Correction, ct)
            : Task.CompletedTask;

    public static async Task Handle(UnitIdentifiersAssigned message, CertificateVersioningEngine engine, CancellationToken ct)
    {
        foreach (var unitId in message.Assignments.Select(row => row.UnitId).Distinct())
            await engine.IssueAsync(CertificateDocumentKind.ReleaseRecord, From(message.Header, unitId),
                CertificateSupersedeReason.IdentifierChange, ct);
    }

    public static async Task Handle(ReplacementShipped message, CertificateVersioningEngine engine, CancellationToken ct)
    {
        await engine.IssueAsync(CertificateDocumentKind.ReleaseRecord, From(message.Header),
            CertificateSupersedeReason.ComponentReplacement, ct);
    }

    public static async Task Handle(ReplacementLinkCorrected message, CertificateVersioningEngine engine, CancellationToken ct)
    {
        var units = new[] { message.OriginalUnitId, message.RecordedReplacementUnitId, message.ActualReplacementUnitId }.Distinct();
        foreach (var unitId in units)
            await engine.IssueAsync(CertificateDocumentKind.ReleaseRecord, From(message.Header, unitId),
                CertificateSupersedeReason.Correction, ct);
    }

    public static Task Handle(CertificateIssueRequested message, CertificateVersioningEngine engine, CancellationToken ct) =>
        engine.IssueAsync(new IssueCertificateVersion(message.RequestId, message.UnitId, message.DocumentKind,
            message.DocumentKind == CertificateDocumentKind.ReleaseRecord ? CertificateFormType.GenericRelease : CertificateFormType.Custom,
            null, message.RequestedAt, "system:certificates", message.LegalEntityId, CertificateProvenance.Reconstructed,
            ["original build facts"], message.EvidenceRefs), ct);

    private static async Task Issue(CertificateVersioningEngine engine, PublishedEventHeader header, string reason,
        CancellationToken cancellationToken) => await engine.IssueAsync(CertificateDocumentKind.ReleaseRecord,
        From(header), reason, cancellationToken);

    internal static CertificateTriggerContext From(PublishedEventHeader header, Guid? unitId = null) => new(
        unitId ?? header.UnitId, header.Schema, header.EventId, header.EventTime, header.RecordedTime, header.Seq,
        header.LegalEntityId, header.ActorRef, header.Document);

    internal static CertificateTriggerContext From(PublishedSummaryHeader header, Guid unitId) => new(
        unitId, header.Schema, header.EventId, header.EventTime, header.RecordedTime, 0,
        header.LegalEntityId, header.ActorRef, header.Document);
}

public static class WarrantyCertificateSubscriptionHandler
{
    // Existing common equivalent for every outbound Entitlement.* ledger fact; Cause retains its exact source kind.
    public static Task Handle(EntitlementCoverageChanged message, CertificateVersioningEngine engine, CancellationToken ct) => Issue(message.Header, engine, ct);

    public static async Task Handle(DocumentSerialsCorrected message, CertificateVersioningEngine engine, CancellationToken ct)
    {
        foreach (var unitId in message.ReversedUnitIds.Concat(message.ShippedUnitIds).Distinct())
            await engine.IssueAsync(CertificateDocumentKind.WarrantyCertificate,
                ReleaseCertificateSubscriptionHandler.From(message.Header, unitId), CertificateSupersedeReason.EntitlementChange, ct);
    }

    public static async Task Handle(OwnershipSourceDocumentCorrected message, CertificateVersioningEngine engine, CancellationToken ct)
    {
        foreach (var unitId in message.UnitIds.Distinct())
            await engine.IssueAsync(CertificateDocumentKind.WarrantyCertificate,
                ReleaseCertificateSubscriptionHandler.From(message.Header, unitId), CertificateSupersedeReason.EntitlementChange, ct);
    }

    private static async Task Issue(PublishedEventHeader header, CertificateVersioningEngine engine,
        CancellationToken cancellationToken) => await engine.IssueAsync(CertificateDocumentKind.WarrantyCertificate,
        ReleaseCertificateSubscriptionHandler.From(header), CertificateSupersedeReason.EntitlementChange, cancellationToken);
}
