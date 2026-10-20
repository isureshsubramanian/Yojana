using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Coexistence;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Integration.Tests;

public sealed class CoexistenceTests
{
    [Fact]
    public void Coexistence_endpoints_need_integration_operations_and_settings_and_planning_need_an_administrator()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<CoexistenceSettings>();
        builder.Services.AddScoped<CoexistenceDriftService>();
        builder.Services.AddScoped<CoexistenceConflicts>();
        builder.Services.AddScoped<SyncControl>();
        builder.Services.AddScoped<ParallelRun>();
        var app = builder.Build();
        app.MapCoexistenceEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToList();
        Assert.Equal(11, endpoints.Count);
        Assert.All(endpoints, endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            policy => policy.Policy == YojanaPolicy.IntegrationOperations));
        var administrator = endpoints.Where(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(policy => policy.Policy == YojanaPolicy.TenantAdministrator))
            .Select(endpoint => endpoint.RoutePattern.RawText + " " + string.Join(',',
                endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods))
            .OrderBy(x => x).ToList();
        Assert.Equal([
            "/api/integration/connections/{connectionId:guid}/coexistence PUT",
            "/api/integration/connections/{connectionId:guid}/parallel-run POST",
        ], administrator);
        // Stopping must stay available to the integration operator: rollback cannot wait for an administrator.
        Assert.DoesNotContain(endpoints.Single(endpoint => endpoint.RoutePattern.RawText!.EndsWith("/stop-sync", StringComparison.Ordinal))
            .Metadata.GetOrderedMetadata<IAuthorizeData>(), policy => policy.Policy == YojanaPolicy.TenantAdministrator);
        foreach (var type in new[] { typeof(StopSync), typeof(ResumeSync), typeof(ResolveCoexistenceConflict),
                     typeof(UpdateCoexistenceSettings), typeof(StartParallelRun), typeof(ConfirmParallelRunItem) })
            Assert.DoesNotContain(type.GetProperties(), property => property.Name.Contains("Actor", StringComparison.OrdinalIgnoreCase)
                || property.Name.EndsWith("By", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(new[] { "CustomerCode", "CustomerCode" }, null)]
    [InlineData(new[] { "Customer Code" }, null)]
    [InlineData(new[] { "CustomerCode) or (1 eq 1" }, null)]
    [InlineData(new string[0], null)]
    [InlineData(new[] { "CustomerCode" }, new[] { "CustomerCode" })]
    public void Settings_reject_unsafe_or_ambiguous_field_lists(string[] watched, string[]? explaining)
    {
        var error = Assert.Throws<CoexistenceRuleException>(() =>
            new CoexistenceConnectionSettings(watched, explaining).Validate());
        Assert.Equal("coexistence_fields_invalid", error.Rule);
    }

    [Fact]
    public void Settings_accept_user_fields_and_bound_paging()
    {
        Assert.NotNull(new CoexistenceConnectionSettings(["CustomerCode", "U_SiteRef"], ["DeliveryCode"]).Validate());
        Assert.Equal("coexistence_paging_invalid", Assert.Throws<CoexistenceRuleException>(() =>
            new CoexistenceConnectionSettings(PageSize: 5000).Validate()).Rule);
    }

    [Fact]
    public void Explaining_hash_changes_only_with_the_explaining_fields_and_distinguishes_empty_from_missing()
    {
        CoexistenceRemoteRecord Record(string? delivery, string owner) => new("1", "card 1",
            new Dictionary<string, string?> { ["DeliveryCode"] = delivery, ["CustomerCode"] = owner });
        var fields = new[] { "DeliveryCode" };
        Assert.Equal(CoexistenceDriftService.ExplainingHash(Record("1", "A"), fields),
            CoexistenceDriftService.ExplainingHash(Record("1", "B"), fields));
        Assert.NotEqual(CoexistenceDriftService.ExplainingHash(Record("1", "A"), fields),
            CoexistenceDriftService.ExplainingHash(Record("2", "A"), fields));
        Assert.NotEqual(CoexistenceDriftService.ExplainingHash(Record("", "A"), fields),
            CoexistenceDriftService.ExplainingHash(Record(null, "A"), fields));
    }
}
