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
        Assert.Equal(23, endpoints.Count);
        Assert.All(endpoints, endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == YojanaPolicy.IntegrationOperations));
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
