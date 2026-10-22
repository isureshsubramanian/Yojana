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

public sealed class KnowledgeScreenQueries(KnowledgeDbContext db, IUserDirectory users, ITenantContext tenant,
    ScreenReferenceCatalogue catalogue)
{
    public async Task<KnowledgeScreenReferencePage> ListAsync(string? cursor, int take,
        CancellationToken cancellationToken)
    {
        if (take is < 1 or > 100)
            throw new QuestionRuleException("page_size_invalid", "Take must be between 1 and 100.");
        var choices = catalogue.Search(null, cursor, take);
        var references = choices.Items.Select(x => x.Reference).ToList();
        var counts = await (from against in db.EntryWrittenAgainst.AsNoTracking()
            join entry in db.Entries.AsNoTracking()
                on new { against.TenantId, against.EntryId, against.Version }
                equals new { entry.TenantId, entry.EntryId, Version = entry.PublishedVersion!.Value }
            where against.TenantId == tenant.TenantId && against.Kind == WrittenAgainstKind.Screen
                && entry.PublishedVersion != null && entry.Status != KnowledgeEntryStatus.Retired
                && references.Contains(against.Reference)
            group against by against.Reference into grouped
            select new { Reference = grouped.Key, PublishedEntries = grouped.Count() })
            .ToDictionaryAsync(x => x.Reference, x => x.PublishedEntries, cancellationToken);
        var latestChanges = await db.ScreenChanges.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && references.Contains(x.ScreenReference)).OrderByDescending(x => x.ChangedAt)
            .ThenByDescending(x => x.ChangeId).ToListAsync(cancellationToken);
        var latestByReference = latestChanges.GroupBy(x => x.ScreenReference)
            .ToDictionary(group => group.Key, group => group.First());
        var actorIds = latestByReference.Values.Select(x => x.ChangedBy.ToString("D")).Distinct().ToList();
        var names = await users.DisplayNamesAsync(actorIds, cancellationToken);
        return new KnowledgeScreenReferencePage(choices.Items.Select(row =>
        {
            latestByReference.TryGetValue(row.Reference, out var change);
            return new KnowledgeScreenReferenceItem(row.Reference, row.Label, counts.GetValueOrDefault(row.Reference),
                change?.ChangedAt, change is null ? null
                    : names.GetValueOrDefault(change.ChangedBy.ToString("D"), "Former staff member"));
        }).ToList(), choices.NextCursor);
    }
}
