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

        var certificateSelection = Source("Certificates", "CertificateSelectionService.cs");
        Assert.Contains(".Select(row => new { row.UnitId, row.CreatedEventTime })", certificateSelection, StringComparison.Ordinal);
        Assert.Contains(".Select(row => new { row.UnitId, row.LifecycleStatus, row.CreatedEventTime })", certificateSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("createdEventIds.Contains", certificateSelection, StringComparison.Ordinal);

        var issuePoints = Source("Certificates", "ReleaseRecordIssuePointSettings.cs");
        Assert.Contains("closingTimes.Contains(row.EventTime)&&closingIds.Contains(row.EventId)", Compact(issuePoints), StringComparison.Ordinal);

        var selections = Source("Selection", "SelectionSetResolver.cs");
        Assert.Contains("db.LedgerBaseOrderReferences.AsNoTracking()", selections, StringComparison.Ordinal);
        Assert.Contains("reference.BaseDocumentType == doc.Type", selections, StringComparison.Ordinal);
        Assert.Contains("reference.BaseDocumentId == doc.Id", selections, StringComparison.Ordinal);
        Assert.Contains("checkpoint?.CheckpointMonth?.AddMonths(1)", selections, StringComparison.Ordinal);
        Assert.Equal(2, Count(selections, "event.event_time < @before"));
        Assert.DoesNotContain("e.Attributes.Contains", selections, StringComparison.Ordinal);
        Assert.DoesNotContain("db.Events.AsNoTracking()\n                    .Where(e => e.TenantId == tenantId && e.BaseDocument", selections, StringComparison.Ordinal);

        var backfill = Source("Ledger", "LedgerBaseOrderBackfill.cs");
        Assert.Equal(2, Count(backfill, "event.tenant_id = @tenant AND event.event_time >= @month AND event.event_time < @next"));
        Assert.Contains("ORDER BY source.event_time DESC, source.event_id DESC", backfill, StringComparison.Ordinal);
    }

    [Fact]
    public void Batched_idempotency_reads_preserve_exact_key_and_partition_time_pairs()
    {
        var reader = Source("Ledger", "UnitEventIdempotencyReader.cs");
        Assert.Contains("OPENJSON(@pairs)", reader, StringComparison.Ordinal);
        Assert.Contains("ledger.[idempotency_key] = requested.[Key]", reader, StringComparison.Ordinal);
        Assert.Contains("ledger.[event_time] = requested.[At]", reader, StringComparison.Ordinal);
        Assert.Contains("FORCESEEK", reader, StringComparison.Ordinal);

        var writer = Source("Ledger", "UnitEventWriter.cs");
        var documents = Source("Documents", "DocumentContext.cs");
        Assert.Contains("idempotency.SeenAsync", writer, StringComparison.Ordinal);
        Assert.Contains("idempotency.SeenAsync", documents, StringComparison.Ordinal);
        Assert.DoesNotContain("keys.Contains(e.IdempotencyKey)", writer, StringComparison.Ordinal);
        Assert.DoesNotContain("times.Contains(e.EventTime)", documents, StringComparison.Ordinal);
    }

    private static string Compact(string source) => string.Concat(source.Where(character => !char.IsWhiteSpace(character)));

    private static int Count(string source, string value) => source.Split(value, StringSplitOptions.None).Length - 1;

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
