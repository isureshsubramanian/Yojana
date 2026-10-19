using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.DeadLetters;
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
        var app = builder.Build();
        app.MapIntegrationEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/integration/",
                StringComparison.Ordinal) == true).ToList();
        Assert.Equal(11, endpoints.Count);
        var indicator = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText ==
            "/api/integration/attention-count");
        Assert.Contains(indicator.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == YojanaPolicy.IntegrationIndicator);
        Assert.All(endpoints.Except([indicator]), endpoint => Assert.Contains(
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == YojanaPolicy.IntegrationOperations));
    }

    [Fact]
    public void Operator_action_requests_do_not_accept_an_actor_from_the_browser()
    {
        foreach (var type in new[] { typeof(MapAndRetryDeadLetterRequest),
                     typeof(CreateProvisionalAndRetryDeadLetterRequest), typeof(DiscardDeadLetterRequest),
                     typeof(CompleteManualTaskRequest) })
            Assert.DoesNotContain(type.GetProperties(), property => property.Name.Contains("Actor",
                StringComparison.OrdinalIgnoreCase));
        Assert.True(YojanaRole.IsKnown(YojanaRole.IntegrationOperator));
    }
}
