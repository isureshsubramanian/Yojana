namespace Yojana.Developer.Contracts;

// The public event catalogue v1 (ADR-0018 D2). These shapes are what a customer's application receives; they are a
// long-term promise (a version is supported 24 months after its successor) and change only by adding fields. They are
// deliberately independent of the internal published events in Yojana.Serialisation.Contracts: the one translator in
// the Developer module maps internal to public, so an internal rename never reaches a customer.

/// <summary>Envelope of every delivered webhook and every event returned by the public API.</summary>
public sealed record PublicEventEnvelope(Guid Id, string Type, string Version, string Tenant, DateTime OccurredAt,
    DateTime RecordedAt, PublicEventRecord Record, long Sequence, IReadOnlyDictionary<string, object?> Data,
    IReadOnlyDictionary<string, string> Links, bool Test = false);

/// <summary>The record the event is about: kind, opaque id and a readable reference (serial, claim number…).</summary>
public sealed record PublicEventRecord(string Kind, Guid Id, string? Reference);

/// <summary>Public event type names, <c>&lt;resource&gt;.&lt;action&gt;</c>. The catalogue lists each with its JSON Schema.</summary>
public static class PublicEventTypes
{
    public const string CurrentVersion = "1";

    public const string EndpointVerify = "endpoint.verify";
    public const string BulkCompleted = "bulk.completed";

    public const string UnitCreated = "unit.created";
    public const string UnitShipped = "unit.shipped";
    public const string UnitOwnershipChanged = "unit.ownership_changed";
    public const string UnitCustodyChanged = "unit.custody_changed";
    public const string UnitIdentifierCorrected = "unit.identifier_corrected";
    public const string UnitVoided = "unit.voided";
    public const string CoverCreated = "cover.created";
    public const string CoverAmended = "cover.amended";
    public const string CoverTransferred = "cover.transferred";
    public const string CoverClosed = "cover.closed";
    public const string ClaimSubmitted = "claim.submitted";
    public const string ClaimAdjudicated = "claim.adjudicated";
    public const string RecoveryClaimRaised = "recovery_claim.raised";
    public const string RecoveryClaimAnswered = "recovery_claim.answered";
    public const string RmaStageChanged = "rma.stage_changed";
    public const string ReplacementShipped = "replacement.shipped";
    public const string ReplacementReturned = "replacement.returned";
    public const string RefurbishmentReleased = "refurbishment.released";
    public const string CertificateIssued = "certificate.issued";
    public const string CertificateSuperseded = "certificate.superseded";
    public const string ImportCompleted = "import.completed";
    public const string CampaignStarted = "campaign.started";
    public const string CampaignEnded = "campaign.ended";

    /// <summary>Types a subscription may choose. <see cref="EndpointVerify"/> is sent only by the handshake.</summary>
    public static readonly IReadOnlyList<string> Subscribable =
    [
        BulkCompleted, UnitCreated, UnitShipped, UnitOwnershipChanged, UnitCustodyChanged, UnitIdentifierCorrected,
        UnitVoided, CoverCreated, CoverAmended, CoverTransferred, CoverClosed, ClaimSubmitted, ClaimAdjudicated,
        RecoveryClaimRaised, RecoveryClaimAnswered, RmaStageChanged, ReplacementShipped, ReplacementReturned,
        RefurbishmentReleased, CertificateIssued, CertificateSuperseded, ImportCompleted, CampaignStarted, CampaignEnded,
    ];
}

/// <summary>API client scopes (ADR-0018 D5). One scope per resource and action.</summary>
public static class PublicApiScopes
{
    public const string UnitsRead = "units.read";
    public const string CoverRead = "cover.read";
    public const string ClaimsRead = "claims.read";
    public const string ClaimsWrite = "claims.write";
    public const string RmaRead = "rma.read";
    public const string RmaWrite = "rma.write";
    public const string CertificatesRead = "certificates.read";
    public const string EventsRead = "events.read";
    public const string UnitsWriteInstallation = "units.write-installation";
    public const string UnitsWriteAssetTag = "units.write-asset-tag";

    public static readonly IReadOnlyList<string> All =
    [
        UnitsRead, CoverRead, ClaimsRead, ClaimsWrite, RmaRead, RmaWrite, CertificatesRead, EventsRead,
        UnitsWriteInstallation, UnitsWriteAssetTag,
    ];
}
