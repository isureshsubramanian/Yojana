using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Search;

/// <summary>
/// Owns <c>search_entry</c> and <c>search_term</c> (MOD-13, P1-T23). Writes are whole-entry replacements built from
/// the module's own tables, so a handler never reasons about deltas: any event on a unit or a document re-indexes
/// that unit or document. Other modules index through <see cref="ISearchIndex"/> with their own entity types.
/// </summary>
public sealed class SearchIndexer(SerialisationDbContext db, ITenantContext tenant, IItemQueryPort items, IClock clock) : ISearchIndex
{
    public const int UnitWeight = 100;
    public const int DocumentWeight = 90;

    public async Task UpsertAsync(IReadOnlyList<SearchEntryDraft> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0) return;
        var tenantId = tenant.TenantId;
        var now = clock.UtcNow;
        // Events of one unit arrive on parallel handlers, each rebuilding the same entry: an application lock per entry
        // serialises them (the last writer reads the same tables, so the order does not matter). The lock lives for the
        // transaction: the handler's own under the bus middleware, or one opened here for a direct caller.
        var ownTransaction = db.Database.CurrentTransaction is null;
        if (ownTransaction) await db.Database.BeginTransactionAsync(cancellationToken);
        // TIGHT COUPLING with SearchBackfill.LockTenantAsync: every search writer uses this tenant resource.
        // Incremental writers take Update (one writer, readers unaffected), while backfill takes Exclusive. Distinct
        // entity locks cannot prevent deadlocks on shared search/full-text index pages; changing either resource or
        // mode independently re-opens that cross-entry lock cycle and lets projection messages reach the error queue.
        await SearchBackfill.LockTenantAsync(db, tenantId, "Update", cancellationToken);
        // 100 entries per statement keeps every delete and insert far below the 5,000-lock escalation threshold, so parallel handlers never escalate to a table lock and deadlock.
        foreach (var chunk in entries.Chunk(100))
        {
            await LockAsync(tenantId, chunk, cancellationToken);
            // Both statements name the entity type as well as the id so they seek the (tenant, type, id) keys; a predicate on
            // the id alone would range-scan the tenant's rows under lock and deadlock with the other handlers' inserts.
            var existing = new Dictionary<(string, Guid), SearchEntry>();
            foreach (var group in chunk.GroupBy(e => e.EntityType))
            {
                var type = group.Key;
                var keys = group.Select(e => e.EntityId).ToList();
                foreach (var row in await db.SearchEntries.Where(e => e.TenantId == tenantId && e.EntityType == type && keys.Contains(e.EntityId)).ToListAsync(cancellationToken)) existing[(row.EntityType, row.EntityId)] = row;
                await db.SearchTerms.Where(t => t.TenantId == tenantId && t.EntityType == type && keys.Contains(t.EntityId)).ExecuteDeleteAsync(cancellationToken);
            }
            foreach (var draft in chunk)
            {
                var text = string.Join(' ', draft.Terms.Select(t => t.Term).Distinct(StringComparer.OrdinalIgnoreCase));
                if (existing.TryGetValue((draft.EntityType, draft.EntityId), out var row))
                {
                    row.Title = Clip(draft.Title, 200);
                    row.TitleMono = draft.TitleMono;
                    row.Subtitle = draft.Subtitle is null ? null : Clip(draft.Subtitle, 400);
                    row.SearchText = text;
                    row.TypeWeight = draft.TypeWeight;
                    row.UpdatedAt = now;
                    row.Metadata = draft.MetadataJson;
                    row.Href = draft.Href;
                }
                else
                {
                    db.SearchEntries.Add(new SearchEntry
                    {
                        TenantId = tenantId, EntityType = draft.EntityType, EntityId = draft.EntityId, EntryId = TimeOrderedId.New(now), Title = Clip(draft.Title, 200), TitleMono = draft.TitleMono,
                        Subtitle = draft.Subtitle is null ? null : Clip(draft.Subtitle, 400), SearchText = text, TypeWeight = draft.TypeWeight, UpdatedAt = now, Metadata = draft.MetadataJson, Href = draft.Href,
                    });
                }

                foreach (var term in draft.Terms.Where(t => t.Term.Length > 0).DistinctBy(t => (t.Term, t.Kind)))
                {
                    db.SearchTerms.Add(new SearchTerm { TenantId = tenantId, Term = Clip(term.Term, 200), EntityType = draft.EntityType, EntityId = draft.EntityId, Kind = term.Kind, Detail = term.Detail is null ? null : Clip(term.Detail, 60) });
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        if (ownTransaction) await db.Database.CommitTransactionAsync(cancellationToken);
    }

    private async Task LockAsync(TenantId tenantId, SearchEntryDraft[] chunk, CancellationToken cancellationToken)
    {
        Array.Sort(chunk, (a, b) => { var byType = string.CompareOrdinal(a.EntityType, b.EntityType); return byType == 0 ? a.EntityId.CompareTo(b.EntityId) : byType; });
        // sp_getapplock in a DECLARE/EXEC batch is not composable through EF, so it runs as a plain command on the context's connection and transaction.
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        var sql = new System.Text.StringBuilder("DECLARE @r int; ");
        for (var i = 0; i < chunk.Length; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@r" + i.ToString(CultureInfo.InvariantCulture);
            parameter.Value = $"search:{tenantId}:{chunk[i].EntityType}:{chunk[i].EntityId:N}";
            command.Parameters.Add(parameter);
            sql.Append(CultureInfo.InvariantCulture, $"EXEC @r = sp_getapplock @Resource = @r{i}, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 20000; IF @r < 0 THROW 51000, 'search entry busy', 1; ");
        }

        command.CommandText = sql.ToString();
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveAsync(string entityType, Guid entityId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await db.SearchTerms.Where(t => t.TenantId == tenantId && t.EntityType == entityType && t.EntityId == entityId).ExecuteDeleteAsync(cancellationToken);
        await db.SearchEntries.Where(e => e.TenantId == tenantId && e.EntityType == entityType && e.EntityId == entityId).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Rebuilds the entries of these units from the unit, identifier, current-state and episode tables.</summary>
    public async Task ReindexUnitsAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken)
    {
        if (unitIds.Count == 0) return;
        var drafts = await BuildUnitDraftsAsync(unitIds, 100, cancellationToken);
        await UpsertAsync(drafts, cancellationToken);
    }

    internal async Task<bool> IsUnitCurrentAsync(Guid unitId, long eventSeq, CancellationToken cancellationToken)
    {
        var metadata = await db.SearchEntries.AsNoTracking()
            .Where(entry => entry.TenantId == tenant.TenantId && entry.EntityType == SearchEntityType.Unit && entry.EntityId == unitId)
            .Select(entry => entry.Metadata)
            .SingleOrDefaultAsync(cancellationToken);
        if (metadata is null) return false;
        using var json = JsonDocument.Parse(metadata);
        return json.RootElement.TryGetProperty("last_event_seq", out var value)
            && value.TryGetInt64(out var indexedSeq) && indexedSeq >= eventSeq;
    }

    // Shared draft construction keeps the bulk and incremental representations identical.
    internal async Task<List<SearchEntryDraft>> BuildUnitDraftsAsync(IReadOnlyCollection<Guid> unitIds, int chunkSize, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var drafts = new List<SearchEntryDraft>();
        var itemCodes = new Dictionary<Guid, string>();
        // Sorted, so two handlers re-indexing overlapping sets take their application locks in the same order and never deadlock.
        foreach (var ids in unitIds.Distinct().Order().Chunk(chunkSize))
        {
            var units = await db.Units.AsNoTracking().Where(u => u.TenantId == tenantId && ids.Contains(u.UnitId)).ToListAsync(cancellationToken);
            var current = await db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && ids.Contains(c.UnitId)).ToDictionaryAsync(c => c.UnitId, cancellationToken);
            var identifiers = (await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && ids.Contains(i.UnitId)).ToListAsync(cancellationToken)).ToLookup(i => i.UnitId);
            var ownership = (await db.Episodes.AsNoTracking().Where(e => e.TenantId == tenantId && ids.Contains(e.UnitId) && e.ValidTo == null).ToListAsync(cancellationToken)).ToLookup(e => e.UnitId);
            // The newest event per unit is the one unit_current names (last_event_time, last_event_seq): one seek per unit on
            // (tenant, unit, event_time) in a single partition, instead of reading every event of every unit, which the
            // backfill's 5,000-unit batches could not afford (P1-T25).
            var lastEvents = (await (from c in db.Current.AsNoTracking()
                                     join e in db.Events.AsNoTracking() on new { c.TenantId, c.UnitId, EventTime = c.LastEventTime } equals new { e.TenantId, e.UnitId, e.EventTime }
                                     where c.TenantId == tenantId && ids.Contains(c.UnitId) && e.Seq == c.LastEventSeq
                                     select new { e.UnitId, e.EventType, e.EventTime, e.Seq }).ToListAsync(cancellationToken))
                .GroupBy(e => e.UnitId).ToDictionary(g => g.Key, g => g.MaxBy(e => e.Seq)!);
            foreach (var unit in units)
            {
                current.TryGetValue(unit.UnitId, out var state);
                lastEvents.TryGetValue(unit.UnitId, out var lastEvent);
                var itemId = state?.ItemId;
                if (itemId is { } item && !itemCodes.ContainsKey(item))
                {
                    itemCodes[item] = (await items.GetAsync(item, cancellationToken))?.ItemCode ?? "item " + item.ToString("N")[..8];
                }

                var ids2 = identifiers[unit.UnitId].OrderBy(i => i.ValidTo == null ? 0 : 1).ThenBy(i => i.Type == IdentifierType.InternalSerial ? 0 : 1).ThenBy(i => i.ValidFrom).ToList();
                var primary = ids2.FirstOrDefault(i => i.ValidTo == null);
                var title = primary?.RawValue ?? $"unit {unit.UnitId:N}"[..13] + " (pending identifier)";
                var owner = ownership[unit.UnitId].FirstOrDefault(e => e.Kind == EpisodeKind.Ownership);
                var custody = ownership[unit.UnitId].FirstOrDefault(e => e.Kind == EpisodeKind.Custody);
                var itemCode = itemId is { } i2 ? itemCodes[i2] : "";
                var subtitle = string.Join(" · ", new[] { itemCode, Humanise(unit.LifecycleStatus), Humanise(state?.LocationKind), owner is null ? null : "owner " + Party(owner) }.Where(s => !string.IsNullOrEmpty(s)));
                var terms = new List<SearchTermDraft>();
                foreach (var id in ids2)
                {
                    var kind = id.ValidTo == null ? SearchTermKind.Identifier : SearchTermKind.Superseded;
                    terms.Add(new SearchTermDraft(id.NormalisedValue.ToUpperInvariant(), kind, id.Type));
                    terms.Add(new SearchTermDraft(id.RawValue.ToUpperInvariant(), kind, id.Type));
                }

                terms.AddRange(Words(itemCode).Select(w => new SearchTermDraft(w, SearchTermKind.Word)));
                var metadata = new Dictionary<string, object?>
                {
                    ["serial"] = primary?.RawValue, ["identifier_type"] = primary?.Type, ["item_code"] = itemCode, ["item_id"] = itemId, ["status"] = Humanise(unit.LifecycleStatus), ["disposition"] = Humanise(state?.Disposition),
                    ["origin"] = Humanise(unit.Origin), ["lot_id"] = unit.LotId, ["expiry_date"] = unit.ExpiryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["owner"] = owner is null ? null : Party(owner), ["owner_since"] = owner?.ValidFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["owner_source"] = owner?.Source,
                    ["custody"] = Humanise(custody?.LocationKind), ["custody_party"] = custody is null || custody.PartyRef is null ? null : Party(custody), ["location_id"] = custody?.LocationId,
                    ["custody_since"] = custody?.ValidFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["due_back"] = custody?.DueBack?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["last_event"] = lastEvent?.EventType, ["last_event_at"] = lastEvent?.EventTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), ["last_event_seq"] = state?.LastEventSeq,
                    ["warranty_end"] = state?.WarrantyEndMax?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["flags"] = unit.OverlayFlags, ["identifiers"] = ids2.Count(i => i.ValidTo == null),
                };
                drafts.Add(new SearchEntryDraft(SearchEntityType.Unit, unit.UnitId, title, true, subtitle, terms, UnitWeight, JsonSerializer.Serialize(metadata), $"/units/{unit.UnitId}"));
            }
        }

        return drafts;
    }

    /// <summary>Rebuilds a document's entry from the events that name it: every serial it lists is a term (the "document lists the serial" reason).</summary>
    public async Task ReindexDocumentAsync(string documentType, Guid documentId, CancellationToken cancellationToken) => await ReindexDocumentAsync(documentType, documentId, null, cancellationToken);

    /// <summary>
    /// With <paramref name="recordedAt"/>: a posting of five hundred lines publishes five hundred events naming the
    /// same document, and each arrives on its own handler; the first rebuild covers them all, so a handler whose event
    /// was recorded before the entry's last rebuild has nothing to add and returns (P1-T21: 227 full rebuilds of one
    /// delivery in a row).
    /// </summary>
    public async Task ReindexDocumentAsync(string documentType, Guid documentId, DateTime? recordedAt, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (recordedAt is { } at && await db.SearchEntries.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.EntityType == SearchEntityType.Document && e.EntityId == documentId && e.UpdatedAt > at, cancellationToken))
        {
            return;
        }
        var events = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.DocumentType == documentType && e.DocumentId == documentId)
            .Select(e => new { e.UnitId, e.DocumentLine, e.EventTime, e.EventType, e.Seq }).ToListAsync(cancellationToken);
        if (events.Count == 0)
        {
            await RemoveAsync(SearchEntityType.Document, documentId, cancellationToken);
            return;
        }

        var unitIds = events.Select(e => e.UnitId).Distinct().ToList();
        // joined in SQL rather than by an id list: a receipt can carry ten thousand serials, more than a parameter list holds
        var serials = await (from e in db.Events.AsNoTracking()
                             join i in db.Identifiers.AsNoTracking() on new { e.TenantId, e.UnitId } equals new { i.TenantId, i.UnitId }
                             where e.TenantId == tenantId && e.DocumentType == documentType && e.DocumentId == documentId && i.ValidTo == null
                             select new { i.UnitId, i.RawValue, i.NormalisedValue }).Distinct().ToListAsync(cancellationToken);
        var lineOf = events.GroupBy(e => e.UnitId).ToDictionary(g => g.Key, g => g.Min(e => e.DocumentLine));
        var terms = new List<SearchTermDraft> { new(documentId.ToString("N").ToUpperInvariant(), SearchTermKind.Word), new(documentId.ToString("N")[..8].ToUpperInvariant(), SearchTermKind.Word) };
        terms.AddRange(Words(Humanise(documentType)).Select(w => new SearchTermDraft(w, SearchTermKind.Word)));
        foreach (var s in serials)
        {
            var line = lineOf.TryGetValue(s.UnitId, out var l) && l is { } ln ? "line " + ln.ToString(CultureInfo.InvariantCulture) : null;
            terms.Add(new SearchTermDraft(s.NormalisedValue.ToUpperInvariant(), SearchTermKind.Lists, line));
            terms.Add(new SearchTermDraft(s.RawValue.ToUpperInvariant(), SearchTermKind.Lists, line));
        }

        var first = events.Min(e => e.EventTime);
        var last = events.Max(e => e.EventTime);
        var lines = events.Where(e => e.DocumentLine is not null).Select(e => e.DocumentLine).Distinct().Count();
        var title = $"{Humanise(documentType)} {documentId.ToString("N")[..8].ToUpperInvariant()}";
        var subtitle = $"{unitIds.Count.ToString("N0", CultureInfo.InvariantCulture)} serials on {lines.ToString(CultureInfo.InvariantCulture)} lines · {first:yyyy-MM-dd}";
        var metadata = new Dictionary<string, object?>
        {
            ["document_type"] = Humanise(documentType), ["document_id"] = documentId, ["serials"] = unitIds.Count, ["lines"] = lines,
            ["first_event"] = first.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), ["last_event"] = last.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["last_event_seq"] = events.Max(e => e.Seq), ["event_types"] = string.Join(", ", events.Select(e => e.EventType).Distinct().Order()),
        };
        await UpsertAsync([new SearchEntryDraft(SearchEntityType.Document, documentId, title, false, subtitle, terms, DocumentWeight, JsonSerializer.Serialize(metadata), $"/units?document={documentType}:{documentId:N}")], cancellationToken);
    }

    public static string Humanise(string? code) => string.IsNullOrEmpty(code) ? "" : code.Replace('_', ' ').Replace('.', ' ');

    private static string Party(CustodyEpisode e) => $"{Humanise(e.PartyType)} {e.PartyRef?.ToString("N")[..8]}".Trim();

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    private static IEnumerable<string> Words(string text) => text.Split([' ', '-', '/', ',', '·'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(w => w.Length >= 2).Select(w => w.ToUpperInvariant());
}

/// <summary>Asks for units to be re-indexed outside the published-event path (bulk ingestion writes the ledger directly).</summary>
public sealed record SearchReindexRequested(IReadOnlyList<Guid> UnitIds, IReadOnlyList<DocumentRef>? Documents = null);
