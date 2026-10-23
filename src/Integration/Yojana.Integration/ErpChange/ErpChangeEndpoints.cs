using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Integration.ErpChange;

public static class ErpChangeEndpoints
{
    public static IEndpointRouteBuilder MapErpChangeEndpoints(this IEndpointRouteBuilder app)
    {
        var reads = app.MapGroup("/api/integration/erp-change")
            .RequireAuthorization(YojanaPolicy.ErpChangeRead);
        reads.MapGet("/runs/active", async (ErpChangeRunService service, CancellationToken ct) =>
            await service.ActiveAsync(ct) is { } run ? Results.Ok(run) : Results.NotFound());
        reads.MapGet("/runs/{runId:guid}", (Guid runId, ErpChangeRunService service, CancellationToken ct) =>
            Invoke(() => service.DetailAsync(runId, ct)));
        reads.MapGet("/runs/{runId:guid}/master-data/exceptions", (Guid runId, string? kind,
            string? status, string? query, string? cursor, int? take, ErpChangeMasterDataService service,
            CancellationToken ct) => Invoke(() => service.ListAsync(runId,
                new(kind, status, query, cursor, take ?? 25), ct)));
        reads.MapGet("/runs/{runId:guid}/stock/differences", (Guid runId, string? reason,
            bool? liveCover, string? status, string? query, string? cursor, int? take,
            ErpChangeStockService service, CancellationToken ct) => Invoke(() => service.ListAsync(runId,
                new(reason, liveCover, status, query, cursor, take ?? 25), ct)));

        var control = app.MapGroup("/api/integration/erp-change")
            .RequireAuthorization(YojanaPolicy.ErpChangeControl);
        control.MapPost("/runs", (StartErpChangeRequest request, ClaimsPrincipal user,
            ErpChangeRunService service, CancellationToken ct) => Invoke(() =>
                service.StartAsync(request, Actor(user), ct)));
        control.MapPost("/runs/{runId:guid}/check", (Guid runId, ClaimsPrincipal user,
            ErpChangeRunService service, CancellationToken ct) => Invoke(() =>
                service.CheckAsync(runId, Actor(user), ct)));
        control.MapPost("/runs/{runId:guid}/advance", (Guid runId, ClaimsPrincipal user,
            ErpChangeRunService service, CancellationToken ct) => Invoke(() =>
                service.AdvanceAsync(runId, Actor(user), ct)));
        control.MapPost("/runs/{runId:guid}/cancel", (Guid runId, CancelErpChangeRequest request,
            ClaimsPrincipal user, ErpChangeRunService service, CancellationToken ct) => Invoke(() =>
                service.CancelAsync(runId, request, Actor(user), ct)));
        control.MapPost("/runs/{runId:guid}/master-data/remap", (Guid runId, ClaimsPrincipal user,
            ErpChangeMasterDataService service, CancellationToken ct) => Invoke(() =>
                service.RemapAsync(runId, Actor(user), ct)));

        var master = app.MapGroup("/api/integration/erp-change")
            .RequireAuthorization(YojanaPolicy.ErpChangeMasterData);
        master.MapPost("/runs/{runId:guid}/master-data/exceptions/{exceptionId:guid}/choose-candidate",
            (Guid runId, Guid exceptionId, ResolveMasterDataExceptionRequest request, ClaimsPrincipal user,
                ErpChangeMasterDataService service, CancellationToken ct) => Invoke(() => service.ResolveAsync(
                runId, exceptionId, request, Actor(user), candidateOnly: true, ct)));
        master.MapPost("/runs/{runId:guid}/master-data/exceptions/{exceptionId:guid}/map",
            (Guid runId, Guid exceptionId, ResolveMasterDataExceptionRequest request, ClaimsPrincipal user,
                ErpChangeMasterDataService service, CancellationToken ct) => Invoke(() => service.ResolveAsync(
                runId, exceptionId, request, Actor(user), candidateOnly: false, ct)));
        master.MapPost("/runs/{runId:guid}/master-data/exceptions/{exceptionId:guid}/not-needed",
            (Guid runId, Guid exceptionId, MarkMasterDataNotNeededRequest request, ClaimsPrincipal user,
                ErpChangeMasterDataService service, CancellationToken ct) => Invoke(() =>
                service.MarkNotNeededAsync(runId, exceptionId, request, Actor(user), ct)));

        var stock = app.MapGroup("/api/integration/erp-change")
            .RequireAuthorization(YojanaPolicy.InventoryController);
        stock.MapPost("/runs/{runId:guid}/stock/reconcile", (Guid runId, ClaimsPrincipal user,
            ErpChangeStockService service, CancellationToken ct) => Invoke(() =>
                service.ReconcileAsync(runId, Actor(user), ct)));
        stock.MapPost("/runs/{runId:guid}/stock/differences/{differenceId:guid}/resolve",
            (Guid runId, Guid differenceId, ResolveStockDifferenceRequest request, ClaimsPrincipal user,
                ErpChangeStockService service, CancellationToken ct) => Invoke(() => service.ResolveAsync(
                runId, differenceId, request, Actor(user), ct)));
        stock.MapPost("/runs/{runId:guid}/stock/sign-off", (Guid runId,
            SignOffStockResidualRequest request, ClaimsPrincipal user, ErpChangeStockService service,
            CancellationToken ct) => Invoke(() => service.SignOffAsync(runId, request, Actor(user), ct)));
        return app;
    }

    private static string Actor(ClaimsPrincipal user) => user.FindFirst(YojanaClaim.UserId)?.Value
        ?? throw new ErpChangeRuleException("audit_actor_required", "The authenticated user has no actor id.");

    private static async Task<IResult> Invoke<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (ErpChangeRuleException exception)
        {
            var status = exception.Rule.EndsWith("not_found", StringComparison.Ordinal)
                ? StatusCodes.Status404NotFound
                : exception.Rule.EndsWith("forbidden", StringComparison.Ordinal)
                    ? StatusCodes.Status403Forbidden : StatusCodes.Status422UnprocessableEntity;
            return Results.Problem(title: exception.Rule, detail: exception.Message, statusCode: status,
                extensions: new Dictionary<string, object?> { ["rule"] = exception.Rule });
        }
    }
}
