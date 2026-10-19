using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class ConnectionWorkerIsolationTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));

    [Fact]
    public async Task Same_connection_waits_while_another_connection_has_an_independent_lane()
    {
        var isolation = new ConnectionWorkerIsolation();
        var firstKey = new ConnectionWorkerKey(Tenant,
            Guid.Parse("20000000-0000-0000-0000-000000000001"));
        var secondKey = new ConnectionWorkerKey(Tenant,
            Guid.Parse("20000000-0000-0000-0000-000000000002"));
        await using var first = await isolation.AcquireAsync(firstKey, CancellationToken.None);

        var sameConnection = isolation.AcquireAsync(firstKey, CancellationToken.None).AsTask();
        await using var otherConnection = await isolation.AcquireAsync(secondKey, CancellationToken.None);

        Assert.False(sameConnection.IsCompleted);
        await first.DisposeAsync();
        await using var unblocked = await sameConnection;
        Assert.Equal(firstKey, unblocked.WorkerKey);
    }

    [Fact]
    public async Task Different_tenants_never_share_a_lane()
    {
        var isolation = new ConnectionWorkerIsolation();
        var connection = Guid.Parse("20000000-0000-0000-0000-000000000001");
        await using var first = await isolation.AcquireAsync(new(Tenant, connection),
            CancellationToken.None);
        var otherTenant = new TenantId(Guid.Parse("10000000-0000-0000-0000-000000000002"));

        await using var second = await isolation.AcquireAsync(new(otherTenant, connection),
            CancellationToken.None);

        Assert.Equal(otherTenant, second.WorkerKey.TenantId);
    }
}
