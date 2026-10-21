using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Integration.LegacyMigration;

public static class MigrationEndpoints
{
    public static IEndpointRouteBuilder MapMigrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/migration").RequireAuthorization(YojanaPolicy.TenantAdministrator);
        group.MapPost("/sources", (CreateMigrationSourceRequest request, ClaimsPrincipal user,
            MigrationSourceService service, CancellationToken ct) => Invoke(() =>
                service.CreateAsync(request, Actor(user), ct), created: true));
        group.MapGet("/sources", (string? cursor, int? take, MigrationSourceService service,
            CancellationToken ct) => Invoke(() => service.ListAsync(cursor, take ?? 50, ct)));
        group.MapPut("/sources", (UpdateMigrationSourceRequest request, ClaimsPrincipal user,
            MigrationSourceService service, CancellationToken ct) => Invoke(() =>
                service.UpdateAsync(request, Actor(user), ct)));
        group.MapPost("/sources/{sourceId:guid}/licence", (Guid sourceId,
            UpdateMigrationLicenceRequest request, ClaimsPrincipal user, MigrationSourceService service,
            CancellationToken ct) => Invoke(() => service.SetLicenceAsync(sourceId, request, Actor(user), ct)));
        group.MapPost("/sources/{sourceId:guid}/test", (Guid sourceId, MigrationSourceService service,
            CancellationToken ct) => Invoke(() => service.TestAsync(sourceId, ct)));
        group.MapPost("/batches", async (StartMigrationBatchRequest request, ClaimsPrincipal user,
            MigrationBatchService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.StartAsync(request, Actor(user), ct);
                return Results.Accepted($"/api/migration/batches/{result.BatchId:D}", result);
            }
            catch (MigrationRuleException ex) { return Problem(ex); }
        });
        group.MapGet("/batches", (string? cursor, int? take, MigrationBatchService service,
            CancellationToken ct) => Invoke(() => service.ListAsync(cursor, take ?? 50, ct)));
        group.MapGet("/batches/{batchId:guid}", async (Guid batchId, MigrationBatchService service,
            CancellationToken ct) => await service.DetailAsync(batchId, ct) is { } result
                ? Results.Ok(result) : Results.NotFound());
        return app;
    }

    private static string Actor(ClaimsPrincipal user) => user.FindFirst(YojanaClaim.UserId)?.Value
        ?? throw new MigrationRuleException("audit_actor_required", "The authenticated user has no actor id.");

    private static async Task<IResult> Invoke<T>(Func<Task<T>> action, bool created = false)
    {
        try
        {
            var result = await action();
            return created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
        }
        catch (MigrationRuleException ex) { return Problem(ex); }
    }

    private static IResult Problem(MigrationRuleException ex) => Results.Problem(title: ex.Rule,
        detail: ex.Message, statusCode: ex.Rule.EndsWith("not_found", StringComparison.Ordinal) ? 404 : 422,
        extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
}
