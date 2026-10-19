using System.Collections.Concurrent;
using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Security.Audit;

public sealed record LocalConnectionAuditOptions(string RootDirectory);

/// <summary>
/// Development sink using one JSON-lines file per tenant and UTC day. It only opens files in append mode; production
/// immutable storage can implement <see cref="IConnectionAuditSink"/> without changing event producers.
/// </summary>
public sealed class LocalAppendOnlyConnectionAuditSink(LocalConnectionAuditOptions options) : IConnectionAuditSink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<TenantId, SemaphoreSlim> tenantGates = new();

    public async Task<ConnectionAuditEvent> AppendAsync(NewConnectionAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        var gate = tenantGates.GetOrAdd(auditEvent.TenantId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var tenantDirectory = TenantDirectory(auditEvent.TenantId);
            Directory.CreateDirectory(tenantDirectory);
            var previous = await LastAsync(auditEvent.TenantId, cancellationToken);
            if (previous is not null && auditEvent.OccurredAt < previous.OccurredAt)
                throw new InvalidOperationException("Connection audit events must be appended in timestamp order.");

            var sealedEvent = ConnectionAuditChain.Seal(auditEvent, (previous?.Sequence ?? 0) + 1,
                previous?.RecordHash ?? ConnectionAuditChain.InitialHash);
            var path = Path.Combine(tenantDirectory, $"{auditEvent.OccurredAt:yyyy-MM-dd}.jsonl");
            var line = JsonSerializer.SerializeToUtf8Bytes(sealedEvent, Json);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(line, cancellationToken);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
            return sealedEvent;
        }
        finally
        {
            gate.Release();
        }
    }

    public async IAsyncEnumerable<ConnectionAuditEvent> ReadAsync(TenantId tenantId, DateOnly fromDate,
        DateOnly toDate, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (toDate < fromDate) throw new ArgumentException("The end date must not precede the start date.");
        var directory = TenantDirectory(tenantId);
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            var path = Path.Combine(directory, $"{date:yyyy-MM-dd}.jsonl");
            if (!File.Exists(path)) continue;
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                yield return JsonSerializer.Deserialize<ConnectionAuditEvent>(line, Json)
                    ?? throw new InvalidDataException($"Connection audit file '{Path.GetFileName(path)}' contains an empty record.");
            }
        }
    }

    private async Task<ConnectionAuditEvent?> LastAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        var directory = TenantDirectory(tenantId);
        if (!Directory.Exists(directory)) return null;
        var latest = Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault();
        if (latest is null) return null;

        string? last = null;
        await foreach (var line in File.ReadLinesAsync(latest, cancellationToken))
            if (!string.IsNullOrWhiteSpace(line)) last = line;
        return last is null ? null : JsonSerializer.Deserialize<ConnectionAuditEvent>(last, Json);
    }

    private string TenantDirectory(TenantId tenantId)
    {
        if (tenantId.IsEmpty) throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(options.RootDirectory))
            throw new InvalidOperationException("A local connection-audit root directory is required.");
        return Path.Combine(Path.GetFullPath(options.RootDirectory), tenantId.ToString());
    }
}
