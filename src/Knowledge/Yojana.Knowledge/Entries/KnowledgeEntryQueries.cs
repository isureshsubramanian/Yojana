using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Knowledge.Questions;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Knowledge.Entries;

public sealed record EntryTagView(string Kind, string Value, string Label);
public sealed record WrittenAgainstView(string Kind, string Reference, string Label);
public sealed record VisibleEntryRecordLink(string RecordKind, Guid RecordId, string RecordName, bool Source);
public sealed record EntryRecordLinksView(IReadOnlyList<VisibleEntryRecordLink> Visible, int HiddenCount);
public sealed record EntryReviewRoundView(int Round, int Version, Guid SubmittedBy, string SubmittedByName,
    DateTime SubmittedAt, string? Decision, Guid? Reviewer, string? ReviewerName, DateTime? ReviewedAt, string? Note);
public sealed record EntryVersionView(int Version, Guid SavedBy, string SavedByName, DateTime SavedAt);
public sealed record EntryAllowedActions(bool CanEdit, bool CanSubmit, bool CanApprove, bool CanSendBack, bool CanRetire);
public sealed record EntrySourceView(Guid QuestionId, string Title, string RecordKind, Guid RecordId,
    string? RecordName, string AcceptedAnswer);
public sealed record KnowledgeEntryView(Guid EntryId, string Title, string Problem, string Answer, string Steps,
    string Status, int CurrentVersion, Guid AuthorId, string AuthorName, DateTime CreatedAt, DateTime LastSavedAt,
    Guid? PublishedBy, string? PublishedByName, DateTime? PublishedAt, string? RetirementNote,
    IReadOnlyList<EntryTagView> Tags, IReadOnlyList<WrittenAgainstView> WrittenAgainst,
    EntryRecordLinksView RecordLinks, EntrySourceView Source, IReadOnlyList<EntryReviewRoundView> ReviewRounds,
    IReadOnlyList<EntryVersionView> Versions, IReadOnlyList<string> ReviewerRoles, string CaseDisclosureTier,
    EntryAllowedActions AllowedActions);

public sealed record KnowledgeEntryListItem(Guid EntryId, string Title, string Answer, int CurrentVersion,
    string AuthorName, string ReviewerName, DateTime PublishedAt, IReadOnlyList<EntryTagView> Tags, int Rank);
public sealed record KnowledgeFacet(string Value, string Label, int Count);
public sealed record KnowledgeEntryPage(IReadOnlyList<KnowledgeEntryListItem> Items, string? NextCursor,
    IReadOnlyList<KnowledgeFacet> Roles, IReadOnlyList<KnowledgeFacet> Tasks);
public sealed record EntryReviewListItem(Guid EntryId, string Title, int CurrentVersion, string AuthorName,
    DateTime SubmittedAt, int Round);
public sealed record EntryReviewPage(IReadOnlyList<EntryReviewListItem> Items, string? NextCursor);
public sealed record EntryFieldChange(string Field, string Before, string After);
public sealed record EntryTagChoices(IReadOnlyList<EntryTagView> Roles, IReadOnlyList<EntryTagView> Tasks,
    IReadOnlyList<EntryTagView> ItemFamilies, IReadOnlyList<EntryTagView> ClaimCauses);

public sealed class KnowledgeEntryQueries(KnowledgeDbContext db, RecordReferenceLookup records,
    IUserDirectory users, IQuestionTextSearch textSearch, EntryReviewerSettings reviewerSettings,
    IItemFamilyNamePort itemFamilies, IClaimCauseNamePort claimCauses, IDisclosurePolicyReadPort disclosure,
    ITenantContext tenant)
{
    public async Task<KnowledgeEntryView?> EntryAsync(Guid entryId, RecordReferenceViewer viewer,
        CancellationToken cancellationToken)
    {
        var entry = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.EntryId == entryId, cancellationToken);
        if (entry is null) return null;
        var reviewerRoles = await reviewerSettings.GetAsync(cancellationToken);
        var isReviewer = HasReviewerRole(viewer, reviewerRoles);
        if (entry.Status is not (KnowledgeEntryStatus.Published or KnowledgeEntryStatus.Retired)
            && entry.AuthorId != viewer.UserId && !isReviewer) return null;

        var tags = await db.EntryTags.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.EntryId == entryId && x.Version == entry.CurrentVersion)
            .OrderBy(x => x.Kind).ThenBy(x => x.Label).Select(x => new EntryTagView(x.Kind, x.Value, x.Label))
            .ToListAsync(cancellationToken);
        var linkRows = await db.EntryRecordLinks.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.EntryId == entryId && x.Version == entry.CurrentVersion).ToListAsync(cancellationToken);
        var question = await db.Questions.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId
            && x.QuestionId == entry.SourceQuestionId, cancellationToken);
        var firstVersion = await db.EntryVersions.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId
            && x.EntryId == entry.EntryId && x.Version == 1, cancellationToken);
        var links = await ResolveLinksAsync(linkRows, question, viewer, cancellationToken);
        var sourceRecord = links.Visible.FirstOrDefault(x => x.Source);
        var roundRows = await db.EntryReviewRounds.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.EntryId == entryId).OrderByDescending(x => x.Round).ToListAsync(cancellationToken);
        var versionRows = await db.EntryVersions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.EntryId == entryId).OrderByDescending(x => x.Version).ToListAsync(cancellationToken);
        var ids = roundRows.Select(x => x.SubmittedBy).Concat(roundRows.Select(x => x.Reviewer).OfType<Guid>())
            .Concat(versionRows.Select(x => x.SavedBy)).Append(entry.AuthorId)
            .Concat(entry.PublishedBy is { } published ? [published] : []).Distinct()
            .Select(x => x.ToString("D")).ToList();
        var names = await users.DisplayNamesAsync(ids, cancellationToken);
        string Name(Guid id) => names.GetValueOrDefault(id.ToString("D"), "Former staff member");
        var policy = await disclosure.ReadAsync(cancellationToken);
        var partyPolicy = policy.Fields.FirstOrDefault(x => x.Field == PortalDisclosureField.PartyData);
        var currentRound = roundRows.FirstOrDefault(x => x.Decision == null);
        var canReview = entry.Status == KnowledgeEntryStatus.InReview && isReviewer && entry.AuthorId != viewer.UserId;
        return new KnowledgeEntryView(entry.EntryId, entry.Title, entry.Problem, entry.Answer, entry.Steps,
            entry.Status, entry.CurrentVersion, entry.AuthorId, Name(entry.AuthorId), entry.CreatedAt, entry.LastSavedAt,
            entry.PublishedBy, entry.PublishedBy is { } publishedBy ? Name(publishedBy) : null, entry.PublishedAt,
            entry.RetirementNote, tags, WrittenAgainst(entry.WrittenAgainstJson), links,
            new EntrySourceView(question.QuestionId, question.Title, question.RecordKind, question.RecordId,
                sourceRecord?.RecordName, firstVersion.Answer),
            roundRows.Select(x => new EntryReviewRoundView(x.Round, x.Version, x.SubmittedBy, Name(x.SubmittedBy),
                x.SubmittedAt, x.Decision, x.Reviewer, x.Reviewer is { } reviewer ? Name(reviewer) : null,
                x.ReviewedAt, x.Note)).ToList(),
            versionRows.Select(x => new EntryVersionView(x.Version, x.SavedBy, Name(x.SavedBy), x.SavedAt)).ToList(),
            reviewerRoles, partyPolicy?.Tier ?? DisclosureTier.Internal,
            new EntryAllowedActions(entry.Status == KnowledgeEntryStatus.Draft && entry.AuthorId == viewer.UserId,
                entry.Status == KnowledgeEntryStatus.Draft && entry.AuthorId == viewer.UserId,
                canReview && currentRound is not null, canReview && currentRound is not null,
                entry.Status == KnowledgeEntryStatus.Published && isReviewer));
    }

    public async Task<KnowledgeEntryPage> BrowseAsync(string? query, string? role, string? task, string? cursor,
        int take, CancellationToken cancellationToken)
    {
        ValidateTake(take);
        var roles = await FacetsAsync(KnowledgeTagKind.Role, cancellationToken);
        var tasks = await FacetsAsync(KnowledgeTagKind.Task, cancellationToken);
        List<(KnowledgeEntry Entry, int Rank)> rows;
        string? next;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var before = string.IsNullOrWhiteSpace(cursor) ? null : DecodeSearch(cursor);
            rows = [];
            while (rows.Count < take + 1)
            {
                var matched = await textSearch.SearchEntriesAsync(query, 100, before, cancellationToken);
                if (matched.Count == 0) break;
                var ids = matched.Select(x => x.EntryId).ToList();
                var entries = await FilteredEntries(role, task).Where(x => ids.Contains(x.EntryId))
                    .ToListAsync(cancellationToken);
                var byId = entries.ToDictionary(x => x.EntryId);
                rows.AddRange(matched.Where(x => byId.ContainsKey(x.EntryId))
                    .Select(x => (byId[x.EntryId], x.Rank)));
                var scanned = matched[^1];
                before = new EntryTextCursor(scanned.Rank, scanned.EntryId);
                if (matched.Count < 100) break;
            }
            rows = rows.Take(take + 1).ToList();
            var last = rows.Take(take).LastOrDefault();
            next = rows.Count > take && last.Entry is not null ? EncodeSearch(last.Rank, last.Entry.EntryId) : null;
        }
        else
        {
            var filtered = FilteredEntries(role, task);
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                var (at, id) = Decode(cursor);
                filtered = filtered.Where(x => x.PublishedAt < at || (x.PublishedAt == at && x.EntryId.CompareTo(id) < 0));
            }
            var found = await filtered.OrderByDescending(x => x.PublishedAt).ThenByDescending(x => x.EntryId)
                .Take(take + 1).ToListAsync(cancellationToken);
            rows = found.Select(x => (x, 0)).ToList();
            var pageLast = found.Take(take).LastOrDefault();
            next = found.Count > take && pageLast is not null ? Encode(pageLast.PublishedAt!.Value, pageLast.EntryId) : null;
        }
        var page = rows.Take(take).ToList();
        var items = await ListItemsAsync(page, cancellationToken);
        return new KnowledgeEntryPage(items, next, roles, tasks);
    }

    public async Task<EntryReviewPage> ReviewQueueAsync(string? cursor, int take, RecordReferenceViewer viewer,
        CancellationToken cancellationToken)
    {
        ValidateTake(take);
        var roles = await reviewerSettings.GetAsync(cancellationToken);
        if (!HasReviewerRole(viewer, roles)) throw new QuestionRuleException("entry_review_role_required",
            "A role selected in the tenant's knowledge-review setting is required.");
        var query = from entry in db.Entries.AsNoTracking()
            join round in db.EntryReviewRounds.AsNoTracking()
                on new { entry.TenantId, entry.EntryId } equals new { round.TenantId, round.EntryId }
            where entry.TenantId == tenant.TenantId && entry.Status == KnowledgeEntryStatus.InReview
                && round.Decision == null && entry.AuthorId != viewer.UserId
            select new { Entry = entry, Round = round };
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var (at, id) = Decode(cursor);
            query = query.Where(x => x.Round.SubmittedAt > at
                || (x.Round.SubmittedAt == at && x.Entry.EntryId.CompareTo(id) > 0));
        }
        var rows = await query.OrderBy(x => x.Round.SubmittedAt).ThenBy(x => x.Entry.EntryId)
            .Take(take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(take).ToList();
        var names = await users.DisplayNamesAsync(page.Select(x => x.Entry.AuthorId.ToString("D")).Distinct().ToList(),
            cancellationToken);
        return new EntryReviewPage(page.Select(x => new EntryReviewListItem(x.Entry.EntryId, x.Entry.Title,
                x.Entry.CurrentVersion, names.GetValueOrDefault(x.Entry.AuthorId.ToString("D"), "Former staff member"),
                x.Round.SubmittedAt, x.Round.Round)).ToList(),
            rows.Count > take && page.Count > 0 ? Encode(page[^1].Round.SubmittedAt, page[^1].Entry.EntryId) : null);
    }

    public async Task<IReadOnlyList<EntryFieldChange>?> ChangesSinceLastRoundAsync(Guid entryId,
        RecordReferenceViewer viewer, CancellationToken cancellationToken)
    {
        var entry = await EntryAsync(entryId, viewer, cancellationToken);
        if (entry is null) return null;
        var rounds = await db.EntryReviewRounds.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.EntryId == entryId).OrderByDescending(x => x.Round).ToListAsync(cancellationToken);
        var current = rounds.FirstOrDefault();
        var previousVersion = rounds.Skip(1).FirstOrDefault()?.Version ?? 1;
        if (current is null) previousVersion = 1;
        var before = await db.EntryVersions.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId
            && x.EntryId == entryId && x.Version == previousVersion, cancellationToken);
        var now = await db.EntryVersions.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId
            && x.EntryId == entryId && x.Version == entry.CurrentVersion, cancellationToken);
        return Changes(before, now);
    }

    public async Task<EntryTagChoices> TagChoicesAsync(CancellationToken cancellationToken)
    {
        var roleChoices = YojanaRole.All.Select(x => new EntryTagView(KnowledgeTagKind.Role, x, Humanise(x))).ToList();
        var taskChoices = await db.EntryTags.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.Kind == KnowledgeTagKind.Task).Select(x => new { x.Value, x.Label }).Distinct()
            .OrderBy(x => x.Label).Select(x => new EntryTagView(KnowledgeTagKind.Task, x.Value, x.Label))
            .ToListAsync(cancellationToken);
        var families = (await itemFamilies.ListAsync(cancellationToken)).Select(x =>
            new EntryTagView(KnowledgeTagKind.ItemFamily, x.Code, x.Name)).ToList();
        var causes = (await claimCauses.ListAsync(cancellationToken)).Where(x => x.Active).Select(x =>
            new EntryTagView(KnowledgeTagKind.ClaimCause, x.Code, x.Label)).ToList();
        return new EntryTagChoices(roleChoices, taskChoices, families, causes);
    }

    public async Task<IReadOnlyList<KnowledgeEntryListItem>> SearchPublishedAsync(string query, int take,
        CancellationToken cancellationToken)
    {
        var matches = await textSearch.SearchEntriesAsync(query, take, null, cancellationToken);
        var ids = matches.Select(x => x.EntryId).ToList();
        var entries = await db.Entries.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.Status == KnowledgeEntryStatus.Published && ids.Contains(x.EntryId)).ToListAsync(cancellationToken);
        var byId = entries.ToDictionary(x => x.EntryId);
        return await ListItemsAsync(matches.Where(x => byId.ContainsKey(x.EntryId))
            .Select(x => (byId[x.EntryId], x.Rank)).ToList(), cancellationToken);
    }

    private IQueryable<KnowledgeEntry> FilteredEntries(string? role, string? task)
    {
        var query = db.Entries.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.Status == KnowledgeEntryStatus.Published);
        if (!string.IsNullOrWhiteSpace(role)) query = query.Where(entry => db.EntryTags.Any(tag =>
            tag.TenantId == tenant.TenantId && tag.EntryId == entry.EntryId && tag.Version == entry.CurrentVersion
            && tag.Kind == KnowledgeTagKind.Role && tag.Value == role));
        if (!string.IsNullOrWhiteSpace(task)) query = query.Where(entry => db.EntryTags.Any(tag =>
            tag.TenantId == tenant.TenantId && tag.EntryId == entry.EntryId && tag.Version == entry.CurrentVersion
            && tag.Kind == KnowledgeTagKind.Task && tag.Value == task));
        return query;
    }

    private async Task<IReadOnlyList<KnowledgeFacet>> FacetsAsync(string kind, CancellationToken cancellationToken)
    {
        var rows = await (from tag in db.EntryTags.AsNoTracking()
            join entry in db.Entries.AsNoTracking()
                on new { tag.TenantId, tag.EntryId, tag.Version }
                equals new { entry.TenantId, entry.EntryId, Version = entry.CurrentVersion }
            where tag.TenantId == tenant.TenantId && entry.Status == KnowledgeEntryStatus.Published && tag.Kind == kind
            group tag by new { tag.Value, tag.Label } into grouped
            select new { grouped.Key.Value, grouped.Key.Label, Count = grouped.Count() })
            .ToListAsync(cancellationToken);
        return rows.Select(x => new KnowledgeFacet(x.Value, x.Label, x.Count))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Label).ToList();
    }

    private async Task<IReadOnlyList<KnowledgeEntryListItem>> ListItemsAsync(
        IReadOnlyList<(KnowledgeEntry Entry, int Rank)> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(x => x.Entry.EntryId).ToList();
        var tags = await db.EntryTags.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && ids.Contains(x.EntryId)).ToListAsync(cancellationToken);
        var actorIds = rows.Select(x => x.Entry.AuthorId).Concat(rows.Select(x => x.Entry.PublishedBy).OfType<Guid>())
            .Distinct().Select(x => x.ToString("D")).ToList();
        var names = await users.DisplayNamesAsync(actorIds, cancellationToken);
        return rows.Select(x => new KnowledgeEntryListItem(x.Entry.EntryId, x.Entry.Title, x.Entry.Answer,
            x.Entry.CurrentVersion, names.GetValueOrDefault(x.Entry.AuthorId.ToString("D"), "Former staff member"),
            x.Entry.PublishedBy is { } reviewer
                ? names.GetValueOrDefault(reviewer.ToString("D"), "Former staff member") : "",
            x.Entry.PublishedAt!.Value, tags.Where(t => t.EntryId == x.Entry.EntryId
                && t.Version == x.Entry.CurrentVersion).OrderBy(t => t.Kind).ThenBy(t => t.Label)
                .Select(t => new EntryTagView(t.Kind, t.Value, t.Label)).ToList(), x.Rank)).ToList();
    }

    private async Task<EntryRecordLinksView> ResolveLinksAsync(IReadOnlyList<KnowledgeEntryRecordLink> links,
        Question source, RecordReferenceViewer viewer, CancellationToken cancellationToken)
    {
        var visible = new List<VisibleEntryRecordLink>();
        var hidden = 0;
        foreach (var group in links.GroupBy(x => x.RecordKind))
        {
            var found = await records.ManyAsync(group.Key, group.Select(x => x.RecordId).ToList(), viewer,
                cancellationToken);
            foreach (var link in group)
            {
                if (found.TryGetValue(link.RecordId, out var record) && record.Exists && record.MayView
                    && !string.IsNullOrWhiteSpace(record.DisplayName))
                    visible.Add(new VisibleEntryRecordLink(link.RecordKind, link.RecordId, record.DisplayName,
                        link.RecordKind == source.RecordKind && link.RecordId == source.RecordId));
                else hidden++;
            }
        }
        return new EntryRecordLinksView(visible, hidden);
    }

    private static List<WrittenAgainstView> WrittenAgainst(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<WrittenAgainstInput>>(json) ?? [])
                .Select(x => new WrittenAgainstView(x.Kind, x.Reference, x.Label)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<EntryFieldChange> Changes(KnowledgeEntryVersion before, KnowledgeEntryVersion after)
    {
        var changes = new List<EntryFieldChange>();
        void Add(string field, string oldValue, string newValue)
        {
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal)) changes.Add(new(field, oldValue, newValue));
        }
        Add("Title", before.Title, after.Title); Add("Problem", before.Problem, after.Problem);
        Add("Answer", before.Answer, after.Answer); Add("Steps", before.Steps, after.Steps);
        Add("Written against", before.WrittenAgainstJson, after.WrittenAgainstJson);
        return changes;
    }

    private static bool HasReviewerRole(RecordReferenceViewer viewer, IReadOnlyList<string> roles) =>
        roles.Any(role => viewer.Roles.Contains(role, StringComparer.Ordinal));
    private static string Humanise(string value) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' '));
    private static void ValidateTake(int take)
    {
        if (take is < 1 or > 100) throw new QuestionRuleException("page_size_invalid", "Take must be between 1 and 100.");
    }
    private static string Encode(DateTime at, Guid id) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{at.Ticks}:{id:D}"));
    private static string EncodeSearch(int rank, Guid id) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{rank}:{id:D}"));
    private static (DateTime At, Guid Id) Decode(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
            return (new DateTime(long.Parse(parts[0], CultureInfo.InvariantCulture), DateTimeKind.Utc), Guid.Parse(parts[1]));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            throw new QuestionRuleException("cursor_invalid", "The knowledge-entry cursor is invalid.");
        }
    }
    private static EntryTextCursor DecodeSearch(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
            return new EntryTextCursor(int.Parse(parts[0], CultureInfo.InvariantCulture), Guid.Parse(parts[1]));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            throw new QuestionRuleException("cursor_invalid", "The knowledge search cursor is invalid.");
        }
    }
}
