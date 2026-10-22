using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Questions;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Knowledge.Entries;

public sealed record KnowledgeScreenReferenceItem(string Reference, string Label, int PublishedEntries,
    DateTime? LastChangedAt, string? LastChangedByName);

public sealed record KnowledgeScreenReferencePage(IReadOnlyList<KnowledgeScreenReferenceItem> Items,
    string? NextCursor);

public sealed class KnowledgeScreenQueries(KnowledgeDbContext db, IUserDirectory users, ITenantContext tenant)
{
    public async Task<KnowledgeScreenReferencePage> ListAsync(string? cursor, int take,
        CancellationToken cancellationToken)
    {
        if (take is < 1 or > 100)
            throw new QuestionRuleException("page_size_invalid", "Take must be between 1 and 100.");
        var after = string.IsNullOrWhiteSpace(cursor) ? null : Decode(cursor);
        var query = from against in db.EntryWrittenAgainst.AsNoTracking()
            join entry in db.Entries.AsNoTracking()
                on new { against.TenantId, against.EntryId, against.Version }
                equals new { entry.TenantId, entry.EntryId, Version = entry.PublishedVersion!.Value }
            where against.TenantId == tenant.TenantId && against.Kind == WrittenAgainstKind.Screen
                && entry.PublishedVersion != null && entry.Status != KnowledgeEntryStatus.Retired
                && (after == null || against.Reference.CompareTo(after) > 0)
            group against by new { against.Reference, against.Label } into grouped
            orderby grouped.Key.Reference
            select new { grouped.Key.Reference, grouped.Key.Label, PublishedEntries = grouped.Count() };
        var rows = await query.Take(take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(take).ToList();
        var references = page.Select(x => x.Reference).ToList();
        var latestChanges = await db.ScreenChanges.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && references.Contains(x.ScreenReference)).OrderByDescending(x => x.ChangedAt)
            .ThenByDescending(x => x.ChangeId).ToListAsync(cancellationToken);
        var latestByReference = latestChanges.GroupBy(x => x.ScreenReference)
            .ToDictionary(group => group.Key, group => group.First());
        var actorIds = latestByReference.Values.Select(x => x.ChangedBy.ToString("D")).Distinct().ToList();
        var names = await users.DisplayNamesAsync(actorIds, cancellationToken);
        return new KnowledgeScreenReferencePage(page.Select(row =>
        {
            latestByReference.TryGetValue(row.Reference, out var change);
            return new KnowledgeScreenReferenceItem(row.Reference, row.Label, row.PublishedEntries,
                change?.ChangedAt, change is null ? null
                    : names.GetValueOrDefault(change.ChangedBy.ToString("D"), "Former staff member"));
        }).ToList(), rows.Count > take && page.Count > 0 ? Encode(page[^1].Reference) : null);
    }

    private static string Encode(string reference) => Convert.ToBase64String(Encoding.UTF8.GetBytes(reference));

    private static string Decode(string cursor)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(cursor)); }
        catch (FormatException)
        {
            throw new QuestionRuleException("cursor_invalid", "The screen-reference cursor is invalid.");
        }
    }
}
