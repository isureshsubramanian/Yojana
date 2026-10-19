using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Integration.DeadLetters;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Integration;

public static class IntegrationEndpoints
{
    public static IEndpointRouteBuilder MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        var integration = app.MapGroup("/api/integration")
            .RequireAuthorization(YojanaPolicy.IntegrationOperations);
        integration.MapGet("/dead-letters", async (Guid? connectionId, string? reason, string? recordType,
            double? minimumAgeHours, double? maximumAgeHours, string? status, string? cursor, int? take,
            DeadLetterService service, CancellationToken ct) => Invoke(() => service.ListAsync(new(connectionId,
                reason, recordType, minimumAgeHours, maximumAgeHours, status, cursor, take ?? 50), ct)));
        integration.MapGet("/dead-letters/{itemId:guid}", async (Guid itemId, DeadLetterService service,
            CancellationToken ct) => await service.DetailAsync(itemId, ct) is { } detail
                ? Results.Ok(detail) : Results.NotFound());
        integration.MapPost("/dead-letters/{itemId:guid}/map-and-retry", (Guid itemId,
            MapAndRetryDeadLetterRequest request, ClaimsPrincipal user, DeadLetterService service,
            CancellationToken ct) => Invoke(() => service.MapAndRetryAsync(itemId, request, Actor(user), ct)));
        integration.MapPost("/dead-letters/{itemId:guid}/create-provisional-and-retry", (Guid itemId,
            CreateProvisionalAndRetryDeadLetterRequest request, ClaimsPrincipal user, DeadLetterService service,
            CancellationToken ct) => Invoke(() => service.CreateProvisionalAndRetryAsync(itemId, request,
                Actor(user), ct)));
        integration.MapPost("/dead-letters/{itemId:guid}/discard", (Guid itemId,
            DiscardDeadLetterRequest request, ClaimsPrincipal user, DeadLetterService service,
            CancellationToken ct) => Invoke(() => service.DiscardAsync(itemId, request.Reason, Actor(user), ct)));

        integration.MapGet("/manual-tasks", async (string? status, string? cursor, int? take,
            DeadLetterService service, CancellationToken ct) => Results.Ok(await service.ManualTasksAsync(status,
                cursor, take ?? 50, ct)));
        integration.MapGet("/manual-tasks/{taskId:guid}", async (Guid taskId, DeadLetterService service,
            CancellationToken ct) => await service.ManualTaskAsync(taskId, ct) is { } detail
                ? Results.Ok(detail) : Results.NotFound());
        integration.MapPost("/manual-tasks/{taskId:guid}/complete", (Guid taskId,
            CompleteManualTaskRequest request, ClaimsPrincipal user, DeadLetterService service,
            CancellationToken ct) => Invoke(() => service.CompleteManualTaskAsync(taskId, request,
                Actor(user), ct)));
        return app;
    }

    private static async Task<IResult> Invoke<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (DeadLetterRuleException ex)
        {
            var status = ex.Rule.EndsWith("not_found", StringComparison.Ordinal)
                ? StatusCodes.Status404NotFound : StatusCodes.Status422UnprocessableEntity;
            return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: status,
                extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
        }
    }

    private static string Actor(ClaimsPrincipal user) => user.FindFirst(YojanaClaim.UserId)?.Value
        ?? throw new DeadLetterRuleException("audit_actor_required", "The authenticated user has no actor id.");
}
