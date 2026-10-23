namespace Yojana.Serialisation.Contracts;

/// <summary>One module-owned unit fact needed by an ERP-change stock comparison.</summary>
public sealed record MigrationReconciliationUnit(
    Guid UnitId,
    Guid ItemId,
    string IdentifierType,
    string Identifier,
    string NormalisedIdentifier,
    string LifecycleStatus,
    string Disposition,
    Guid? LocationId,
    string? LocationKind,
    Guid? OwnerPartyRef,
    bool HasLiveEntitlement,
    DateOnly? WarrantyEnd,
    Guid LegalEntityId);

/// <summary>How many live module units refer to an item or owning party.</summary>
public sealed record MigrationMasterReferenceUsage(Guid ReferenceId, string Kind, int LiveUnits);

public sealed record MigrationUnitPage(IReadOnlyList<MigrationReconciliationUnit> Items, Guid? NextUnitId);

public static class MigrationUnitHistoryChangeKind
{
    public const string Deliberate = "deliberate";
    public const string Unexpected = "unexpected";
    public static readonly IReadOnlyList<string> All = [Deliberate, Unexpected];
}

/// <summary>Integration-owned links that can attribute unit events to one ERP-change run.</summary>
public sealed record MigrationUnitHistoryAttribution(
    Guid ErpChangeRunId,
    IReadOnlyList<Guid> DeliberateCommandIds,
    IReadOnlyList<Guid> UnexpectedDocumentIds);

public sealed record MigrationUnitHistoryChangeCounts(int DeliberateUnits, int UnexpectedUnits);

/// <summary>One append-only unit history attributed to an ERP-change command or target document.</summary>
public sealed record MigrationUnitHistoryChange(
    Guid UnitId, string Identifier, string Kind, int EventsAdded, DateTime LastRecordedAt);

public sealed record MigrationUnitHistoryChangePage(
    IReadOnlyList<MigrationUnitHistoryChange> Items, Guid? NextUnitId);

public sealed record MigrationCorrectIdentifier(
    Guid ErpChangeRunId, Guid CommandId, Guid UnitId, string IdentifierType, string NewRawValue,
    DateTime EventTime, string ActorRef, Guid LegalEntityId, string? Note = null);

public sealed record MigrationMergeUnits(
    Guid ErpChangeRunId, Guid CommandId, Guid SurvivorUnitId, Guid AbsorbedUnitId,
    DateTime EventTime, string ActorRef, Guid LegalEntityId, string? Note = null);

public sealed record MigrationChangeCustody(
    Guid ErpChangeRunId, Guid CommandId, Guid UnitId, string LocationKind, DateTime EventTime,
    string ActorRef, Guid LegalEntityId, Guid? LocationId = null, string? Note = null);

public sealed record MigrationResolutionResult(string Command, Guid CommandId, Guid UnitId);

/// <summary>
/// Narrow boundary used by Integration's ERP-change workflow. Integration receives read records and asks
/// Serialisation to execute ordinary commands; it never reads or edits Serialisation persistence.
/// </summary>
public interface IMigrationReconciliationPort
{
    Task<IReadOnlyList<MigrationMasterReferenceUsage>> MasterReferenceUsageAsync(
        CancellationToken cancellationToken);

    Task<MigrationUnitPage> InStockUnitsAsync(Guid? afterUnitId, int take,
        CancellationToken cancellationToken);

    Task<MigrationUnitHistoryChangeCounts> CountAttributedUnitHistoryChangesAsync(
        MigrationUnitHistoryAttribution attribution, CancellationToken cancellationToken);

    Task<MigrationUnitHistoryChangePage> AttributedUnitHistoryChangesAsync(
        MigrationUnitHistoryAttribution attribution, string kind, Guid? afterUnitId, int take,
        CancellationToken cancellationToken);

    Task<MigrationResolutionResult> CorrectIdentifierAsync(MigrationCorrectIdentifier request,
        CancellationToken cancellationToken);

    Task<MigrationResolutionResult> MergeUnitsAsync(MigrationMergeUnits request,
        CancellationToken cancellationToken);

    Task<MigrationResolutionResult> ChangeCustodyAsync(MigrationChangeCustody request,
        CancellationToken cancellationToken);
}

public static class MigrationReconciliationReason
{
    public const string Value = "migration_reconciliation";
}
