using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Security.Audit;

public sealed class AzureBlobConnectionAuditOptions
{
    public const string SectionName = "Integration:Security:Audit:AzureBlob";

    public required string ContainerName { get; init; }
    public Uri? ServiceUri { get; init; }
    public string? ConnectionString { get; init; }
    public string? ManagedIdentityClientId { get; init; }

    public static AzureBlobConnectionAuditOptions From(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var container = section["ContainerName"];
        if (string.IsNullOrWhiteSpace(container))
            throw new InvalidOperationException($"{SectionName}:ContainerName is required.");
        var connectionString = section["ConnectionString"];
        Uri? serviceUri = null;
        if (string.IsNullOrWhiteSpace(connectionString) &&
            (!Uri.TryCreate(section["ServiceUri"], UriKind.Absolute, out serviceUri) ||
             !StringComparer.OrdinalIgnoreCase.Equals(serviceUri.Scheme, Uri.UriSchemeHttps)))
            throw new InvalidOperationException($"{SectionName}:ServiceUri must be an absolute HTTPS URI when ConnectionString is absent.");
        return new AzureBlobConnectionAuditOptions
        {
            ContainerName = container,
            ServiceUri = serviceUri,
            ConnectionString = connectionString,
            ManagedIdentityClientId = section["ManagedIdentityClientId"],
        };
    }
}

internal sealed record AppendBlobContent(byte[] Content, long Length);

internal interface IAppendBlobStore
{
    IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken);
    Task<AppendBlobContent?> ReadAsync(string name, CancellationToken cancellationToken);
    Task<bool> AppendAsync(string name, ReadOnlyMemory<byte> content, long expectedLength,
        CancellationToken cancellationToken);
}

internal sealed class AzureAppendBlobStore(BlobContainerClient container) : IAppendBlobStore
{
    public async IAsyncEnumerable<string> ListAsync(string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix,
                           cancellationToken))
            yield return item.Name;
    }

    public async Task<AppendBlobContent?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        var blob = container.GetAppendBlobClient(name);
        try
        {
            var download = await blob.DownloadContentAsync(cancellationToken);
            return new AppendBlobContent(download.Value.Content.ToArray(), download.Value.Details.ContentLength);
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> AppendAsync(string name, ReadOnlyMemory<byte> content, long expectedLength,
        CancellationToken cancellationToken)
    {
        var blob = container.GetAppendBlobClient(name);
        await blob.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            await blob.AppendBlockAsync(stream, new AppendBlobAppendBlockOptions
            {
                Conditions = new AppendBlobRequestConditions { IfAppendPositionEqual = expectedLength },
            }, cancellationToken);
            return true;
        }
        catch (RequestFailedException error) when (error.Status is 409 or 412)
        {
            return false;
        }
    }
}

/// <summary>
/// Stores one append blob per tenant and UTC day. The adapter exposes no delete or overwrite operation; the operator
/// applies a time-based immutability policy to the container so platform enforcement continues beyond this process.
/// </summary>
public sealed class AzureBlobConnectionAuditSink : IConnectionAuditSink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IAppendBlobStore blobs;

    internal AzureBlobConnectionAuditSink(IAppendBlobStore blobs) => this.blobs = blobs;

    public async Task<ConnectionAuditEvent> AppendAsync(NewConnectionAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var latest = await ReadLatestAsync(auditEvent.TenantId, cancellationToken);
            if (latest.Event is not null && auditEvent.OccurredAt < latest.Event.OccurredAt)
                throw new InvalidOperationException("Connection audit events must be appended in timestamp order.");

            var sealedEvent = ConnectionAuditChain.Seal(auditEvent, (latest.Event?.Sequence ?? 0) + 1,
                latest.Event?.RecordHash ?? ConnectionAuditChain.InitialHash);
            var name = BlobName(auditEvent.TenantId, DateOnly.FromDateTime(auditEvent.OccurredAt));
            var existing = StringComparer.Ordinal.Equals(latest.Name, name) ? latest.ContentLength : 0;
            var line = JsonSerializer.SerializeToUtf8Bytes(sealedEvent, Json);
            var content = new byte[line.Length + 1];
            line.CopyTo(content, 0);
            content[^1] = (byte)'\n';
            if (await blobs.AppendAsync(name, content, existing, cancellationToken)) return sealedEvent;
        }

        throw new InvalidOperationException("The connection audit append could not obtain a stable blob position.");
    }

    public async IAsyncEnumerable<ConnectionAuditEvent> ReadAsync(TenantId tenantId, DateOnly fromDate,
        DateOnly toDate, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (toDate < fromDate) throw new ArgumentException("The end date must not precede the start date.");
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            var content = await blobs.ReadAsync(BlobName(tenantId, date), cancellationToken);
            if (content is null) continue;
            foreach (var item in Deserialize(content.Content))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }

    private async Task<(string? Name, long ContentLength, ConnectionAuditEvent? Event)> ReadLatestAsync(
        TenantId tenantId, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        await foreach (var name in blobs.ListAsync(Prefix(tenantId), cancellationToken)) names.Add(name);
        foreach (var name in names.OrderByDescending(item => item, StringComparer.Ordinal))
        {
            var content = await blobs.ReadAsync(name, cancellationToken);
            if (content is null || content.Length == 0) continue;
            return (name, content.Length, Deserialize(content.Content).LastOrDefault());
        }
        return (null, 0, null);
    }

    private static IEnumerable<ConnectionAuditEvent> Deserialize(byte[] content)
    {
        using var reader = new StringReader(System.Text.Encoding.UTF8.GetString(content));
        while (reader.ReadLine() is { } line)
            if (!string.IsNullOrWhiteSpace(line))
                yield return JsonSerializer.Deserialize<ConnectionAuditEvent>(line, Json)
                    ?? throw new InvalidDataException("A connection audit blob contains an empty record.");
    }

    private static string Prefix(TenantId tenantId)
    {
        if (tenantId.IsEmpty) throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        return $"tenant/{tenantId}/";
    }

    private static string BlobName(TenantId tenantId, DateOnly date) => $"{Prefix(tenantId)}{date:yyyy-MM-dd}.jsonl";
}
