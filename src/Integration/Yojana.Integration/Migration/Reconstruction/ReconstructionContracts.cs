namespace Yojana.Integration.LegacyMigration.Reconstruction;

public sealed record StartReconstructionResponse(Guid RunId, Guid BatchId, string Status);

public sealed record ReconstructionCount(string Key, long Count);

public sealed record ReconstructionView(
    Guid RunId,
    Guid BatchId,
    Guid CanonicalisationRunId,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string Status,
    string? Failure,
    long CandidatesReconstructed,
    long CandidatesSkippedByDisposition,
    IReadOnlyList<ReconstructionCount> EventsByMovementKind,
    IReadOnlyList<ReconstructionCount> HistoryDepth,
    long CancellationPairsRemoved,
    IReadOnlyList<ReconstructionCount> QueuedForReviewByReason);

public sealed record MigrationHistoryEventView(
    Guid EventId,
    int Sequence,
    string MovementKind,
    string MovementLabel,
    DateTime EffectiveDate,
    string DocumentTypeLabel,
    int? DocumentNumber,
    string? Party,
    string? Warehouse,
    decimal Quantity,
    string Provenance);

public sealed record MigrationOwnershipEpisodeView(
    Guid EpisodeId,
    string Party,
    Guid FromEventId,
    Guid? ToEventId,
    string OpenReason,
    string? CloseReason);

public sealed record MigrationReconstructionLogView(
    Guid LogId,
    string? SourceKey,
    string? RelatedSourceKey,
    string ReasonCode,
    string Note);

public sealed record MigrationCandidateHistoryView(
    Guid CandidateId,
    string ItemCode,
    string? ItemName,
    string Serial,
    string HistoryDepth,
    IReadOnlyList<MigrationHistoryEventView> Events,
    string? NextCursor,
    IReadOnlyList<MigrationOwnershipEpisodeView> OwnershipEpisodes,
    IReadOnlyList<MigrationReconstructionLogView> LogEntries);
