using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Knowledge.Questions;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Paging;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using CommandRefusedException = Yojana.Platform.Contracts.CommandRefusedException;

namespace Yojana.Knowledge.Entries;

public sealed record EntryTagView(string Kind, string Value, string Label);
public sealed record WrittenAgainstView(string Kind, string Reference, string Label, bool Resolved);
public sealed record VisibleEntryRecordLink(string RecordKind, Guid RecordId, string RecordName, bool Source);
public sealed record EntryRecordLinksView(IReadOnlyList<VisibleEntryRecordLink> Visible, int HiddenCount);
public sealed record EntryReviewRoundView(int Round, int Version, Guid SubmittedBy, string SubmittedByName,
    DateTime SubmittedAt, string? Decision, Guid? Reviewer, string? ReviewerName, DateTime? ReviewedAt, string? Note);
public sealed record EntryVersionView(int Version, Guid SavedBy, string SavedByName, DateTime SavedAt);
public sealed record EntryReviewReasonView(Guid ReasonId, string Kind, string Label, string Detail, DateTime FlaggedAt);
public sealed record EntryAllowedActions(bool CanEdit, bool CanSubmit, bool CanApprove, bool CanSendBack,
    bool CanRetire, bool CanConfirmStillCorrect);
public sealed record EntrySourceView(Guid QuestionId, string Title, string RecordKind, Guid RecordId,
    string? RecordName, string AcceptedAnswer);
public sealed record KnowledgeEntryView(Guid EntryId, string Title, string Problem, string Answer, string Steps,
    string Status, int CurrentVersion, Guid AuthorId, string AuthorName, DateTime CreatedAt, DateTime LastSavedAt,
    Guid? PublishedBy, string? PublishedByName, DateTime? PublishedAt, string? RetirementNote,
    IReadOnlyList<EntryTagView> Tags, IReadOnlyList<WrittenAgainstView> WrittenAgainst,
    EntryRecordLinksView RecordLinks, EntrySourceView Source, IReadOnlyList<EntryReviewRoundView> ReviewRounds,
    IReadOnlyList<EntryVersionView> Versions, IReadOnlyList<string> ReviewerRoles, string CaseDisclosureTier,
    bool NeedsReview, IReadOnlyList<EntryReviewReasonView> ReviewReasons, EntryAllowedActions AllowedActions);

public sealed record KnowledgeEntryListItem(Guid EntryId, string Title, string Answer, int CurrentVersion,
    string AuthorName, string ReviewerName, DateTime PublishedAt, IReadOnlyList<EntryTagView> Tags, int Rank,
    bool NeedsReview, IReadOnlyList<EntryReviewReasonView> ReviewReasons);
public sealed record KnowledgeFacet(string Value, string Label, int Count);
public sealed record KnowledgeEntryPage(IReadOnlyList<KnowledgeEntryListItem> Items, string? NextCursor,
    IReadOnlyList<KnowledgeFacet> Roles, IReadOnlyList<KnowledgeFacet> Tasks);
public sealed record EntryReviewListItem(Guid EntryId, string Title, int CurrentVersion, string AuthorName,
    DateTime ActivityAt, int? Round, string Status, bool NeedsReview,
    IReadOnlyList<EntryReviewReasonView> ReviewReasons, bool CanConfirmStillCorrect);
public sealed record EntryReviewPage(IReadOnlyList<EntryReviewListItem> Items, string? NextCursor);
public sealed record EntryFieldChange(string Field, string Before, string After);
public sealed record EntryTagChoices(IReadOnlyList<EntryTagView> Roles, IReadOnlyList<EntryTagView> Tasks,
    IReadOnlyList<EntryTagView> ItemFamilies, IReadOnlyList<EntryTagView> ClaimCauses);

public sealed class EntryReviewProjection
{
    public Guid EntryId { get; init; }
    public DateTime ActivityAt { get; init; }
    public required KnowledgeEntry Entry { get; init; }
    public int? Round { get; init; }
}

public sealed class KnowledgeEntryQueries(KnowledgeDbContext db, RecordReferenceLookup records,
    IUserDirectory users, IQuestionTextSearch textSearch, EntryReviewerSettings reviewerSettings,
    IItemFamilyNamePort itemFamilies, IClaimCauseNamePort claimCauses, IDisclosurePolicyReadPort disclosure,
    IPolicyVersionPickerPort policyVersions, ScreenReferenceCatalogue screenCatalogue, ITenantContext tenant)
{
    public async Task<KnowledgeEntryView?> EntryAsync(Guid entryId, RecordReferenceViewer viewer,
        CancellationToken cancellationToken)
    {
        var entry = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.EntryId == entryId, cancellationToken);
        if (entry is null) return null;
        var reviewerRoles = await reviewerSettings.GetAsync(cancellationToken);
        var isReviewer = HasReviewerRole(viewer, reviewerRoles);
        var hasPublishedVersion = entry.PublishedVersion is not null;
        if (!hasPublishedVersion && entry.Status is not KnowledgeEntryStatus.Retired
            && entry.AuthorId != viewer.UserId && !isReviewer) return null;
        var selectedVersion = entry.Status is KnowledgeEntryStatus.Draft or KnowledgeEntryStatus.InReview
            && entry.AuthorId != viewer.UserId && !isReviewer
            ? entry.PublishedVersion!.Value : entry.CurrentVersion;
        var selected = selectedVersion == entry.CurrentVersion ? null
            : await db.EntryVersions.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId
                && x.EntryId == entryId && x.Version == selectedVersion, cancellationToken);

        var tags = await db.EntryTags.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.EntryId == entryId && x.Version == selectedVersion)
            .OrderBy(x => x.Kind).ThenBy(x => x.Label).Select(x => new EntryTagView(x.Kind, x.Value, x.Label))
            .ToListAsync(cancellationToken);
        var linkRows = await db.EntryRecordLinks.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.EntryId == entryId && x.Version == selectedVersion).ToListAsync(cancellationToken);
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
        var reasons = await ActiveReasonsAsync([entry.EntryId], cancellationToken);
        var content = selected ?? new KnowledgeEntryVersion
        {
            TenantId = entry.TenantId, EntryId = entry.EntryId, Version = entry.CurrentVersion,
            Title = entry.Title, Problem = entry.Problem, Answer = entry.Answer, Steps = entry.Steps,
            WrittenAgainstJson = entry.WrittenAgainstJson, SavedBy = entry.LastSavedBy, SavedAt = entry.LastSavedAt,
        };
        var authorCanWork = entry.AuthorId == viewer.UserId && (entry.Status == KnowledgeEntryStatus.Draft
            || (entry.Status == KnowledgeEntryStatus.Published && entry.NeedsReview));
        return new KnowledgeEntryView(entry.EntryId, content.Title, content.Problem, content.Answer, content.Steps,
            entry.Status, entry.CurrentVersion, entry.AuthorId, Name(entry.AuthorId), entry.CreatedAt, entry.LastSavedAt,
            entry.PublishedBy, entry.PublishedBy is { } publishedBy ? Name(publishedBy) : null, entry.PublishedAt,
            entry.RetirementNote, tags, await WrittenAgainstAsync(content.WrittenAgainstJson, cancellationToken), links,
            new EntrySourceView(question.QuestionId, question.Title, question.RecordKind, question.RecordId,
                sourceRecord?.RecordName, firstVersion.Answer),
            roundRows.Select(x => new EntryReviewRoundView(x.Round, x.Version, x.SubmittedBy, Name(x.SubmittedBy),
                x.SubmittedAt, x.Decision, x.Reviewer, x.Reviewer is { } reviewer ? Name(reviewer) : null,
                x.ReviewedAt, x.Note)).ToList(),
            versionRows.Select(x => new EntryVersionView(x.Version, x.SavedBy, Name(x.SavedBy), x.SavedAt)).ToList(),
            reviewerRoles, partyPolicy?.Tier ?? DisclosureTier.Internal, entry.NeedsReview,
            reasons.GetValueOrDefault(entry.EntryId, []),
            new EntryAllowedActions(authorCanWork, authorCanWork,
                canReview && currentRound is not null, canReview && currentRound is not null,
                entry.Status == KnowledgeEntryStatus.Published && isReviewer,
                entry.Status == KnowledgeEntryStatus.Published && entry.NeedsReview && isReviewer
                    && entry.AuthorId != viewer.UserId));
    }

    public Task<KnowledgeEntryPage> BrowseAsync(string? query, string? role, string? task, string? cursor,
        int? requestedTake, CancellationToken cancellationToken) =>
        BrowseAsync(query, role, task, null, null, cursor, requestedTake, cancellationToken);

    public async Task<KnowledgeEntryPage> BrowseAsync(string? query, string? role, string? task, string? sort,
        string? direction, string? cursor, int? requestedTake, CancellationToken cancellationToken)
    {
        var take = requestedTake ?? 25;
        ValidateTake(take);
        var roles = await FacetsAsync(KnowledgeTagKind.Role, cancellationToken);
        var tasks = await FacetsAsync(KnowledgeTagKind.Task, cancellationToken);
        List<(KnowledgeEntry Entry, int Rank)> rows;
        string? next;
        if (!string.IsNullOrWhiteSpace(query))
        {
            if (!string.IsNullOrWhiteSpace(sort) && sort != "relevance")
                throw new CommandRefusedException("sort_not_supported", $"Sort '{sort}' is not supported while searching.");
            if (!string.IsNullOrWhiteSpace(direction) && direction != "desc")
                throw new CommandRefusedException("sort_not_supported", $"Sort direction '{direction}' is not supported while searching.");
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
            var paging = KnowledgeEndpoints.EntryPages.Parse(sort, direction, requestedTake, cursor,
                defaultTake: 25);
            var found = await paging.Apply(filtered).Take(paging.Take + 1).ToListAsync(cancellationToken);
            rows = found.Select(x => (x, 0)).ToList();
            var pageLast = found.Take(take).LastOrDefault();
            next = found.Count > take && pageLast is not null ? paging.CursorFor(pageLast) : null;
        }
        var page = rows.Take(take).ToList();
        var items = await ListItemsAsync(page, cancellationToken);
        return new KnowledgeEntryPage(items, next, roles, tasks);
    }

    public Task<EntryReviewPage> ReviewQueueAsync(string? filter, string? cursor, int? requestedTake,
        RecordReferenceViewer viewer, CancellationToken cancellationToken) =>
        ReviewQueueAsync(filter, null, null, cursor, requestedTake, viewer, cancellationToken);

    public async Task<EntryReviewPage> ReviewQueueAsync(string? filter, string? sort, string? direction,
        string? cursor, int? requestedTake,
        RecordReferenceViewer viewer,
        CancellationToken cancellationToken)
    {
        var take = requestedTake ?? 25;
        ValidateTake(take);
        var paging = KnowledgeEndpoints.ReviewPages.Parse(sort, direction, requestedTake, cursor,
            defaultTake: 25);
        var roles = await reviewerSettings.GetAsync(cancellationToken);
        if (!HasReviewerRole(viewer, roles)) throw new QuestionRuleException("entry_review_role_required",
            "A role selected in the tenant's knowledge-review setting is required.");
        if (filter is not (null or "pending" or "needs_review"))
            throw new QuestionRuleException("entry_review_filter_unknown", "The review filter is unknown.");
        if (filter == "needs_review")
            return await NeedsReviewQueueAsync(paging, viewer, cancellationToken);
        var query = from entry in db.Entries.AsNoTracking()
            join round in db.EntryReviewRounds.AsNoTracking()
                on new { entry.TenantId, entry.EntryId } equals new { round.TenantId, round.EntryId }
            where entry.TenantId == tenant.TenantId && entry.Status == KnowledgeEntryStatus.InReview
                && round.Decision == null && entry.AuthorId != viewer.UserId
            select new EntryReviewProjection
            {
                EntryId = entry.EntryId, ActivityAt = round.SubmittedAt, Entry = entry, Round = round.Round,
            };
        var rows = await paging.Apply(query).Take(paging.Take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(take).ToList();
        var names = await users.DisplayNamesAsync(page.Select(x => x.Entry.AuthorId.ToString("D")).Distinct().ToList(),
            cancellationToken);
        var reasons = await ActiveReasonsAsync(page.Select(x => x.Entry.EntryId).ToList(), cancellationToken);
        return new EntryReviewPage(page.Select(x => new EntryReviewListItem(x.Entry.EntryId, x.Entry.Title,
                x.Entry.CurrentVersion, names.GetValueOrDefault(x.Entry.AuthorId.ToString("D"), "Former staff member"),
                x.ActivityAt, x.Round, x.Entry.Status, x.Entry.NeedsReview,
                reasons.GetValueOrDefault(x.Entry.EntryId, []), false)).ToList(),
            rows.Count > take && page.Count > 0 ? paging.CursorFor(page[^1]) : null);
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
            && x.PublishedVersion != null && x.Status != KnowledgeEntryStatus.Retired
            && ids.Contains(x.EntryId)).ToListAsync(cancellationToken);
        var byId = entries.ToDictionary(x => x.EntryId);
        return await ListItemsAsync(matches.Where(x => byId.ContainsKey(x.EntryId))
            .Select(x => (byId[x.EntryId], x.Rank)).ToList(), cancellationToken);
    }

    private IQueryable<KnowledgeEntry> FilteredEntries(string? role, string? task)
    {
        var query = db.Entries.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.PublishedVersion != null && x.Status != KnowledgeEntryStatus.Retired);
        if (!string.IsNullOrWhiteSpace(role)) query = query.Where(entry => db.EntryTags.Any(tag =>
            tag.TenantId == tenant.TenantId && tag.EntryId == entry.EntryId
            && tag.Kind == KnowledgeTagKind.Role && tag.Value == role && tag.Version == entry.PublishedVersion));
        if (!string.IsNullOrWhiteSpace(task)) query = query.Where(entry => db.EntryTags.Any(tag =>
            tag.TenantId == tenant.TenantId && tag.EntryId == entry.EntryId
            && tag.Kind == KnowledgeTagKind.Task && tag.Value == task && tag.Version == entry.PublishedVersion));
        return query;
    }

    private async Task<IReadOnlyList<KnowledgeFacet>> FacetsAsync(string kind, CancellationToken cancellationToken)
    {
        var rows = await (from tag in db.EntryTags.AsNoTracking()
            join entry in db.Entries.AsNoTracking()
                on new { tag.TenantId, tag.EntryId, tag.Version }
                equals new { entry.TenantId, entry.EntryId, Version = entry.PublishedVersion!.Value }
            where tag.TenantId == tenant.TenantId && entry.PublishedVersion != null
                && entry.Status != KnowledgeEntryStatus.Retired && tag.Kind == kind
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
        var versions = await db.EntryVersions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && ids.Contains(x.EntryId)).ToListAsync(cancellationToken);
        var tags = await db.EntryTags.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && ids.Contains(x.EntryId)).ToListAsync(cancellationToken);
        var reasons = await ActiveReasonsAsync(ids, cancellationToken);
        var actorIds = rows.Select(x => x.Entry.AuthorId).Concat(rows.Select(x => x.Entry.PublishedBy).OfType<Guid>())
            .Distinct().Select(x => x.ToString("D")).ToList();
        var names = await users.DisplayNamesAsync(actorIds, cancellationToken);
        return rows.Select(x =>
        {
            var publishedVersion = x.Entry.PublishedVersion ?? x.Entry.CurrentVersion;
            var content = versions.Single(v => v.EntryId == x.Entry.EntryId && v.Version == publishedVersion);
            return new KnowledgeEntryListItem(x.Entry.EntryId, content.Title, content.Answer,
            x.Entry.CurrentVersion, names.GetValueOrDefault(x.Entry.AuthorId.ToString("D"), "Former staff member"),
            x.Entry.PublishedBy is { } reviewer
                ? names.GetValueOrDefault(reviewer.ToString("D"), "Former staff member") : "",
            x.Entry.PublishedAt!.Value, tags.Where(t => t.EntryId == x.Entry.EntryId
                && t.Version == publishedVersion).OrderBy(t => t.Kind).ThenBy(t => t.Label)
                .Select(t => new EntryTagView(t.Kind, t.Value, t.Label)).ToList(), x.Rank,
                x.Entry.NeedsReview, reasons.GetValueOrDefault(x.Entry.EntryId, []));
        }).ToList();
    }

    private async Task<EntryReviewPage> NeedsReviewQueueAsync(
        SortedPageRequest<EntryReviewProjection, Guid> paging, RecordReferenceViewer viewer,
        CancellationToken cancellationToken)
    {
        var query = db.Entries.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.NeedsReview
                && x.PublishedVersion != null && x.Status != KnowledgeEntryStatus.Retired && x.AuthorId != viewer.UserId)
            .Select(x => new EntryReviewProjection
            {
                EntryId = x.EntryId, ActivityAt = x.NeedsReviewSince!.Value, Entry = x, Round = null,
            });
        var rows = await paging.Apply(query).Take(paging.Take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(paging.Take).ToList();
        var names = await users.DisplayNamesAsync(page.Select(x => x.Entry.AuthorId.ToString("D")).Distinct().ToList(),
            cancellationToken);
        var reasons = await ActiveReasonsAsync(page.Select(x => x.EntryId).ToList(), cancellationToken);
        return new EntryReviewPage(page.Select(x => new EntryReviewListItem(x.EntryId, x.Entry.Title,
                x.Entry.CurrentVersion, names.GetValueOrDefault(x.Entry.AuthorId.ToString("D"), "Former staff member"),
                x.ActivityAt, null, x.Entry.Status, true, reasons.GetValueOrDefault(x.EntryId, []),
                x.Entry.Status == KnowledgeEntryStatus.Published)).ToList(),
            rows.Count > paging.Take && page.Count > 0 ? paging.CursorFor(page[^1]) : null);
    }

    private async Task<Dictionary<Guid, IReadOnlyList<EntryReviewReasonView>>> ActiveReasonsAsync(
        List<Guid> entryIds, CancellationToken cancellationToken)
    {
        if (entryIds.Count == 0) return [];
        var rows = await db.EntryReviewReasons.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && entryIds.Contains(x.EntryId) && x.ClearedAt == null)
            .OrderBy(x => x.FlaggedAt).ThenBy(x => x.ReasonId).ToListAsync(cancellationToken);
        return rows.GroupBy(x => x.EntryId).ToDictionary(group => group.Key,
            group => (IReadOnlyList<EntryReviewReasonView>)group.Select(x => new EntryReviewReasonView(
                x.ReasonId, x.Kind, x.Label, x.Detail, x.FlaggedAt)).ToList());
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

    private async Task<List<WrittenAgainstView>> WrittenAgainstAsync(string json,
        CancellationToken cancellationToken)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<List<WrittenAgainstInput>>(json) ?? [];
            var policyIds = stored.Where(x => x.Kind == WrittenAgainstKind.PolicyVersion)
                .Select(x => Guid.TryParse(x.Reference, out var id) ? id : (Guid?)null).OfType<Guid>()
                .Distinct().ToList();
            var policies = await policyVersions.ResolveAsync(policyIds, cancellationToken);
            return stored.Select(x =>
            {
                if (x.Kind == WrittenAgainstKind.PolicyVersion && Guid.TryParse(x.Reference, out var id)
                    && policies.TryGetValue(id, out var policyLabel))
                    return new WrittenAgainstView(x.Kind, x.Reference, policyLabel, true);
                if (x.Kind == WrittenAgainstKind.Screen && screenCatalogue.Resolve(x.Reference) is { } screen)
                    return new WrittenAgainstView(x.Kind, x.Reference, screen.Label, true);
                return new WrittenAgainstView(x.Kind, x.Reference, x.Label, false);
            }).ToList();
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
    private static string Humanise(string value)
    {
        var words = value.Replace('_', ' ');
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }
    private static void ValidateTake(int take)
    {
        if (take > SortedPage<KnowledgeEntry, Guid>.MaximumTake)
            throw new CommandRefusedException("page_size_too_large",
                $"Page size cannot exceed {SortedPage<KnowledgeEntry, Guid>.MaximumTake}.");
        if (take < 1) throw new CommandRefusedException("page_size_invalid", "Page size must be at least 1.");
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
