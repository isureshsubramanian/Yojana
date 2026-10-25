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
        return builder;
    }
}
