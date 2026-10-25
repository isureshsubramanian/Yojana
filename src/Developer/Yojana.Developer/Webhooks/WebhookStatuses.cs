namespace Yojana.Developer.Webhooks;

public static class WebhookSubscriptionStatus
{
    public const string PendingVerification = "pending_verification";
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Disabled = "disabled";
}

public static class WebhookDeliveryStatus
{
    public const string Pending = "pending";
    public const string Succeeded = "succeeded";
    public const string Parked = "parked";
    public const string Discarded = "discarded";
}
