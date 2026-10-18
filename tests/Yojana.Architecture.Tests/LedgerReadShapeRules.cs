using Xunit;

namespace Yojana.Architecture.Tests;

/// <summary>Guards the ledger predicates whose missing leading keys caused the P6 concurrency collapse.</summary>
public sealed class LedgerReadShapeRules
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Interactive_ledger_reads_keep_their_index_boundary()
    {
        var rma = Source("Rmas", "RmaQueries.cs");
        Assert.Contains("row.DocumentType == RmaDocumentType", rma, StringComparison.Ordinal);
        Assert.Contains("row.DocumentId == rmaId", rma, StringComparison.Ordinal);
        Assert.Contains("row.EventTime == dispatchedAt", rma, StringComparison.Ordinal);

        var units = Source("Queries", "UnitQueries.cs");
        Assert.Contains("current.LastEventTime", units, StringComparison.Ordinal);
        Assert.Contains("@event.Seq == current.LastEventSeq", units, StringComparison.Ordinal);
        Assert.DoesNotContain("GroupBy(e => e.UnitId)", units, StringComparison.Ordinal);

        var corrections = Source("Corrections", "AssignIdentifiersHandler.cs");
        Assert.Contains("unitIds.Contains(e.UnitId)", corrections, StringComparison.Ordinal);

        var labels = Source("Labels", "LabelBatchPrintHandler.cs");
        Assert.Contains("current.LastEventTime", labels, StringComparison.Ordinal);
        Assert.DoesNotContain("GroupBy(row => row.UnitId)", labels, StringComparison.Ordinal);

        var replacements = Source("Replacements", "CorrectReplacementLinkHandler.cs");
        Assert.Contains("row.UnitId == recordedUnitId && row.EventId", replacements, StringComparison.Ordinal);
    }

    [Fact]
    public void Batched_idempotency_reads_put_the_unique_key_before_partition_time()
    {
        AssertKeyBeforeTime(Source("Ledger", "UnitEventWriter.cs"), "keys.Contains(e.IdempotencyKey)");
        AssertKeyBeforeTime(Source("Documents", "DocumentContext.cs"), "values.Contains(e.IdempotencyKey)");
    }

    private static void AssertKeyBeforeTime(string source, string keyPredicate)
    {
        var key = source.IndexOf(keyPredicate, StringComparison.Ordinal);
        var time = source.IndexOf("times.Contains(e.EventTime)", key, StringComparison.Ordinal);
        Assert.True(key >= 0 && time > key, $"Expected '{keyPredicate}' before event-time filtering.");
    }

    private static string Source(string directory, string file) => File.ReadAllText(Path.Combine(Root,
        "src", "Serialisation", "Yojana.Serialisation", directory, file));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yojana.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
