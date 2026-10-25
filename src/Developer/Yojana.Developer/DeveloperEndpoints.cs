using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
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
        _ = screens;
        return app;
    }
}
