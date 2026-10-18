using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Yojana.Platform.Tests;

public sealed class ConcurrencyRetryExhaustionMiddlewareTests
{
    [Fact]
    public async Task Exhausted_retry_is_counted_and_returned_as_a_typed_conflict()
    {
        var before = ConcurrencyRetryMetrics.Exhausted;
        var middleware = new ConcurrencyRetryExhaustionMiddleware(
            _ => throw new DbUpdateConcurrencyException("probe"),
            NullLogger<ConcurrencyRetryExhaustionMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Equal(before + 1, ConcurrencyRetryMetrics.Exhausted);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("concurrent_update_retry_exhausted", json.RootElement.GetProperty("rule").GetString());
        Assert.Equal("try again", json.RootElement.GetProperty("unblockingStep").GetString());
    }
}
