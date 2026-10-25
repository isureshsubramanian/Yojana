using Microsoft.Extensions.Hosting;
// P14-B
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Yojana.Developer.Authentication;
using Yojana.Developer.Clients;
using Yojana.Developer.Data;
using Yojana.Developer.OpenApi;
using Yojana.Developer.PublicApi;
using Yojana.Platform;
using Yojana.Platform.Contracts.Security;
using Microsoft.Extensions.Options;
using Yojana.Developer.Bulk;
using Yojana.Developer.Delivery;
using Yojana.Developer.Events;
using Yojana.Developer.Webhooks;
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
        var configuredLimit = builder.Configuration[
            $"{ApiAuthenticationOptions.SectionName}:RefusalLimitPerMinute"];
        var authentication = new ApiAuthenticationOptions
        {
            RefusalLimitPerMinute = int.TryParse(configuredLimit, out var limit) ? limit : 20,
        };
        authentication.Validate();
        builder.Services.AddSingleton(authentication);
        builder.Services.AddSingleton<ApiAuthenticationRefusalLimiter>();
        builder.Services.AddScoped<SecurityEventTenantResolver>();
        // P14-B
        builder.Services.AddScoped<ApiClientService>();
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddScoped<IdempotencyService>();
        builder.Services.AddScoped<IdempotencyCleanup>();
        builder.Services.AddSingleton(RecurringTenantJob.For<IdempotencyCleanupJob>(
            IdempotencyCleanupJob.JobName, IdempotencyCleanupJob.Cron));
        builder.Services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
            PublicApiAuthenticationHandler>(PublicApiAuthentication.Scheme, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(PublicApiAuthentication.Policy, policy => policy
                .AddAuthenticationSchemes(PublicApiAuthentication.Scheme).RequireAuthenticatedUser()
                .RequireClaim(YojanaClaim.TenantId)
                .RequireClaim(PublicApiAuthentication.ClientIdClaim));
            options.AddPolicy(PublicApiAuthentication.DocumentationPolicy, policy => policy
                .AddAuthenticationSchemes(PublicApiAuthentication.Scheme,
                    CookieAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireAssertion(context =>
                    context.User.HasClaim(claim => claim.Type == PublicApiAuthentication.ClientIdClaim)
                    || context.User.HasClaim(YojanaClaim.Role, YojanaRole.TenantAdministrator)));
        });
        builder.Services.AddPublicApiRateLimiting();
        builder.Services.AddPublicOpenApi();
        builder.Services.Configure<Microsoft.AspNetCore.Http.ProblemDetailsOptions>(options =>
        {
            var existing = options.CustomizeProblemDetails;
            options.CustomizeProblemDetails = context =>
            {
                existing?.Invoke(context);
                if (context.HttpContext.Request.Path.StartsWithSegments(DeveloperEndpoints.PublicPrefix))
                    context.ProblemDetails.Extensions.TryAdd("code",
                        PublicApiHttp.CodeForStatus(context.ProblemDetails.Status
                            ?? context.HttpContext.Response.StatusCode));
            };
        });
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
        builder.Services.AddScoped<DeveloperWebhookSettingsService>();
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
