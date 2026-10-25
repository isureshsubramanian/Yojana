using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Yojana.Developer.PublicApi;

public sealed record PublicApiScopeMetadata(string Scope);
public sealed record PublicApiDeprecationMetadata(DateTime Sunset);

public sealed class PublicApiScopeFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var requirement = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<PublicApiScopeMetadata>();
        if (requirement is null || context.HttpContext.User.Claims.Any(claim =>
                claim.Type == Authentication.PublicApiAuthentication.ScopeClaim
                && string.Equals(claim.Value, requirement.Scope, StringComparison.Ordinal)))
            return next(context);
        return ValueTask.FromResult<object?>(PublicApiHttp.Problem("insufficient_scope",
            $"The bearer token does not grant '{requirement?.Scope}'.", StatusCodes.Status403Forbidden));
    }
}

public sealed class PublicApiDeprecationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<PublicApiDeprecationMetadata>() is { } metadata)
        {
            context.HttpContext.Response.Headers["Deprecation"] = "true";
            context.HttpContext.Response.Headers["Sunset"] = metadata.Sunset.ToUniversalTime().ToString("R");
        }
        return await next(context);
    }
}

public static class PublicApiEndpointConventions
{
    public static RouteHandlerBuilder RequirePublicScope(this RouteHandlerBuilder endpoint, string scope) =>
        endpoint.WithMetadata(new PublicApiScopeMetadata(scope)).AddEndpointFilter<PublicApiScopeFilter>();
}
