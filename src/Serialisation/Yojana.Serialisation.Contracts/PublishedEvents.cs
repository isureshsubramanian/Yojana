namespace Yojana.Serialisation.Contracts;

// Events the Serialisation module publishes (ADR-0001 "Integration boundaries and coupling"), through the transactional
// outbox after the unit of work that produced them commits. Each carries its schema name and version; the JSON schema of
// every version lives beside the contracts under Schemas/<major>.<minor>/ and is embedded in this assembly. A new field is
// a minor; a removed, renamed or retyped field is a major, and the previous major keeps being published for one release.
//
// COUPLING with Developer.PublicEventTranslator: internal event shapes are translated into a stable public catalogue.
// The boundary is necessary because customer integrations must not observe internal renames. A breaking change here
// without the matching translator change breaks the public-schema contract tests and would otherwise break customers.

/// <summary>Common header of every published event.</summary>
public sealed record PublishedEventHeader(string Schema, string Version, Guid EventId, Guid UnitId, DateTime EventTime,
    DateTime RecordedTime, long Seq, Guid LegalEntityId, string ActorRef, DocumentRef? Document,
    Guid? OperationId = null, int? OperationSize = null, string? OperationKind = null);

public interface IPublishedUnitEvent
{
    PublishedEventHeader Header { get; }
}

public sealed record UnitCommissioned(PublishedEventHeader Header, Guid ItemId, string Origin, string LifecycleStatus, IReadOnlyList<PublishedIdentifier> Identifiers, Guid? LotId, Guid? LocationId, string? LocationKind, bool CommissionedLate = false) : IPublishedUnitEvent
{
    public const string Schema = "Unit.Commissioned";
    public const string Version = "1.1";
}

public sealed record PublishedIdentifier(string Type, string Scheme, string Value, Guid? IssuerPartyRef);

public sealed record UnitReleased(PublishedEventHeader Header, Guid ItemId, Guid BuildRecordId, int BuildRecordVersion, string PayloadHash, IReadOnlyList<PublishedIdentifier> Identifiers) : IPublishedUnitEvent
{
    public const string Schema = "Unit.Released";
    public const string Version = "1.0";
}

/// <summary>A receipt-created unit was reversed because its source document was cancelled.</summary>
public sealed record UnitReceiptReversed(PublishedEventHeader Header, Guid CommissionedEventId,
    IReadOnlyList<Guid> ClosedIdentifierIds, IReadOnlyList<Guid> ClosedEpisodeIds) : IPublishedUnitEvent
{
    public const string Schema = "Unit.ReceiptReversed";
    public const string Version = "1.0";
}

/// <summary>The unit's lifecycle value changed, independently of the workflow that caused the transition.</summary>
public sealed record UnitLifecycleChanged(PublishedEventHeader Header, string LifecycleStatus, string Cause) : IPublishedUnitEvent
{
    public const string Schema = "Unit.LifecycleChanged";
    public const string Version = "1.0";
}

/// <summary>The unit's projected disposition changed, independently of the workflow that caused the transition.</summary>
public sealed record UnitDispositionChanged(PublishedEventHeader Header, string Disposition,
    string PreviousDisposition, string Cause) : IPublishedUnitEvent
{
    public const string Schema = "Unit.DispositionChanged";
    public const string Version = "1.0";
}

public sealed record UnitIdentifierCorrected(PublishedEventHeader Header, string IdentifierType, string Reason, string BeforeValue, string AfterValue, Guid SupersededIdentifierId, Guid NewIdentifierId, string? ApproverRef) : IPublishedUnitEvent
{
    public const string Schema = "Unit.IdentifierCorrected";
    public const string Version = "1.0";
}

public sealed record OwnershipOpened(PublishedEventHeader Header, Guid EpisodeId, Guid PartyRef, string PartyType, string Reason, string Source, string Confidence, string? CustomerPoRef, string? CustomerItemRef, Guid? PreviousEpisodeId) : IPublishedUnitEvent
{
    public const string Schema = "Ownership.Opened";
    public const string Version = "1.0";
}

public sealed record OwnershipClosed(PublishedEventHeader Header, Guid EpisodeId, Guid? PartyRef, string? PartyType, string Reason, Guid? ReplacedByEpisodeId) : IPublishedUnitEvent
{
    public const string Schema = "Ownership.Closed";
    public const string Version = "1.0";
}

public sealed record CustodyChanged(PublishedEventHeader Header, Guid EpisodeId, string ToLocationKind, Guid? ToLocationId, Guid? ToPartyRef, string? ToPartyType, string? FromLocationKind, Guid? FromLocationId, string Reason, string TriggerEventType) : IPublishedUnitEvent
{
    public const string Schema = "Custody.Changed";
    public const string Version = "1.0";
}

/// <summary>A serialised child was installed into a parent; repair policy consumers decide whether service cover follows.</summary>
public sealed record UnitPartInstalled(PublishedEventHeader Header, Guid ParentUnitId, Guid AssemblyLinkId, Guid ItemId, string Relation, string? Slot) : IPublishedUnitEvent
{
    public const string Schema = "Unit.PartInstalled";
    public const string Version = "1.0";
}
