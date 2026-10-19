using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class JournalledConnectionAuditTests
{
    [Fact]
    public async Task Immutable_provider_also_keeps_the_queryable_journal_with_independent_valid_chains()
    {
        var tenant = new TenantId(Guid.CreateVersion7());
        var journal = new InMemoryConnectionAuditSink();
        var archive = new InMemoryConnectionAuditSink();
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var input = new NewConnectionAuditEvent(tenant, Guid.CreateVersion7(), Guid.CreateVersion7(),
            ConnectionAuditEventType.ConnectionRegistered, "fictional-operator", now, "{}");
        var sink = new JournalledConnectionAuditSink(journal, archive);
        await sink.AppendAsync(input, default);
        var local = new List<ConnectionAuditEvent>();
        await foreach (var item in sink.ReadAsync(tenant, DateOnly.FromDateTime(now), DateOnly.FromDateTime(now), default))
            local.Add(item);
        var stored = new List<ConnectionAuditEvent>();
        await foreach (var item in archive.ReadAsync(tenant, DateOnly.FromDateTime(now), DateOnly.FromDateTime(now), default))
            stored.Add(item);
        Assert.Equal(input.EventId, Assert.Single(local).EventId);
        Assert.Equal(input.EventId, Assert.Single(stored).EventId);
        Assert.True(ConnectionAuditChain.Verify(local).IsValid);
        Assert.True(ConnectionAuditChain.Verify(stored).IsValid);
    }
}
