using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>
/// One searchable record (ADR-0001 "Global search and find-within"): maintained by handlers on the module's
/// published events through the outbox, so it lags the ledger by the outbox delay and never blocks a posting.
/// <c>search_text</c> feeds SQL Server full-text search where the feature is installed; <c>entry_id</c> is the
/// single-column unique key a full-text index needs.
/// </summary>
public sealed class SearchEntry : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public required string EntityType { get; set; }

    public Guid EntityId { get; set; }

    public Guid? MergedFromUnitId { get; set; }

    public Guid EntryId { get; set; }

    public required string Title { get; set; }

    public bool TitleMono { get; set; }

    public string? Subtitle { get; set; }

    public required string SearchText { get; set; }

    /// <summary>Percent multiplier on the engine rank (unit 100, document 90, ...).</summary>
    public int TypeWeight { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>Flat JSON object; the tenant's search metadata profile picks the hover-card fields from it.</summary>
    public required string Metadata { get; set; }

    public required string Href { get; set; }
}

/// <summary>
/// The prefix index of the fallback engine: one row per term of an entry, keyed by (tenant, term) so an exact or
/// prefix query is an index seek whatever the table size. Identifiers appear raw and normalised. With full-text
/// installed the term table still answers the exact and prefix cases (which must rank first and seek); full-text
/// adds the word and near matches.
/// </summary>
public sealed class SearchTerm : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public required string Term { get; set; }

    public required string EntityType { get; set; }

    public Guid EntityId { get; set; }

    public Guid? MergedFromUnitId { get; set; }

    /// <summary><see cref="Contracts.SearchTermKind"/>.</summary>
    public required string Kind { get; set; }

    /// <summary>Identifier type for identifier terms; document line for listed serials; null for words.</summary>
    public string? Detail { get; set; }
}
