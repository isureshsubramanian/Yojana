using Yojana.Integration.Connections;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.DeadLetters;
using Yojana.Integration.Backfills;
using Yojana.Integration.Reconciliation;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Integration.Tests;

public sealed class IntegrationEndpointTests
{
    [Fact]
    public void Dead_letter_and_manual_task_endpoints_share_the_operator_policy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(YojanaPolicy.IntegrationOperations, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(YojanaPolicy.IntegrationIndicator, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(YojanaPolicy.TenantAdministrator, policy => policy.RequireAuthenticatedUser());
        });
        builder.Services.AddScoped<DeadLetterService>();
        builder.Services.AddScoped<ManualTaskDeskSettings>();
        builder.Services.AddScoped<ReconciliationService>();
        builder.Services.AddScoped<BackfillService>();
        var app = builder.Build();
        app.MapIntegrationOperationsEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/integration/",
                StringComparison.Ordinal) == true).ToList();
        Assert.Equal(25, endpoints.Count);
        Assert.All(endpoints, endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == YojanaPolicy.IntegrationOperations));
        var fieldMapWrite = endpoints.Single(endpoint => endpoint.RoutePattern.RawText ==
            "/api/integration/connections/{connectionId:guid}/cx01/field-map"
            && endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Contains("PUT"));
        Assert.Contains(fieldMapWrite.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == YojanaPolicy.TenantAdministrator);
        var confirm = endpoints.Single(endpoint => endpoint.RoutePattern.RawText ==
            "/api/integration/connections/{connectionId:guid}/onboarding-checklist/{itemCode}/confirm");
        Assert.Contains(confirm.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == YojanaPolicy.TenantAdministrator);
    }

    [Fact]
    public void Attention_indicator_keeps_its_broader_read_policy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(YojanaPolicy.IntegrationOperations, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(YojanaPolicy.IntegrationIndicator, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(YojanaPolicy.TenantAdministrator, policy => policy.RequireAuthenticatedUser());
        });
        builder.Services.AddScoped<DeadLetterService>();
        var app = builder.Build();
        app.MapIntegrationAttentionEndpoint();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(candidate => candidate.RoutePattern.RawText ==
                "/api/integration/attention-count");

        Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == YojanaPolicy.IntegrationIndicator);
    }

    [Fact]
    public void Connection_status_is_read_only_and_operator_commands_use_the_narrower_policy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<ConnectionCommands>();
        builder.Services.AddScoped<ConnectionQueries>();
        var app = builder.Build();
        app.MapConnectionEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToList();
        Assert.Equal(8, endpoints.Count);
        var status = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/integration/status");
        Assert.Contains(status.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            policy => policy.Policy == YojanaPolicy.IntegrationIndicator);
        Assert.All(endpoints.Where(endpoint => endpoint != status), endpoint =>
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                policy => policy.Policy == YojanaPolicy.IntegrationOperations));
        var registration = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText?.TrimEnd('/') == "/api/integration/connections"
            && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));
        Assert.Contains(registration.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            policy => policy.Policy == YojanaPolicy.TenantAdministrator);
        var smokeRead = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText!.EndsWith("/smoke-read", StringComparison.Ordinal));
        Assert.DoesNotContain(smokeRead.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            policy => policy.Policy == YojanaPolicy.TenantAdministrator);
        foreach (var type in new[] { typeof(RegisterConnection), typeof(ArchiveConnection) })
            Assert.DoesNotContain(type.GetProperties(), property => property.Name.Contains("Actor", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(TenantConnectionStatus).GetProperties(), property =>
            property.Name.Contains("Error", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Operator_action_requests_do_not_accept_an_actor_from_the_browser()
    {
        foreach (var type in new[] { typeof(MapAndRetryDeadLetterRequest),
                     typeof(CreateProvisionalAndRetryDeadLetterRequest), typeof(DiscardDeadLetterRequest),
                     typeof(CompleteManualTaskRequest), typeof(StartBackfillRequest) })
            Assert.DoesNotContain(type.GetProperties(), property => property.Name.Contains("Actor",
                StringComparison.OrdinalIgnoreCase));
        Assert.True(YojanaRole.IsKnown(YojanaRole.IntegrationOperator));
    }
}
