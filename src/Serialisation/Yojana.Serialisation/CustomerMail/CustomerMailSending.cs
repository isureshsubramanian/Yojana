using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Contracts.Paging;
using Yojana.Platform.Mail;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.CustomerMail;

/// <summary>Who may receive a message: the customer's contacts, or addresses the sender typed and confirmed.</summary>
public static class CustomerMailRecipients
{
    public const int MaxTo = 10;

    public static bool IsAddress(string value) => value.Length <= 320 && value.Contains('@')
        && MailboxAddress.TryParse(value, out var parsed) && parsed.Address == value;

    public static IReadOnlyList<MailRecipientInput> Validate(IReadOnlyList<MailRecipientInput>? recipients, IReadOnlyList<PartyContactLabel> contacts,
        int max, string role)
    {
        var list = (recipients ?? []).Select(x => x with { Address = (x.Address ?? "").Trim(), Name = string.IsNullOrWhiteSpace(x.Name) ? null : x.Name.Trim() }).ToList();
        if (list.Count > max) throw new CommandRefusedException("mail_recipients_too_many", $"Send to at most {max} {role} recipients.");
        foreach (var recipient in list)
        {
            if (!IsAddress(recipient.Address))
                throw new CommandRefusedException("mail_recipient_invalid", $"'{recipient.Address}' is not an e-mail address.");
            if (recipient.Name is { Length: > 200 } || (recipient.Name?.Any(char.IsControl) ?? false))
                throw new CommandRefusedException("mail_recipient_invalid", "A recipient's name is at most 200 printable characters.");
            var known = contacts.Any(x => x.Email.Equals(recipient.Address, StringComparison.OrdinalIgnoreCase));
            if (!known && !recipient.Confirmed)
                throw new CommandRefusedException("mail_recipient_unconfirmed",
                    $"{recipient.Address} is not one of the customer's contacts.", "Confirm the address before sending.");
        }
        if (list.Select(x => x.Address.ToLowerInvariant()).Distinct().Count() != list.Count)
            throw new CommandRefusedException("mail_recipient_duplicate", "Each address appears once.");
        return list;
    }
}

/// <summary>What the compose screen needs, and the preview of one message before it is sent.</summary>
public sealed class CoverageMailCompose(SerialisationDbContext db, ITenantContext tenant, CustomerMailKinds kinds,
    CustomerMailRendering rendering, IPartyContactPort contacts, CompanyMailIdentity company, MailOptions mail)
{
    public async Task<CoverageMailComposeView?> GetAsync(Guid unitId, MailActor actor, CancellationToken cancellationToken)
    {
        var kind = kinds.Get(CustomerMailKind.CoverageSheet);
        var record = await kind.ReadAsync(unitId, cancellationToken);
        if (record is null) return null;
        var people = record.CustomerPartyRef is { } party ? await contacts.ContactsAsync(party, cancellationToken) : [];
        var templates = await PublishedAsync(CustomerMailKind.CoverageSheet, cancellationToken);
        var identity = await company.GetCurrentAsync(cancellationToken);
        var sheet = record.Facts;
        return new CoverageMailComposeView(unitId, sheet["unit"]!["serial"]!.GetValue<string>(),
            sheet["item"]!["model_name"]!.GetValue<string>(), record.CustomerName,
            people.Select(x => new CustomerContact(x.Name, x.Email, x.Role)).ToList(), templates,
            people.Select(x => x.Language).FirstOrDefault(x => x is not null),
            string.IsNullOrWhiteSpace(identity.SenderName) ? mail.FromName : identity.SenderName,
            string.IsNullOrWhiteSpace(identity.ReplyTo) ? null : identity.ReplyTo, mail.FromAddress, actor.Name,
            CustomerMailRendering.Team(actor.Roles), record.AttachmentName ?? "");
    }

    public async Task<MailPreviewView?> PreviewAsync(Guid unitId, ComposePreviewRequest request, MailActor actor, CancellationToken cancellationToken)
    {
        var kind = kinds.Get(CustomerMailKind.CoverageSheet);
        var version = await PublishedVersionAsync(request.TemplateVersionId, CustomerMailKind.CoverageSheet, cancellationToken);
        return await rendering.PreviewAsync(kind, MailTemplateService.Read(version), request.Language, unitId, request.RecipientName,
            request.Note, actor, request.SignedBy, cancellationToken);
    }

    public async Task<IReadOnlyList<ComposeTemplateChoice>> PublishedAsync(string kind, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rows = await (from template in db.MailTemplates.AsNoTracking()
                          join version in db.MailTemplateVersions.AsNoTracking() on new { template.TenantId, template.TemplateId } equals new { version.TenantId, version.TemplateId }
                          where template.TenantId == tenantId && template.Kind == kind && version.Status == MailTemplateStatus.Published
                          select new { template.TemplateId, template.Name, Version = version }).ToListAsync(cancellationToken);
        var defaults = await db.MailTemplateDefaults.AsNoTracking().Where(x => x.TenantId == tenantId && x.Kind == kind).ToListAsync(cancellationToken);
        return rows.Select(x =>
        {
            var content = MailTemplateService.Read(x.Version);
            return new ComposeTemplateChoice(x.TemplateId, x.Version.VersionId, x.Name, x.Version.Version, x.Version.PublishedAt!.Value,
                content.Languages.Select(l => l.Language).ToList(),
                defaults.Where(d => d.TemplateId == x.TemplateId).Select(d => d.Language).Order().ToList(), content.AskForNote, content.AttachPdf);
        }).OrderByDescending(x => x.DefaultFor.Count).ThenBy(x => x.Name).ToList();
    }

    /// <summary>Whether this tenant can e-mail the sheet at all: some What's covered template is published.</summary>
    public Task<bool> AvailableAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return (from version in db.MailTemplateVersions.AsNoTracking()
                join template in db.MailTemplates.AsNoTracking() on new { version.TenantId, version.TemplateId } equals new { template.TenantId, template.TemplateId }
                where version.TenantId == tenantId && version.Status == MailTemplateStatus.Published && template.Kind == CustomerMailKind.CoverageSheet
                select version.VersionId).AnyAsync(cancellationToken);
    }

    internal async Task<MailTemplateVersion> PublishedVersionAsync(Guid versionId, string kind, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var found = await (from version in db.MailTemplateVersions.AsNoTracking()
                           join template in db.MailTemplates.AsNoTracking() on new { version.TenantId, version.TemplateId } equals new { template.TenantId, template.TemplateId }
                           where version.TenantId == tenantId && version.VersionId == versionId
                           select new { Version = version, template.Kind }).FirstOrDefaultAsync(cancellationToken);
        if (found is null || found.Kind != kind)
            throw new CommandRefusedException("mail_template_version_unknown", "Choose a published template for this kind of message.");
        if (found.Version.Status != MailTemplateStatus.Published)
            throw new CommandRefusedException("mail_template_version_not_published", "That template version is no longer published; choose the current one.");
        return found.Version;
    }
}

/// <summary>
/// Sends the What's covered sheet: one message per To recipient (each gets their own copy; Cc is shared), each recorded
/// with its rendered copy. Runs as a Wolverine handler so the recorded rows and the queued mail commit together, and a
/// repeated command (same CommandId) returns the messages it already queued instead of sending again.
/// COUPLING: its dependencies that reach PlatformDbContext (IMailOutbox, CompanyMailIdentity inside the compose and
/// rendering services) are registered as factories (PlatformSetup and SerialisationModule say why on their side). Taking
/// any platform service with a plain registration here makes the host refuse to start.
/// </summary>
public static class SendCoverageMailHandler
{
    public static async Task<MailQueuedView> Handle(SendCoverageMail command, SerialisationDbContext db, ITenantContext tenant,
        CustomerMailKinds kinds, CoverageMailCompose compose, CustomerMailRendering rendering, IPartyContactPort contacts,
        IMailOutbox outbox, IClock clock, CancellationToken cancellationToken)
    {
        var already = await db.CustomerMailMessages.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.CommandId == command.CommandId).Select(x => x.MessageId).ToListAsync(cancellationToken);
        if (already.Count > 0) return new MailQueuedView(already);

        var kind = kinds.Get(CustomerMailKind.CoverageSheet);
        var version = await compose.PublishedVersionAsync(command.TemplateVersionId, kind.View.Kind, cancellationToken);
        var template = await db.MailTemplates.AsNoTracking().FirstAsync(x => x.TenantId == tenant.TenantId && x.TemplateId == version.TemplateId, cancellationToken);
        var content = MailTemplateService.Read(version);
        if (command.SignedBy is not ("me" or "team"))
            throw new CommandRefusedException("mail_signed_by_invalid", "Sign as yourself or as the company.");
        if (!content.AskForNote && !string.IsNullOrWhiteSpace(command.Note))
            throw new CommandRefusedException("mail_note_not_offered", "This template does not take a personal note.");
        var actor = new MailActor(command.ActorRef, command.ActorName, command.ActorEmail, command.ActorRoles);
        var record = await kind.ReadAsync(command.UnitId, cancellationToken)
            ?? throw new CommandRefusedException("mail_record_unknown", "That unit does not exist.");
        var people = record.CustomerPartyRef is { } party ? await contacts.ContactsAsync(party, cancellationToken) : [];

        IReadOnlyList<MailRecipientInput> to;
        IReadOnlyList<MailRecipientInput> cc;
        if (command.CopyToMeOnly)
        {
            to = [new MailRecipientInput(actor.Email, actor.Name, true)];
            cc = [];
        }
        else
        {
            to = CustomerMailRecipients.Validate(command.To, people, CustomerMailRecipients.MaxTo, "To");
            if (to.Count == 0) throw new CommandRefusedException("mail_recipients_required", "Add at least one recipient.");
            cc = CustomerMailRecipients.Validate(command.Cc, people, MailLimits.MaxCc, "Cc");
            if (cc.Any(c => to.Any(t => t.Address.Equals(c.Address, StringComparison.OrdinalIgnoreCase))))
                throw new CommandRefusedException("mail_recipient_duplicate", "An address is either in To or in Cc, not both.");
        }

        MailAttachmentFile? attachment = null;
        if (content.AttachPdf)
        {
            // The PDF cannot be left out (owner decision 2026-10-21): no PDF, no message.
            attachment = await kind.AttachmentAsync(command.UnitId, command.Language, cancellationToken)
                ?? throw new CommandRefusedException("mail_attachment_unavailable", "The What's covered PDF could not be made, so nothing was sent.",
                    "Try again in a few minutes; the PDF renderer may be busy.");
        }

        var now = clock.UtcNow;
        var batch = Guid.CreateVersion7(now);
        var ids = new List<Guid>();
        foreach (var recipient in to)
        {
            var contactName = recipient.Name ?? people.FirstOrDefault(x => x.Email.Equals(recipient.Address, StringComparison.OrdinalIgnoreCase))?.Name;
            var prepared = await rendering.RenderAsync(kind, content, command.Language, command.UnitId, contactName, command.Note, actor,
                command.SignedBy, cancellationToken);
            var subject = command.CopyToMeOnly ? "[Copy] " + prepared.Mail.Subject : prepared.Mail.Subject;
            var id = await outbox.QueueAsync(new OutgoingMail(kind.View.Kind, tenant.TenantId, null,
                new MailRecipient(recipient.Address, contactName), subject, prepared.Mail.Html, prepared.Mail.Text,
                cc.Select(x => new MailRecipient(x.Address, x.Name)).ToList(),
                attachment is null ? null : [new MailAttachment(attachment.FileName, attachment.MediaType, attachment.Content)]), cancellationToken);
            db.CustomerMailMessages.Add(new CustomerMailMessage
            {
                TenantId = tenant.TenantId, MessageId = id, BatchId = batch, CommandId = command.CommandId, Kind = kind.View.Kind,
                TemplateId = template.TemplateId, TemplateVersionId = version.VersionId, TemplateVersion = version.Version,
                TemplateName = template.Name, Language = command.Language, UnitId = command.UnitId, AboutLabel = record.AboutLabel.Split(" · ")[0],
                CustomerPartyRef = record.CustomerPartyRef, CustomerName = record.CustomerName, ToAddress = recipient.Address,
                ToName = contactName, Cc = JsonSerializer.Serialize(cc.Select(x => new { address = x.Address, name = x.Name })),
                Subject = subject, HtmlBody = prepared.Mail.Html, TextBody = prepared.Mail.Text, AttachmentName = attachment?.FileName,
                HasNote = !string.IsNullOrWhiteSpace(command.Note), SentBy = actor.Ref, CopyToSelf = command.CopyToMeOnly,
                Status = CustomerMailStatus.Queued, QueuedAt = now,
            });
            ids.Add(id);
        }
        return new MailQueuedView(ids);
    }
}

/// <summary>
/// Keeps the sent log's delivery state in step with the platform's delivery attempts (<see cref="MailDeliveryRecorded"/>).
/// Only customer kinds are this module's; an attempt older than the one already recorded is a redelivery and is ignored.
/// </summary>
public static class CustomerMailDeliveryRecordedHandler
{
    public static async Task Handle(MailDeliveryRecorded message, SerialisationDbContext db, ITenantContext tenant, CancellationToken cancellationToken)
    {
        if (!message.Kind.StartsWith("customer.", StringComparison.Ordinal) || message.Tenant != tenant.TenantId) return;
        var row = await db.CustomerMailMessages.FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.MessageId == message.MailId, cancellationToken);
        // Test sends from the designer go to the designer only and are not part of the sent log.
        if (row is null || message.Attempts < row.Attempts) return;
        row.Attempts = message.Attempts;
        row.Status = message.Status;
        row.LastAttemptAt = message.AttemptedAt;
        row.SentAt = message.SentAt;
        row.NextAttemptAt = message.NextAttemptAt;
        row.LastError = message.Error;
        row.Transport = message.Transport;
    }
}

/// <summary>The sent log: newest first, keyset-paged, filtered by status, kind and sender.</summary>
public sealed class SentMailQueries(SerialisationDbContext db, ITenantContext tenant, IClock clock, CustomerMailKinds kinds,
    IUserDirectory people)
{
    private static readonly SortedPage<CustomerMailMessage, Guid> Pages = new("queued", row => row.MessageId,
        new SortColumn<CustomerMailMessage, DateTime>("queued", row => row.QueuedAt, SortDirection.Descending),
        new SortColumn<CustomerMailMessage, string>("status", row => row.Status));
    public const string Automatic = "automatic";

    /// <param name="text">Part of the recipient, subject, record label or customer name; applied after the 30-day figures, which
    /// describe the log rather than the search.</param>
    public async Task<SentMailPage> ListAsync(string? status, string? kind, string? sentBy, Guid? unitId, string? text,
        string? sort, string? direction, string? cursor, int take, CancellationToken cancellationToken)
    {
        var paging = Pages.Parse(sort, direction, take, cursor, defaultTake: 25);
        var tenantId = tenant.TenantId;
        var query = db.CustomerMailMessages.AsNoTracking().Where(x => x.TenantId == tenantId && !x.CopyToSelf);
        if (unitId is { } unit) query = query.Where(x => x.UnitId == unit);
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(x => x.Kind == kind);
        if (sentBy == Automatic) query = query.Where(x => x.SentBy == null);
        else if (!string.IsNullOrWhiteSpace(sentBy)) query = query.Where(x => x.SentBy == sentBy);
        var since = clock.UtcNow.AddDays(-30);
        var counts = await query.Where(x => x.QueuedAt >= since).GroupBy(x => new { x.Status, x.Kind })
            .Select(g => new { g.Key.Status, g.Key.Kind, Count = g.Count() }).ToListAsync(cancellationToken);
        if (status == "retrying") query = query.Where(x => x.Status == CustomerMailStatus.Retrying || x.Status == CustomerMailStatus.Queued);
        else if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        if (text?.Trim() is { Length: > 0 } find)
            query = query.Where(x => x.ToAddress.Contains(find) || (x.ToName != null && x.ToName.Contains(find)) || x.Subject.Contains(find)
                || x.AboutLabel.Contains(find) || (x.CustomerName != null && x.CustomerName.Contains(find)));
        var total = await query.CountAsync(cancellationToken);
        var rows = await paging.Apply(query).Take(paging.Take + 1).ToListAsync(cancellationToken);
        var next = rows.Count > paging.Take ? rows[paging.Take - 1] : null;
        var page = rows.Take(paging.Take).ToList();
        var names = await NamesAsync(page.Select(x => x.SentBy), cancellationToken);
        var sent = counts.Where(x => x.Status == CustomerMailStatus.Sent).Sum(x => x.Count);
        var failed = counts.Where(x => x.Status == CustomerMailStatus.Failed).Sum(x => x.Count);
        var most = counts.GroupBy(x => x.Kind).Select(g => new { Kind = g.Key, Count = g.Sum(x => x.Count) }).MaxBy(x => x.Count);
        var kpis = new SentMailKpis(counts.Sum(x => x.Count), sent,
            counts.Where(x => x.Status is CustomerMailStatus.Retrying or CustomerMailStatus.Queued).Sum(x => x.Count), failed,
            counts.Sum(x => x.Count) == 0 ? null : Math.Round(100m * sent / counts.Sum(x => x.Count), 1),
            most is null ? null : KindName(most.Kind), most?.Count ?? 0);
        return new SentMailPage(page.Select(x => Row(x, names)).ToList(), total,
            next is null ? null : paging.CursorFor(next), kpis,
            kinds.Views.Select(x => new MailKindName(x.Kind, x.Name)).ToList());
    }

    public Task<SentMailPage> ListAsync(string? status, string? kind, string? sentBy, Guid? unitId, string? text,
        string? cursor, int take, CancellationToken cancellationToken) =>
        ListAsync(status, kind, sentBy, unitId, text, null, null, cursor, take, cancellationToken);

    public async Task<SentMailDetail?> GetAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var row = await db.CustomerMailMessages.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.MessageId == messageId, cancellationToken);
        if (row is null) return null;
        var names = await NamesAsync([row.SentBy], cancellationToken);
        var timeline = new List<SentMailEvent> { new(row.QueuedAt, "Queued", row.UnitId is null ? null : "from the unit's What's covered page") };
        if (row.Status == CustomerMailStatus.Retrying && row.LastAttemptAt is { } tried)
            timeline.Add(new(tried, $"Attempt {row.Attempts} failed", row.NextAttemptAt is { } nextTry ? $"{row.LastError} · next try {nextTry:HH:mm} UTC" : row.LastError));
        if (row.Status == CustomerMailStatus.Sent && row.SentAt is { } sentAt)
            timeline.Add(new(sentAt, "Delivered to the mail server", $"{Ordinal(row.Attempts)} attempt · {row.Transport}"));
        if (row.Status == CustomerMailStatus.Failed && row.LastAttemptAt is { } failedAt)
            timeline.Add(new(failedAt, "Not delivered", row.LastError));
        var cc = JsonSerializer.Deserialize<List<CcRow>>(row.Cc, MailTemplateService.Json) ?? [];
        return new SentMailDetail(Row(row, names), cc.Select(x => new MailRecipientInput(x.Address, x.Name)).ToList(), row.HtmlBody,
            row.TextBody, row.AttachmentName, row.HasNote, row.Transport, row.SentAt, timeline.OrderByDescending(x => x.At).ToList());
    }

    public async IAsyncEnumerable<SentMailRow> AllAsync(string? status, string? kind, string? sentBy, string? text,
        string? sort, string? direction,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await ListAsync(status, kind, sentBy, null, text, sort, direction, cursor, 200, cancellationToken);
            foreach (var row in page.Rows) yield return row;
            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    public IAsyncEnumerable<SentMailRow> AllAsync(string? status, string? kind, string? sentBy, string? text,
        CancellationToken cancellationToken) => AllAsync(status, kind, sentBy, text, null, null, cancellationToken);

    private sealed record CcRow(string Address, string? Name);

    private SentMailRow Row(CustomerMailMessage x, IReadOnlyDictionary<string, string> names) => new(x.MessageId, x.QueuedAt, x.ToAddress, x.ToName,
        (JsonSerializer.Deserialize<List<CcRow>>(x.Cc, MailTemplateService.Json) ?? []).Count, x.CustomerName, x.Subject, x.TemplateName,
        x.TemplateVersion, x.Language, x.Kind, KindName(x.Kind), x.AboutLabel, x.UnitId,
        x.SentBy is null ? "Sent automatically" : names.GetValueOrDefault(x.SentBy, x.SentBy), x.SentBy is null, x.Status, x.Attempts,
        x.NextAttemptAt, x.LastError);

    private string KindName(string kind) => kinds.Exists(kind) ? kinds.Get(kind).View.Name : kind;

    private static string Ordinal(int n) => n switch { 1 => "first", 2 => "second", 3 => "third", _ => $"{n}th" };

    private static (DateTime QueuedAt, Guid MessageId)? Cursor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('_');
        return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && Guid.TryParseExact(parts[1], "N", out var id) && ticks >= 0 && ticks <= DateTime.MaxValue.Ticks
            ? (new DateTime(ticks, DateTimeKind.Utc), id)
            : throw new CommandRefusedException("cursor_invalid", "The page cursor is not valid; start from the first page.");
    }

    private async Task<IReadOnlyDictionary<string, string>> NamesAsync(IEnumerable<string?> references, CancellationToken cancellationToken)
    {
        var distinct = references.OfType<string>().Distinct().ToList();
        return distinct.Count == 0 ? new Dictionary<string, string>() : await people.DisplayNamesAsync(distinct, cancellationToken);
    }
}
