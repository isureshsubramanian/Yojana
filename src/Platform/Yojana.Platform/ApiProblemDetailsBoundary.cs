using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Yojana.Platform;

/// <summary>Keeps every API failure on the Problem Details contract, including Development.</summary>
public static class ApiProblemDetailsBoundary
{
    public static IApplicationBuilder UseApiProblemDetails(this IApplicationBuilder app)
    {
        return app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), api =>
        {
            api.UseExceptionHandler(handler => handler.Run(async context =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await Results.Problem(
                    title: "Something went wrong",
                    detail: "Something went wrong (HTTP 500)",
                    statusCode: StatusCodes.Status500InternalServerError)
                    .ExecuteAsync(context);
            }));
            api.UseStatusCodePages(async statusContext =>
            {
                var response = statusContext.HttpContext.Response;
                await Results.Problem(
                    title: response.StatusCode switch
                    {
                        StatusCodes.Status401Unauthorized => "Authentication required",
                        StatusCodes.Status403Forbidden => "Access denied",
                        StatusCodes.Status404NotFound => "Not found",
                        _ => "Request failed",
                    },
                    detail: $"The request failed (HTTP {response.StatusCode}).",
                    statusCode: response.StatusCode)
                    .ExecuteAsync(statusContext.HttpContext);
            });
        });
    }
}
