using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.ErpChange;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Integration.Tests;

public sealed class ErpChangeTests
{
    [Theory]
    [InlineData("  ST-44   Storage  Array ", "ST 44 STORAGE ARRAY")]
    [InlineData("Réseau  Switch", "RESEAU SWITCH")]
    public void Descriptions_are_normalised_in_one_place(string value, string expected) =>
        Assert.Equal(expected, ErpChangeNaturalKeys.Text(value));

    [Theory]
    [InlineData("GB 291 4471 06", "GB291447106")]
    [InlineData(" sw48 2506-1121 ", "SW4825061121")]
    [InlineData("in", "IN")]
    public void Structured_natural_keys_ignore_formatting(string value, string expected)
    {
        Assert.Equal(expected, ErpChangeNaturalKeys.TaxId(value));
        Assert.Equal(expected, ErpChangeNaturalKeys.Serial(value));
    }

    [Fact]
    public void Erp_change_endpoints_keep_each_write_on_its_owner_role()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<ErpChangeRunService>();
        builder.Services.AddScoped<ErpChangeMasterDataService>();
        builder.Services.AddScoped<ErpChangeStockService>();
        builder.Services.AddScoped<ErpChangeDualRunService>();
        builder.Services.AddScoped<ErpChangeCutoverService>();
        builder.Services.AddScoped<ErpChangeCleanPeriodSettings>();
        builder.Services.AddScoped<ErpChangeNotificationSettings>();
        builder.Services.AddScoped<ErpChangeWorkService>();
        var app = builder.Build();
        app.MapErpChangeEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToList();

        Assert.Equal(39, endpoints.Count);
        Assert.All(endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Contains("GET")), endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                policy => policy.Policy == YojanaPolicy.ErpChangeRead));
        Assert.All(endpoints.Where(endpoint => endpoint.RoutePattern.RawText!.Contains("/stock/")
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST")),
            endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                policy => policy.Policy == YojanaPolicy.InventoryController));
        Assert.All(endpoints.Where(endpoint => endpoint.RoutePattern.RawText!.Contains("/master-data/exceptions/")
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST")),
            endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                policy => policy.Policy == YojanaPolicy.ErpChangeMasterData));
        Assert.All(endpoints.Where(endpoint => endpoint.RoutePattern.RawText!.Contains("/document-divergences/")
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST")),
            endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                policy => policy.Policy == YojanaPolicy.IntegrationOperator));
        foreach (var route in new[]
                 {
                     "/api/integration/erp-change/runs",
                     "/api/integration/erp-change/runs/{runId:guid}/check",
                     "/api/integration/erp-change/runs/{runId:guid}/advance",
                     "/api/integration/erp-change/runs/{runId:guid}/cancel",
                 })
            Assert.Contains(endpoints.Single(endpoint => endpoint.RoutePattern.RawText == route)
                    .Metadata.GetOrderedMetadata<IAuthorizeData>(),
                policy => policy.Policy == YojanaPolicy.ErpChangeControl);
        var notificationsWrite = endpoints.Single(endpoint => endpoint.RoutePattern.RawText ==
            "/api/integration/erp-change/settings/notifications" && endpoint.Metadata
                .GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("PUT"));
        Assert.Contains(notificationsWrite.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            policy => policy.Policy == YojanaPolicy.ErpChangeControl);
    }

    [Fact]
    public void Browser_requests_never_supply_an_actor()
    {
        foreach (var type in new[]
                 {
                     typeof(StartErpChangeRequest), typeof(CancelErpChangeRequest),
                     typeof(StartReverseErpChangeRequest), typeof(UpdateErpChangeCleanPeriodRequest),
                     typeof(UpdateErpChangeNotificationSettings),
                     typeof(BeginCutoverRequest), typeof(RecordOutboundValidationRequest),
                     typeof(ConfirmFirstOutboundRequest), typeof(ConvertParkedOutboundRequest),
                     typeof(ExplainDivergenceRequest), typeof(RaiseDivergenceCorrectionRequest),
                     typeof(ResolveMasterDataExceptionRequest), typeof(MarkMasterDataNotNeededRequest),
                     typeof(ResolveStockDifferenceRequest), typeof(SignOffStockResidualRequest),
                 })
            Assert.DoesNotContain(type.GetProperties(), property =>
                property.Name.Contains("Actor", StringComparison.OrdinalIgnoreCase));
    }
}
