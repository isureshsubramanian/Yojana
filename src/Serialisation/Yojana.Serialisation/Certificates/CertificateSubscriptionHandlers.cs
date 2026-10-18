using Yojana.Serialisation.Contracts;
using Wolverine;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Certificates;

/// <summary>
/// The durable, service-name-independent endpoint for published facts that can issue certificates. A short-lived
/// publisher such as the demo loader may stop before dispatch completes; the web host must be able to resume the same
/// endpoint rather than looking in a service-local queue with a different name.
/// </summary>
public static class CertificateTriggerQueue
{
    public const string Name = "serialisation-certificate-triggers";
}

internal static class CertificatePublishedEventQueue
{
    public const string Name = "serialisation-published-events";
}

// TIGHT COUPLING (customer-sale command hand-off): ChangeCustodyHandler and OpenOwnershipHandler publish this work
// directly because Wolverine routing is message-type-wide and cannot durably subscribe only to sale facts. Replacing
// this message or queue without changing those command handlers silently loses first-shipment certificate issuance.
/// <summary>The narrow, tenant-scoped hand-off from published-event subscribers to certificate persistence.</summary>
public sealed record CertificateTriggerWork(IssueCertificateVersion Command);

public static class CertificateTriggerWorkHandler
{
    public static async Task Handle(CertificateTriggerWork work,
        CertificateVersioningEngine engine, CancellationToken cancellationToken) =>
        await engine.IssueAsync(work.Command, cancellationToken);
}

public static class ReleaseCertificateSubscriptionHandler
{
    public static Task Handle(UnitReleased message, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        Queue(bus, tenant, CertificateDocumentKind.ReleaseRecord, message.Header, CertificateSupersedeReason.Reassembly, ct);

    public static Task Handle(UnitAsBuiltCorrected message, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        Queue(bus, tenant, CertificateDocumentKind.ReleaseRecord, message.Header, CertificateSupersedeReason.Correction, ct);

    public static Task Handle(UnitIdentifierCorrected message, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        Queue(bus, tenant, CertificateDocumentKind.ReleaseRecord, message.Header, CertificateSupersedeReason.IdentifierChange, ct);

    public static Task Handle(UnitIdentifiersSwapped message, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        Queue(bus, tenant, CertificateDocumentKind.ReleaseRecord, message.Header, CertificateSupersedeReason.IdentifierChange, ct);

    public static Task Handle(UnitItemCorrected message, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        Queue(bus, tenant, CertificateDocumentKind.ReleaseRecord, message.Header, CertificateSupersedeReason.Correction, ct);

    public static Task Handle(UnitGraded message, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        // TIGHT COUPLING (refurbishment release trigger): RefurbishmentService grades and releases in one transaction.
        // Its Refurbishment.Released fact owns that certificate version and reason; queueing this adjacent grade first
        // would create a misleading correction version. Separating the facts requires a shared operation id instead.
        message.Header.Document?.Type == "refurbishment_work_order"
            ? Task.CompletedTask
            : Queue(bus, tenant, CertificateDocumentKind.ReleaseRecord, message.Header,
                CertificateSupersedeReason.Correction, ct);

    public static Task Handle(RefurbishmentReleased message, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        Queue(bus, tenant, CertificateDocumentKind.ReleaseRecord, message.Header with { UnitId = message.ReleasedUnitId }, CertificateSupersedeReason.Refurbishment, ct);

    public static Task Handle(UnitMerged message, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        message.Header.UnitId == message.SurvivorUnitId
            ? Queue(bus, tenant, CertificateDocumentKind.ReleaseRecord, message.Header, CertificateSupersedeReason.Correction, ct)
            : Task.CompletedTask;

    public static async Task Handle(UnitIdentifiersAssigned message, IMessageBus bus, ITenantContext tenant, CancellationToken ct)
    {
        foreach (var unitId in message.Assignments.Select(row => row.UnitId).Distinct())
            await Queue(bus, tenant, CertificateDocumentKind.ReleaseRecord, From(message.Header, unitId),
                CertificateSupersedeReason.IdentifierChange, ct);
    }

    public static Task Handle(ReplacementShipped message, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        Queue(bus, tenant, CertificateDocumentKind.ReleaseRecord, From(message.Header),
            CertificateSupersedeReason.ComponentReplacement, ct);

    public static async Task Handle(ReplacementLinkCorrected message, IMessageBus bus, ITenantContext tenant, CancellationToken ct)
    {
        var units = new[] { message.OriginalUnitId, message.RecordedReplacementUnitId, message.ActualReplacementUnitId }.Distinct();
        foreach (var unitId in units)
            await Queue(bus, tenant, CertificateDocumentKind.ReleaseRecord, From(message.Header, unitId),
                CertificateSupersedeReason.Correction, ct);
    }

    public static Task Handle(CertificateIssueRequested message, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        Queue(bus, tenant, new IssueCertificateVersion(message.RequestId, message.UnitId, message.DocumentKind,
            message.DocumentKind == CertificateDocumentKind.ReleaseRecord ? CertificateFormType.GenericRelease : CertificateFormType.Custom,
            null, message.RequestedAt, "system:certificates", message.LegalEntityId, CertificateProvenance.Reconstructed,
            ["original build facts"], message.EvidenceRefs, TriggerSchema: ReleaseRecordIssuePolicy.RequestedTrigger), ct);

    internal static Task Queue(IMessageBus bus, ITenantContext tenant, string documentKind,
        PublishedEventHeader header, string reason, CancellationToken cancellationToken) =>
        Queue(bus, tenant, Command(documentKind, From(header), reason), cancellationToken);

    internal static Task Queue(IMessageBus bus, ITenantContext tenant, string documentKind,
        CertificateTriggerContext trigger, string reason, CancellationToken cancellationToken) =>
        Queue(bus, tenant, Command(documentKind, trigger, reason), cancellationToken);

    private static async Task Queue(IMessageBus bus, ITenantContext tenant, IssueCertificateVersion command,
        CancellationToken cancellationToken) => await bus.PublishAsync(new CertificateTriggerWork(command),
        new DeliveryOptions { TenantId = tenant.TenantId.ToString() });

    private static IssueCertificateVersion Command(string documentKind, CertificateTriggerContext trigger, string reason) =>
        new(trigger.EventId, trigger.UnitId, documentKind,
            documentKind == CertificateDocumentKind.ReleaseRecord ? CertificateFormType.GenericRelease : CertificateFormType.Custom,
            reason, trigger.EventTime, "system:certificates", trigger.LegalEntityId, TriggerSchema: trigger.Schema);

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
    public static Task Handle(EntitlementCoverageChanged message, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
        ReleaseCertificateSubscriptionHandler.Queue(bus, tenant, CertificateDocumentKind.WarrantyCertificate,
            message.Header, CertificateSupersedeReason.EntitlementChange, ct);

    public static async Task Handle(DocumentSerialsCorrected message, IMessageBus bus, ITenantContext tenant, CancellationToken ct)
    {
        foreach (var unitId in message.ReversedUnitIds.Concat(message.ShippedUnitIds).Distinct())
            await ReleaseCertificateSubscriptionHandler.Queue(bus, tenant, CertificateDocumentKind.WarrantyCertificate,
                ReleaseCertificateSubscriptionHandler.From(message.Header, unitId), CertificateSupersedeReason.EntitlementChange, ct);
    }

    public static async Task Handle(OwnershipSourceDocumentCorrected message, IMessageBus bus, ITenantContext tenant, CancellationToken ct)
    {
        foreach (var unitId in message.UnitIds.Distinct())
            await ReleaseCertificateSubscriptionHandler.Queue(bus, tenant, CertificateDocumentKind.WarrantyCertificate,
                ReleaseCertificateSubscriptionHandler.From(message.Header, unitId), CertificateSupersedeReason.EntitlementChange, ct);
    }
}
