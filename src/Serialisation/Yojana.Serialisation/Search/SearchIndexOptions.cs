namespace Yojana.Serialisation.Search;

/// <summary>Work limits for asynchronous search index maintenance.</summary>
public sealed class SearchIndexOptions
{
    public const string SectionName = "Serialisation:Search";
    public const string ReindexQueueName = "serialisation-search-reindex";

    /// <summary>Maximum concurrent bulk re-index messages; kept low so ledger postings retain database capacity.</summary>
    public int MaximumParallelReindexMessages { get; set; } = 1;
}
