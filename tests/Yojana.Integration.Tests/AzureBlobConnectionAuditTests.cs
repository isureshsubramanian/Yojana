using Azure.Storage.Blobs;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class AzureBlobConnectionAuditTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly Guid Connection = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly DateTime Now = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Sink_retries_append_position_conflicts_and_chains_across_daily_blobs()
    {
        var blobs = new FakeAppendBlobStore { ConflictsRemaining = 1 };
        var sink = new AzureBlobConnectionAuditSink(blobs);
        await sink.AppendAsync(Event(Guid.Parse("30000000-0000-0000-0000-000000000001"), Now),
            CancellationToken.None);
        await sink.AppendAsync(Event(Guid.Parse("30000000-0000-0000-0000-000000000002"), Now.AddDays(1)),
            CancellationToken.None);

        var records = new List<ConnectionAuditEvent>();
        await foreach (var item in sink.ReadAsync(Tenant, DateOnly.FromDateTime(Now),
                           DateOnly.FromDateTime(Now.AddDays(1)), CancellationToken.None)) records.Add(item);
        Assert.Equal(2, records.Count);
        Assert.True(ConnectionAuditChain.Verify(records).IsValid);
        Assert.Equal(records[0].RecordHash, records[1].PreviousRecordHash);
    }

    [Fact]
    public async Task Azurite_append_blob_round_trip_is_opt_in()
    {
        var connectionString = Environment.GetEnvironmentVariable("YOJANA_TEST_AZURITE");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var container = new BlobServiceClient(connectionString)
            .GetBlobContainerClient($"audit-{Guid.NewGuid():N}");
        await container.CreateIfNotExistsAsync();
        var sink = new AzureBlobConnectionAuditSink(new AzureAppendBlobStore(container));
        await sink.AppendAsync(Event(Guid.Parse("30000000-0000-0000-0000-000000000003"), Now),
            CancellationToken.None);

        var records = new List<ConnectionAuditEvent>();
        await foreach (var item in sink.ReadAsync(Tenant, DateOnly.FromDateTime(Now),
                           DateOnly.FromDateTime(Now), CancellationToken.None)) records.Add(item);
        Assert.Single(records);
    }

    private static NewConnectionAuditEvent Event(Guid eventId, DateTime occurredAt) => new(Tenant, eventId,
        Connection, ConnectionAuditEventType.ConnectionWrite, "system:connection", occurredAt,
        "{\"outcome\":\"accepted\"}");
}
