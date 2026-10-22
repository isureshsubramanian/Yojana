using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Knowledge.Questions;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Knowledge.Entries;

/// <summary>Consumes narrow change messages and owns every mutation of an entry's currentness state.</summary>
public static class KnowledgeCurrentnessHandler
{
    public static async Task Handle(WarrantyPolicyVersionSuperseded message, KnowledgeDbContext db,
        ITenantContext tenant, IClock clock, IMessageBus bus, CancellationToken cancellationToken)
    {
        var reference = message.SupersededPolicyVersionId.ToString("D");
        var detail = $"Policy {message.PolicyCode} v{message.SupersededVersion} was superseded by v{message.NewVersion}.";
        await FlagAsync(WrittenAgainstKind.PolicyVersion, reference,
            KnowledgeReviewReasonKind.PolicyVersionSuperseded, message.NewPolicyVersionId, detail,
            message.PublishedAt, db, tenant, clock, bus, cancellationToken);
    }

    public static async Task<ScreenChangeResult> Handle(MarkKnowledgeScreenChanged command, KnowledgeDbContext db,
        ITenantContext tenant, IClock clock, IMessageBus bus, CancellationToken cancellationToken)
    {
        if (!command.Actor.Roles.Contains(YojanaRole.TenantAdministrator, StringComparer.Ordinal))
            throw Rule("screen_change_admin_required", "A tenant administrator must record a screen change.");
        var reference = Required(command.ScreenReference, 500, "screen_reference_required",
            "screen_reference_too_long", "Screen reference");
        var reason = Required(command.Reason, 500, "screen_change_reason_required",
            "screen_change_reason_too_long", "Change reason");
        var label = await db.EntryWrittenAgainst.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.Kind == WrittenAgainstKind.Screen && x.Reference == reference)
            .OrderByDescending(x => x.Version).Select(x => x.Label).FirstOrDefaultAsync(cancellationToken)
            ?? throw Rule("screen_reference_not_found", "No knowledge entry references this screen.");
        var now = clock.UtcNow;
        var changeId = TimeOrderedId.New(now);
        db.ScreenChanges.Add(new KnowledgeScreenChange
        {
            TenantId = tenant.TenantId, ChangeId = changeId, ScreenReference = reference,
            ScreenLabel = label, Reason = reason, ChangedBy = command.Actor.UserId, ChangedAt = now,
        });
        var count = await FlagAsync(WrittenAgainstKind.Screen, reference,
            KnowledgeReviewReasonKind.ScreenChanged, changeId, $"{label} changed: {reason}", now,
            db, tenant, clock, bus, cancellationToken);
        return new ScreenChangeResult(changeId, reference, label, count, now);
    }

    public static async Task<EntryChangeResult> Handle(ConfirmKnowledgeEntryStillCorrect command,
        KnowledgeDbContext db, EntryReviewerSettings settings, ITenantContext tenant, IClock clock,
        IMessageBus bus, CancellationToken cancellationToken)
    {
        var entry = await db.Entries.FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.EntryId == command.EntryId, cancellationToken)
            ?? throw Rule("entry_not_found", "The knowledge entry does not exist.");
        if (entry.Status != KnowledgeEntryStatus.Published || !entry.NeedsReview)
            throw Rule("entry_not_needing_review", "Only a published entry needing review can be confirmed.");
        if (entry.AuthorId == command.Actor.UserId)
            throw Rule("entry_self_review_forbidden", "The entry author can never clear their own entry.");
        var reviewerRoles = await settings.GetAsync(cancellationToken);
        if (!reviewerRoles.Any(role => command.Actor.Roles.Contains(role, StringComparer.Ordinal)))
            throw Rule("entry_review_role_required",
                "A role selected in the tenant's knowledge-review setting is required.");
        var now = clock.UtcNow;
        await KnowledgeEntryCommandHandler.ClearReviewReasonsAsync(entry, command.Actor.UserId, now,
            "still_correct", db, tenant, cancellationToken);
        await bus.PublishAsync(new KnowledgeEntryCurrentnessConfirmed(entry.EntryId,
            entry.PublishedVersion ?? entry.CurrentVersion, command.Actor.UserId, now),
            new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        return new EntryChangeResult(entry.EntryId, entry.Status, entry.CurrentVersion);
    }

    private static async Task<int> FlagAsync(string writtenAgainstKind, string reference, string reasonKind,
        Guid triggerId, string detail, DateTime occurredAt, KnowledgeDbContext db, ITenantContext tenant,
        IClock clock, IMessageBus bus, CancellationToken cancellationToken)
    {
        var dependencies = await (from entry in db.Entries
            join against in db.EntryWrittenAgainst
                on new { entry.TenantId, entry.EntryId, Version = entry.PublishedVersion!.Value }
                equals new { against.TenantId, against.EntryId, against.Version }
            where entry.TenantId == tenant.TenantId && entry.PublishedVersion != null
                && entry.Status != KnowledgeEntryStatus.Retired && against.Kind == writtenAgainstKind
                && against.Reference == reference
            select new { Entry = entry, against.Label }).ToListAsync(cancellationToken);
        var flagged = 0;
        foreach (var dependency in dependencies)
        {
            if (await db.EntryReviewReasons.AnyAsync(x => x.TenantId == tenant.TenantId
                && x.EntryId == dependency.Entry.EntryId && x.Kind == reasonKind
                && x.Reference == reference && x.TriggerId == triggerId, cancellationToken)) continue;
            var reasonId = TimeOrderedId.New(clock.UtcNow);
            db.EntryReviewReasons.Add(new KnowledgeEntryReviewReason
            {
                TenantId = tenant.TenantId, ReasonId = reasonId, EntryId = dependency.Entry.EntryId,
                Kind = reasonKind, Reference = reference, Label = dependency.Label, Detail = detail,
                TriggerId = triggerId, FlaggedAt = occurredAt,
            });
            dependency.Entry.NeedsReview = true;
            dependency.Entry.NeedsReviewSince = dependency.Entry.NeedsReviewSince is { } since && since < occurredAt
                ? since : occurredAt;
            await bus.PublishAsync(new KnowledgeEntryNeedsReview(dependency.Entry.EntryId, reasonId,
                reasonKind, dependency.Label, occurredAt),
                new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
            flagged++;
        }
        return flagged;
    }

    private static string Required(string value, int maximum, string requiredRule, string maximumRule,
        string label)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Rule(requiredRule, $"{label} is required.");
        var trimmed = value.Trim();
        if (trimmed.Length > maximum) throw Rule(maximumRule, $"{label} is at most {maximum} characters.");
        return trimmed;
    }

    private static QuestionRuleException Rule(string rule, string message) => new(rule, message);
}
