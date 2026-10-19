using Microsoft.Extensions.Logging;
using Quartz;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Transport;

[DisallowConcurrentExecution]
public sealed class TransportReceiverJob(ITenantContextSetter setter, ITenantContext tenant,
    IntegrationTransportSettings settings, IIntegrationTransport transport, ILogger<TransportReceiverJob> logger)
    : TenantJob(setter)
{
    public const string JobName = "integration.transport-receivers";
    public const string Cron = "0 * * * * ?";

    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        RestoreAsync(cancellationToken);

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        var configured = await settings.ReadAsync(cancellationToken);
        IntegrationTransportUnavailableException? currentFailure = null;
        foreach (var route in configured.DrainingRoutes.Prepend(configured.Route).Distinct())
        {
            try { await transport.EnsureReceivingAsync(tenant.TenantId, route, cancellationToken); }
            catch (IntegrationTransportUnavailableException exception)
            {
                // A retired namespace becoming unavailable must not block another route's receiver after restart.
                logger.LogWarning("A message queue receiver is unavailable for route {Route}.", route.AddressPrefix(tenant.TenantId));
                if (route == configured.Route) currentFailure = exception;
            }
        }
        if (currentFailure is not null) throw currentFailure;
    }
}
