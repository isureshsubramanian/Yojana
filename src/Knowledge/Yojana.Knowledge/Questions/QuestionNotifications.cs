using Microsoft.EntityFrameworkCore;
using Quartz;
using Wolverine;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Mail;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Knowledge.Questions;

public static class QuestionNotificationSettings
{
    public const string PersonalDefaultKey = "knowledge.questions.notifications.personal_default";
    public const string RoleDefaultKey = "knowledge.questions.notifications.role_default";
    public const string DefaultPersonal = QuestionNotificationMode.Immediately;
    public const string DefaultRole = QuestionNotificationMode.DailyDigest;
}

public sealed record QuestionNotificationPreferenceView(string PersonalDefault, string RoleDefault, string? Choice);
public sealed record UpdateQuestionNotificationPreference(string? Mode);

public sealed record QuestionBellItem(Guid QuestionId, string Title, string RecordKind, Guid RecordId,
    string RecordName, string Reason, DateTime ActivityAt, string Link);
public sealed record QuestionBellSummary(int Count, IReadOnlyList<QuestionBellItem> Items);

public sealed class QuestionNotificationDefaults(ITenantSettings settings)
{
    public async Task<(string Personal, string Role)> GetAsync(CancellationToken cancellationToken)
    {
        var personal = await ReadAsync(QuestionNotificationSettings.PersonalDefaultKey,
            QuestionNotificationSettings.DefaultPersonal, cancellationToken);
        var role = await ReadAsync(QuestionNotificationSettings.RoleDefaultKey,
            QuestionNotificationSettings.DefaultRole, cancellationToken);
        return (personal, role);
    }

    private async Task<string> ReadAsync(string key, string fallback, CancellationToken cancellationToken)
    {
        var value = await settings.GetAsync(key, cancellationToken) ?? fallback;
        if (!QuestionNotificationMode.All.Contains(value, StringComparer.Ordinal))
            throw new InvalidOperationException($"Tenant setting {key} must be one of {string.Join(", ", QuestionNotificationMode.All)}.");
        return value;
    }
}

public sealed class QuestionNotificationService(KnowledgeDbContext db, RecordReferenceLookup records,
    QuestionNotificationDefaults defaults, ITenantContext tenant, IClock clock)
{
    public async Task<QuestionNotificationPreferenceView> PreferenceAsync(Guid userId,
        CancellationToken cancellationToken)
    {
        var configured = await defaults.GetAsync(cancellationToken);
        var choice = await db.NotificationPreferences.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.UserId == userId).Select(x => x.Mode).FirstOrDefaultAsync(cancellationToken);
        return new QuestionNotificationPreferenceView(configured.Personal, configured.Role, choice);
    }

    public async Task<QuestionNotificationPreferenceView> UpdatePreferenceAsync(Guid userId, string? mode,
        CancellationToken cancellationToken)
    {
        if (mode is not null && !QuestionNotificationMode.All.Contains(mode, StringComparer.Ordinal))
            throw new QuestionRuleException("question_notification_mode_unknown", $"'{mode}' is not a notification mode.");
        var row = await db.NotificationPreferences.FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.UserId == userId, cancellationToken);
        if (mode is null)
        {
            if (row is not null) db.NotificationPreferences.Remove(row);
        }
        else if (row is null)
        {
            db.NotificationPreferences.Add(new QuestionNotificationPreference
            {
                TenantId = tenant.TenantId, UserId = userId, Mode = mode, UpdatedAt = clock.UtcNow,
            });
        }
        else
        {
            row.Mode = mode;
            row.UpdatedAt = clock.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        return await PreferenceAsync(userId, cancellationToken);
    }

    public async Task<QuestionBellSummary> BellAsync(RecordReferenceViewer viewer, int take,
        CancellationToken cancellationToken)
    {
        if (take is < 1 or > 10) throw new QuestionRuleException("page_size_invalid", "Take must be between 1 and 10.");
        var relevant = await db.Questions.AsNoTracking().Where(q => q.TenantId == tenant.TenantId &&
            ((q.Status == QuestionStatus.Open && (q.DirectedToUser == viewer.UserId
                || (q.DirectedToRole != null && viewer.Roles.Contains(q.DirectedToRole)))
                && !db.Replies.Any(r => r.TenantId == q.TenantId && r.QuestionId == q.QuestionId
                    && r.Author == viewer.UserId && (r.At >= q.UpdatedAt || r.EditedAt >= q.UpdatedAt)))
             || (q.AskedBy == viewer.UserId && db.Participants.Any(p => p.TenantId == q.TenantId
                && p.QuestionId == q.QuestionId && p.UserId == viewer.UserId
                && db.Replies.Any(r => r.TenantId == q.TenantId && r.QuestionId == q.QuestionId
                    && r.Author != viewer.UserId && (p.LastReadAt == null || r.At > p.LastReadAt))))))
            .OrderByDescending(q => q.UpdatedAt).ThenByDescending(q => q.QuestionId).ToListAsync(cancellationToken);

        var visible = new List<(Question Question, RecordReference Record)>();
        foreach (var group in relevant.GroupBy(x => x.RecordKind))
        foreach (var ids in group.Select(x => x.RecordId).Distinct().Chunk(IRecordReferenceResolver.MaxRecordIds))
        {
            var found = await records.ManyAsync(group.Key, ids, viewer, cancellationToken);
            visible.AddRange(group.Where(question => found.TryGetValue(question.RecordId, out var record)
                && record.Exists && record.MayView && !string.IsNullOrWhiteSpace(record.DisplayName))
                .Select(question => (question, found[question.RecordId])));
        }
        var ordered = visible.OrderByDescending(x => x.Question.UpdatedAt).ThenByDescending(x => x.Question.QuestionId)
            .ToList();
        var idsOnPage = ordered.Take(take).Select(x => x.Question.QuestionId).ToList();
        var unread = await db.Participants.AsNoTracking().Where(p => p.TenantId == tenant.TenantId
                && p.UserId == viewer.UserId && idsOnPage.Contains(p.QuestionId))
            .Select(p => new { p.QuestionId, p.LastReadAt }).ToDictionaryAsync(x => x.QuestionId, cancellationToken);
        var items = ordered.Take(take).Select(x =>
        {
            var askedByMe = x.Question.AskedBy == viewer.UserId;
            var reason = askedByMe && unread.TryGetValue(x.Question.QuestionId, out var participant)
                && (participant.LastReadAt == null || participant.LastReadAt < x.Question.UpdatedAt)
                ? "New reply" : "Waiting for your reply";
            return new QuestionBellItem(x.Question.QuestionId, x.Question.Title, x.Question.RecordKind,
                x.Question.RecordId, x.Record.DisplayName!, reason, x.Question.UpdatedAt,
                RecordLink(x.Question.RecordKind, x.Question.RecordId, x.Question.QuestionId));
        }).ToList();
        return new QuestionBellSummary(ordered.Count, items);
    }

    public async Task MarkReadAsync(Guid questionId, Guid userId, CancellationToken cancellationToken)
    {
        var participant = await db.Participants.FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.QuestionId == questionId && x.UserId == userId, cancellationToken);
        if (participant is null)
        {
            db.Participants.Add(new QuestionParticipant
            {
                TenantId = tenant.TenantId, QuestionId = questionId, UserId = userId, LastReadAt = clock.UtcNow,
            });
        }
        else participant.LastReadAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static string RecordLink(string kind, Guid recordId, Guid questionId)
    {
        var path = kind switch
        {
            RecordKind.Unit => $"/units/{recordId:D}",
            RecordKind.Claim => $"/claims/{recordId:D}",
            RecordKind.Rma => $"/returns/{recordId:D}",
            RecordKind.RefurbishmentWorkOrder => $"/refurbishments/{recordId:D}",
            RecordKind.Certificate => $"/certificates/{recordId:D}",
            RecordKind.WarrantyPolicy => "/entitlements/policies",
            RecordKind.ImportJob => "/imports",
            RecordKind.IntegrationMessage => "/integration/messages-attention",
            _ => "/questions",
        };
        return $"{path}?tab=questions&question={questionId:D}";
    }
}

public sealed record RunQuestionDailyDigest(DateOnly Date);

[DisallowConcurrentExecution]
public sealed class QuestionDailyDigestJob(ITenantContextSetter setter, ITenantContext tenant,
    IMessageBus bus, IClock clock) : TenantJob(setter)
{
    public const string JobName = "knowledge.question_daily_digest";
    public const string Cron = "0 0 7 * * ?";

    protected override async Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        await bus.PublishAsync(new RunQuestionDailyDigest(DateOnly.FromDateTime(clock.UtcNow)),
            new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
    }
}

public static class QuestionNotificationHandler
{
    public static Task Handle(QuestionAsked message, KnowledgeDbContext db, RecordReferenceLookup records,
        ITenantStaffDirectory staff, IUserDirectory users, QuestionNotificationDefaults defaults,
        IMailOutbox mail, MailOptions mailOptions, ITenantContext tenant, IClock clock, CancellationToken cancellationToken) =>
        NotifyAsync("asked", message.QuestionId, message.QuestionId, message.AskedBy, message.AskedAt, db, records,
            staff, users, defaults, mail, mailOptions, tenant, clock, cancellationToken);

    public static Task Handle(QuestionReplied message, KnowledgeDbContext db, RecordReferenceLookup records,
        ITenantStaffDirectory staff, IUserDirectory users, QuestionNotificationDefaults defaults,
        IMailOutbox mail, MailOptions mailOptions, ITenantContext tenant, IClock clock, CancellationToken cancellationToken) =>
        NotifyAsync("replied", message.QuestionId, message.ReplyId, message.RepliedBy, message.RepliedAt, db, records,
            staff, users, defaults, mail, mailOptions, tenant, clock, cancellationToken);

    public static Task Handle(QuestionAnswered message, KnowledgeDbContext db, RecordReferenceLookup records,
        ITenantStaffDirectory staff, IUserDirectory users, QuestionNotificationDefaults defaults,
        IMailOutbox mail, MailOptions mailOptions, ITenantContext tenant, IClock clock, CancellationToken cancellationToken) =>
        NotifyAsync("answered", message.QuestionId, message.ReplyId, message.AnsweredBy, message.AnsweredAt, db, records,
            staff, users, defaults, mail, mailOptions, tenant, clock, cancellationToken);

    public static async Task Handle(RunQuestionDailyDigest message, KnowledgeDbContext db,
        RecordReferenceLookup records, ITenantStaffDirectory staff, IUserDirectory users, IMailOutbox mail,
        MailOptions mailOptions, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var pending = await db.Notifications.Where(x => x.TenantId == tenant.TenantId
            && x.DeliveryMode == QuestionNotificationMode.DailyDigest && x.DigestDate == null
            && x.OccurredAt < message.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
            .OrderBy(x => x.OccurredAt).ToListAsync(cancellationToken);
        foreach (var group in pending.GroupBy(x => x.RecipientUserId))
        {
            if (await db.Digests.AnyAsync(x => x.TenantId == tenant.TenantId && x.UserId == group.Key
                && x.DigestDate == message.Date, cancellationToken)) continue;
            var recipient = (await staff.NotificationRecipientsAsync(group.Key, null, cancellationToken)).SingleOrDefault();
            if (recipient is null) continue;
            var questionIds = group.Select(x => x.QuestionId).Distinct().ToList();
            var questions = await db.Questions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && questionIds.Contains(x.QuestionId)).ToListAsync(cancellationToken);
            var visible = new List<(Question Question, RecordReference Record)>();
            var viewer = new RecordReferenceViewer(recipient.UserId, recipient.Roles);
            foreach (var byKind in questions.GroupBy(x => x.RecordKind))
            {
                var found = await records.ManyAsync(byKind.Key, byKind.Select(x => x.RecordId).Distinct().ToList(),
                    viewer, cancellationToken);
                visible.AddRange(byKind.Where(q => found.TryGetValue(q.RecordId, out var record)
                    && record.Exists && record.MayView && !string.IsNullOrWhiteSpace(record.DisplayName))
                    .Select(q => (q, found[q.RecordId])));
            }
            if (visible.Count == 0)
            {
                foreach (var row in group) row.DigestDate = message.Date;
                continue;
            }
            var facts = visible.OrderByDescending(x => x.Question.UpdatedAt).Select(x =>
                ($"{x.Record.DisplayName}", x.Question.Title)).ToList();
            var rendered = AccountMailLayout.Render(new AccountMailContent("Yojana", "Questions",
                $"{visible.Count} question update{(visible.Count == 1 ? "" : "s")}",
                ["Here are the question updates waiting for you."], facts,
                ("Open questions", mailOptions.Link("/questions")),
                ["Only records available to your current role are included."], "Sent by Yojana."));
            var mailId = await mail.QueueAsync(new OutgoingMail("knowledge.question_digest", tenant.TenantId,
                recipient.UserId, new MailRecipient(recipient.Email, recipient.Name),
                $"{visible.Count} question update{(visible.Count == 1 ? "" : "s")}", rendered.Html, rendered.Text),
                cancellationToken);
            db.Digests.Add(new QuestionDigest
            {
                TenantId = tenant.TenantId, UserId = recipient.UserId, DigestDate = message.Date,
                MailId = mailId, QueuedAt = clock.UtcNow,
            });
            foreach (var row in group) row.DigestDate = message.Date;
        }
    }

    private static async Task NotifyAsync(string eventKind, Guid questionId, Guid eventId, Guid actor,
        DateTime occurredAt, KnowledgeDbContext db, RecordReferenceLookup records, ITenantStaffDirectory staff,
        IUserDirectory users, QuestionNotificationDefaults defaults, IMailOutbox mail, MailOptions mailOptions,
        ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var question = await db.Questions.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.QuestionId == questionId, cancellationToken);
        if (question is null) return;
        var recipients = new List<TenantStaffNotificationRecipient>();
        recipients.AddRange(await staff.NotificationRecipientsAsync(question.DirectedToUser, question.DirectedToRole,
            cancellationToken));
        if (eventKind != "asked")
            recipients.AddRange(await staff.NotificationRecipientsAsync(question.AskedBy, null, cancellationToken));
        var configured = await defaults.GetAsync(cancellationToken);
        var actorNames = await users.DisplayNamesAsync([actor.ToString("D")], cancellationToken);
        var actorName = actorNames.GetValueOrDefault(actor.ToString("D"), "A colleague");
        foreach (var recipient in recipients.Where(x => x.UserId != actor).DistinctBy(x => x.UserId))
        {
            if (await db.Notifications.AnyAsync(x => x.TenantId == tenant.TenantId
                && x.RecipientUserId == recipient.UserId && x.EventKind == eventKind && x.EventId == eventId,
                cancellationToken)) continue;
            var record = await records.OneAsync(question.RecordKind, question.RecordId,
                new RecordReferenceViewer(recipient.UserId, recipient.Roles), cancellationToken);
            if (!record.Exists || !record.MayView || string.IsNullOrWhiteSpace(record.DisplayName)) continue;
            var choice = await db.NotificationPreferences.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.UserId == recipient.UserId).Select(x => x.Mode).FirstOrDefaultAsync(cancellationToken);
            var mode = choice ?? (question.DirectedToUser is null ? configured.Role : configured.Personal);
            if (mode == QuestionNotificationMode.Off) continue;
            var row = new QuestionNotification
            {
                TenantId = tenant.TenantId, NotificationId = TimeOrderedId.New(clock.UtcNow),
                QuestionId = question.QuestionId, RecipientUserId = recipient.UserId, EventKind = eventKind,
                EventId = eventId, DeliveryMode = mode, OccurredAt = occurredAt,
            };
            db.Notifications.Add(row);
            if (mode != QuestionNotificationMode.Immediately) continue;
            var verb = eventKind switch { "asked" => "asked a question", "answered" => "accepted an answer", _ => "replied" };
            var link = mailOptions.Link(QuestionNotificationService.RecordLink(question.RecordKind,
                question.RecordId, question.QuestionId));
            var rendered = AccountMailLayout.Render(new AccountMailContent("Yojana", "Questions", question.Title,
                [$"{actorName} {verb}."], [("Record", record.DisplayName!)], ("Open question", link),
                ["The full thread remains in Yojana."], "Sent by Yojana."));
            row.MailId = await mail.QueueAsync(new OutgoingMail("knowledge.question_immediate", tenant.TenantId,
                recipient.UserId, new MailRecipient(recipient.Email, recipient.Name),
                $"{actorName} {verb}: {question.Title}", rendered.Html, rendered.Text), cancellationToken);
        }
    }
}
