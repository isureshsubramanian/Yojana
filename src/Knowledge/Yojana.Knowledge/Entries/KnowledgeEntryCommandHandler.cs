using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Knowledge.Questions;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Knowledge.Entries;

public static class KnowledgeEntryCommandHandler
{
    public static async Task<EntryChangeResult> Handle(CreateEntryFromAnswer command, KnowledgeDbContext db,
        RecordReferenceLookup records, ITenantContext tenant, IClock clock, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var question = await db.Questions.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.QuestionId == command.QuestionId, cancellationToken)
            ?? throw Rule("question_not_found", "The question does not exist.");
        if (question.Status != QuestionStatus.Answered || question.AcceptedReplyId is null)
            throw Rule("entry_requires_accepted_answer", "Only an answered question with an accepted reply can become an entry.");
        if (await db.Entries.AnyAsync(x => x.TenantId == tenant.TenantId && x.SourceQuestionId == question.QuestionId,
                cancellationToken))
            throw Rule("entry_already_exists", "This accepted answer already has a knowledge entry.");
        var record = await records.OneAsync(question.RecordKind, question.RecordId,
            new RecordReferenceViewer(command.Actor.UserId, command.Actor.Roles), cancellationToken);
        EnsureVisible(record);
        var reply = await db.Replies.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId
            && x.QuestionId == question.QuestionId && x.ReplyId == question.AcceptedReplyId, cancellationToken);
        var now = clock.UtcNow;
        var entry = new KnowledgeEntry
        {
            TenantId = tenant.TenantId, EntryId = TimeOrderedId.New(now), Title = question.Title,
            Problem = question.Body, Answer = reply.Body, Steps = "", WrittenAgainstJson = "[]",
            Status = KnowledgeEntryStatus.Draft, SourceQuestionId = question.QuestionId, CurrentVersion = 1,
            AuthorId = command.Actor.UserId, CreatedAt = now, LastSavedBy = command.Actor.UserId, LastSavedAt = now,
        };
        db.Entries.Add(entry);
        AddVersion(db, entry, command.Actor.UserId, now, [],
            [new EntryRecordLinkInput(question.RecordKind, question.RecordId)]);
        await PublishAsync(bus, tenant, new KnowledgeEntryCreated(entry.EntryId, question.QuestionId,
            command.Actor.UserId, now), cancellationToken);
        return new EntryChangeResult(entry.EntryId, entry.Status, entry.CurrentVersion);
    }

    public static async Task<EntryChangeResult> Handle(EditDraft command, KnowledgeDbContext db,
        RecordReferenceLookup records, EntryTagLabelResolver tagLabels, WrittenAgainstResolver writtenAgainstResolver,
        ITenantContext tenant, IClock clock, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var entry = await FindAsync(db, tenant, command.EntryId, cancellationToken);
        if (entry.AuthorId != command.Actor.UserId)
            throw Rule("entry_edit_author_only", "Only the entry author may edit its draft.");
        var revisingPublishedEntry = entry.Status == KnowledgeEntryStatus.Published && entry.NeedsReview;
        if (entry.Status != KnowledgeEntryStatus.Draft && !revisingPublishedEntry)
            throw Rule("entry_not_draft", "Only a draft or a published entry needing review can be edited.");
        ValidateDraft(command.Draft);
        var tags = await tagLabels.ResolveAsync(command.Draft.Tags, cancellationToken);
        var writtenAgainst = await writtenAgainstResolver.ResolveAsync(command.Draft.WrittenAgainst,
            WrittenAgainst(entry.WrittenAgainstJson), cancellationToken);
        var links = await LinksAsync(entry, command.Draft.RecordLinks, db, records, tenant,
            command.Actor, cancellationToken);
        var now = clock.UtcNow;
        entry.Title = command.Draft.Title.Trim();
        entry.Problem = command.Draft.Problem.Trim();
        entry.Answer = command.Draft.Answer.Trim();
        entry.Steps = command.Draft.Steps.Trim();
        entry.WrittenAgainstJson = JsonSerializer.Serialize(writtenAgainst);
        entry.CurrentVersion++;
        if (revisingPublishedEntry) entry.Status = KnowledgeEntryStatus.Draft;
        entry.LastSavedBy = command.Actor.UserId;
        entry.LastSavedAt = now;
        AddVersion(db, entry, command.Actor.UserId, now, tags, links);
        await PublishAsync(bus, tenant, new KnowledgeEntryVersionSaved(entry.EntryId, entry.CurrentVersion,
            command.Actor.UserId, now), cancellationToken);
        return new EntryChangeResult(entry.EntryId, entry.Status, entry.CurrentVersion);
    }

    public static async Task<EntryChangeResult> Handle(SubmitEntryForReview command, KnowledgeDbContext db,
        ITenantContext tenant, IClock clock, IMessageBus bus, CancellationToken cancellationToken)
    {
        var entry = await FindAsync(db, tenant, command.EntryId, cancellationToken);
        if (entry.AuthorId != command.Actor.UserId)
            throw Rule("entry_submit_author_only", "Only the entry author may submit it for review.");
        if (entry.Status != KnowledgeEntryStatus.Draft
            && !(entry.Status == KnowledgeEntryStatus.Published && entry.NeedsReview))
            throw Rule("entry_not_draft", "Only a draft or a published entry needing review can be submitted for review.");
        var round = (await db.EntryReviewRounds.Where(x => x.TenantId == tenant.TenantId
            && x.EntryId == entry.EntryId).MaxAsync(x => (int?)x.Round, cancellationToken) ?? 0) + 1;
        var now = clock.UtcNow;
        db.EntryReviewRounds.Add(new KnowledgeEntryReviewRound
        {
            TenantId = tenant.TenantId, EntryId = entry.EntryId, Round = round, Version = entry.CurrentVersion,
            SubmittedBy = command.Actor.UserId, SubmittedAt = now,
        });
        entry.Status = KnowledgeEntryStatus.InReview;
        await PublishAsync(bus, tenant, new KnowledgeEntrySubmitted(entry.EntryId, entry.CurrentVersion, round,
            command.Actor.UserId, now), cancellationToken);
        return new EntryChangeResult(entry.EntryId, entry.Status, entry.CurrentVersion);
    }

    public static async Task<EntryChangeResult> Handle(ApproveEntry command, KnowledgeDbContext db,
        EntryReviewerSettings settings, ITenantContext tenant, IClock clock, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var (entry, round) = await ReviewableAsync(command.EntryId, command.Actor, db, settings, tenant,
            cancellationToken);
        var now = clock.UtcNow;
        round.Decision = "approve";
        round.Reviewer = command.Actor.UserId;
        round.ReviewedAt = now;
        round.Note = Optional(command.Note, 1000, "entry_review_note_too_long", "Review note");
        entry.Status = KnowledgeEntryStatus.Published;
        entry.PublishedVersion = entry.CurrentVersion;
        entry.PublishedBy = command.Actor.UserId;
        entry.PublishedAt = now;
        await ClearReviewReasonsAsync(entry, command.Actor.UserId, now, "approved", db, tenant,
            cancellationToken);
        await PublishAsync(bus, tenant, new KnowledgeEntryApproved(entry.EntryId, entry.CurrentVersion, round.Round,
            command.Actor.UserId, now), cancellationToken);
        return new EntryChangeResult(entry.EntryId, entry.Status, entry.CurrentVersion);
    }

    public static async Task<EntryChangeResult> Handle(SendBackEntry command, KnowledgeDbContext db,
        EntryReviewerSettings settings, ITenantContext tenant, IClock clock, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        Required(command.Note, 1000, "entry_send_back_note_required", "entry_review_note_too_long", "Send-back note");
        var (entry, round) = await ReviewableAsync(command.EntryId, command.Actor, db, settings, tenant,
            cancellationToken);
        var now = clock.UtcNow;
        round.Decision = "send_back";
        round.Reviewer = command.Actor.UserId;
        round.ReviewedAt = now;
        round.Note = command.Note.Trim();
        entry.Status = KnowledgeEntryStatus.Draft;
        await PublishAsync(bus, tenant, new KnowledgeEntrySentBack(entry.EntryId, entry.CurrentVersion, round.Round,
            command.Actor.UserId, now, round.Note), cancellationToken);
        return new EntryChangeResult(entry.EntryId, entry.Status, entry.CurrentVersion);
    }

    public static async Task<EntryChangeResult> Handle(RetireEntry command, KnowledgeDbContext db,
        EntryReviewerSettings settings, ITenantContext tenant, IClock clock, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        Required(command.Note, 1000, "entry_retirement_note_required", "entry_retirement_note_too_long", "Retirement note");
        var entry = await FindAsync(db, tenant, command.EntryId, cancellationToken);
        if (entry.Status != KnowledgeEntryStatus.Published)
            throw Rule("entry_not_published", "Only a published entry can be retired.");
        await EnsureReviewerRoleAsync(command.Actor, settings, cancellationToken);
        var now = clock.UtcNow;
        entry.Status = KnowledgeEntryStatus.Retired;
        entry.RetiredBy = command.Actor.UserId;
        entry.RetiredAt = now;
        entry.RetirementNote = command.Note.Trim();
        await PublishAsync(bus, tenant, new KnowledgeEntryRetired(entry.EntryId, command.Actor.UserId, now,
            entry.RetirementNote), cancellationToken);
        return new EntryChangeResult(entry.EntryId, entry.Status, entry.CurrentVersion);
    }

    private static async Task<(KnowledgeEntry Entry, KnowledgeEntryReviewRound Round)> ReviewableAsync(Guid entryId,
        QuestionActor actor, KnowledgeDbContext db, EntryReviewerSettings settings, ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var entry = await FindAsync(db, tenant, entryId, cancellationToken);
        if (entry.Status != KnowledgeEntryStatus.InReview)
            throw Rule("entry_not_in_review", "This entry is not awaiting a review decision.");
        if (entry.AuthorId == actor.UserId)
            throw Rule("entry_self_review_forbidden", "The entry author can never review their own entry.");
        await EnsureReviewerRoleAsync(actor, settings, cancellationToken);
        var round = await db.EntryReviewRounds.SingleAsync(x => x.TenantId == tenant.TenantId
            && x.EntryId == entry.EntryId && x.Decision == null, cancellationToken);
        return (entry, round);
    }

    private static async Task EnsureReviewerRoleAsync(QuestionActor actor, EntryReviewerSettings settings,
        CancellationToken cancellationToken)
    {
        var roles = await settings.GetAsync(cancellationToken);
        if (!roles.Any(role => actor.Roles.Contains(role, StringComparer.Ordinal)))
            throw Rule("entry_review_role_required", "A role selected in the tenant's knowledge-review setting is required.");
    }

    private static async Task<IReadOnlyList<EntryRecordLinkInput>> LinksAsync(KnowledgeEntry entry,
        IReadOnlyList<EntryRecordLinkInput> requested, KnowledgeDbContext db, RecordReferenceLookup records,
        ITenantContext tenant, QuestionActor actor, CancellationToken cancellationToken)
    {
        if (requested.Count > 50) throw Rule("entry_links_too_many", "An entry can link at most 50 records.");
        var links = requested.Distinct().ToList();
        var source = await db.Questions.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId
            && x.QuestionId == entry.SourceQuestionId, cancellationToken);
        var sourceLink = new EntryRecordLinkInput(source.RecordKind, source.RecordId);
        if (!links.Contains(sourceLink)) links.Insert(0, sourceLink);
        var viewer = new RecordReferenceViewer(actor.UserId, actor.Roles);
        var previous = await db.EntryRecordLinks.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.EntryId == entry.EntryId && x.Version == entry.CurrentVersion)
            .Select(x => new EntryRecordLinkInput(x.RecordKind, x.RecordId)).ToListAsync(cancellationToken);
        foreach (var group in previous.Where(x => !links.Contains(x)).GroupBy(x => x.RecordKind))
        {
            var found = await records.ManyAsync(group.Key, group.Select(x => x.RecordId).Distinct().ToList(),
                viewer, cancellationToken);
            // A link that became invisible is retained in the new immutable version. Otherwise an edit to the text
            // would silently erase a case relationship merely because the author's permissions changed.
            links.AddRange(group.Where(link => !found.TryGetValue(link.RecordId, out var record) || !record.MayView));
        }
        if (links.Count > 50) throw Rule("entry_links_too_many", "An entry can link at most 50 records.");
        foreach (var group in links.GroupBy(x => x.RecordKind))
        {
            var found = await records.ManyAsync(group.Key, group.Select(x => x.RecordId).Distinct().ToList(),
                viewer, cancellationToken);
            foreach (var link in group)
            {
                if (!found.TryGetValue(link.RecordId, out var record) || !record.Exists)
                    throw Rule("entry_link_not_found", "A linked record does not exist.");
                if (!record.MayView && !previous.Contains(link))
                    throw Rule("entry_link_not_visible", "You cannot link a record you may not view.");
            }
        }
        return links;
    }

    private static void AddVersion(KnowledgeDbContext db, KnowledgeEntry entry, Guid actor, DateTime at,
        IReadOnlyList<ResolvedEntryTag> tags, IReadOnlyList<EntryRecordLinkInput> links)
    {
        db.EntryVersions.Add(new KnowledgeEntryVersion
        {
            TenantId = entry.TenantId, EntryId = entry.EntryId, Version = entry.CurrentVersion,
            Title = entry.Title, Problem = entry.Problem, Answer = entry.Answer, Steps = entry.Steps,
            WrittenAgainstJson = entry.WrittenAgainstJson, SavedBy = actor, SavedAt = at,
        });
        db.EntryTags.AddRange(tags.Select(x => new KnowledgeEntryTag
        {
            TenantId = entry.TenantId, EntryId = entry.EntryId, Version = entry.CurrentVersion,
            Kind = x.Kind, Value = x.Value, Label = x.Label,
        }));
        db.EntryRecordLinks.AddRange(links.Select(x => new KnowledgeEntryRecordLink
        {
            TenantId = entry.TenantId, EntryId = entry.EntryId, Version = entry.CurrentVersion,
            RecordKind = x.RecordKind, RecordId = x.RecordId,
        }));
        db.EntryWrittenAgainst.AddRange(WrittenAgainst(entry.WrittenAgainstJson).Select(x =>
            new KnowledgeEntryWrittenAgainst
            {
                TenantId = entry.TenantId, EntryId = entry.EntryId, Version = entry.CurrentVersion,
                Kind = x.Kind, Reference = x.Reference.Trim(), Label = x.Label.Trim(),
            }));
    }

    internal static async Task ClearReviewReasonsAsync(KnowledgeEntry entry, Guid reviewer, DateTime at,
        string clearance, KnowledgeDbContext db, ITenantContext tenant, CancellationToken cancellationToken)
    {
        if (!entry.NeedsReview) return;
        var reasons = await db.EntryReviewReasons.Where(x => x.TenantId == tenant.TenantId
            && x.EntryId == entry.EntryId && x.ClearedAt == null).ToListAsync(cancellationToken);
        foreach (var reason in reasons)
        {
            reason.ClearedBy = reviewer;
            reason.ClearedAt = at;
            reason.ClearedAtVersion = entry.CurrentVersion;
            reason.Clearance = clearance;
        }
        entry.NeedsReview = false;
        entry.NeedsReviewSince = null;
    }

    private static List<WrittenAgainstInput> WrittenAgainst(string json)
    {
        try { return JsonSerializer.Deserialize<List<WrittenAgainstInput>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static void ValidateDraft(EntryDraftInput draft)
    {
        Required(draft.Title, 240, "entry_title_required", "entry_title_too_long", "Entry title");
        Required(draft.Problem, 8000, "entry_problem_required", "entry_problem_too_long", "Problem");
        Required(draft.Answer, 8000, "entry_answer_required", "entry_answer_too_long", "Answer");
        if (draft.Steps.Trim().Length > 8000) throw Rule("entry_steps_too_long", "Steps are at most 8000 characters.");
        if (draft.WrittenAgainst.Count > 50 || draft.WrittenAgainst.Any(x =>
                !WrittenAgainstKind.All.Contains(x.Kind, StringComparer.Ordinal)
                || string.IsNullOrWhiteSpace(x.Reference) || x.Reference.Trim().Length > 500
                || string.IsNullOrWhiteSpace(x.Label) || x.Label.Trim().Length > 500))
            throw Rule("entry_written_against_invalid", "Written-against items need a known kind, reference and label.");
        if (draft.WrittenAgainst.Select(x => (x.Kind, x.Reference)).Distinct().Count() != draft.WrittenAgainst.Count)
            throw Rule("entry_written_against_duplicate", "Do not add the same written-against item twice.");
        if (JsonSerializer.Serialize(draft.WrittenAgainst).Length > 8000)
            throw Rule("entry_written_against_too_long", "The written-against list is too long.");
    }

    private static async Task<KnowledgeEntry> FindAsync(KnowledgeDbContext db, ITenantContext tenant, Guid entryId,
        CancellationToken cancellationToken) => await db.Entries.FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.EntryId == entryId, cancellationToken)
        ?? throw Rule("entry_not_found", "The knowledge entry does not exist.");

    private static void EnsureVisible(RecordReference record)
    {
        if (!record.Exists) throw Rule("record_not_found", "The record does not exist.");
        if (!record.MayView) throw Rule("record_not_visible", "The current user may not view this record.");
    }

    private static void Required(string value, int max, string requiredRule, string maxRule, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Rule(requiredRule, $"{label} is required.");
        if (value.Trim().Length > max) throw Rule(maxRule, $"{label} is at most {max} characters.");
    }

    private static string? Optional(string? value, int max, string rule, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Trim().Length > max) throw Rule(rule, $"{label} is at most {max} characters.");
        return value.Trim();
    }

    private static QuestionRuleException Rule(string rule, string message) => new(rule, message);

    private static ValueTask PublishAsync(IMessageBus bus, ITenantContext tenant, object message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return bus.PublishAsync(message, new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
    }
}
