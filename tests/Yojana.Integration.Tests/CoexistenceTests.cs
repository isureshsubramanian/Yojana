using System.Security.Claims;
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
    public async Task Coexistence_endpoints_need_integration_operations_and_stop_resume_settings_and_planning_need_an_administrator()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(YojanaPolicy.IntegrationOperations, policy => policy.RequireAuthenticatedUser()
                .RequireClaim(YojanaClaim.TenantId)
                .RequireClaim(YojanaClaim.Role, YojanaRole.TenantAdministrator,
                    YojanaRole.IntegrationOperator));
            options.AddPolicy(YojanaPolicy.TenantAdministrator, policy => policy.RequireAuthenticatedUser()
                .RequireClaim(YojanaClaim.TenantId)
                .RequireClaim(YojanaClaim.Role, YojanaRole.TenantAdministrator));
            options.AddPolicy(YojanaPolicy.ParallelRunRead, policy => policy.RequireAuthenticatedUser());
        });
        builder.Services.AddScoped<CoexistenceSettings>();
        builder.Services.AddScoped<WarrantyContractMaintenanceSettings>();
        builder.Services.AddScoped<CoexistenceDriftService>();
        builder.Services.AddScoped<CoexistenceConflicts>();
        builder.Services.AddScoped<SyncControl>();
        builder.Services.AddScoped<ParallelRun>();
        builder.Services.AddScoped<ParallelRunDashboardService>();
        var app = builder.Build();
        app.MapCoexistenceEndpoints();
        var all = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToList();
        Assert.Equal(21, all.Count);
        // The three P9-T11 reads sit on the wider ParallelRunRead policy (IntegrationEndpointTests checks them).
        var endpoints = all.Where(endpoint => !endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(policy => policy.Policy == YojanaPolicy.ParallelRunRead)).ToList();
        Assert.Equal(18, endpoints.Count);
        Assert.All(endpoints, endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            policy => policy.Policy == YojanaPolicy.IntegrationOperations));
        var administrator = endpoints.Where(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(policy => policy.Policy == YojanaPolicy.TenantAdministrator))
            .Select(endpoint => endpoint.RoutePattern.RawText + " " + string.Join(',',
                endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods))
            .OrderBy(x => x).ToList();
        Assert.Equal([
            "/api/integration/connections/{connectionId:guid}/coexistence PUT",
            "/api/integration/connections/{connectionId:guid}/go-live/record POST",
            "/api/integration/connections/{connectionId:guid}/parallel-run POST",
            "/api/integration/connections/{connectionId:guid}/parallel-run/handover/{kind}/confirm POST",
            "/api/integration/connections/{connectionId:guid}/parallel-run/handover/{kind}/plan PUT",
            "/api/integration/connections/{connectionId:guid}/parallel-run/month-end-close PUT",
            "/api/integration/connections/{connectionId:guid}/resume-sync POST",
            "/api/integration/connections/{connectionId:guid}/stop-sync POST",
            "/api/integration/connections/{connectionId:guid}/warranty-contracts PUT",
        ], administrator);
        foreach (var route in new[] { "/stop-sync", "/resume-sync" })
            Assert.Contains(endpoints.Single(endpoint => endpoint.RoutePattern.RawText!.EndsWith(route,
                    StringComparison.Ordinal)).Metadata.GetOrderedMetadata<IAuthorizeData>(),
                policy => policy.Policy == YojanaPolicy.TenantAdministrator);
        var authorisation = app.Services.GetRequiredService<IAuthorizationService>();
        ClaimsPrincipal Principal(string role) => new(new ClaimsIdentity([
            new Claim(YojanaClaim.TenantId, Guid.NewGuid().ToString("D")),
            new Claim(YojanaClaim.Role, role),
        ], "test"));
        Assert.True((await authorisation.AuthorizeAsync(Principal(YojanaRole.TenantAdministrator), null,
            YojanaPolicy.TenantAdministrator)).Succeeded);
        Assert.False((await authorisation.AuthorizeAsync(Principal(YojanaRole.IntegrationOperator), null,
            YojanaPolicy.TenantAdministrator)).Succeeded);
        Assert.False((await authorisation.AuthorizeAsync(Principal(YojanaRole.ServiceManager), null,
            YojanaPolicy.TenantAdministrator)).Succeeded);
        foreach (var type in new[] { typeof(StopSync), typeof(ResumeSync), typeof(ResolveCoexistenceConflict),
                     typeof(UpdateCoexistenceSettings), typeof(UpdateWarrantyContractMaintenance),
                     typeof(StartParallelRun), typeof(ConfirmParallelRunItem), typeof(SetMonthEndClose),
                     typeof(PlanHandover), typeof(ConfirmHandover), typeof(ConfirmGoLiveItem) })
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
