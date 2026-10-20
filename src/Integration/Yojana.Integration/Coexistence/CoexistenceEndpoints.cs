using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Integration.Coexistence;

public static class CoexistenceEndpoints
{
    public static IEndpointRouteBuilder MapCoexistenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integration").RequireAuthorization(YojanaPolicy.IntegrationOperations);
        group.MapGet("/connections/{connectionId:guid}/coexistence", (Guid connectionId, CoexistenceSettings settings,
            CancellationToken ct) => Invoke(() => settings.ViewAsync(connectionId, ct)));
        group.MapPut("/connections/{connectionId:guid}/coexistence", (Guid connectionId, UpdateCoexistenceSettings request,
            ClaimsPrincipal user, CoexistenceSettings settings, CancellationToken ct) =>
                Invoke(() => settings.UpdateAsync(connectionId, request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        group.MapPost("/connections/{connectionId:guid}/coexistence/drift-scan", (Guid connectionId,
            CoexistenceDriftService drift, CancellationToken ct) => Invoke(() => drift.ScanAsync(connectionId, ct)));
        group.MapPost("/connections/{connectionId:guid}/stop-sync", (Guid connectionId, StopSync request,
            ClaimsPrincipal user, SyncControl control, CancellationToken ct) =>
                Invoke(() => control.StopAsync(connectionId, request, Actor(user), ct)));
        group.MapPost("/connections/{connectionId:guid}/resume-sync", (Guid connectionId, ResumeSync request,
            ClaimsPrincipal user, SyncControl control, CancellationToken ct) =>
                Invoke(() => control.ResumeAsync(connectionId, request, Actor(user), ct)));
        group.MapGet("/coexistence/conflicts", (Guid? connectionId, string? status, string? cursor, int? take,
            CoexistenceConflicts conflicts, CancellationToken ct) =>
                Invoke(() => conflicts.ListAsync(connectionId, status, cursor, take ?? 50, ct)));
        group.MapGet("/coexistence/conflicts/{conflictId:guid}", (Guid conflictId, CoexistenceConflicts conflicts,
            CancellationToken ct) => Invoke(() => conflicts.GetAsync(conflictId, ct)));
        group.MapPost("/coexistence/conflicts/{conflictId:guid}/resolve", (Guid conflictId,
            ResolveCoexistenceConflict request, ClaimsPrincipal user, CoexistenceConflicts conflicts, CancellationToken ct) =>
                Invoke(() => conflicts.ResolveAsync(conflictId, request, Actor(user), ct)));
        group.MapGet("/connections/{connectionId:guid}/parallel-run", (Guid connectionId, ParallelRun run,
            CancellationToken ct) => Invoke(() => run.ViewAsync(connectionId, ct)));
        group.MapPost("/connections/{connectionId:guid}/parallel-run", (Guid connectionId, StartParallelRun request,
            ClaimsPrincipal user, ParallelRun run, CancellationToken ct) =>
                Invoke(() => run.StartAsync(connectionId, request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        group.MapPost("/connections/{connectionId:guid}/parallel-run/{itemCode}/confirm", (Guid connectionId,
            string itemCode, ConfirmParallelRunItem request, ClaimsPrincipal user, ParallelRun run, CancellationToken ct) =>
                Invoke(() => run.ConfirmAsync(connectionId, itemCode, request, Actor(user), ct)));
        return app;
    }

    private static string Actor(ClaimsPrincipal user) => user.FindFirst(YojanaClaim.UserId)?.Value
        ?? throw new CoexistenceRuleException("audit_actor_required", "The authenticated user has no actor id.");

    private static async Task<IResult> Invoke<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (CoexistenceRuleException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message,
                statusCode: ex.Rule.EndsWith("not_found", StringComparison.Ordinal) ? 404 : 422,
                extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
        }
    }
}
