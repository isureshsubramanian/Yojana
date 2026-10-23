using System.Net;

namespace Yojana.Integration.Connectors.CX02.Testing;

public enum Cx02AttachmentUploadPattern
{
    SeparateContent,
    InlineContent,
}

public sealed record Cx02SimulatorOptions
{
    public const string RequiredScope = "https://api.businesscentral.dynamics.com/.default";

    public string ClientSecret { get; init; } = "fictional-secret";
    public int TokenExpiresInSeconds { get; init; } = 3599;

    // ASSUMED A-14: the public documentation names the error family but not the HTTP status.
    public HttpStatusCode ExpiredTokenStatus { get; init; } = HttpStatusCode.Unauthorized;

    // ASSUMED P-02: public examples show a domain tenant segment, while GUID use is common practice.
    public bool AcceptTenantGuid { get; init; } = true;

    public int PageSizeCap { get; init; } = 20_000;

    // ASSUMED P-17: continuation tokens are documented; the literal OData property is inferred.
    public bool IncludeNextLink { get; init; } = true;

    // ASSUMED P-12: the product documentation does not state 409 versus 412.
    public HttpStatusCode EntityChangedStatus { get; init; } = HttpStatusCode.PreconditionFailed;

    // ASSUMED P-22: error codes are documented, while the JSON wrapper follows the OData standard.
    public bool UseODataErrorEnvelope { get; init; } = true;

    // ASSUMED P-26: scalar JSON parameters on a custom bound action are not documented.
    public bool BoundActionAcceptsParameters { get; init; } = true;

    public int RequestLimit { get; init; } = 6_000;
    public TimeSpan RequestWindow { get; init; } = TimeSpan.FromSeconds(300);
    public int ConcurrentRequestLimit { get; init; } = 5;
    public int QueuedRequestLimit { get; init; } = 95;
    public TimeSpan QueueWaitTimeout { get; init; } = TimeSpan.FromMinutes(8);

    // ASSUMED R-11: no product page documents whether a 429 carries Retry-After.
    public bool IncludeRetryAfter { get; init; } = true;

    // ASSUMED R-12: the only located name for the 429 code was third-party material.
    public string ThrottleErrorCode { get; init; } = "Application_TooManyRequests";

    public int SubscriptionCap { get; init; } = 200;
    public TimeSpan SubscriptionLifetime { get; init; } = TimeSpan.FromDays(3);
    public TimeSpan WebhookCoalescingWindow { get; init; } = TimeSpan.FromSeconds(30);
    public int CollectionNotificationThreshold { get; init; } = 1_000;
    public TimeSpan WebhookRetryWindow { get; init; } = TimeSpan.FromHours(36);

    // ASSUMED W-04: method, media type and timeout are not documented.
    public HttpMethod HandshakeMethod { get; init; } = HttpMethod.Post;
    public string HandshakeContentType { get; init; } = "text/plain";
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    // ASSUMED W-17: retry intervals and attempt count are not documented.
    public IReadOnlyList<TimeSpan> WebhookRetrySchedule { get; init; } =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)];

    // ASSUMED W-18: only 200 is known to be accepted.
    public IReadOnlySet<HttpStatusCode> DeliveredWebhookStatuses { get; init; } =
        new HashSet<HttpStatusCode> { HttpStatusCode.OK };

    // ASSUMED W-14: the sample's filter field differs from the API resource field.
    public string CollectionFilterField { get; init; } = "lastDateTimeModified";

    // ASSUMED W-25: whether an application identity causes a notification is not stated.
    public bool NotifyChangesByApplicationIdentity { get; init; }

    // ASSUMED B-11: no handshake or expiry is documented for business-event subscriptions.
    public bool BusinessEventRequiresHandshake { get; init; }
    public TimeSpan? BusinessEventSubscriptionLifetime { get; init; }

    // ASSUMED B-12: application-identity subscription is not stated.
    public bool AllowApplicationBusinessEventSubscription { get; init; } = true;

    // ASSUMED B-02/B-03: retry is undocumented; a third-party report only suggests that some retry exists.
    public bool RetryBusinessEvents { get; init; }

    // ASSUMED D-04: the documented values contradict the navigation parents.
    public IReadOnlySet<string> DocumentAttachmentParentTypes { get; init; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Item", "Customer", "Vendor", "Employee", "Project", "Sales Invoice", "Sales Order",
            "Sales Quote", "Sales Credit Memo", "Purchase Invoice", "Purchase Order", "Purchase Credit Memo",
        };

    // ASSUMED D-06: the resource documentation does not state the content upload pattern.
    public Cx02AttachmentUploadPattern AttachmentUploadPattern { get; init; } =
        Cx02AttachmentUploadPattern.SeparateContent;

    // D-05 is inferred for API stream uploads from the documented overall request-body limit.
    public long AttachmentSizeLimit { get; init; } = 350L * 1024 * 1024;
}
