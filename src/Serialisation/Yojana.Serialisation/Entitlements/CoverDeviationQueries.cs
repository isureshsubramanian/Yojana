using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Entitlements;

public sealed class CoverDeviationQueries(SerialisationDbContext db, ITenantContext tenant, IClock clock, CoverHistory history,
    IItemNamePort items, IPartyNamePort parties, IDocumentNumberPort documents, IUserDirectory people, ITenantCurrency currency,
    IItemFamilyNamePort itemFamilies)
{
    public async Task<CoverSinceSale?> ForUnitAsync(Guid unitId, CancellationToken ct)
    {
        var unit = await db.Current.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == tenant.TenantId && c.UnitId == unitId, ct);
        if (unit is null) return null;
        var today = DateOnly.FromDateTime(clock.UtcNow);
        var data = await history.LoadAsync(unitId, today, ct);
        var names = await NamesAsync([unit.ItemId], data.Entitlements.SelectMany(e => new[] { e.OwnerPartyRef, e.PayerPartyRef }).Append(data.SaleCustomerPartyRef).OfType<Guid>().ToList(),
            data.Facts.Where(f => f.DocumentId != null && f.DocumentType != null).Select(f => new DocumentRef(f.DocumentType!, f.DocumentId!.Value, f.DocumentLine))
                .Concat(data.Base is { SourceDocumentId: { } document, SourceDocumentType: { } type } ? [new DocumentRef(type, document)] : []).ToList(), ct);
        var serial = await SerialAsync(unitId, ct);
        if (data.Base is null) return new(unitId, serial, names.Item(unit.ItemId), null, null, null, 0, [], null, [], [], data.Notice);
        var policyIds = data.Entitlements.Select(e => e.PolicyVersionId).Distinct().ToList();
        var versions = await db.PolicyVersions.AsNoTracking().Where(p => p.TenantId == tenant.TenantId && policyIds.Contains(p.PolicyVersionId)).ToDictionaryAsync(p => p.PolicyVersionId, ct);
        var families = await db.CoverageFamilies.AsNoTracking().Where(f => f.TenantId == tenant.TenantId).ToListAsync(ct);
        var refs = data.Changes.SelectMany(c => new[] { c.Fact.Actor, c.Approver }).OfType<string>().Distinct().ToList();
        var users = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in refs.Chunk(IUserDirectory.MaxReferences))
            foreach (var pair in await people.DisplayNamesAsync(chunk, ct)) users[pair.Key] = pair.Value;
        string Person(string? reference) => reference is not null && users.TryGetValue(reference, out var name) ? name
            : reference is null ? "Not recorded" : Guid.TryParse(reference, out _) ? "Unknown user" : reference;
        string Policy(Entitlement e) => versions.TryGetValue(e.PolicyVersionId, out var p) ? p.Name : "Policy unavailable";
        string Family(string code) => families.FirstOrDefault(f => f.FamilyCode == code)?.Name ?? "Cover";
        var changes = data.Changes.Select(c => new CoverChange(c.Fact.EventId, c.Kind, Family(c.Entitlement.CoverageFamily), Policy(c.Entitlement),
            c.Date, c.Months, names.Document(c.Fact.DocumentType, c.Fact.DocumentId), c.Fact.DocumentLine, c.Amount, c.Currency,
            c.Entitlement.PayerPartyRef is { } payer ? names.Party(payer) : null, Person(c.Fact.Actor), c.Approver is null ? null : Person(c.Approver),
            c.Reason, c.Counted, c.Fields)).ToList();
        var comparisons = new List<CoverFamilyComparison>();
        foreach (var family in data.Baseline.Select(e => e.CoverageFamily).Concat(data.Entitlements.Where(e => e.UnitId == unitId).Select(e => e.CoverageFamily)).Distinct().OrderBy(f => f is CoverageFamilyCode.Base or "base" ? 0 : 1).ThenBy(f => Family(f)))
        {
            var b = data.Baseline.FirstOrDefault(e => e.CoverageFamily == family);
            var l = data.Entitlements.Where(e => e.UnitId == unitId && e.CoverageFamily == family && e.Status == EntitlementStatus.Active && (e.EndDate == null || e.EndDate >= today)).SingleOrDefault();
            var familyChanges = data.Changes.Where(c => c.Entitlement.CoverageFamily == family).ToList();
            if (b is null && l is null) continue;
            var kind = ComparisonKind(b, l, data, familyChanges);
            comparisons.Add(new(Family(family), b is null ? null : OriginalTerm(b, data, Policy(b)), l is null ? null : new(Policy(l), l.StartDate, l.EndDate, l.Status, l.StartDate > today),
                kind, l is null ? data.Entitlements.Where(e => e.CoverageFamily == family).OrderByDescending(e => e.ClosedAt).FirstOrDefault()?.ClosedReason : null,
                changes.Where(c => c.FamilyName == Family(family)).ToList()));
        }
        // Policy applicability needs country/region and sale context. These are not retained on the unit's sale
        // snapshot, and the resolver accepts an absent country without checking scope. Do not present an offer as
        // applicable merely because the item has a policy mapping.
        const string applicabilityNotice = "Covers not taken are omitted: the sale history does not retain the country and context needed to establish policy applicability.";
        var values = await MoneyAsync(data.Changes.Where(c => c.Counted && c.Amount != null && c.Currency != null).Select(c => new CoverMoney(c.Currency!, c.Amount!.Value)), ct);
        return new(unitId, serial, names.Item(unit.ItemId), data.SaleCustomerPartyRef is { } owner ? names.Party(owner) : null, data.SaleDate,
            names.Document(data.Base.SourceDocumentType, data.Base.SourceDocumentId), changes.Count(c => c.Counted), values,
            comparisons.Select(c => c.Today?.EndDate).Max(), comparisons, changes, applicabilityNotice);
    }

    private static string ComparisonKind(Entitlement? baseline, Entitlement? live, CoverHistoryData data, List<CoverHistoryChange> changes)
    {
        if (baseline is null) return "added";
        if (live is null)
        {
            if (changes.Any(c => c.Kind == "closed_early")) return "closed_early";
            var lineage = data.Entitlements.Where(e => e.UnitId == data.UnitId && e.CoverageFamily == baseline.CoverageFamily
                && CoverHistoryRules.DescendsFrom(e, baseline, data.Entitlements)).ToList();
            if (lineage.Any(e => e.Status == EntitlementStatus.Suspended)) return "suspended";
            if (lineage.Any(e => e.ClosedReason == EntitlementClosedReason.Expired && e.EndDate <= data.Today)) return "ended";
            if (changes.Any(c => c.Kind == "transferred") || baseline.ClosedReason is EntitlementClosedReason.Replaced or EntitlementClosedReason.TransferredOut) return "transferred";
            return "unresolved";
        }
        if (live.EntitlementId == baseline.EntitlementId)
            return changes.Any(c => c.Kind == "amended") ? "amended" : changes.Any(c => c.Kind == "transferred") ? "transferred" : "same";
        if (!CoverHistoryRules.DescendsFrom(live, baseline, data.Entitlements)) return "changed";
        var chain = new List<Entitlement>();
        var next = live;
        while (!chain.Any(e => e.EntitlementId == next.EntitlementId))
        {
            chain.Add(next);
            if (next.EntitlementId == baseline.EntitlementId) break;
            var previous = next.SupersedesEntitlementId ?? next.ParentEntitlementId ?? next.RootEntitlementId;
            if (data.Entitlements.FirstOrDefault(e => e.EntitlementId == previous) is not { } parent) break;
            next = parent;
        }
        if (!chain.Any(e => e.SupersedesEntitlementId != null)) return "transferred";
        // Price belongs to the actual policy purchase. A later unpriced change must not inherit an earlier upgrade's
        // label; a transfer of the purchased policy can still find its priced predecessor in this lineage.
        var purchased = chain.Where(e => e.PolicyVersionId == live.PolicyVersionId).Any(e => e.SalePrice > 0 || changes.Any(c => c.Entitlement.EntitlementId == e.EntitlementId && c.Amount > 0 && c.Kind is "upgraded" or "changed"));
        return live.PolicyVersionId != baseline.PolicyVersionId && purchased ? "upgraded" : "changed";
    }

    private static CoverTerm OriginalTerm(Entitlement e, CoverHistoryData data, string name)
    {
        var start = e.StartDate;
        var end = e.EndDate;
        var created = data.Facts.FirstOrDefault(f => f.EventId == e.CreatedEventId);
        if (created is not null)
        {
            using var json = JsonDocument.Parse(created.Attributes);
            if (DateOnly.TryParse(CoverHistoryRules.TextValue(json.RootElement, "start_date"), out var s)) start = s;
            if (DateOnly.TryParse(CoverHistoryRules.TextValue(json.RootElement, "end_date"), out var t)) end = t;
        }
        foreach (var field in data.Changes.Where(c => c.Entitlement.EntitlementId == e.EntitlementId && c.Kind == "amended").OrderByDescending(c => c.Fact.At).SelectMany(c => c.Fields))
        {
            if (field.Field == "end_date" && DateOnly.TryParse(field.Before, out var t)) end = t;
            if (field.Field == "start_date" && DateOnly.TryParse(field.Before, out var s)) start = s;
        }
        return new(name, start, end, EntitlementStatus.Active, false);
    }

    private IQueryable<UnitCoverDeviation> Filter(CoverChangeFilters filter, bool changed)
    {
        if (filter.SoldFrom > filter.SoldTo) throw new ArgumentException("Sold from must be no later than sold to.");
        if (filter.ChangeKind is not null && !CoverHistoryRules.CountedKinds.Contains(filter.ChangeKind)) throw new ArgumentException("Unknown change kind.");
        var query = db.CoverDeviations.AsNoTracking().Where(r => r.TenantId == tenant.TenantId);
        if (changed) query = query.Where(r => r.Changed);
        if (filter.SoldFrom is { } from) query = query.Where(r => r.SaleDate >= from);
        if (filter.SoldTo is { } to) query = query.Where(r => r.SaleDate <= to);
        if (filter.Customer is { } customer) query = query.Where(r => r.CustomerPartyRef == customer);
        if (filter.ItemFamily is { Length: > 0 } family) query = query.Where(r => r.ItemFamily == family);
        if (filter.Serial is { Length: > 0 } serial) query = query.Where(r => db.Identifiers.Any(i => i.TenantId == r.TenantId && i.UnitId == r.UnitId && i.RawValue.StartsWith(serial)));
        if (changed && filter.ChangeKind is { } kind) query = query.Where(r => db.CoverDeviationChanges.Any(c => c.TenantId == r.TenantId && c.UnitId == r.UnitId && c.Kind == kind));
        return query;
    }

    public async Task<CoverChangePage> PageAsync(CoverChangeFilters filter, string? cursor, int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 200);
        var query = Filter(filter, true);
        if (!string.IsNullOrEmpty(cursor))
        {
            var point = Decode(cursor);
            query = query.Where(r => r.LastChangeAt < point.At || r.LastChangeAt == point.At && r.UnitId.CompareTo(point.Unit) < 0);
        }
        var rows = await query.OrderByDescending(r => r.LastChangeAt).ThenByDescending(r => r.UnitId).Take(take + 1).ToListAsync(ct);
        var more = rows.Count > take;
        rows = rows.Take(take).ToList();
        var ids = rows.Select(r => r.UnitId).ToList();
        var values = await db.CoverDeviationValues.AsNoTracking().Where(r => r.TenantId == tenant.TenantId && ids.Contains(r.UnitId)).ToListAsync(ct);
        var kinds = await db.CoverDeviationChanges.AsNoTracking().Where(r => r.TenantId == tenant.TenantId && ids.Contains(r.UnitId)).Select(r => new { r.UnitId, r.Kind }).Distinct().ToListAsync(ct);
        var labels = await (from change in db.CoverDeviationChanges.AsNoTracking()
            join term in db.Entitlements.AsNoTracking() on new { change.TenantId, change.EntitlementId } equals new { term.TenantId, term.EntitlementId }
            join policy in db.PolicyVersions.AsNoTracking() on new { term.TenantId, term.PolicyVersionId } equals new { policy.TenantId, policy.PolicyVersionId }
            join family in db.CoverageFamilies.AsNoTracking() on new { term.TenantId, FamilyCode = term.CoverageFamily } equals new { family.TenantId, family.FamilyCode }
            where change.TenantId == tenant.TenantId && ids.Contains(change.UnitId)
            select new { change.UnitId, change.Kind, FamilyName = family.Name, PolicyName = policy.Name }).Distinct().ToListAsync(ct);
        var serials = await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenant.TenantId && ids.Contains(i.UnitId) && i.ValidTo == null)
            .Select(i => new { i.UnitId, i.Type, i.RawValue }).ToListAsync(ct);
        var names = await NamesAsync(rows.Select(r => r.ItemId).ToList(), rows.Select(r => r.CustomerPartyRef).OfType<Guid>().ToList(),
            rows.Where(r => r.SaleDocumentId != null && r.SaleDocumentType != null).Select(r => new DocumentRef(r.SaleDocumentType!, r.SaleDocumentId!.Value)).ToList(), ct);
        var familyNames = await itemFamilies.ListAsync(ct);
        var mainCurrency = await currency.GetAsync(ct);
        return new(rows.Select(r => new CoverChangeUnit(r.UnitId,
            serials.Where(i => i.UnitId == r.UnitId).OrderBy(i => i.Type == IdentifierType.InternalSerial ? 0 : 1).Select(i => i.RawValue).FirstOrDefault() ?? "Serial not recorded",
            names.Item(r.ItemId), r.CustomerPartyRef is { } party ? names.Party(party) : null,
            familyNames.FirstOrDefault(f => f.Code == r.ItemFamily)?.Name, r.SaleDate, names.Document(r.SaleDocumentType, r.SaleDocumentId),
            r.ChangeCount, kinds.Where(k => k.UnitId == r.UnitId).Select(k => k.Kind).Order().ToList(),
            OrderMoney(values.Where(v => v.UnitId == r.UnitId).Select(v => new CoverMoney(v.Currency, v.Amount)), mainCurrency), r.LastChangeAt,
            labels.Where(c => c.UnitId == r.UnitId).OrderBy(c => c.FamilyName).ThenBy(c => c.Kind).Select(c => new CoverChangeLabel(c.Kind, c.FamilyName, c.PolicyName)).ToList())).ToList(),
            more ? Encode(rows[^1].LastChangeAt, rows[^1].UnitId) : null);
    }

    public async Task<CoverChangeSummary> SummaryAsync(CoverChangeFilters filter, CancellationToken ct)
    {
        var sold = Filter(filter, false);
        var units = Filter(filter, true);
        var changes = db.CoverDeviationChanges.AsNoTracking().Where(c => c.TenantId == tenant.TenantId && units.Any(u => u.UnitId == c.UnitId));
        if (filter.ChangeKind is { } kind) changes = changes.Where(c => c.Kind == kind);
        var count = await changes.CountAsync(ct);
        // Aggregate a month histogram in SQL. Median needs neither all units nor all change rows in application memory.
        var histogram = await changes.GroupBy(c => new { c.Kind, c.MonthsAfterSale }).Select(g => new { g.Key.Kind, Month = g.Key.MonthsAfterSale, Count = g.Count() }).ToListAsync(ct);
        var byKind = await changes.GroupBy(c => c.Kind).Select(g => new { Kind = g.Key, Units = g.Select(c => c.UnitId).Distinct().Count() }).ToListAsync(ct);
        var money = await changes.Where(c => c.Currency != null && c.Amount != null).GroupBy(c => new { c.Kind, c.Currency }).Select(g => new { g.Key.Kind, Currency = g.Key.Currency!, Amount = g.Sum(c => c.Amount!.Value) }).ToListAsync(ct);
        var firstPurchaseMonths = await changes.Where(c => c.Kind == "added" && c.Amount > 0)
            .GroupBy(c => c.UnitId).Select(g => new { Month = g.Min(c => c.MonthsAfterSale) })
            .GroupBy(c => c.Month).Select(g => new { Month = g.Key, Count = g.Count() }).ToListAsync(ct);
        var main = await currency.GetAsync(ct);
        var summaries = byKind.OrderBy(g => g.Kind).Select(g =>
        {
            var months = histogram.Where(h => h.Kind == g.Kind).OrderBy(h => h.Month).ToList();
            var n = months.Sum(h => h.Count);
            int At(int rank) { var seen = 0; foreach (var h in months) { seen += h.Count; if (seen > rank) return h.Month; } return 0; }
            return new CoverChangeKindSummary(g.Kind, g.Units, n == 0 ? null : (At((n - 1) / 2) + At(n / 2)) / 2m,
                OrderMoney(money.Where(m => m.Kind == g.Kind).Select(m => new CoverMoney(m.Currency, m.Amount)), main));
        }).ToList();
        return new(await sold.CountAsync(ct), await units.CountAsync(ct), count,
            OrderMoney(money.Select(m => new CoverMoney(m.Currency, m.Amount)), main), summaries,
            Median(firstPurchaseMonths.Select(h => (h.Month, h.Count))));
    }

    private static decimal? Median(IEnumerable<(int Month, int Count)> histogram)
    {
        var ordered = histogram.OrderBy(h => h.Month).ToList();
        var count = ordered.Sum(h => h.Count);
        if (count == 0) return null;
        int At(int rank) { var seen = 0; foreach (var row in ordered) { seen += row.Count; if (seen > rank) return row.Month; } return 0; }
        return (At((count - 1) / 2) + At(count / 2)) / 2m;
    }

    public async Task<bool> ExportTooLargeAsync(CoverChangeFilters filter, CancellationToken ct)
    {
        var units = Filter(filter, true);
        return await db.CoverDeviationChanges.AsNoTracking().Where(c => c.TenantId == tenant.TenantId && units.Any(u => u.UnitId == c.UnitId)
            && (filter.ChangeKind == null || c.Kind == filter.ChangeKind)).Take(100001).CountAsync(ct) > 100000;
    }

    public async IAsyncEnumerable<IReadOnlyList<string?>> ExportAsync(CoverChangeFilters filter, [EnumeratorCancellation] CancellationToken ct)
    {
        string? cursor = null;
        var exported = 0;
        do
        {
            var page = await PageAsync(filter, cursor, 200, ct);
            var ids = page.Rows.Select(r => r.UnitId).ToList();
            var changes = await db.CoverDeviationChanges.AsNoTracking().Where(c => c.TenantId == tenant.TenantId && ids.Contains(c.UnitId)
                    && (filter.ChangeKind == null || c.Kind == filter.ChangeKind)).OrderByDescending(c => c.EventTime).ThenByDescending(c => c.EventId)
                .Take(100000 - exported).ToListAsync(ct);
            // Values and classifications come only from the projection. Ledger reads enrich its exact event keys;
            // exporting a large report never recomputes unit histories or reconstructs deviations.
            foreach (var chunk in changes.Chunk(200))
            {
                var entitlementIds = chunk.Select(c => c.EntitlementId).Distinct().ToList();
                var terms = await (from e in db.Entitlements.AsNoTracking()
                    join policy in db.PolicyVersions.AsNoTracking() on new { e.TenantId, e.PolicyVersionId } equals new { policy.TenantId, policy.PolicyVersionId }
                    join family in db.CoverageFamilies.AsNoTracking() on new { e.TenantId, FamilyCode = e.CoverageFamily } equals new { family.TenantId, family.FamilyCode }
                    where e.TenantId == tenant.TenantId && entitlementIds.Contains(e.EntitlementId)
                    select new { e.EntitlementId, e.PayerPartyRef, PolicyName = policy.Name, FamilyName = family.Name }).ToListAsync(ct);
                var facts = await ExportFactsAsync(chunk, ct);
                var docRefs = facts.Where(f => f.DocumentType != null && f.DocumentId != null).Select(f => new DocumentRef(f.DocumentType!, f.DocumentId!.Value)).ToList();
                var names = await NamesAsync([], terms.Select(t => t.PayerPartyRef).OfType<Guid>().ToList(), docRefs, ct);
                var references = facts.Select(f => f.Actor).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var fact in facts)
                {
                    using var attributes = JsonDocument.Parse(fact.Attributes ?? "{}");
                    if (CoverHistoryRules.TextValue(attributes.RootElement, "approved_by") is { } approver) references.Add(approver);
                }
                var users = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var refs in references.Chunk(IUserDirectory.MaxReferences))
                    foreach (var pair in await people.DisplayNamesAsync(refs, ct)) users[pair.Key] = pair.Value;
                string Person(string? reference) => reference is null ? "Not recorded" : users.TryGetValue(reference, out var name) ? name : Guid.TryParse(reference, out _) ? "Unknown user" : reference;
                foreach (var change in chunk)
                {
                    var unit = page.Rows.Single(u => u.UnitId == change.UnitId);
                    var term = terms.FirstOrDefault(t => t.EntitlementId == change.EntitlementId);
                    var fact = facts.FirstOrDefault(f => f.EventId == change.EventId);
                    using var attributes = JsonDocument.Parse(fact?.Attributes ?? "{}");
                    var root = attributes.RootElement;
                    var reason = CoverHistoryRules.TextValue(root, "reason_code") ?? CoverHistoryRules.TextValue(root, "reason");
                    var fields = root.TryGetProperty("changes", out var beforeAfter) && beforeAfter.ValueKind == JsonValueKind.Array
                        ? string.Join("; ", beforeAfter.EnumerateArray().Select(f => $"{CoverHistoryRules.TextValue(f, "field")}: {CoverHistoryRules.TextValue(f, "before")} → {CoverHistoryRules.TextValue(f, "after")}")) : null;
                    exported++;
                    yield return [unit.Serial, unit.ItemName, unit.CustomerName, unit.ItemFamilyName, unit.SaleDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), unit.SaleDocumentNumber,
                        term?.FamilyName ?? "Cover unavailable", term?.PolicyName ?? "Policy unavailable", change.Kind,
                        change.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), change.MonthsAfterSale.ToString(CultureInfo.InvariantCulture),
                        names.Document(fact?.DocumentType, fact?.DocumentId), fact?.DocumentLine?.ToString(CultureInfo.InvariantCulture), change.Amount?.ToString("0.####", CultureInfo.InvariantCulture), change.Currency,
                        term?.PayerPartyRef is { } payer ? names.Party(payer) : null, Person(fact?.Actor), Person(CoverHistoryRules.TextValue(root, "approved_by")), reason, fields];
                }
            }
            cursor = page.NextCursor;
        } while (cursor is not null && exported < 100000);
    }

    private Task<List<ExportFact>> ExportFactsAsync(UnitCoverDeviationChange[] changes, CancellationToken ct)
    {
        var pairs = JsonSerializer.Serialize(changes.Select(c => new { c.EventId, c.EventTime }));
        var tenantId = tenant.TenantId.Value;
        return db.Database.SqlQuery<ExportFact>($"""
            SELECT f.event_id AS EventId, f.actor_ref AS Actor, f.document_type AS DocumentType,
                f.document_id AS DocumentId, f.document_line AS DocumentLine, f.attributes AS Attributes
            FROM OPENJSON({pairs}) WITH (EventId uniqueidentifier '$.EventId', EventTime datetime2(3) '$.EventTime') p
            INNER JOIN serialisation.unit_event f ON f.tenant_id = {tenantId} AND f.event_time = p.EventTime AND f.event_id = p.EventId
            UNION ALL
            SELECT f.event_id AS EventId, f.actor_ref AS Actor, f.document_type AS DocumentType,
                f.document_id AS DocumentId, f.document_line AS DocumentLine, f.attributes AS Attributes
            FROM OPENJSON({pairs}) WITH (EventId uniqueidentifier '$.EventId', EventTime datetime2(3) '$.EventTime') p
            INNER JOIN serialisation.unit_event_archive f ON f.tenant_id = {tenantId} AND f.event_time = p.EventTime AND f.event_id = p.EventId
            """).ToListAsync(ct);
    }

    private sealed record ExportFact(Guid EventId, string Actor, string? DocumentType, Guid? DocumentId, int? DocumentLine, string? Attributes);

    private async Task<string> SerialAsync(Guid id, CancellationToken ct) => await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenant.TenantId && i.UnitId == id && i.ValidTo == null)
        .OrderBy(i => i.Type == IdentifierType.InternalSerial ? 0 : 1).ThenBy(i => i.RawValue).Select(i => i.RawValue).FirstOrDefaultAsync(ct) ?? "Serial not recorded";
    private async Task<IReadOnlyList<CoverMoney>> MoneyAsync(IEnumerable<CoverMoney> money, CancellationToken ct) => OrderMoney(money, await currency.GetAsync(ct));
    private static List<CoverMoney> OrderMoney(IEnumerable<CoverMoney> money, string? main) => money.GroupBy(m => m.Currency)
        .Select(g => new CoverMoney(g.Key, g.Sum(m => m.Amount))).OrderBy(m => m.Currency == main ? 0 : 1).ThenBy(m => m.Currency).ToList();
    private sealed record Cursor(DateTime At, Guid Unit);
    private static string Encode(DateTime at, Guid unit) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Cursor(at, unit))));
    private static Cursor Decode(string cursor)
    {
        try { return JsonSerializer.Deserialize<Cursor>(Encoding.UTF8.GetString(Convert.FromBase64String(cursor))) ?? throw new ArgumentException("Invalid cover changes cursor."); }
        catch (Exception ex) when (ex is FormatException or JsonException) { throw new ArgumentException("Invalid cover changes cursor.", ex); }
    }
    private async Task<CoverNames> NamesAsync(IReadOnlyList<Guid> itemIds, IReadOnlyList<Guid> partyIds, IReadOnlyList<DocumentRef> docs, CancellationToken ct) =>
        new(await items.NamesAsync(itemIds.Distinct().ToList(), ct), await parties.NamesAsync(partyIds.Distinct().ToList(), ct), await documents.NumbersAsync(docs.Distinct().ToList(), ct));
    private sealed record CoverNames(IReadOnlyDictionary<Guid, ItemLabel> Items, IReadOnlyDictionary<Guid, PartyLabel> Parties, IReadOnlyDictionary<DocumentKey, DocumentLabel> Documents)
    {
        public string Item(Guid id) => Items.TryGetValue(id, out var label) ? label.Name ?? label.Code : "Item unavailable";
        public string Party(Guid id) => Parties.TryGetValue(id, out var label) ? label.Name ?? label.Code : "Customer unavailable";
        public string? Document(string? type, Guid? id) => type is not null && id is { } value ? Documents.TryGetValue(new(type, value), out var label) ? label.Number : "Document number unavailable" : null;
    }
}
