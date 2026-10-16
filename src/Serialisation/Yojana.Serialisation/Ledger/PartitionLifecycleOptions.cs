namespace Yojana.Serialisation.Ledger;

/// <summary>
/// Configuration of the ledger's partition lifecycle (ADR-0001 AI 3). Section <c>Serialisation:PartitionLifecycle</c>.
/// Nothing here ever deletes a row: archival is a metadata switch and merging only collapses cold archive
/// months into the leftmost catch-all partition.
/// </summary>
public sealed class PartitionLifecycleOptions
{
    public const string SectionName = "Serialisation:PartitionLifecycle";

    /// <summary>Monthly boundaries are kept this far ahead of today, so an insert never lands in the open-ended last partition.</summary>
    public int MonthsAhead { get; set; } = 12;

    /// <summary>A month whose upper boundary is older than this is switched from <c>unit_event</c> into the archive.</summary>
    public int ArchiveAfterMonths { get; set; } = 36;

    /// <summary>
    /// A boundary older than this is merged away; the archive rows of that month fold into the partition to its left
    /// (ultimately the catch-all first partition). Null turns merging off. Keeps the partition count bounded
    /// (the ADR plans for 120–180 live partitions).
    /// </summary>
    public int? MergeAfterMonths { get; set; } = 48;

    /// <summary>
    /// Boundaries merged in one run. A merge moves that month's archive rows into the catch-all partition (about a
    /// million rows, half a minute on the P1-T21 laptop), so a backlog of years (a migrated history) is worked off a
    /// few boundaries a night instead of holding the job for hours; steady state is one boundary a month.
    /// </summary>
    public int MaxMergesPerRun { get; set; } = 4;

    /// <summary>Quartz cron for the daily run; the job is database-wide, not per tenant.</summary>
    public string Cron { get; set; } = "0 10 2 * * ?";
}
