namespace Yojana.Serialisation.Contracts;

// Read models of the Serialisation module (MOD-03 queries; the approved P1 screens read exactly these). Every list is a
// page with a total; nothing returns "all".

public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take);

/// <summary>What an identifier string resolves to: a direct hit (open or superseded), several hits on different items, or nothing.</summary>
public sealed record LookupResult(string Query, string Resolution, IReadOnlyList<LookupHit> Hits)
{
    public static class Resolutions
    {
        /// <summary>Exactly one open identifier matched: open the unit.</summary>
        public const string Exact = "exact";

        /// <summary>The string was an identifier once and was superseded: open the unit, say so.</summary>
        public const string Superseded = "superseded";

        /// <summary>The same string is open on more than one item: the caller disambiguates.</summary>
        public const string Ambiguous = "ambiguous";

        /// <summary>No exact hit; use the prefix search.</summary>
        public const string None = "none";
    }
}

public sealed record LookupHit(Guid UnitId, Guid IdentifierId, string IdentifierType, string Scheme, string NormalisedValue, string RawValue, bool Open, Guid? SupersededByIdentifierId, string? ResolvesTo, Guid? ItemId, string LifecycleStatus);

/// <summary>Filters of the prefix search; every filter is optional and the result is server-paged.</summary>
public sealed record UnitSearch(string? Prefix, string? IdentifierType = null, Guid? ItemId = null, string? LifecycleStatus = null, Guid? OwnerPartyRef = null, Guid? LocationId = null, bool IncludeSuperseded = true, string Sort = "identifier", int Skip = 0, int Take = 50, DocumentRef? Document = null);

/// <summary>One matching identifier row; <c>Identifier</c> is the normalised value the match ran on, <c>RawValue</c> the string as printed on the label (what screens show).</summary>
public sealed record UnitSearchRow(Guid UnitId, string Identifier, string IdentifierType, string Scheme, bool Open, string? ResolvesTo, Guid ItemId, Guid? ModelVersionId, string LifecycleStatus, string Disposition, string? LocationKind, Guid? LocationId, Guid? OwnerPartyRef, Guid? CustodyPartyRef, string? LastEventType, DateTime LastEventTime, string? RawValue = null);

/// <summary>
/// A bounded search result. <see cref="Identifiers"/> is the number visited up to the end of this page, never an
/// unbounded count over the full match set. When <see cref="TotalIsLowerBound"/> is true callers display it with a
/// trailing plus sign; the other summary figures describe the returned page sample.
/// </summary>
public sealed record UnitSearchResult(Page<UnitSearchRow> Page, int Identifiers, int Units, int Items, int IdentifierTypes,
    int Superseded, long ServedInMilliseconds, bool HasMore = false, bool TotalIsLowerBound = false,
    bool SummaryIsSample = false);

public sealed record IdentifierRow(Guid IdentifierId, string Type, string Scheme, string RawValue, string NormalisedValue, string ScopeKey, Guid? IssuerPartyRef, string? Qualifier, DateTime ValidFrom, DateTime? ValidTo, Guid? SupersededByIdentifierId, Guid? SupersedesIdentifierId, string? CorrectionReason, string ActorRef);

public sealed record EpisodeRow(Guid EpisodeId, string Kind, Guid? PartyRef, string? PartyType, Guid? LocationId, string? LocationKind, string? Reason, string? Source, string? Confidence, DateTime ValidFrom, DateTime? ValidTo, DocumentRef? OpeningDocument, DocumentRef? ClosingDocument, string? CustomerPoRef, string? CustomerItemRef, DateOnly? DueBack);

public sealed record AsBuiltRow(Guid LinkId, string? Slot, string Relation, Guid? ChildUnitId, string? ChildSerial, Guid? ChildItemId, Guid? ChildLotId, decimal? Quantity, string? ReleaseStatus, Guid? ThroughPhantomItemId, DateTime InstalledAt, DateTime? RemovedAt);

public sealed record HistoryRow(Guid EventId, long Seq, string EventType, DateTime EventTime, DateTime RecordedTime, string? BizStep, string? Disposition, Guid? LocationId, string ActorRef, DocumentRef? Document, string? Attributes);

public sealed record BuildRecordRow(Guid BuildRecordId, int Version, DateTime ReleasedAt, string PayloadHash, string? ReleaseSignatureRef);

/// <summary>Everything the Unit detail screen shows above the fold; the history is paged separately.</summary>
public sealed record UnitDetail(
    Guid UnitId,
    string LifecycleStatus,
    string Origin,
    string SerialisationPoint,
    string RetentionClass,
    string HistoryDepth,
    Guid? LotId,
    DateOnly? ExpiryDate,
    Guid? DerivedFromUnitId,
    Guid? MergedIntoUnitId,
    Guid ItemId,
    Guid? ModelVersionId,
    string Disposition,
    string? ConditionGrade,
    Guid? LocationId,
    string? LocationKind,
    Guid? CustodyPartyRef,
    Guid? OwnerPartyRef,
    Guid? OpenOwnershipEpisodeId,
    Guid? OpenCustodyEpisodeId,
    string OverlayFlags,
    long LastEventSeq,
    DateTime LastEventTime,
    IReadOnlyList<IdentifierRow> Identifiers,
    IReadOnlyList<EpisodeRow> Episodes,
    IReadOnlyList<AsBuiltRow> AsBuilt,
    IReadOnlyList<BuildRecordRow> BuildRecords,
    Guid? ParentUnitId,
    int EventCount);

public sealed record SerialRangeRow(Guid RangeId, string Scheme, string? StartValue, string? EndValue, int Count, int CommissionedCount, string Status, bool Randomised, string? AllocatedToDocumentType, Guid? AllocatedToDocumentId, Guid? ItemId, DateTime ReservedAt, string ReservedBy, string? Note);

public sealed record SerialRangeList(Page<SerialRangeRow> Page, string Scheme, int Ranges, long SerialsIssued, string? NextFree);

public sealed record SerialSchemeRow(string Code, string IdentifierType, string Prefix, string? BlockFormat, string Separator, int CounterDigits, string Example);

public sealed record WarrantyCoveringEntitlement(Guid EntitlementId, string Family, string Type, string StoredStatus, string EffectiveStatus, DateOnly? Start, DateOnly? End, int? DaysRemaining, string Coverage);

public sealed record WarrantyCheckResult(Guid UnitId, DateOnly On, string? Family, bool Covered, IReadOnlyList<WarrantyCoveringEntitlement> CoveringEntitlements, DateOnly? EndDate, int? DaysRemaining, string? Reason);

/// <summary>Completeness for a list of serials or a selection set (the As-built completeness screen).</summary>
public sealed record CompletenessQuery(IReadOnlyList<string>? Serials = null, Guid? SelectionId = null, DocumentRef? Document = null, DateOnly? EffectiveAt = null, int Skip = 0, int Take = 50);

public sealed record CompletenessRow(Guid UnitId, string? Serial, Guid ItemId, string Policy, int Expected, int Captured, int Missing, int Unexpected, bool Complete, IReadOnlyList<CompletenessSlot> Slots);

public sealed record CompletenessSlot(string Slot, string ExpectedItemCode, Guid ExpectedItemId, string Outcome, string? CapturedSerial, Guid? CapturedUnitId, Guid? CapturedItemId);

public sealed record CompletenessResult(Page<CompletenessRow> Page, int Units, int Complete, int MissingUnits, int MissingSerials, int UnexpectedUnits, IReadOnlyList<string> Slots, IReadOnlyList<string> Unknown);
