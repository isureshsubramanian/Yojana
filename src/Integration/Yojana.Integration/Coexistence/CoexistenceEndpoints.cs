using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

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
        group.MapGet("/settings/warranty-contracts", (WarrantyContractMaintenanceSettings settings,
            CancellationToken ct) => Invoke(() => settings.GetAsync(ct)));
        group.MapPut("/connections/{connectionId:guid}/warranty-contracts", (Guid connectionId,
            UpdateWarrantyContractMaintenance request, ClaimsPrincipal user,
            WarrantyContractMaintenanceSettings settings, CancellationToken ct) =>
                Invoke(() => settings.UpdateAsync(connectionId, request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        group.MapPost("/connections/{connectionId:guid}/coexistence/drift-scan", (Guid connectionId,
            CoexistenceDriftService drift, CancellationToken ct) => Invoke(() => drift.ScanAsync(connectionId, ct)));
        group.MapPost("/connections/{connectionId:guid}/stop-sync", (Guid connectionId, StopSync request,
            ClaimsPrincipal user, SyncControl control, CancellationToken ct) =>
                Invoke(() => control.StopAsync(connectionId, request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        group.MapPost("/connections/{connectionId:guid}/resume-sync", (Guid connectionId, ResumeSync request,
            ClaimsPrincipal user, SyncControl control, CancellationToken ct) =>
                Invoke(() => control.ResumeAsync(connectionId, request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
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
        group.MapPut("/connections/{connectionId:guid}/parallel-run/month-end-close", (Guid connectionId,
            SetMonthEndClose request, ClaimsPrincipal user, ParallelRunDashboardService dashboard, CancellationToken ct) =>
                Invoke(() => dashboard.SetMonthEndCloseAsync(connectionId, request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        group.MapPut("/connections/{connectionId:guid}/parallel-run/handover/{kind}/plan", (Guid connectionId, string kind,
            PlanHandover request, ClaimsPrincipal user, ParallelRunDashboardService dashboard, CancellationToken ct) =>
                Invoke(() => dashboard.PlanHandoverAsync(connectionId, kind, request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        group.MapPost("/connections/{connectionId:guid}/parallel-run/handover/{kind}/confirm", (Guid connectionId,
            string kind, ConfirmHandover request, ClaimsPrincipal user, ParallelRunDashboardService dashboard,
            CancellationToken ct) => Invoke(() => dashboard.ConfirmHandoverAsync(connectionId, kind, request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        group.MapPost("/connections/{connectionId:guid}/go-live/{itemCode}/confirm", (Guid connectionId, string itemCode,
            ConfirmGoLiveItem request, ClaimsPrincipal user, ParallelRunDashboardService dashboard, CancellationToken ct) =>
                // The customer's own sign-off is a tenant administrator's to record; the other items are operations'.
                itemCode == GoLiveItemCodes.CustomerSignoff && !user.HasClaim(YojanaClaim.Role, YojanaRole.TenantAdministrator)
                    ? Task.FromResult(Results.Forbid())
                    : Invoke(() => dashboard.ConfirmGoLiveItemAsync(connectionId, itemCode, request, Actor(user), ct)));
        group.MapPost("/connections/{connectionId:guid}/go-live/record", (Guid connectionId, ClaimsPrincipal user,
            ParallelRunDashboardService dashboard, CancellationToken ct) =>
                Invoke(() => dashboard.IssueRecordAsync(connectionId, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        // Reads only, on the wider policy: migration leads see the dashboard from the migration console (P9-T11).
        var reads = app.MapGroup("/api/integration").RequireAuthorization(YojanaPolicy.ParallelRunRead);
        reads.MapGet("/connections/{connectionId:guid}/parallel-run/dashboard", (Guid connectionId,
            ParallelRunDashboardService dashboard, CancellationToken ct) => Invoke(() => dashboard.ViewAsync(connectionId, ct)));
        reads.MapGet("/go-live/records/{recordId:guid}", (Guid recordId, ParallelRunDashboardService dashboard,
            CancellationToken ct) => Invoke(() => dashboard.RecordAsync(recordId, ct)));
        reads.MapGet("/go-live/records/{recordId:guid}/csv", async (Guid recordId, ParallelRunDashboardService dashboard,
            CancellationToken ct) =>
        {
            try
            {
                return Results.File(await dashboard.RecordCsvAsync(recordId, ct), "text/csv", $"go-live-record-{recordId:N}.csv");
            }
            catch (CoexistenceRuleException ex)
            {
                return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status404NotFound,
                    extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
            }
        });
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
        catch (TenantSettingVersionConflictException)
        {
            return Results.Conflict(new { error = "warranty_contract_setting_changed" });
        }
        catch (CommandRefusedException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message,
                statusCode: ex.Rule.EndsWith("not_found", StringComparison.Ordinal) ? 404 : 422,
                extensions: new Dictionary<string, object?>
                {
                    ["rule"] = ex.Rule,
                    ["unblockingStep"] = ex.UnblockingStep,
                });
        }
    }
}
