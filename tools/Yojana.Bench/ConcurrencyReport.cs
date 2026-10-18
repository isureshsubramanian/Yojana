using System.Globalization;
using System.Text;

namespace Yojana.Bench;

public sealed record LoadSample(string Operation, double Milliseconds, int StatusCode, string? ErrorType, bool ExpectedConflict);

public sealed record OperationSummary(string Operation, int Requests, int Successful, int Errors, double Throughput,
    double P50, double P95, double P99);

public sealed record ConcurrencyDiagnostics(
    long ConcurrencyExceptions,
    int RetriesExhausted,
    long NumberSeriesLockWaitMilliseconds,
    long LockWaitDeltaMilliseconds,
    long ConnectionAcquisitions,
    double ConnectionAcquisitionMilliseconds,
    double ConnectionAcquisitionMaximumMilliseconds,
    int Deadlocks,
    int ExpectedDocumentConflicts,
    int LostUpdates,
    IReadOnlyDictionary<string, long> WaitMilliseconds,
    int PublishedQueueStart,
    int PublishedQueueMaximum,
    int PublishedQueueEnd,
    IReadOnlyList<QueryStoreDiagnostic> RmaCreateQueries);

public static class ConcurrencyReportMath
{
    public static IReadOnlyList<OperationSummary> Summarise(IReadOnlyCollection<LoadSample> samples, TimeSpan elapsed) => samples
        .GroupBy(sample => sample.Operation, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group =>
        {
            var rows = group.ToList();
            var latency = BenchReport.Percentiles(rows.Select(row => row.Milliseconds).ToList());
            var errors = rows.Count(row => row.ErrorType is not null && !row.ExpectedConflict);
            return new OperationSummary(group.Key, rows.Count, rows.Count - errors, errors,
                rows.Count / Math.Max(0.001, elapsed.TotalSeconds), latency.P50, latency.P95, latency.P99);
        }).ToList();

    public static IReadOnlyDictionary<string, int> Errors(IReadOnlyCollection<LoadSample> samples) => samples
        .Where(sample => sample.ErrorType is not null && !sample.ExpectedConflict)
        .GroupBy(sample => sample.ErrorType!, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
}

public static class ConcurrencyReportWriter
{
    public static async Task<(string Markdown, string Csv)> WriteAsync(BenchOptions options, DateTime startedAt,
        TimeSpan elapsed, IReadOnlyCollection<LoadSample> samples, ConcurrencyDiagnostics diagnostics,
        IReadOnlyList<string> notes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.OutDirectory);
        var stamp = startedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var summaries = ConcurrencyReportMath.Summarise(samples, elapsed);
        var errors = ConcurrencyReportMath.Errors(samples);
        var markdownPath = Path.Combine(options.OutDirectory, $"concurrency-{stamp}-{options.NumberingMode}.md");
        var csvPath = Path.Combine(options.OutDirectory, $"concurrency-{stamp}-{options.NumberingMode}.csv");
        var markdown = new StringBuilder()
            .AppendLine($"# Concurrency load run · {options.NumberingMode}").AppendLine()
            .AppendLine($"Started {startedAt:O}; {options.Engineers} simulated engineers for {elapsed}; HTTP target `{options.Url}`; database `{options.Database}`; tenant `{options.ConcurrencyTenantLabel}` (`{options.ConcurrencyTenantId}`). Real cookie authentication, {options.ContentionPercent}% hot-set selection.").AppendLine()
            .AppendLine("## Throughput and latency").AppendLine()
            .AppendLine("| Operation | Requests | Successful | Errors | req/s | p50 | p95 | p99 |")
            .AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in summaries)
            markdown.AppendLine($"| {row.Operation} | {row.Requests} | {row.Successful} | {row.Errors} | {row.Throughput:0.00} | {row.P50:0.0} ms | {row.P95:0.0} ms | {row.P99:0.0} ms |");
        markdown.AppendLine().AppendLine("## Concurrency integrity").AppendLine()
            .AppendLine($"- 409 `document_changed` responses: {diagnostics.ExpectedDocumentConflicts} (expected, not failures)")
            .AppendLine($"- Platform concurrency exceptions observed: {diagnostics.ConcurrencyExceptions}")
            .AppendLine($"- Retries exhausted: {diagnostics.RetriesExhausted}")
            .AppendLine($"- Sampled `number_series` request lock-wait time: {diagnostics.NumberSeriesLockWaitMilliseconds} ms")
            .AppendLine($"- SQL Server lock-wait-stat delta: {diagnostics.LockWaitDeltaMilliseconds} ms")
            .AppendLine($"- Application SQL connection acquisition: {diagnostics.ConnectionAcquisitionMilliseconds:0.0} ms across {diagnostics.ConnectionAcquisitions:N0} opens ({(diagnostics.ConnectionAcquisitions == 0 ? 0 : diagnostics.ConnectionAcquisitionMilliseconds / diagnostics.ConnectionAcquisitions):0.000} ms/open; process maximum {diagnostics.ConnectionAcquisitionMaximumMilliseconds:0.0} ms)")
            .AppendLine($"- `serialisation-published-events` queue depth: start {diagnostics.PublishedQueueStart:N0}, maximum {diagnostics.PublishedQueueMaximum:N0}, end {diagnostics.PublishedQueueEnd:N0}")
            .AppendLine($"- Deadlocks: {diagnostics.Deadlocks}")
            .AppendLine($"- Lost updates: {diagnostics.LostUpdates} {(diagnostics.LostUpdates == 0 ? "(PASS)" : "(FAIL)")}").AppendLine()
            .AppendLine("### SQL wait-stat delta").AppendLine();
        foreach (var wait in diagnostics.WaitMilliseconds.Where(pair => pair.Value > 0).OrderByDescending(pair => pair.Value))
            markdown.AppendLine($"- `{wait.Key}`: {wait.Value:N0} ms");
        if (!diagnostics.WaitMilliseconds.Any(pair => pair.Value > 0)) markdown.AppendLine("None.");
        markdown.AppendLine().AppendLine("### RMA create-path Query Store delta").AppendLine()
            .AppendLine("| Query | Executions | Average duration | Logical reads |")
            .AppendLine("|---|---:|---:|---:|");
        foreach (var query in diagnostics.RmaCreateQueries)
            markdown.AppendLine($"| `{query.Query.Replace("|", "\\|", StringComparison.Ordinal)}` | {query.Executions:N0} | {query.AverageMilliseconds:0.000} ms | {query.LogicalReads:N0} |");
        if (diagnostics.RmaCreateQueries.Count == 0) markdown.AppendLine("| No captured queries | 0 | 0 ms | 0 |");
        markdown.AppendLine()
            .AppendLine("## Errors by type").AppendLine();
        if (errors.Count == 0) markdown.AppendLine("None.");
        else foreach (var error in errors) markdown.AppendLine($"- `{error.Key}`: {error.Value}");
        markdown.AppendLine().AppendLine("## Notes").AppendLine();
        foreach (var note in notes) markdown.AppendLine($"- {note}");
        await File.WriteAllTextAsync(markdownPath, markdown.ToString(), cancellationToken);

        var csv = new StringBuilder("record_type,name,requests,successful,errors,throughput_per_second,p50_ms,p95_ms,p99_ms,value\n");
        foreach (var row in summaries)
            csv.Append("operation,").Append(Csv(row.Operation)).Append(',').Append(row.Requests).Append(',').Append(row.Successful).Append(',').Append(row.Errors).Append(',')
                .Append(row.Throughput.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.P50.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.P95.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.P99.ToString("0.000", CultureInfo.InvariantCulture)).AppendLine(",");
        foreach (var error in errors)
            csv.Append("error,").Append(Csv(error.Key)).Append(",,,,,,,,").Append(error.Value).AppendLine();
        CsvDiagnostic(csv, "document_changed", diagnostics.ExpectedDocumentConflicts);
        CsvDiagnostic(csv, "concurrency_exceptions", diagnostics.ConcurrencyExceptions);
        CsvDiagnostic(csv, "retries_exhausted", diagnostics.RetriesExhausted);
        CsvDiagnostic(csv, "number_series_lock_wait_ms", diagnostics.NumberSeriesLockWaitMilliseconds);
        CsvDiagnostic(csv, "lock_wait_delta_ms", diagnostics.LockWaitDeltaMilliseconds);
        CsvDiagnostic(csv, "connection_acquisitions", diagnostics.ConnectionAcquisitions);
        CsvDiagnostic(csv, "connection_acquisition_ms", diagnostics.ConnectionAcquisitionMilliseconds);
        CsvDiagnostic(csv, "connection_acquisition_max_ms", diagnostics.ConnectionAcquisitionMaximumMilliseconds);
        CsvDiagnostic(csv, "published_queue_maximum", diagnostics.PublishedQueueMaximum);
        CsvDiagnostic(csv, "deadlocks", diagnostics.Deadlocks);
        CsvDiagnostic(csv, "lost_updates", diagnostics.LostUpdates);
        await File.WriteAllTextAsync(csvPath, csv.ToString(), cancellationToken);
        return (markdownPath, csvPath);
    }

    private static void CsvDiagnostic(StringBuilder csv, string name, long value) =>
        csv.Append("diagnostic,").Append(Csv(name)).Append(",,,,,,,,").Append(value).AppendLine();

    private static void CsvDiagnostic(StringBuilder csv, string name, double value) =>
        csv.Append("diagnostic,").Append(Csv(name)).Append(",,,,,,,,").Append(value.ToString("0.000", CultureInfo.InvariantCulture)).AppendLine();

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
