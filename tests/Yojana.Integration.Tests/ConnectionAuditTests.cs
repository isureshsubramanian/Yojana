using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class ConnectionAuditTests : IDisposable
{
    private static readonly TenantId Tenant = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly Guid Connection = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly DateTime DayOne = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"yojana-security-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task In_memory_sink_chains_records_and_verifier_detects_tampering()
    {
        var sink = new InMemoryConnectionAuditSink();
        await sink.AppendAsync(Event(Guid.Parse("30000000-0000-0000-0000-000000000001"), DayOne,
            ConnectionAuditEventType.CredentialSet), CancellationToken.None);
        await sink.AppendAsync(Event(Guid.Parse("30000000-0000-0000-0000-000000000002"), DayOne.AddMinutes(1),
            ConnectionAuditEventType.CredentialRotated), CancellationToken.None);
        var records = await ReadAsync(sink);

        Assert.True(ConnectionAuditChain.Verify(records).IsValid);
        Assert.DoesNotContain(Enum.GetNames<ConnectionAuditEventType>(), x => x.Contains("Reveal", StringComparison.Ordinal));
        var changed = records.ToArray();
        changed[0] = changed[0] with { DetailsJson = "{\"changed\":true}" };
        var verification = ConnectionAuditChain.Verify(changed);
        Assert.False(verification.IsValid);
        Assert.Equal(1, verification.InvalidPosition);
    }

    [Fact]
    public async Task Local_sink_uses_daily_append_only_files_and_one_continuous_chain()
    {
        var sink = new LocalAppendOnlyConnectionAuditSink(new LocalConnectionAuditOptions(directory));
        await sink.AppendAsync(Event(Guid.Parse("30000000-0000-0000-0000-000000000001"), DayOne,
            ConnectionAuditEventType.ConnectionRegistered), CancellationToken.None);
        await sink.AppendAsync(Event(Guid.Parse("30000000-0000-0000-0000-000000000002"), DayOne.AddDays(1),
            ConnectionAuditEventType.PermissionsChanged), CancellationToken.None);

        var tenantDirectory = Path.Combine(directory, Tenant.ToString());
        Assert.Equal(2, Directory.GetFiles(tenantDirectory, "*.jsonl").Length);
        var records = await ReadAsync(sink, DateOnly.FromDateTime(DayOne), DateOnly.FromDateTime(DayOne.AddDays(1)));
        Assert.True(ConnectionAuditChain.Verify(records).IsValid);
        Assert.Equal(records[0].RecordHash, records[1].PreviousRecordHash);
    }

    private static NewConnectionAuditEvent Event(Guid eventId, DateTime occurredAt,
        ConnectionAuditEventType eventType) => new(Tenant, eventId, Connection, eventType, "system:connection",
        occurredAt, "{\"outcome\":\"accepted\"}");

    private static async Task<List<ConnectionAuditEvent>> ReadAsync(IConnectionAuditSink sink,
        DateOnly? from = null, DateOnly? to = null)
    {
        var result = new List<ConnectionAuditEvent>();
        await foreach (var item in sink.ReadAsync(Tenant, from ?? DateOnly.FromDateTime(DayOne),
                           to ?? DateOnly.FromDateTime(DayOne), CancellationToken.None))
            result.Add(item);
        return result;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
