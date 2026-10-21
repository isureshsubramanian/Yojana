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
public sealed record OutgoingMail(
    string Kind,
    TenantId? Tenant,
    Guid? UserId,
    MailRecipient To,
    string Subject,
    string HtmlBody,
    string TextBody);

public sealed record MailRecipient(string Address, string? DisplayName = null);

/// <summary>Mail kinds. A module adds its own under its prefix (for example <c>customer.coverage_sheet</c>).</summary>
public static class MailKind
{
    public const string PasswordReset = "account.password_reset";
    public const string PasswordChanged = "account.password_changed";
    public const string Invitation = "account.invitation";
    public const string SignInMethodAdded = "account.sign_in_method_added";
    public const string SignInMethodRemoved = "account.sign_in_method_removed";
    public const string TransportTest = "platform.transport_test";
}
