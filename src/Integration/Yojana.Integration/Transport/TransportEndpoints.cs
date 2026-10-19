using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Integration.Contracts;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Transport;

public static class TransportEndpoints
{
    public static IEndpointRouteBuilder MapIntegrationTransportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integration/settings/transport")
            .RequireAuthorization(YojanaPolicy.IntegrationOperations);
        group.MapGet("", async (IntegrationTransportSettings settings, CancellationToken ct) =>
            Results.Ok(await settings.ReadAsync(ct)));
        group.MapGet("/health", async (IIntegrationTransportHealth health, CancellationToken ct) =>
            Results.Ok(await health.ReadAsync(ct)));
        group.MapPut("", (UpdateIntegrationTransportSettings request, ClaimsPrincipal user,
            IntegrationTransportSettings settings, CancellationToken ct) => Invoke(() =>
            settings.UpdateAsync(request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        group.MapPost("/credentials", (StoreMessageQueueCredential request,
            MessageQueueCredentials credentials, CancellationToken ct) => Invoke(() => credentials.StoreAsync(request, ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        return app;
    }

    private static string Actor(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("The authenticated actor is required.");

    private static async Task<IResult> Invoke<T>(Func<Task<T>> operation)
    {
        try { return Results.Ok(await operation()); }
        catch (TenantSettingVersionConflictException)
        { return Results.Conflict(new { error = "transport_setting_changed" }); }
        catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        catch (IntegrationTransportUnavailableException)
        { return Results.Problem(statusCode: 503, title: "The message queue transport could not be configured."); }
        catch (InvalidOperationException)
        { return Results.Problem(statusCode: 503, title: "The message queue transport could not be configured."); }
    }
}
