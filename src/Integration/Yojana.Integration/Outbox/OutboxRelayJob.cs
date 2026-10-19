using Microsoft.EntityFrameworkCore;
using Quartz;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Outbox;

[DisallowConcurrentExecution]
public sealed class OutboxRelayJob(ITenantContextSetter setter, ITenantContext tenant, IntegrationDbContext db,
    OutboundRelay relay) : TenantJob(setter)
{
    public const string JobName = "integration.outbox-relay";
    public const string Cron = "0 * * * * ?";

    protected override async Task ExecuteForTenant(IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        var connections = await db.Connections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.Status == ConnectionStatus.Active)
            .OrderBy(x => x.ConnectionId).Select(x => x.ConnectionId).ToListAsync(cancellationToken);
        foreach (var connectionId in connections)
            await relay.RelayConnectionAsync(connectionId, cancellationToken);
    }
}
