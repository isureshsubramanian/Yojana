using Microsoft.AspNetCore.Builder;
// P14-B
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Developer.Authentication;
using Yojana.Developer.Clients;
using Yojana.Developer.OpenApi;
using Yojana.Developer.PublicApi;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Developer;

/// <summary>
/// Two route groups that must never share authentication (ADR-0018 D5): the settings screens under
/// <c>/api/developer</c> use the staff sign-in cookie; the public API under <c>/api/public/v1</c> accepts only an API
/// client's bearer token.
/// </summary>
public static class DeveloperEndpoints
{
    public const string ScreensPrefix = "/api/developer";
    public const string PublicPrefix = "/api/public/v1";

    public static IEndpointRouteBuilder MapDeveloperEndpoints(this IEndpointRouteBuilder app)
    {
        var screens = app.MapGroup(ScreensPrefix).RequireAuthorization(YojanaPolicy.TenantAdministrator);
        // P14-B
        screens.MapApiClientEndpoints();
        app.MapPublicApiEndpoints();
        app.MapGet(PublicPrefix + "/openapi.json", PublicOpenApi.DocumentAsync)
            .RequireAuthorization(PublicApiAuthentication.DocumentationPolicy)
            .WithSummary("Download the public API v1 OpenAPI document.");
        return app;
    }
}
