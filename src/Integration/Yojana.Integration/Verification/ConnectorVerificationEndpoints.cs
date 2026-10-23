using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Verification;

public static class ConnectorVerificationEndpoints
{
    public static IEndpointRouteBuilder MapConnectorVerificationEndpoints(this IEndpointRouteBuilder app)
    {
        var reads = app.MapGroup("/api/integration/connections/{connectionId:guid}/verification")
            .RequireAuthorization(YojanaPolicy.IntegrationOperations);
        reads.MapGet("/", (Guid connectionId, ConnectorAcceptanceService service,
            CancellationToken ct) => Invoke(() => service.OverviewAsync(connectionId, ct)));
        reads.MapGet("/runs/{runId:guid}", (Guid connectionId, Guid runId,
            ConnectorAcceptanceService service, CancellationToken ct) =>
            Invoke(() => service.DetailAsync(connectionId, runId, ct)));

        var control = app.MapGroup("/api/integration/connections/{connectionId:guid}/verification")
            .RequireAuthorization(YojanaPolicy.IntegrationOperations);
        control.MapPost("/runs", (Guid connectionId, StartAcceptanceRunRequest request,
            ClaimsPrincipal user, ConnectorAcceptanceService service, CancellationToken ct) =>
            Invoke(() => service.StartAsync(connectionId, request, Actor(user), ct)));
        control.MapPut("/runs/{runId:guid}/items/{itemId:guid}", (Guid connectionId, Guid runId,
            Guid itemId, MarkAcceptanceItemRequest request, ClaimsPrincipal user,
            ConnectorAcceptanceService service, CancellationToken ct) =>
            Invoke(() => service.MarkAsync(connectionId, runId, itemId, request, Actor(user), ct)));
        control.MapPost("/runs/{runId:guid}/imports", (Guid connectionId, Guid runId,
            ImportEdgeResultsRequest request, ClaimsPrincipal user, ConnectorAcceptanceService service,
            CancellationToken ct) => Invoke(() => service.ImportAsync(connectionId, runId, request,
                Actor(user), ct)));
        control.MapPost("/runs/{runId:guid}/complete", (Guid connectionId, Guid runId,
            ClaimsPrincipal user, ConnectorAcceptanceService service, CancellationToken ct) =>
            Invoke(() => service.CompleteAsync(connectionId, runId, Actor(user), ct)));

        var settingReads = app.MapGroup("/api/integration/acceptance")
            .RequireAuthorization(YojanaPolicy.IntegrationOperations);
        settingReads.MapGet("/settings", (AcceptanceGateSettings settings, CancellationToken ct) =>
            Invoke(() => settings.GetAsync(ct)));
        settingReads.MapPut("/settings", (UpdateAcceptanceGateSettings request,
                AcceptanceGateSettings settings, CancellationToken ct) => Invoke(() => settings.SetAsync(request, ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        return app;
    }

    private static string Actor(ClaimsPrincipal user) => user.FindFirst(YojanaClaim.UserId)?.Value
        ?? throw new ConnectorAcceptanceRuleException("audit_actor_required",
            "The authenticated user has no actor id.");

    private static async Task<IResult> Invoke<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (CommandRefusedException exception)
        {
            var status = exception.Rule.EndsWith("not_found", StringComparison.Ordinal)
                ? StatusCodes.Status404NotFound : StatusCodes.Status422UnprocessableEntity;
            return Results.Problem(title: exception.Rule, detail: exception.Message,
                statusCode: status, extensions: new Dictionary<string, object?>
                {
                    ["rule"] = exception.Rule,
                    ["unblockingStep"] = exception.UnblockingStep,
                });
        }
        catch (Yojana.Platform.Contracts.Tenancy.TenantSettingVersionConflictException exception)
        {
            return Results.Problem(title: "tenant_setting_version_conflict", detail: exception.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["rule"] = "tenant_setting_version_conflict" });
        }
    }
}
