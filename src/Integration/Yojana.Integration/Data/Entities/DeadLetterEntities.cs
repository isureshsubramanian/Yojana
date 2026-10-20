using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

public sealed class DeadLetterItem : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid DeadLetterItemId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string Direction { get; set; }
    public required string RecordType { get; set; }
    public required string SourceKind { get; set; }
    public Guid SourceId { get; set; }
    public required string ReasonCode { get; set; }
    public required string RawPayload { get; set; }
    public required string CanonicalAttempt { get; set; }
    public required string Failure { get; set; }
    public required string Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class DeadLetterEvent : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid DeadLetterEventId { get; set; }
    public Guid DeadLetterItemId { get; set; }
    public required string Action { get; set; }
    public required string ActorRef { get; set; }
    public string? Reason { get; set; }
    public required string DetailsJson { get; set; }
    public DateTime OccurredAt { get; set; }
}

public sealed class ManualOutboundTask : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ManualOutboundTaskId { get; set; }
    // Connection cleanup tasks have no failed message or outbound document to map.
    public Guid? DeadLetterItemId { get; set; }
    public Guid? OutboundCommandId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string CommandType { get; set; }
    public required string Desk { get; set; }
    public required string Draft { get; set; }
    public required string Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ErpExternalId { get; set; }
    public string? ErpChangeMarker { get; set; }
    public string? CompletedBy { get; set; }
    public DateTime? CompletedAt { get; set; }
    // Why a person must act, when the draft alone does not say (for example a record waiting for review remotely).
    public string? Note { get; set; }
}

public static class DeadLetterStatus
{
    public const string NeedsAttention = "needs_attention";
    public const string RetryPending = "retry_pending";
    public const string Resolved = "resolved";
    public const string Discarded = "discarded";
}

public static class DeadLetterDirection
{
    public const string Inbound = "inbound";
    public const string Outbound = "outbound";
}

public static class DeadLetterSourceKind
{
    public const string InboundMessage = "inbound_message";
    public const string OutboundCommand = "outbound_command";
    public const string ReconciliationResult = "reconciliation_result";
}

public static class DeadLetterAction
{
    public const string Parked = "parked";
    public const string MapAndRetry = "map_and_retry";
    public const string CreateProvisionalAndRetry = "create_provisional_and_retry";
    public const string Discard = "discard";
    public const string ManualMapping = "manual_mapping";
}

public static class ManualTaskStatus
{
    public const string Open = "open";
    public const string Completed = "completed";
}

public static class ManualTaskCommandType
{
    public const string RemoveRemoteSubscription = "RemoveRemoteSubscription";
    /// <summary>Raised by a coexistence conflict kept on the module side; the next drift scan confirms the revert.</summary>
    public const string RevertRemoteChange = "RevertRemoteChange";

    /// <summary>Tasks a person acknowledges without a remote document id; they never create a document mapping.</summary>
    public static bool IsAcknowledgementOnly(string commandType) =>
        commandType is RemoveRemoteSubscription or RevertRemoteChange;
}
