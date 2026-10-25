using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Search;

/// <summary>
/// Which engine the database offers. Full-text search ranks word and near matches when the feature is installed
/// (Azure SQL; SQL Server with the full-text component; not the Linux container image by default). The term table
/// answers exact and prefix hits on either engine, because those must rank first and must be index seeks.
/// The full-text index is created lazily, once per process, when the feature is present and the index is not.
/// </summary>
public sealed class SearchEngineCapabilities(ILogger<SearchEngineCapabilities> logger) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool? fullText;

    public void Dispose() => gate.Dispose();

    public async Task<bool> FullTextAsync(SerialisationDbContext db, CancellationToken cancellationToken)
    {
        if (fullText is { } known) return known;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (fullText is { } again) return again;
            // Through EF (not the raw connection): the platform's connection interceptor sets the tenant session context
            // that row-level security reads, and a connection opened behind its back would answer every later query with no rows.
            var probe = await db.Database.SqlQueryRaw<FullTextProbe>(
                "SELECT CAST(ISNULL(FULLTEXTSERVICEPROPERTY('IsFullTextInstalled'), 0) AS int) AS Installed, CAST(ISNULL(OBJECTPROPERTYEX(OBJECT_ID('serialisation.search_entry'), 'TableHasActiveFulltextIndex'), 0) AS int) AS Indexed")
                .ToListAsync(cancellationToken);
            var installed = probe.Count > 0 ? probe[0].Installed : 0;
            var indexed = probe.Count > 0 ? probe[0].Indexed : 0;
            if (installed == 1 && indexed == 0)
            {
                try
                {
                    await db.Database.ExecuteSqlRawAsync("IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'yojana_search') CREATE FULLTEXT CATALOG yojana_search;", cancellationToken);
                    await db.Database.ExecuteSqlRawAsync("CREATE FULLTEXT INDEX ON serialisation.search_entry (search_text LANGUAGE 1033, title LANGUAGE 1033) KEY INDEX ux_search_entry_entry_id ON yojana_search WITH CHANGE_TRACKING AUTO;", cancellationToken);
                    indexed = 1;
                    logger.LogInformation("Full-text index created on serialisation.search_entry");
                }
                catch (SqlException ex)
                {
                    logger.LogWarning(ex, "Full-text is installed but the index could not be created; the prefix engine is used");
                }
            }

            fullText = installed == 1 && indexed == 1;
            logger.LogInformation("Global search engine: {Engine}", fullText.Value ? "full-text" : "prefix");
            return fullText.Value;
        }
        finally
        {
            gate.Release();
        }
    }
}

public sealed class FullTextProbe
{
    public int Installed { get; set; }
    public int Indexed { get; set; }
}

/// <summary>
/// The relevance query (ADR-0001 "Global search"): rank = hit score × type weight × recency, with a reason per hit.
/// Exact identifier hits always rank first and open directly when there is exactly one. Scopes count every hit per
/// entity type; a scope narrows the groups to one type.
/// </summary>
public sealed class SearchQueries(SerialisationDbContext db, ITenantContext tenant, SearchEngineCapabilities engine, SearchMetadataProfiles profiles, IClock clock)
{
    private const int CandidateLimit = 400;
    private const int ResultsPerGroup = 8;

    public async Task<SearchResponse> SearchAsync(string query, string? scope, string tier, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var q = query.Trim();
        var upper = q.ToUpperInvariant();
        var stripped = new string(upper.Where(c => c != '-' && c != ' ' && c != '.' && c != '/').ToArray());
        if (upper.Length == 0) return new SearchResponse(q, 0, [], [], null, "none");
        var tenantId = tenant.TenantId;
        var variants = new[] { upper, stripped }.Where(v => v.Length > 0).Distinct().ToList();

        // exact and prefix hits: index seeks on (tenant, term), read in key order so TOP stops the seek. The exact term is the
        // smallest string with its own prefix, so it still comes first. Ordering by term length sorted every match first:
        // 0.5-4.9 s for a word shared by 1.19 M units in the OD-13 test (2026-10-26), against 3 ms for a rare word.
        var candidates = new List<TermHit>();
        foreach (var v in variants)
        {
            var pattern = Escape(v) + "%";
            candidates.AddRange(await db.SearchTerms.AsNoTracking()
                .Where(t => t.TenantId == tenantId && EF.Functions.Like(t.Term, pattern, "\\"))
                .OrderBy(t => t.Term).Take(CandidateLimit)
                .Select(t => new TermHit(t.EntityType, t.EntityId, t.Term, t.Kind, t.Detail)).ToListAsync(cancellationToken));
        }

        var scored = new Dictionary<(string Type, Guid Id), Hit>();
        foreach (var hit in candidates)
        {
            var exact = variants.Contains(hit.Term);
            var coverage = (double)upper.Length / Math.Max(hit.Term.Length, upper.Length);
            var (score, reason) = (hit.Kind, exact) switch
            {
                (SearchTermKind.Identifier, true) => (100, "exact match · " + Label(hit.Detail)),
                (SearchTermKind.Superseded, true) => (92, "exact match on a superseded " + Label(hit.Detail) + " · resolves to this unit"),
                (SearchTermKind.Lists, true) => (85, "document lists the serial" + (hit.Detail is null ? "" : " on " + hit.Detail)),
                (SearchTermKind.Word, true) => (80, "exact word match"),
                (SearchTermKind.Identifier, false) => (60 + (int)Math.Round(25 * coverage), "prefix of " + Label(hit.Detail)),
                (SearchTermKind.Superseded, false) => (55 + (int)Math.Round(25 * coverage), "prefix of a superseded " + Label(hit.Detail)),
                (SearchTermKind.Lists, false) => (50 + (int)Math.Round(25 * coverage), "document lists a serial starting with the query"),
                _ => (40 + (int)Math.Round(20 * coverage), "word starts with the query"),
            };
            Merge(scored, hit.EntityType, hit.EntityId, score, reason, exactIdentifier: hit.Kind == SearchTermKind.Identifier && exact);
        }

        // Full-text serves word-like queries only. An identifier-like query (digits or hyphens) is tokenised by the
        // full-text engine into a phrase ("X7-00001*" becomes "x7" followed by "00001*"), which scans every document
        // sharing the first token: 1.3 s on 50,000 entries in the P1-T21 run, against 0 ms for the term seek. So
        // identifiers stay on the term index plus the bounded edit distance, whatever the engine.
        var fullText = await engine.FullTextAsync(db, cancellationToken);
        var identifierLike = upper.Any(char.IsDigit) || upper.Contains('-');
        if (fullText && upper.Length >= 3 && !identifierLike)
        {
            await AddFullTextAsync(scored, variants, tenantId, cancellationToken);
        }
        else if (upper.Length >= 5 && !scored.Values.Any(h => h.ExactIdentifier))
        {
            await AddNearAsync(scored, upper, tenantId, cancellationToken);
        }

        // entries, weights, recency
        var entries = new List<Data.Entities.SearchEntry>();
        foreach (var group in scored.Keys.GroupBy(k => k.Type))
        {
            var type = group.Key;
            var ids = group.Select(k => k.Id).ToList();
            entries.AddRange(await db.SearchEntries.AsNoTracking().Where(e => e.TenantId == tenantId && e.EntityType == type && ids.Contains(e.EntityId)).ToListAsync(cancellationToken));
        }

        var now = clock.UtcNow;
        var results = new List<(string Type, SearchResult Result)>();
        var profileCache = new Dictionary<string, SearchMetadataProfile>();
        foreach (var entry in entries)
        {
            if (!scored.TryGetValue((entry.EntityType, entry.EntityId), out var hit)) continue;
            var age = now - entry.UpdatedAt;
            var recency = age.TotalDays <= 90 ? 1.0 : age.TotalDays <= 365 ? 0.97 : 0.93;
            var relevance = hit.ExactIdentifier ? 100 : (int)Math.Clamp(Math.Round(hit.Score * entry.TypeWeight / 100.0 * recency), 1, 99);
            if (!profileCache.TryGetValue(entry.EntityType, out var profile)) profileCache[entry.EntityType] = profile = await profiles.GetAsync(entry.EntityType, cancellationToken);
            var metadata = Metadata(entry.EntityType, entry.Title, entry.TitleMono, entry.Subtitle, entry.Metadata, entry.Href, profile, tier);
            results.Add((entry.EntityType, new SearchResult($"{entry.EntityType}:{entry.EntityId:N}", entry.EntityType, entry.Href, entry.Title, entry.TitleMono, Spans(entry.Title, q), entry.Subtitle ?? "", relevance, hit.Reason, metadata)));
        }

        var ordered = results.OrderByDescending(r => r.Result.Relevance).ThenBy(r => r.Result.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var scopes = ordered.GroupBy(r => r.Type).Select(g => new SearchScope(ScopeId(g.Key), ScopeLabel(g.Key), g.Count())).OrderByDescending(s => s.Count).ToList();
        var shown = scope is null ? ordered : ordered.Where(r => ScopeId(r.Type) == scope).ToList();
        var groups = shown.GroupBy(r => r.Type).OrderBy(g => GroupOrder(g.Key)).Select(g => new SearchGroup(ScopeLabel(g.Key), g.Take(scope is null ? ResultsPerGroup : 50).Select(r => r.Result).ToList())).ToList();
        var exactUnits = ordered.Where(r => r.Type == SearchEntityType.Unit && scored[(r.Type, Guid.Parse(r.Result.Id.Split(':')[1]))].ExactIdentifier).ToList();
        var openDirectly = exactUnits.Count == 1 ? exactUnits[0].Result.Href : null;
        return new SearchResponse(q, (int)watch.ElapsedMilliseconds, scopes, groups, openDirectly, fullText ? "full-text" : "prefix");
    }

    private async Task AddNearAsync(Dictionary<(string, Guid), Hit> scored, string upper, TenantId tenantId, CancellationToken cancellationToken)
    {
        // Bounded edit distance over prefix candidates: terms sharing the first three characters and within one character of the query's length.
        var pattern = Escape(upper[..3]) + "%";
        var min = upper.Length - 1;
        var max = upper.Length + 1;
        var near = await db.SearchTerms.AsNoTracking()
            .Where(t => t.TenantId == tenantId && EF.Functions.Like(t.Term, pattern, "\\") && t.Term.Length >= min && t.Term.Length <= max && t.Kind != SearchTermKind.Word)
            .OrderBy(t => t.Term).Take(CandidateLimit)
            .Select(t => new TermHit(t.EntityType, t.EntityId, t.Term, t.Kind, t.Detail)).ToListAsync(cancellationToken);
        foreach (var hit in near)
        {
            if (scored.ContainsKey((hit.EntityType, hit.EntityId))) continue;
            if (EditDistance(upper, hit.Term, 1) == 1)
            {
                Merge(scored, hit.EntityType, hit.EntityId, hit.Kind == SearchTermKind.Lists ? 30 : 38, "near match · 1 edit from " + Label(hit.Detail), false);
            }
        }
    }

    private async Task AddFullTextAsync(Dictionary<(string, Guid), Hit> scored, List<string> variants, TenantId tenantId, CancellationToken cancellationToken)
    {
        // CONTAINSTABLE prefix terms on search_text and title; the rank (0..1000) maps to 40..79 so a seek hit always outranks it.
        // The full-text index is one per database, across tenants, so its own top_n would rank other tenants' rows first and
        // could leave a small tenant with no hits. The tenant filter comes first and TOP after it: 81-212 ms for a word in
        // all 1.19 M rows of the OD-13 test (2026-10-26), 2-4 ms for a rare one.
        var ft = string.Join(" OR ", variants.Select(v => "\"" + v.Replace("\"", "") + "*\""));
        var hits = await db.Database.SqlQueryRaw<FullTextHit>(
            "SELECT TOP (200) e.entity_type AS EntityType, e.entity_id AS EntityId, k.[RANK] AS Rank FROM CONTAINSTABLE(serialisation.search_entry, (search_text, title), {0}) AS k JOIN serialisation.search_entry AS e ON e.entry_id = k.[KEY] WHERE e.tenant_id = {1} ORDER BY k.[RANK] DESC", ft, tenantId.Value)
            .ToListAsync(cancellationToken);
        foreach (var hit in hits)
        {
            if (scored.ContainsKey((hit.EntityType, hit.EntityId))) continue;
            Merge(scored, hit.EntityType, hit.EntityId, 40 + (int)Math.Round(39 * Math.Clamp(hit.Rank / 1000.0, 0, 1)), "text match · full-text rank " + hit.Rank.ToString(CultureInfo.InvariantCulture), false);
        }
    }

    private static void Merge(Dictionary<(string, Guid), Hit> scored, string type, Guid id, int score, string reason, bool exactIdentifier)
    {
        var key = (type, id);
        if (!scored.TryGetValue(key, out var current) || score > current.Score || (exactIdentifier && !current.ExactIdentifier))
        {
            scored[key] = new Hit(Math.Max(score, current?.Score ?? 0), reason, exactIdentifier || (current?.ExactIdentifier ?? false));
        }
    }

    private static ResultMetadata Metadata(string type, string title, bool mono, string? subtitle, string json, string href, SearchMetadataProfile profile, string tier)
    {
        using var doc = JsonDocument.Parse(json);
        var fields = new List<MetadataField>();
        foreach (var f in profile.Fields)
        {
            if (tier == SearchDisclosureTier.Portal && f.Tier != SearchDisclosureTier.Portal) continue;
            string? value = null;
            if (doc.RootElement.TryGetProperty(f.Key, out var v) && v.ValueKind != JsonValueKind.Null)
            {
                value = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
            }

            fields.Add(new MetadataField(f.Label, string.IsNullOrEmpty(value) ? null : value, f.Mono));
        }

        var eyebrow = type == SearchEntityType.Unit ? "Unit" + (doc.RootElement.TryGetProperty("item_code", out var ic) && ic.ValueKind == JsonValueKind.String ? " · " + ic.GetString() : "") : ScopeLabel(type).TrimEnd('s');
        var actions = type == SearchEntityType.Unit
            ? new List<MetadataAction> { new("Open unit", href, true), new("History", href + "#history") }
            : [new("Show units", href, true)];
        return new ResultMetadata(eyebrow, title, mono, subtitle, fields, actions, "Fields come from the tenant's search metadata profile (Settings › Search › Metadata).");
    }

    private static IReadOnlyList<MatchSpan> Spans(string title, string q)
    {
        var i = title.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? [new MatchSpan(i, q.Length)] : [];
    }

    private static string Label(string? identifierType) => string.IsNullOrEmpty(identifierType) ? "identifier" : identifierType.Replace('_', ' ');

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    public static string ScopeId(string type) => type + "s";

    public static string ScopeLabel(string type) => type switch
    {
        SearchEntityType.Unit => "Units",
        SearchEntityType.Document => "Documents",
        SearchEntityType.Entitlement => "Entitlements",
        SearchEntityType.Claim => "Claims",
        SearchEntityType.Rma => "RMAs",
        SearchEntityType.Certificate => "Certificates",
        SearchEntityType.Party => "Parties",
        _ => type,
    };

    private static int GroupOrder(string type) => type switch { SearchEntityType.Unit => 0, SearchEntityType.Document => 1, _ => 2 };

    /// <summary>Levenshtein distance capped at <paramref name="max"/> + 1 (returns early once the bound is exceeded).</summary>
    public static int EditDistance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }

            if (rowMin > max) return max + 1;
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private sealed record TermHit(string EntityType, Guid EntityId, string Term, string Kind, string? Detail);



    private sealed record Hit(int Score, string Reason, bool ExactIdentifier);

    public sealed class FullTextHit
    {
        public string EntityType { get; set; } = "";
        public Guid EntityId { get; set; }
        public int Rank { get; set; }
    }
}
