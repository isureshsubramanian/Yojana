using System.Net;
using System.Text.Json.Serialization;

namespace Yojana.Integration.Connectors.CX04.Testing;

public enum Cx04LicenceTier
{
    Essentials,
    Select,
    Prime,
    Enterprise,
}

public enum Cx04QueueLimitReading
{
    PerTier,
    FixedTwenty,
}

public enum Cx04RefreshExpiryMode
{
    Absolute,
    Sliding,
    Disabled,
}

public enum Cx04AllocationQuantityBehaviour
{
    LineAndAllocations,
    AllocationsOnly,
}

public enum Cx04SerialAssignment
{
    WhenReceived,
    WhenUsed,
}

public sealed record Cx04LicenceProfile(
    int RequestsPerMinute,
    int MaximumQueueSize,
    int ParallelRequests,
    int MaximumSessions,
    int LinesPerTransaction,
    int SerialNumbersPerDocument)
{
    public static Cx04LicenceProfile For(Cx04LicenceTier tier, int enterpriseSessionLimit) => tier switch
    {
        Cx04LicenceTier.Essentials => new(50, 5, 1, 5, 1_000, 2_000),
        Cx04LicenceTier.Select => new(100, 10, 2, 10, 2_000, 4_000),
        Cx04LicenceTier.Prime => new(150, 20, 3, 20, 2_500, 7_500),
        Cx04LicenceTier.Enterprise => new(300, 40, 8, enterpriseSessionLimit, 50_000, 50_000),
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };
}

public sealed record Cx04SimulatorOptions
{
    public Cx04LicenceTier LicenceTier { get; init; } = Cx04LicenceTier.Essentials;
    public Cx04QueueLimitReading QueueLimitReading { get; init; } = Cx04QueueLimitReading.PerTier;
    public Cx04RefreshExpiryMode RefreshExpiryMode { get; init; } = Cx04RefreshExpiryMode.Absolute;
    public Cx04AllocationQuantityBehaviour AllocationQuantityBehaviour { get; init; }
        = Cx04AllocationQuantityBehaviour.LineAndAllocations;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan AbsoluteRefreshChainLifetime { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan SlidingRefreshChainLifetime { get; init; } = TimeSpan.FromDays(90);
    public TimeSpan QueuedRequestLifetime { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan PushRetryInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan FailedNotificationRetention { get; init; } = TimeSpan.FromDays(2);
    public int AttachmentSizeLimitBytes { get; init; } = 25 * 1024 * 1024;
    public int EnterpriseSessionLimit { get; init; } = int.MaxValue;
    public int ActionPollsBeforeCompletion { get; init; } = 1;
    public bool EmptyThrottleResponse { get; init; } = true;
    public bool IncludeRetryAfterOnThrottle { get; init; }
    public bool IdWinsWhenBothProvided { get; init; } = true;
    public bool RequireSerialQuantityOne { get; init; } = true;
    public bool SupportRmAllocations { get; init; } = true;
    public bool ReapIdleSessionsBelowLimit { get; init; }
    public bool StablePagingOrder { get; init; } = true;
    public HttpStatusCode RunningActionStatusCode { get; init; } = HttpStatusCode.Accepted;
    public string ReceiptReleaseActionName { get; init; } = "ReleasePurchaseReceipt";
    public Func<TimeSpan, CancellationToken, ValueTask> DelayAsync { get; init; }
        = static (delay, cancellationToken) => new(Task.Delay(delay, cancellationToken));
    public string WebhookSecretHeader { get; init; } = "X-Fictional-Webhook-Secret";
    public string WebhookSecret { get; init; } = "fictional-shared-secret";

    public Cx04LicenceProfile LicenceProfile => Cx04LicenceProfile.For(LicenceTier, EnterpriseSessionLimit);
}

public sealed record Cx04PushNotification(
    [property: JsonPropertyName("Inserted")] IReadOnlyList<IReadOnlyDictionary<string, object?>> Inserted,
    [property: JsonPropertyName("Deleted")] IReadOnlyList<IReadOnlyDictionary<string, object?>> Deleted,
    [property: JsonPropertyName("Query")] string Query,
    [property: JsonPropertyName("CompanyId")] string CompanyId,
    [property: JsonPropertyName("Id")] string Id,
    [property: JsonPropertyName("TimeStamp")] long TimeStamp,
    [property: JsonPropertyName("AdditionalInfo")] IReadOnlyDictionary<string, object?> AdditionalInfo);

public sealed record Cx04NotificationDelivery(string Id, int Attempts, bool Delivered, bool Parked);

public sealed record Cx04ParkedNotification(
    Cx04PushNotification Notification,
    int Attempts,
    DateTimeOffset ParkedAt,
    bool Truncated);

public sealed record Cx04StoredFile(Uri Location, string? Comment, byte[] Content);

public sealed record Cx04Faults
{
    public bool DropNextCreateReply { get; set; }
    public bool DuplicateNextNotification { get; set; }
    public bool ReverseNextNotificationBatch { get; set; }
    public bool TruncateNextNotification { get; set; }
    public bool LoseNextNotificationBeforeSend { get; set; }
    public bool DisableNextAction { get; set; }
    public bool ForbidNextRequest { get; set; }
    public bool HoldProcessedRequests { get; set; }
    public int TooManyRequestsStormRemaining { get; set; }
    public int ExpireAccessTokenAfterRequests { get; set; } = int.MaxValue;
}

internal sealed record Cx04ErrorBody(string Message, string ExceptionMessage, string ExceptionType);

internal static class Cx04Status
{
    public const HttpStatusCode UnprocessableEntity = (HttpStatusCode)422;
}
