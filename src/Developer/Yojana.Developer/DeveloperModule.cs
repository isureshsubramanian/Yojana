using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Yojana.Developer.Bulk;
using Yojana.Developer.Data;
using Yojana.Developer.Delivery;
using Yojana.Developer.Events;
using Yojana.Developer.Webhooks;
using Yojana.Platform;
using Yojana.Platform.Scheduling;

namespace Yojana.Developer;

/// <summary>
/// Module registration (ADR-0018). Owns the <c>developer</c> schema through <see cref="DeveloperDbContext"/>: the public
/// event catalogue, webhook subscriptions and deliveries, API clients and the public API. It reads other modules only
/// through their contracts, so it can change or be removed without editing a connector or the serial ledger.
/// </summary>
public static class DeveloperModule
{
    public const string Schema = DeveloperDbContext.SchemaName;

    public static IHostApplicationBuilder AddDeveloperModule(this IHostApplicationBuilder builder)
    {
        builder.AddModuleDbContext<DeveloperDbContext>(Schema);
        // P14-A: public events, webhook subscriptions and delivery jobs.
        builder.Services.AddOptions<DeveloperWebhookOptions>()
            .Bind(builder.Configuration.GetSection(DeveloperWebhookOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<DeveloperWebhookOptions>, DeveloperWebhookOptionsValidator>();
        builder.Services.AddScoped<DeveloperWebhookSettings>();
        builder.Services.AddScoped<PublicEventTranslator>();
        builder.Services.AddScoped<PublicEventStager>();
        builder.Services.AddScoped<IWebhookDnsResolver, WebhookDnsResolver>();
        builder.Services.AddScoped<WebhookTargetGuard>();
        builder.Services.AddScoped<WebhookTransport>();
        builder.Services.AddScoped<WebhookSubscriptionService>();
        builder.Services.AddScoped<WebhookSubscriptionDispatchLock>();
        builder.Services.AddScoped<WebhookDeliveryDispatcher>();
        builder.Services.AddSingleton<WebhookDeliveryCoordinator>();
        builder.Services.AddHostedService(services => services.GetRequiredService<WebhookDeliveryCoordinator>());
        builder.Services.AddScoped<WebhookDeliveryQueries>();
        builder.Services.AddScoped<WebhookRetentionService>();
        builder.Services.AddScoped<BulkOperationUnitsQuery>();
        builder.Services.AddSingleton(RecurringTenantJob.For<WebhookDeliveryJob>(WebhookDeliveryJob.JobName,
            WebhookDeliveryJob.Cron));
        builder.Services.AddSingleton(RecurringTenantJob.For<WebhookRetentionJob>(WebhookRetentionJob.JobName,
            WebhookRetentionJob.Cron));
        // End P14-A.
        return builder;
    }
}
