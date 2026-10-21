using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;
using Wolverine;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;

namespace Yojana.Platform.Mail;

/// <summary>
/// One delivery attempt of a queued message. Retries are new messages with the next attempt number. This message is the
/// only place the bodies live (mail_message deliberately has none, because reset links are single-use secrets), so while
/// a retry is scheduled they sit in Wolverine's durable incoming table until the attempt is handled.
/// </summary>
public sealed record DeliverMail(PreparedMail Prepared, DateTime QueuedAt, int Attempt);

/// <summary>How a tenant signs off its mail (OD-38: one tenant mail setting for account and customer mail).</summary>
public static class TenantMailSettings
{
    public const string SenderNameKey = "mail.sender_name";
    public const string ReplyToKey = "mail.reply_to";

    public static async Task<(string? SenderName, string? ReplyTo)> ReadAsync(ITenantSettings settings, CancellationToken cancellationToken)
    {
        var name = (await settings.GetAsync(SenderNameKey, cancellationToken))?.Trim();
        var replyTo = (await settings.GetAsync(ReplyToKey, cancellationToken))?.Trim();
        return (string.IsNullOrEmpty(name) || name.Length > 100 || name.Any(char.IsControl) ? null : name,
            string.IsNullOrEmpty(replyTo) || !IsSingleAddress(replyTo) ? null : replyTo);
    }

    internal static bool IsSingleAddress(string value) =>
        value.Length <= 320 && MailboxAddress.TryParse(value, out var parsed) && parsed.Address == value && value.Contains('@');
}

public sealed class MailOutbox(IMessageBus bus, ITenantSettings settings, ITenantContext tenant, IClock clock) : IMailOutbox
{
    public async Task<Guid> QueueAsync(OutgoingMail mail, CancellationToken cancellationToken)
    {
        Validate(mail);
        string? senderName = null, replyTo = null;
        if (mail.Tenant is { } mailTenant)
        {
            // Tenant settings are read under the scope's tenant only; a mail for another tenant would read the wrong sign-off.
            if (!tenant.HasTenant || tenant.TenantId != mailTenant)
                throw new ArgumentException("A tenant's mail must be queued inside that tenant's scope.", nameof(mail));
            (senderName, replyTo) = await TenantMailSettings.ReadAsync(settings, cancellationToken);
        }

        var now = clock.UtcNow;
        var prepared = new PreparedMail(TimeOrderedId.New(now), mail, senderName, replyTo);
        await bus.PublishAsync(new DeliverMail(prepared, now, 1), new DeliveryOptions { TenantId = mail.Tenant?.ToString() });
        return prepared.MailId;
    }

    public static void Validate(OutgoingMail mail)
    {
        if (string.IsNullOrWhiteSpace(mail.Kind) || mail.Kind.Length > 60 || mail.Kind.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_')))
            throw new ArgumentException("A mail kind is 1 to 60 lower-case letters, digits, dots and underscores.", nameof(mail));
        if (!TenantMailSettings.IsSingleAddress(mail.To.Address))
            throw new ArgumentException("The recipient must be a single e-mail address.", nameof(mail));
        if (mail.To.DisplayName is { } name && (name.Length > 200 || name.Any(char.IsControl)))
            throw new ArgumentException("The recipient's name must be at most 200 printable characters.", nameof(mail));
        if (string.IsNullOrWhiteSpace(mail.Subject) || mail.Subject.Length > 300 || mail.Subject.Any(char.IsControl))
            throw new ArgumentException("A subject is 1 to 300 printable characters on one line.", nameof(mail));
        if (string.IsNullOrWhiteSpace(mail.TextBody) || string.IsNullOrWhiteSpace(mail.HtmlBody))
            throw new ArgumentException("A mail needs both a plain-text and an HTML body.", nameof(mail));
        var cc = mail.Cc ?? [];
        if (cc.Count > MailLimits.MaxCc)
            throw new ArgumentException($"A mail has at most {MailLimits.MaxCc} copy recipients.", nameof(mail));
        foreach (var copy in cc)
        {
            if (!TenantMailSettings.IsSingleAddress(copy.Address))
                throw new ArgumentException("Every copy recipient must be a single e-mail address.", nameof(mail));
            if (copy.DisplayName is { } copyName && (copyName.Length > 200 || copyName.Any(char.IsControl)))
                throw new ArgumentException("A copy recipient's name must be at most 200 printable characters.", nameof(mail));
        }
        var attachments = mail.Attachments ?? [];
        if (attachments.Count > MailLimits.MaxAttachments)
            throw new ArgumentException($"A mail has at most {MailLimits.MaxAttachments} attachments.", nameof(mail));
        if (attachments.Sum(file => (long)file.Content.Length) > MailLimits.MaxAttachmentBytes)
            throw new ArgumentException($"Attachments total at most {MailLimits.MaxAttachmentBytes} bytes.", nameof(mail));
        foreach (var file in attachments)
        {
            if (!MailLimits.AttachmentMediaTypes.Contains(file.MediaType))
                throw new ArgumentException($"Attachment type '{file.MediaType}' is not allowed.", nameof(mail));
            if (string.IsNullOrWhiteSpace(file.FileName) || file.FileName.Length > 150
                || file.FileName.Any(c => char.IsControl(c) || c is '/' or '\\' or '"' or ':'))
                throw new ArgumentException("An attachment name is 1 to 150 characters without slashes, colons or quotes.", nameof(mail));
            if (file.Content.Length == 0)
                throw new ArgumentException("An attachment cannot be empty.", nameof(mail));
        }
    }
}

public static class MailRetrySchedule
{
    /// <summary>The delay after failed attempt <paramref name="attempt"/> (1-based).</summary>
    public static TimeSpan After(int attempt, MailOptions options) =>
        TimeSpan.FromSeconds(options.RetryDelays[Math.Min(attempt, options.RetryDelays.Count) - 1]);
}

public static class DeliverMailHandler
{
    public static async Task<OutgoingMessages> Handle(DeliverMail message, PlatformDbContext db, IMailTransport transport,
        MailOptions options, IClock clock, ILogger<MailOutbox> logger, CancellationToken cancellationToken)
    {
        var outgoing = new OutgoingMessages();
        var prepared = message.Prepared;
        var row = await db.MailMessages.FirstOrDefaultAsync(m => m.Id == prepared.MailId, cancellationToken);
        if (row is null)
        {
            row = new MailMessage
            {
                Id = prepared.MailId,
                QueuedAt = message.QueuedAt,
                Kind = prepared.Mail.Kind,
                TenantId = prepared.Mail.Tenant,
                UserId = prepared.Mail.UserId,
                Recipient = prepared.Mail.To.Address,
                Subject = prepared.Mail.Subject,
                Status = MailStatus.Retrying,
                Transport = transport.Name,
            };
            db.MailMessages.Add(row);
        }
        else if (row.Status != MailStatus.Retrying || message.Attempt <= row.Attempts)
        {
            // A redelivered envelope of an attempt already recorded: the outcome is settled, so do nothing.
            return outgoing;
        }

        var result = await transport.SendAsync(prepared, cancellationToken);
        var now = clock.UtcNow;
        row.Attempts = message.Attempt;
        row.LastAttemptAt = now;
        row.Transport = transport.Name;
        switch (result.Outcome)
        {
            case MailSendOutcome.Sent:
                row.Status = MailStatus.Sent;
                row.SentAt = now;
                row.ProviderMessageId = Truncate(result.ProviderMessageId, 200);
                row.NextAttemptAt = null;
                row.LastError = null;
                break;
            case MailSendOutcome.Transient when message.Attempt < options.MaxAttempts:
                var delay = MailRetrySchedule.After(message.Attempt, options);
                row.NextAttemptAt = now + delay;
                row.LastError = Truncate(result.Error, 400);
                outgoing.Delay(message with { Attempt = message.Attempt + 1 }, delay);
                logger.LogWarning("Mail {MailId} ({Kind}) attempt {Attempt} failed, retrying in {Delay}: {Error}",
                    row.Id, row.Kind, message.Attempt, delay, result.Error);
                break;
            default:
                row.Status = MailStatus.Failed;
                row.NextAttemptAt = null;
                row.LastError = Truncate(result.Outcome == MailSendOutcome.Transient
                    ? $"gave up after {message.Attempt} attempts: {result.Error}"
                    : result.Error, 400);
                logger.LogError("Mail {MailId} ({Kind}) failed after {Attempt} attempt(s): {Error}",
                    row.Id, row.Kind, message.Attempt, result.Error);
                break;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (row.TenantId is { } tenantId)
        {
            // Cascaded inside this handler's transaction: the announcement leaves only if the recorded outcome commits.
            outgoing.Add(new MailDeliveryRecorded(row.Id, row.Kind, tenantId, row.Status, row.Attempts, now, row.SentAt,
                row.NextAttemptAt, row.LastError, row.Transport));
        }
        return outgoing;
    }

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
