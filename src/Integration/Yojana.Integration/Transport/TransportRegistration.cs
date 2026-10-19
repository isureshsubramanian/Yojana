using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ErrorHandling;
using Yojana.Integration.Contracts;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;

namespace Yojana.Integration.Transport;

public static class TransportRegistration
{
    public static IHostApplicationBuilder AddIntegrationTransport(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<MessageQueueCredentials>();
        // The settings port uses Platform's context. Keep it behind the factory, outside Integration's EF handler graph.
        builder.Services.AddScoped(sp => new IntegrationTransportSettings(sp.GetRequiredService<ITenantSettings>(),
            sp.GetRequiredService<ITenantContext>(), sp.GetRequiredService<IIntegrationTransport>(),
            sp.GetRequiredService<MessageQueueCredentials>(), sp.GetRequiredService<IClock>()));
        builder.Services.AddScoped<IIntegrationRouteResolver>(sp => sp.GetRequiredService<IntegrationTransportSettings>());
        builder.Services.AddSingleton<IIntegrationTransportReceiver, IntegrationTransportReceiver>();
        builder.Services.AddSingleton(new MessageQueueReceiveOptions(
            !string.Equals(builder.Configuration["Integration:Transport:ReceiveInbound"], "false", StringComparison.OrdinalIgnoreCase),
            !string.Equals(builder.Configuration["Integration:Transport:ReceiveOutbound"], "false", StringComparison.OrdinalIgnoreCase)));
        builder.Services.TryAddSingleton<IIntegrationTransport, MessageQueueTransport>();
        builder.Services.AddScoped<IIntegrationTransportHealth, TenantIntegrationTransportHealth>();
        builder.Services.AddScoped<IntegrationRelayDispatcher>();
        builder.Services.AddSingleton(RecurringTenantJob.For<TransportReceiverJob>(
            TransportReceiverJob.JobName, TransportReceiverJob.Cron));
        builder.Services.ConfigureWolverine(options => options.OnException<IntegrationTransportUnavailableException>()
            .RetryWithCooldown(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(6)));
        return builder;
    }
}

public sealed class IntegrationTransportUnavailableException(string message) : Exception(message);
