using Microsoft.EntityFrameworkCore;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Entries;
using Yojana.Knowledge.Questions;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Knowledge.Training;

public sealed record TrainingPathSummary(Guid PathId, string Role, string RoleLabel, string Introduction,
    int Version, int Entries, int ReadEntries, bool Finished, bool NewMember, bool CanMaintain);
public sealed record TrainingPathListView(IReadOnlyList<TrainingPathSummary> Items, bool CanMaintain);

public sealed record TrainingPathEntryView(int Position, Guid EntryId, string Title, string? TaskLabel,
    int PublishedVersion, bool Read, DateTime? ReadAt, bool NeedsReview, string? Warning);

public sealed record TrainingPathView(Guid PathId, string Role, string RoleLabel, string Introduction, int Version,
    IReadOnlyList<TrainingPathEntryView> Entries, int ReadEntries, bool Finished, bool NewMember,
    bool WelcomeDismissed, bool CanMaintain, bool CanViewTeam);

public sealed record TrainingWelcomeView(Guid PathId, string Role, string RoleLabel, string Introduction,
    int Entries, int ReadEntries);

public sealed record TrainingTeamMemberView(string Name, DateTime JoinedAt, bool NewMember,
    int ReadEntries, int TotalEntries, DateTime? LastReadAt, bool Finished);

public sealed record TrainingTeamView(string Role, string RoleLabel, IReadOnlyList<TrainingTeamMemberView> People,
    string? NextCursor, int QuestionsAskedInFirstThirtyDays);

public sealed class TrainingPathQueries(KnowledgeDbContext db, EntryReviewerSettings reviewerSettings,
    TrainingSettings settings, ITenantTrainingDirectory directory, ITenantContext tenant, IClock clock)
{
    public async Task<TrainingPathListView> ListAsync(RecordReferenceViewer viewer,
        CancellationToken cancellationToken)
    {
        var member = await directory.ActiveMemberAsync(viewer.UserId, cancellationToken)
            ?? throw Rule("training_member_not_found", "The signed-in user is not an active staff member.");
        var canMaintain = await CanMaintainAsync(viewer.Roles, cancellationToken);
        var query = db.TrainingPaths.AsNoTracking().Where(x => x.TenantId == tenant.TenantId);
        if (!canMaintain) query = query.Where(x => member.Roles.Contains(x.Role));
        var paths = await query.OrderBy(x => x.Role).Select(x => new
        {
            x.PathId, x.Role, x.Introduction, x.CurrentVersion,
        }).ToListAsync(cancellationToken);
        var result = new List<TrainingPathSummary>();
        foreach (var path in paths)
        {
            var detail = await DetailCoreAsync(path.PathId, path.Role, path.Introduction, path.CurrentVersion,
                member, viewer, canMaintain, cancellationToken);
            result.Add(new TrainingPathSummary(detail.PathId, detail.Role, detail.RoleLabel, detail.Introduction,
                detail.Version, detail.Entries.Count, detail.ReadEntries, detail.Finished, detail.NewMember,
                detail.CanMaintain));
        }
        return new TrainingPathListView(result, canMaintain);
    }

    public async Task<TrainingPathView?> DetailAsync(string role, RecordReferenceViewer viewer,
        CancellationToken cancellationToken)
    {
        var member = await directory.ActiveMemberAsync(viewer.UserId, cancellationToken)
            ?? throw Rule("training_member_not_found", "The signed-in user is not an active staff member.");
        var canMaintain = await CanMaintainAsync(viewer.Roles, cancellationToken);
        if (!member.Roles.Contains(role, StringComparer.Ordinal) && !canMaintain)
            throw Rule("training_path_role_required", "This training path is for a role the current user does not hold.");
        var path = await db.TrainingPaths.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.Role == role)
            .Select(x => new { x.PathId, x.Role, x.Introduction, x.CurrentVersion })
            .SingleOrDefaultAsync(cancellationToken);
        return path is null ? null : await DetailCoreAsync(path.PathId, path.Role, path.Introduction,
            path.CurrentVersion, member, viewer, canMaintain, cancellationToken);
    }

    public async Task<TrainingWelcomeView?> WelcomeAsync(RecordReferenceViewer viewer,
        CancellationToken cancellationToken)
    {
        var member = await directory.ActiveMemberAsync(viewer.UserId, cancellationToken);
        if (member is null || !await IsNewAsync(member.MembershipCreatedAt, cancellationToken)) return null;
        var paths = await db.TrainingPaths.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && member.Roles.Contains(x.Role))
            .OrderBy(x => x.Role).Select(x => new { x.PathId, x.Role, x.Introduction, x.CurrentVersion })
            .ToListAsync(cancellationToken);
        foreach (var path in paths)
        {
            if (await db.TrainingWelcomeDismissals.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId
                && x.PathId == path.PathId && x.UserId == viewer.UserId, cancellationToken)) continue;
            var detail = await DetailCoreAsync(path.PathId, path.Role, path.Introduction, path.CurrentVersion,
                member, viewer, false, cancellationToken);
            if (!detail.Finished)
                return new TrainingWelcomeView(path.PathId, path.Role, detail.RoleLabel, path.Introduction,
                    detail.Entries.Count, detail.ReadEntries);
        }
        return null;
    }

    public async Task<TrainingTeamView> TeamAsync(string role, string? cursor, int take,
        RecordReferenceViewer viewer, CancellationToken cancellationToken)
    {
        if (!await CanViewTeamAsync(viewer.Roles, cancellationToken))
            throw Rule("training_team_view_forbidden", "The tenant's training-team-view role is required.");
        var path = await db.TrainingPaths.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.Role == role)
            .Select(x => new { x.PathId, x.CurrentVersion }).SingleOrDefaultAsync(cancellationToken)
            ?? throw Rule("training_path_not_found", "The training path does not exist.");
        var activeEntries = await ActiveEntryVersionsAsync(path.PathId, path.CurrentVersion, cancellationToken);
        var page = await directory.ActiveMembersInRoleAsync(role, cursor, take, cancellationToken);
        var ids = page.Items.Select(x => x.UserId).ToList();
        var progress = ids.Count == 0 ? [] : await db.TrainingProgress.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.PathId == path.PathId && ids.Contains(x.UserId))
            .Select(x => new { x.UserId, x.EntryId, x.ReadEntryVersion, x.ReadAt }).ToListAsync(cancellationToken);
        var days = await settings.NewMemberDaysAsync(cancellationToken);
        var people = page.Items.Select(person =>
        {
            var reads = progress.Where(x => x.UserId == person.UserId
                && activeEntries.TryGetValue(x.EntryId, out var version) && x.ReadEntryVersion >= version).ToList();
            return new TrainingTeamMemberView(person.Name, person.MembershipCreatedAt,
                person.MembershipCreatedAt.AddDays(days) > clock.UtcNow && reads.Count < activeEntries.Count,
                reads.Count, activeEntries.Count, reads.Count == 0 ? null : reads.Max(x => x.ReadAt),
                activeEntries.Count > 0 && reads.Count == activeEntries.Count);
        }).ToList();
        return new TrainingTeamView(role, RoleLabel(role), people, page.NextCursor,
            await TeamQuestionTotalAsync(role, cancellationToken));
    }

    private async Task<TrainingPathView> DetailCoreAsync(Guid pathId, string role, string introduction, int version,
        TenantTrainingMember member, RecordReferenceViewer viewer, bool canMaintain,
        CancellationToken cancellationToken)
    {
        var rows = await (from item in db.TrainingPathEntries.AsNoTracking()
            join entry in db.Entries.AsNoTracking()
                on new { item.TenantId, item.EntryId } equals new { entry.TenantId, entry.EntryId }
            where item.TenantId == tenant.TenantId && item.PathId == pathId && item.Version == version
                && entry.Status == KnowledgeEntryStatus.Published && entry.PublishedVersion != null
            orderby item.Position
            select new { item.Position, item.EntryId, entry.Title, PublishedVersion = entry.PublishedVersion!.Value,
                entry.NeedsReview }).ToListAsync(cancellationToken);
        var ids = rows.Select(x => x.EntryId).ToList();
        var taskLabels = ids.Count == 0 ? [] : await db.EntryTags.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && ids.Contains(x.EntryId) && x.Kind == KnowledgeTagKind.Task)
            .GroupBy(x => x.EntryId).Select(x => new { EntryId = x.Key, Label = x.Min(y => y.Label) })
            .ToListAsync(cancellationToken);
        var tasks = taskLabels.ToDictionary(x => x.EntryId, x => x.Label);
        var reads = ids.Count == 0 ? [] : await db.TrainingProgress.AsNoTracking().Where(x =>
                x.TenantId == tenant.TenantId && x.PathId == pathId && x.UserId == viewer.UserId
                && ids.Contains(x.EntryId))
            .Select(x => new { x.EntryId, x.ReadEntryVersion, x.ReadAt }).ToListAsync(cancellationToken);
        var readByEntry = reads.ToDictionary(x => x.EntryId);
        var items = rows.Select(row =>
        {
            readByEntry.TryGetValue(row.EntryId, out var read);
            var currentRead = read is not null && read.ReadEntryVersion >= row.PublishedVersion;
            return new TrainingPathEntryView(row.Position, row.EntryId, row.Title,
                tasks.GetValueOrDefault(row.EntryId), row.PublishedVersion, currentRead,
                currentRead ? read!.ReadAt : null, row.NeedsReview,
                row.NeedsReview ? "This entry may be out of date." : null);
        }).ToList();
        var readCount = items.Count(x => x.Read);
        var finished = items.Count > 0 && readCount == items.Count;
        var dismissed = await db.TrainingWelcomeDismissals.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId
            && x.PathId == pathId && x.UserId == viewer.UserId, cancellationToken);
        return new TrainingPathView(pathId, role, RoleLabel(role), introduction, version, items, readCount,
            finished, await IsNewAsync(member.MembershipCreatedAt, cancellationToken) && !finished, dismissed,
            canMaintain, await CanViewTeamAsync(viewer.Roles, cancellationToken));
    }

    private async Task<Dictionary<Guid, int>> ActiveEntryVersionsAsync(Guid pathId, int version,
        CancellationToken cancellationToken) => await (from item in db.TrainingPathEntries.AsNoTracking()
        join entry in db.Entries.AsNoTracking()
            on new { item.TenantId, item.EntryId } equals new { entry.TenantId, entry.EntryId }
        where item.TenantId == tenant.TenantId && item.PathId == pathId && item.Version == version
            && entry.Status == KnowledgeEntryStatus.Published && entry.PublishedVersion != null
        select new { item.EntryId, Version = entry.PublishedVersion!.Value })
        .ToDictionaryAsync(x => x.EntryId, x => x.Version, cancellationToken);

    private async Task<int> TeamQuestionTotalAsync(string role, CancellationToken cancellationToken)
    {
        var members = await directory.AllActiveMembersInRoleAsync(role, cancellationToken);
        var dates = members.ToDictionary(x => x.UserId, x => x.MembershipCreatedAt);
        var total = 0;
        foreach (var batch in dates.Keys.Chunk(500))
        {
            var ids = batch.ToList();
            var questions = await db.Questions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                    && ids.Contains(x.AskedBy))
                .Select(x => new { x.AskedBy, x.AskedAt }).ToListAsync(cancellationToken);
            total += questions.Count(x => x.AskedAt >= dates[x.AskedBy]
                && x.AskedAt < dates[x.AskedBy].AddDays(30));
        }
        return total;
    }

    private async Task<bool> IsNewAsync(DateTime joinedAt, CancellationToken cancellationToken) =>
        joinedAt.AddDays(await settings.NewMemberDaysAsync(cancellationToken)) > clock.UtcNow;

    private async Task<bool> CanMaintainAsync(IReadOnlyList<string> roles, CancellationToken cancellationToken)
    {
        var reviewers = await reviewerSettings.GetAsync(cancellationToken);
        return reviewers.Any(role => roles.Contains(role, StringComparer.Ordinal));
    }

    private async Task<bool> CanViewTeamAsync(IReadOnlyList<string> roles, CancellationToken cancellationToken)
    {
        var allowed = await settings.TeamViewRolesAsync(cancellationToken);
        return allowed.Any(role => roles.Contains(role, StringComparer.Ordinal));
    }

    private static string RoleLabel(string value)
    {
        var words = value.Replace('_', ' ');
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static QuestionRuleException Rule(string rule, string message) => new(rule, message);
}
