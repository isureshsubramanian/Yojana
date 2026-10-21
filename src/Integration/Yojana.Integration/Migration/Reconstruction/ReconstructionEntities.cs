using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Reconstruction;

public static class ReconstructionRunStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public static class HistoryMovementKind
{
    public const string OpeningBalance = "opening_balance";
    public const string Received = "received";
    public const string Produced = "produced";
    public const string Consumed = "consumed";
    public const string Shipped = "shipped";
    public const string CustomerReturned = "customer_returned";
    public const string VendorReturned = "vendor_returned";
    public const string Transferred = "transferred";
    public const string IssuedOut = "issued_out";
    public const string Unknown = "unknown";
}

public static class HistoryProvenance
{
    public const string Source = "source";
    public const string Reconstructed = "reconstructed";
}

public static class HistoryDepth
{
    public const string Full = "full";
    public const string Partial = "partial";
    public const string OpeningBalanceOnly = "opening_balance_only";
}

public static class ReconstructionLogReason
{
    public const string CancelledInSource = "cancelled_in_source";
    public const string DuplicateCustomerReturn = "duplicate_customer_return";
    public const string IssuedOut = "issued_out";
    public const string UnknownMovement = "unknown_movement";
    public const string ShippedTwiceWithoutReturn = "shipped_twice_without_return";
    public const string QuantitySumNotZeroOrOne = "quantity_sum_not_zero_or_one";
}

public sealed class MigrationReconstructionRun : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RunId { get; set; }
    public Guid CanonicalisationRunId { get; set; }
    public Guid BatchId { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public required string Status { get; set; }
    public string? FailureCode { get; set; }
    public long CandidatesReconstructed { get; set; }
    public long CandidatesSkippedByDisposition { get; set; }
    public long CancellationPairsRemoved { get; set; }
    public long CandidatesQueuedForReview { get; set; }
}

public sealed class MigrationHistoryEvent : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid HistoryEventId { get; set; }
    public Guid ReconstructionRunId { get; set; }
    public Guid CandidateId { get; set; }
    public int Sequence { get; set; }
    public required string MovementKind { get; set; }
    public DateTime EffectiveDate { get; set; }
    public required string SourceDocumentTypeLabel { get; set; }
    public int? DocumentNumber { get; set; }
    public string? PartyCode { get; set; }
    public string? Warehouse { get; set; }
    public decimal Quantity { get; set; }
    public required string Provenance { get; set; }
    public Guid? EventCandidateId { get; set; }
}

public sealed class MigrationOwnershipEpisode : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid OwnershipEpisodeId { get; set; }
    public Guid ReconstructionRunId { get; set; }
    public Guid CandidateId { get; set; }
    public required string PartyCode { get; set; }
    public Guid FromHistoryEventId { get; set; }
    public Guid? ToHistoryEventId { get; set; }
    public required string OpenReason { get; set; }
    public string? CloseReason { get; set; }
}

public sealed class MigrationReconstructionLog : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid LogId { get; set; }
    public Guid ReconstructionRunId { get; set; }
    public Guid CandidateId { get; set; }
    public string? SourceKey { get; set; }
    public string? RelatedSourceKey { get; set; }
    public required string ReasonCode { get; set; }
    public required string Note { get; set; }
}
