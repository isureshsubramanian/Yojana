using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation;

public sealed record InteractiveReadOptions
{
    public InteractiveReadOptions(int commandTimeoutSeconds)
    {
        if (commandTimeoutSeconds is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(commandTimeoutSeconds), "Interactive read timeout must be from 1 to 60 seconds.");
        CommandTimeoutSeconds = commandTimeoutSeconds;
    }

    public int CommandTimeoutSeconds { get; }
}

/// <summary>
/// Gives interactive Serialisation reads a short SQL budget. A pathological read releases its pooled connection
/// instead of occupying it for the provider's 30-second default and starving unrelated requests.
/// </summary>
public sealed class InteractiveReadTimeoutMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, SerialisationDbContext db, InteractiveReadOptions options)
    {
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
        {
            await next(http);
            return;
        }

        var previous = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(options.CommandTimeoutSeconds);
        try
        {
            await next(http);
        }
        catch (SqlException exception) when (exception.Number == -2 && !http.Response.HasStarted)
        {
            http.Response.Clear();
            await Results.Problem(
                title: "interactive_read_timeout",
                detail: $"The read exceeded the {options.CommandTimeoutSeconds}-second interactive query limit. Narrow the filters and try again.",
                statusCode: StatusCodes.Status504GatewayTimeout,
                extensions: new Dictionary<string, object?>
                {
                    ["rule"] = "interactive_read_timeout",
                    ["unblockingStep"] = "narrow the identifier, document, status or date filters and retry",
                }).ExecuteAsync(http);
        }
        finally
        {
            db.Database.SetCommandTimeout(previous);
        }
    }
}

public static class InteractiveReadTimeoutExtensions
{
    public static IApplicationBuilder UseSerialisationInteractiveReads(this IApplicationBuilder app) =>
        app.UseMiddleware<InteractiveReadTimeoutMiddleware>();
}
