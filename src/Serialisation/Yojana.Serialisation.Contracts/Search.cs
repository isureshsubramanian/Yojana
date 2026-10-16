namespace Yojana.Serialisation.Contracts;

// Global search (ADR-0001 "Global search and find-within"; MOD-13; P1-T23). The response is the shape the shell's
// palette renders (web/src/components/types.ts SearchResponse): scopes with counts, groups of ranked results with an
// explicit reason each, the hover metadata from the tenant's profile, and openDirectly when exactly one exact
// identifier hit exists. Later modules add entity types through ISearchIndex; the palette does not change.

/// <summary>Entity types the index knows; the code is also the palette's icon.</summary>
public static class SearchEntityType
{
    public const string Unit = "unit";
    public const string Document = "document";
    public const string Entitlement = "entitlement";
    public const string Claim = "claim";
    public const string Rma = "rma";
    public const string Certificate = "certificate";
    public const string Party = "party";
}

/// <summary>Why a term is on an entry; the reason text of a hit is derived from it.</summary>
public static class SearchTermKind
{
    /// <summary>An open identifier of a unit (raw and normalised forms).</summary>
    public const string Identifier = "identifier";

    /// <summary>A superseded identifier (still searchable, resolves to the unit; Identity rule 5).</summary>
    public const string Superseded = "superseded";

    /// <summary>A serial a document lists on one of its lines.</summary>
    public const string Lists = "lists";

    /// <summary>A word of the title or subtitle.</summary>
    public const string Word = "word";
}

/// <summary>One term of an entry (upper-cased; identifiers in both raw and normalised form).</summary>
public sealed record SearchTermDraft(string Term, string Kind, string? Detail = null);

/// <summary>What a module contributes for one record. Metadata is a flat JSON object the tenant's profile picks fields from.</summary>
public sealed record SearchEntryDraft(string EntityType, Guid EntityId, string Title, bool TitleMono, string? Subtitle, IReadOnlyList<SearchTermDraft> Terms, int TypeWeight, string MetadataJson, string Href);

/// <summary>Port every module indexes through; the Serialisation module owns the table and the query.</summary>
public interface ISearchIndex
{
    Task UpsertAsync(IReadOnlyList<SearchEntryDraft> entries, CancellationToken cancellationToken);

    Task RemoveAsync(string entityType, Guid entityId, CancellationToken cancellationToken);
}

public sealed record MatchSpan(int Start, int Length);

public sealed record MetadataField(string Label, string? Value, bool Mono = false);

public sealed record MetadataAction(string Label, string Href, bool Primary = false);

public sealed record ResultMetadata(string Eyebrow, string Title, bool TitleMono, string? Subtitle, IReadOnlyList<MetadataField> Fields, IReadOnlyList<MetadataAction> Actions, string? Note);

public sealed record SearchResult(string Id, string Type, string Href, string Title, bool TitleMono, IReadOnlyList<MatchSpan> Matches, string Subtitle, int Relevance, string Reason, ResultMetadata? Metadata);

public sealed record SearchScope(string Id, string Label, int Count);

public sealed record SearchGroup(string Label, IReadOnlyList<SearchResult> Results);

public sealed record SearchResponse(string Query, int TookMs, IReadOnlyList<SearchScope> Scopes, IReadOnlyList<SearchGroup> Groups, string? OpenDirectly, string Engine);

/// <summary>One field of a search metadata profile: the metadata key to show, its label, and the lowest disclosure tier that sees it.</summary>
public sealed record SearchProfileField(string Key, string Label, bool Mono = false, string Tier = SearchDisclosureTier.BackOffice);

/// <summary>Who may see a field: back-office users see everything; portal users see only portal-tier fields.</summary>
public static class SearchDisclosureTier
{
    public const string BackOffice = "back_office";
    public const string Portal = "portal";
}

/// <summary>The tenant's hover-card profile for one entity type (<c>tenant_setting</c> key <c>search.metadata.&lt;entity_type&gt;</c>, JSON array of fields).</summary>
public sealed record SearchMetadataProfile(string EntityType, IReadOnlyList<SearchProfileField> Fields);
