using Microsoft.AspNetCore.Http;

namespace Yojana.Developer.PublicApi;

/// <summary>Adds the public contract's stable code when infrastructure rejects a request before an endpoint runs.</summary>
public sealed class PublicApiErrorCodeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);
        if (!context.Request.Path.StartsWithSegments(DeveloperEndpoints.PublicPrefix)
            || context.Response.StatusCode < 400 || context.Response.HasStarted
            || context.Response.ContentLength is not null || !string.IsNullOrEmpty(context.Response.ContentType))
            return;
        var code = PublicApiHttp.CodeForStatus(context.Response.StatusCode);
        await PublicApiHttp.Problem(code, $"The request failed (HTTP {context.Response.StatusCode}).",
            context.Response.StatusCode).ExecuteAsync(context);
    }
}
