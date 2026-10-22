using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Knowledge.Entries;
using Yojana.Knowledge.Questions;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Knowledge.Training;

public static class TrainingPathCommandHandler
{
    public static async Task<TrainingPathChangeResult> Handle(SaveTrainingPath command, KnowledgeDbContext db,
        EntryReviewerSettings reviewerSettings, ITenantContext tenant, IClock clock, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        await EnsureReviewerAsync(command.Actor, reviewerSettings, cancellationToken);
        if (!YojanaRole.IsKnown(command.Role))
            throw Rule("training_path_role_invalid", "Choose a known tenant role for the training path.");
        var introduction = command.Introduction.Trim();
        if (introduction.Length is < 1 or > 2000)
            throw Rule("training_path_introduction_invalid", "The introduction must be from 1 to 2000 characters.");
        if (command.EntryIds.Count is < 1 or > 100 || command.EntryIds.Distinct().Count() != command.EntryIds.Count)
            throw Rule("training_path_entries_invalid", "Choose from 1 to 100 different published entries.");
        var entries = await db.Entries.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && command.EntryIds.Contains(x.EntryId) && x.Status == KnowledgeEntryStatus.Published
                && x.PublishedVersion != null)
            .Select(x => x.EntryId).ToListAsync(cancellationToken);
        if (entries.Count != command.EntryIds.Count)
            throw Rule("training_path_entry_not_published", "Every training-path item must be a published reviewed entry.");

        var path = await db.TrainingPaths.FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.Role == command.Role, cancellationToken);
        var now = clock.UtcNow;
        if (path is null)
        {
            if (command.ExpectedVersion != 0)
                throw Rule("training_path_version_conflict", "The training path changed; reload it before saving.");
            path = new TrainingPath
            {
                TenantId = tenant.TenantId, PathId = TimeOrderedId.New(now), Role = command.Role,
                Introduction = introduction, CurrentVersion = 1, CreatedBy = command.Actor.UserId,
                CreatedAt = now, LastChangedBy = command.Actor.UserId, LastChangedAt = now,
            };
            db.TrainingPaths.Add(path);
        }
        else
        {
            if (command.ExpectedVersion != path.CurrentVersion)
                throw Rule("training_path_version_conflict", "The training path changed; reload it before saving.");
            path.CurrentVersion++;
            path.Introduction = introduction;
            path.LastChangedBy = command.Actor.UserId;
            path.LastChangedAt = now;
        }
        db.TrainingPathVersions.Add(new TrainingPathVersion
        {
            TenantId = tenant.TenantId, PathId = path.PathId, Version = path.CurrentVersion, Role = path.Role,
            Introduction = path.Introduction, ChangedBy = command.Actor.UserId, ChangedAt = now,
        });
        db.TrainingPathEntries.AddRange(command.EntryIds.Select((entryId, index) => new TrainingPathEntry
        {
            TenantId = tenant.TenantId, PathId = path.PathId, Version = path.CurrentVersion,
            Position = index + 1, EntryId = entryId,
        }));
        await bus.PublishAsync(new TrainingPathChanged(path.PathId, path.Role, path.CurrentVersion,
            command.Actor.UserId, now), new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        return new TrainingPathChangeResult(path.PathId, path.Role, path.CurrentVersion);
    }

    public static async Task<TrainingReadResult> Handle(MarkTrainingEntryRead command, KnowledgeDbContext db,
        TrainingMemberDirectory directory, ITenantContext tenant, IClock clock, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var member = await directory.ActiveMemberAsync(command.Actor.UserId, cancellationToken)
            ?? throw Rule("training_member_not_found", "The signed-in user is not an active staff member.");
        if (!member.Roles.Contains(command.Role, StringComparer.Ordinal))
            throw Rule("training_path_role_required", "This training path is for a role the current user does not hold.");
        var path = await db.TrainingPaths.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.Role == command.Role, cancellationToken)
            ?? throw Rule("training_path_not_found", "The training path does not exist.");
        var entry = await (from item in db.TrainingPathEntries.AsNoTracking()
            join knowledge in db.Entries.AsNoTracking()
                on new { item.TenantId, item.EntryId } equals new { knowledge.TenantId, knowledge.EntryId }
            where item.TenantId == tenant.TenantId && item.PathId == path.PathId
                && item.Version == path.CurrentVersion && item.EntryId == command.EntryId
                && knowledge.Status == KnowledgeEntryStatus.Published && knowledge.PublishedVersion != null
            select new { knowledge.EntryId, Version = knowledge.PublishedVersion!.Value })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw Rule("training_path_entry_not_found", "The published entry is not in this training path.");
        var now = clock.UtcNow;
        var progress = await db.TrainingProgress.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.PathId == path.PathId && x.EntryId == entry.EntryId && x.UserId == command.Actor.UserId,
            cancellationToken);
        if (progress is null)
        {
            progress = new TrainingProgress
            {
                TenantId = tenant.TenantId, PathId = path.PathId, EntryId = entry.EntryId,
                UserId = command.Actor.UserId, ReadEntryVersion = entry.Version, ReadAt = now,
            };
            db.TrainingProgress.Add(progress);
        }
        else
        {
            progress.ReadEntryVersion = entry.Version;
            progress.ReadAt = now;
        }
        await bus.PublishAsync(new TrainingEntryRead(path.PathId, entry.EntryId, entry.Version,
            command.Actor.UserId, now), new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        return new TrainingReadResult(path.PathId, entry.EntryId, entry.Version, now);
    }

    public static async Task<TrainingPathChangeResult> Handle(DismissTrainingWelcome command,
        KnowledgeDbContext db, TrainingMemberDirectory directory, ITenantContext tenant, IClock clock,
        CancellationToken cancellationToken)
    {
        var member = await directory.ActiveMemberAsync(command.Actor.UserId, cancellationToken)
            ?? throw Rule("training_member_not_found", "The signed-in user is not an active staff member.");
        var path = await db.TrainingPaths.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.PathId == command.PathId, cancellationToken)
            ?? throw Rule("training_path_not_found", "The training path does not exist.");
        if (!member.Roles.Contains(path.Role, StringComparer.Ordinal))
            throw Rule("training_path_role_required", "This training path is for a role the current user does not hold.");
        if (!await db.TrainingWelcomeDismissals.AnyAsync(x => x.TenantId == tenant.TenantId
            && x.PathId == path.PathId && x.UserId == command.Actor.UserId, cancellationToken))
            db.TrainingWelcomeDismissals.Add(new TrainingWelcomeDismissal
            {
                TenantId = tenant.TenantId, PathId = path.PathId, UserId = command.Actor.UserId,
                DismissedAt = clock.UtcNow,
            });
        return new TrainingPathChangeResult(path.PathId, path.Role, path.CurrentVersion);
    }

    private static async Task EnsureReviewerAsync(QuestionActor actor, EntryReviewerSettings settings,
        CancellationToken cancellationToken)
    {
        var roles = await settings.GetAsync(cancellationToken);
        if (!roles.Any(role => actor.Roles.Contains(role, StringComparer.Ordinal)))
            throw Rule("training_path_reviewer_role_required",
                "A role selected in the tenant's knowledge-review setting is required.");
    }

    private static QuestionRuleException Rule(string rule, string message) => new(rule, message);
}

public sealed record TrainingPathChanged(Guid PathId, string Role, int Version, Guid ChangedBy, DateTime ChangedAt);
public sealed record TrainingEntryRead(Guid PathId, Guid EntryId, int EntryVersion, Guid UserId, DateTime ReadAt);
