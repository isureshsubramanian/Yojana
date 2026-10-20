using System.Text.RegularExpressions;

namespace Yojana.Integration.Coexistence;

/// <summary>
/// Connector read boundary for coexistence drift: pages through the remote records that carry fields the module
/// owns while the incumbent stays live (installed-base owner, condition, entitlement). Read-only. Registered as a
/// keyed service by connector code; a connector without one simply has no drift scan.
/// </summary>
public interface ICoexistenceDriftReader
{
    /// <summary>Neutral record type name stored with baselines and conflicts.</summary>
    string RecordType { get; }

    /// <summary>Module-owned remote fields watched when the connection has not chosen its own list.</summary>
    IReadOnlyList<string> DefaultWatchedFields { get; }

    /// <summary>Fields carrying the incumbent's warranty-contract link and term.</summary>
    IReadOnlyList<string> WarrantyContractFields { get; }

    /// <summary>
    /// Remote fields that change only when a document is posted (for example the link to the latest delivery).
    /// A watched field that changes together with one of these is a document consequence, not a person's edit.
    /// </summary>
    IReadOnlyList<string> DefaultExplainingFields { get; }

    Task<CoexistenceRecordPage> ReadPageAsync(Guid connectionId, IReadOnlyList<string> fields, string? cursor,
        int take, CancellationToken cancellationToken);
}

public sealed record CoexistenceRemoteRecord(string RecordKey, string Label,
    IReadOnlyDictionary<string, string?> Values, DateTime? ChangedAt = null);

public sealed record CoexistenceRecordPage(IReadOnlyList<CoexistenceRemoteRecord> Records, string? NextCursor);

/// <summary>Per-connection coexistence choices; null lists mean "use the connector's defaults".</summary>
public sealed record CoexistenceConnectionSettings(
    IReadOnlyList<string>? WatchedFields = null,
    IReadOnlyList<string>? ExplainingFields = null,
    bool DriftScanEnabled = true,
    int PageSize = 200,
    int MaxPagesPerRun = 50)
{
    private static readonly Regex FieldName = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant);

    public CoexistenceConnectionSettings Validate()
    {
        foreach (var list in new[] { WatchedFields, ExplainingFields })
        {
            if (list is null) continue;
            if (list.Count > 20 || list.Any(field => field is null || !FieldName.IsMatch(field))
                || list.Distinct(StringComparer.Ordinal).Count() != list.Count)
                throw new CoexistenceRuleException("coexistence_fields_invalid",
                    "Field lists hold at most 20 distinct remote field names (letters, digits and underscores).");
        }
        if (WatchedFields is { Count: 0 })
            throw new CoexistenceRuleException("coexistence_fields_invalid", "Watch at least one field, or switch the drift scan off.");
        if (WatchedFields is not null && ExplainingFields is not null && WatchedFields.Intersect(ExplainingFields).Any())
            throw new CoexistenceRuleException("coexistence_fields_invalid", "A field cannot be both watched and explaining.");
        if (PageSize is < 20 or > 500 || MaxPagesPerRun is < 1 or > 1000)
            throw new CoexistenceRuleException("coexistence_paging_invalid", "Page size 20–500 and 1–1000 pages per run.");
        return this;
    }
}

public static class NativeWarrantyObjectModes
{
    /// <summary>OD-14 default: the remote warranty object is left as it is; edits there are conflicts.</summary>
    public const string Frozen = "frozen";
    /// <summary>The module pushes its values to the remote warranty object.</summary>
    public const string Pushed = "pushed";
}

public static class WarrantyContractsMaintainedIn
{
    public const string IncumbentUntilCutover = "incumbent_until_cutover";
    public const string Module = "module";
}

public sealed record WarrantyContractMaintenanceView(string MaintainedIn, string Version);

public sealed record UpdateWarrantyContractMaintenance(string MaintainedIn, string ExpectedVersion);

public sealed record CoexistenceView(
    Guid ConnectionId,
    string ConnectionLabel,
    string SyncState,
    string NativeWarrantyObjectMode,
    string WarrantyContractsMaintainedIn,
    bool ContractFieldsAlwaysWatched,
    bool PushedModeAvailable,
    bool DriftScanSupported,
    string? RecordType,
    bool DriftScanEnabled,
    IReadOnlyList<string> WatchedFields,
    IReadOnlyList<string> ExplainingFields,
    IReadOnlyList<string> DefaultWatchedFields,
    IReadOnlyList<string> DefaultExplainingFields,
    int PageSize,
    int MaxPagesPerRun,
    DateTime? LastPassCompletedAt,
    long LastPassRecords,
    bool PassInProgress,
    int OpenConflicts);

public sealed record UpdateCoexistenceSettings(
    string NativeWarrantyObjectMode,
    IReadOnlyList<string>? WatchedFields,
    IReadOnlyList<string>? ExplainingFields,
    bool DriftScanEnabled = true,
    int PageSize = 200,
    int MaxPagesPerRun = 50);

public sealed record DriftScanResult(Guid ConnectionId, bool Supported, int Pages, int Records, int NewBaselines,
    int DocumentExplained, int ConflictsRaised, int ConflictsClosed, bool PassCompleted);

public sealed record CoexistenceConflictView(
    Guid ConflictId,
    Guid ConnectionId,
    string ConnectionLabel,
    string RecordType,
    string RecordKey,
    string RecordLabel,
    string Kind,
    string FieldName,
    string? BaselineValue,
    string? RemoteValue,
    string Status,
    DateTime DetectedAt,
    DateTime LastSeenAt,
    DateTime? IncumbentChangedAt,
    string? Decision,
    string? DecisionNote,
    string? DecidedBy,
    DateTime? DecidedAt,
    Guid? ManualTaskId,
    DateTime? ClosedAt);

public sealed record CoexistenceConflictPage(IReadOnlyList<CoexistenceConflictView> Items, string? NextCursor);

public sealed record ResolveCoexistenceConflict(string Decision, string Note);

public sealed record StopSync(string Reason);

public sealed record ResumeSync(string? Note = null);

public sealed record SyncControlResult(Guid ConnectionId, string SyncState, bool NotificationsHeld,
    int WaitingOutbound, string? Warning = null);

public sealed record StartParallelRun(int Days, DateOnly? StartDate = null);

public sealed record ConfirmParallelRunItem(string Evidence);

public sealed record ParallelRunItem(string Code, string Label, string Kind, string Status, string Detail,
    string? Evidence = null, string? ConfirmedBy = null, DateTime? ConfirmedAt = null);

public sealed record ParallelRunView(Guid ConnectionId, string ConnectionLabel, string Status, int? Days,
    DateOnly? StartDate, DateOnly? EarliestDecommissionDate, int RecommendedDays, IReadOnlyList<ParallelRunItem> Items);

public sealed class CoexistenceRuleException(string rule, string message) : Exception(message)
{
    public string Rule { get; } = rule;
}
