using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Yojana.Platform;

/// <summary>Turns a concurrency exception that escaped Wolverine's retry policy into the platform's typed retry response.</summary>
public sealed class ConcurrencyRetryExhaustionMiddleware(RequestDelegate next, ILogger<ConcurrencyRetryExhaustionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (DbUpdateConcurrencyException exception) when (!context.Response.HasStarted)
        {
            ConcurrencyRetryMetrics.ObserveExhausted();
            logger.LogWarning(exception, "Optimistic-concurrency retries were exhausted for {Method} {Path}",
                context.Request.Method, context.Request.Path);
            await Results.Problem(
                title: "concurrent_update_retry_exhausted",
                detail: "The record kept changing while this request was being applied.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["rule"] = "concurrent_update_retry_exhausted",
                    ["unblockingStep"] = "try again",
                }).ExecuteAsync(context);
        }
    }
}
