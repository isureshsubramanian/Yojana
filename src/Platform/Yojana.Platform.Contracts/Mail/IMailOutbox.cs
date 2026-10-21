using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Platform.Contracts.Mail;

/// <summary>
/// The one way a module sends e-mail (OD-38): account mail calls it directly, customer mail (P9-T20) calls it after
/// rendering its template. The caller never sees a transport, a credential or a retry; it hands over a finished message
/// and gets the id under which delivery is recorded.
/// </summary>
public interface IMailOutbox
{
    /// <summary>
    /// Queues one message for delivery. Inside a Wolverine handler the message joins the handler's transaction (sent only
    /// if the handler commits); anywhere else it is written to the durable local queue before this returns. Delivery is at
    /// least once: a crash between the transport accepting the message and the delivery being recorded sends it again.
    /// </summary>
    Task<Guid> QueueAsync(OutgoingMail mail, CancellationToken cancellationToken);
}

/// <summary>
/// A finished message for one recipient. <see cref="HtmlBody"/> must already be e-mail-safe HTML produced on the server;
/// <see cref="TextBody"/> is the plain-text part. The bodies are never written to the delivery log, because account mail
/// carries single-use links.
/// </summary>
/// <param name="Kind">One of <see cref="MailKind"/>; recorded in the delivery log.</param>
/// <param name="Tenant">The tenant the mail is sent for, or null for mail about a global identity before a tenant is chosen.</param>
/// <param name="UserId">The platform user the mail is about, when there is one.</param>
/// <param name="Cc">Copy recipients; every one of them sees the others and the recipient in <paramref name="To"/>.</param>
/// <param name="Attachments">Files sent with the message, at most <see cref="MailLimits.MaxAttachments"/> and
/// <see cref="MailLimits.MaxAttachmentBytes"/> together. Like the bodies they travel inside the durable queue until delivered
/// and are never written to the delivery log.</param>
public sealed record OutgoingMail(
    string Kind,
    TenantId? Tenant,
    Guid? UserId,
    MailRecipient To,
    string Subject,
    string HtmlBody,
    string TextBody,
    IReadOnlyList<MailRecipient>? Cc = null,
    IReadOnlyList<MailAttachment>? Attachments = null);

public sealed record MailRecipient(string Address, string? DisplayName = null);

/// <summary>A file sent with a message. <see cref="MediaType"/> must be one of <see cref="MailLimits.AttachmentMediaTypes"/>.</summary>
public sealed record MailAttachment(string FileName, string MediaType, byte[] Content);

public static class MailLimits
{
    public const int MaxCc = 10;
    public const int MaxAttachments = 5;
    /// <summary>
    /// Together, before encoding. Kept under the 4 MB request limit of the Graph <c>sendMail</c> action once base64 grows
    /// the bytes by a third, so a message that queues can always be handed to either transport.
    /// </summary>
    public const int MaxAttachmentBytes = 2_800_000;
    public static readonly IReadOnlySet<string> AttachmentMediaTypes =
        new HashSet<string>(["application/pdf", "text/csv", "text/plain"], StringComparer.Ordinal);
}

/// <summary>
/// Published after every delivery attempt of a tenant's mail, so the module that queued it can show delivery without
/// reading the platform's delivery log. Attempts arrive in order per message but a subscriber must still ignore one whose
/// <see cref="Attempts"/> is lower than what it already holds (redelivery). <see cref="Status"/> is one of
/// <see cref="MailDeliveryStatus"/>.
/// </summary>
public sealed record MailDeliveryRecorded(
    Guid MailId,
    string Kind,
    TenantId Tenant,
    string Status,
    int Attempts,
    DateTime AttemptedAt,
    DateTime? SentAt,
    DateTime? NextAttemptAt,
    string? Error,
    string Transport);

public static class MailDeliveryStatus
{
    public const string Retrying = "retrying";
    public const string Sent = "sent";
    public const string Failed = "failed";
}

/// <summary>Mail kinds. A module adds its own under its prefix (for example <c>customer.coverage_sheet</c>).</summary>
public static class MailKind
{
    public const string PasswordReset = "account.password_reset";
    public const string PasswordChanged = "account.password_changed";
    public const string Invitation = "account.invitation";
    public const string SignInMethodAdded = "account.sign_in_method_added";
    public const string TransportTest = "platform.transport_test";
}
