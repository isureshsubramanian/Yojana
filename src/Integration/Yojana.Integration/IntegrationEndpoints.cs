using Yojana.Integration.Coexistence;
using Yojana.Integration.Connections;
using Yojana.Integration.Data.Entities;
using System.Security.Claims;
using Yojana.Integration.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using Yojana.Integration.DeadLetters;
using Yojana.Integration.Notifications;
using Yojana.Integration.Connectors.CX02;
using Yojana.Integration.Connectors.CX04;
using Yojana.Platform.Contracts.Security;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Reconciliation;
using Yojana.Integration.Backfills;
using Yojana.Integration.Gateway;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.ErpChange;
using Yojana.Integration.Verification;

namespace Yojana.Integration;

public static class IntegrationEndpoints
{
    public static IEndpointRouteBuilder MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapIntegrationTransportEndpoints();
        app.MapIntegrationNotificationEndpoints();
        app.MapCx02WebhookEndpoints();
        app.MapCx04WebhookEndpoints();
        app.MapCx04AuthorisationEndpoints();
        app.MapConnectionEndpoints();
        app.MapCoexistenceEndpoints();
        app.MapMigrationEndpoints();
        app.MapErpChangeEndpoints();
        app.MapConnectorVerificationEndpoints();
        Yojana.Integration.Relays.RelayEndpoints.MapRelayEndpoints(app);
        app.MapIntegrationAttentionEndpoint();
        app.MapCx01WebhookEndpoints();
        return app.MapIntegrationOperationsEndpoints();
    }

    internal static IEndpointRouteBuilder MapIntegrationAttentionEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/integration/attention-count", async (DeadLetterService service,
                CancellationToken ct) => Results.Ok(await service.AttentionAsync(ct)))
            .RequireAuthorization(YojanaPolicy.IntegrationIndicator);
        return app;
    }

    internal static IEndpointRouteBuilder MapIntegrationOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var integration = app.MapGroup("/api/integration")
            .RequireAuthorization(YojanaPolicy.IntegrationOperations);
        integration.MapGet("/dead-letters", async (Guid? connectionId, string? reason, string? recordType,
            double? minimumAgeHours, double? maximumAgeHours, string? status, string? query, string? cursor, int? take,
            DeadLetterService service, CancellationToken ct) => await Invoke(() => service.ListAsync(new(connectionId,
                reason, recordType, minimumAgeHours, maximumAgeHours, status, query, cursor, take ?? 50), ct)));
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

        integration.MapGet("/manual-tasks", async (string? status, string? desk, string? cursor, int? take,
            DeadLetterService service, CancellationToken ct) => Results.Ok(await service.ManualTasksAsync(status,
                desk, cursor, take ?? 50, ct)));
        integration.MapGet("/manual-tasks/{taskId:guid}", async (Guid taskId, DeadLetterService service,
            CancellationToken ct) => await service.ManualTaskAsync(taskId, ct) is { } detail
                ? Results.Ok(detail) : Results.NotFound());
        integration.MapPost("/manual-tasks/{taskId:guid}/complete", (Guid taskId,
            CompleteManualTaskRequest request, ClaimsPrincipal user, DeadLetterService service,
            CancellationToken ct) => Invoke(() => service.CompleteManualTaskAsync(taskId, request,
                Actor(user), ct)));
        integration.MapGet("/manual-task-desks", async (ManualTaskDeskSettings settings,
            CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)));
        integration.MapPut("/manual-task-desks", async (UpdateManualTaskDeskSettings request,
            ManualTaskDeskSettings settings, CancellationToken ct) => await Invoke(() => settings.SetAsync(request, ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        integration.MapGet("/connections/{connectionId:guid}/cx01/survey", async (Guid connectionId,
            string? cursor, int? take,
            [FromServices] Cx01SurveyService survey, CancellationToken ct) =>
            await survey.LatestPageAsync(connectionId, cursor, take ?? 100, ct) is { } result
                ? Results.Ok(result) : Results.NotFound());
        integration.MapPost("/connections/{connectionId:guid}/cx01/survey", async (Guid connectionId,
            [FromBody] Cx01SurveyOwnerAnswers request, [FromServices] Cx01SurveyService survey,
            CancellationToken ct) =>
        {
            _ = await survey.RunAsync(connectionId, request, ct);
            return Results.Ok(await survey.LatestPageAsync(connectionId, null, 100, ct));
        });
        integration.MapGet("/connections/{connectionId:guid}/cx01/field-map", (Guid connectionId,
            [FromServices] Cx01FieldMapService fieldMap, CancellationToken ct) =>
            Invoke(() => fieldMap.GetAsync(connectionId, ct)));
        integration.MapPut("/connections/{connectionId:guid}/cx01/field-map", (Guid connectionId,
                [FromBody] UpdateCx01FieldMap request, [FromServices] Cx01FieldMapService fieldMap,
                CancellationToken ct) => Invoke(() => fieldMap.SetAsync(connectionId, request, ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        integration.MapGet("/connections/{connectionId:guid}/cx01/documents", (Guid connectionId,
            [FromServices] Cx01DocumentsService documents, CancellationToken ct) =>
            Invoke(() => documents.GetAsync(connectionId, ct)));
        integration.MapPut("/connections/{connectionId:guid}/cx01/documents", (Guid connectionId,
                [FromBody] UpdateCx01Documents request, ClaimsPrincipal user,
                [FromServices] Cx01DocumentsService documents, CancellationToken ct) =>
            Invoke(() => documents.SetAsync(connectionId, request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        integration.MapGet("/connections/{connectionId:guid}/cx01/approval-handling", (Guid connectionId,
            [FromServices] Cx01ApprovalService approvals, CancellationToken ct) =>
            Invoke(() => approvals.GetAsync(connectionId, ct)));
        integration.MapPut("/connections/{connectionId:guid}/cx01/approval-handling", (Guid connectionId,
                [FromBody] UpdateCx01ApprovalHandling request, ClaimsPrincipal user,
                [FromServices] Cx01ApprovalService approvals, CancellationToken ct) =>
            Invoke(() => approvals.SetAsync(connectionId, request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        integration.MapGet("/connections/{connectionId:guid}/onboarding-checklist", (Guid connectionId,
            [FromServices] ConnectorOnboardingChecklistGenerator checklists, CancellationToken ct) =>
            Invoke(() => checklists.GenerateAsync(connectionId, ct)));
        integration.MapPost("/connections/{connectionId:guid}/onboarding-checklist/{itemCode}/confirm", (
                Guid connectionId, string itemCode, [FromBody] ConfirmOnboardingItemRequest request,
                ClaimsPrincipal user, [FromServices] ConnectorOnboardingConfirmations confirmations,
                CancellationToken ct) =>
            Invoke(() => confirmations.ConfirmAsync(connectionId, itemCode, request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        integration.MapPost("/reconciliation/runs", (RunReconciliationRequest request, ClaimsPrincipal user,
            ReconciliationService service, CancellationToken ct) => Invoke(async () =>
            new RunReconciliationResponse(await service.RunAsync(request.ConnectionId, request.RangeStart, request.RangeEnd,
                ReconciliationTrigger.OnDemand, includeEntityDays: true, Actor(user), ct))));
        integration.MapGet("/reconciliation/runs/{runId:guid}/entity-days", (Guid runId, string? cursor, int? take,
            ReconciliationService service, CancellationToken ct) => Invoke(() => service.EntityDaysAsync(runId, cursor, take ?? 12, ct)));
        integration.MapGet("/reconciliation/runs/{runId:guid}/locations", (Guid runId, string? cursor, int? take, bool? differences,
            ReconciliationService service, CancellationToken ct) => Invoke(() => service.LocationsAsync(runId, cursor, take ?? 12, differences ?? true, ct)));
        integration.MapGet("/reconciliation/runs", (Guid? connectionId, string? status, string? cursor,
            int? take, ReconciliationService service, CancellationToken ct) => Invoke(() =>
            service.ListAsync(new(connectionId, status, cursor, take ?? 50), ct)));
        integration.MapGet("/reconciliation/runs/{runId:guid}", async (Guid runId,
            ReconciliationService service, CancellationToken ct) =>
            await service.DetailAsync(runId, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        integration.MapPost("/reconciliation/runs/{runId:guid}/explain", (Guid runId, ExplainReconciliationRun request,
            ClaimsPrincipal user, ReconciliationService service, CancellationToken ct) =>
            Invoke(() => service.ExplainAsync(runId, request.Note, Actor(user), ct)));
        integration.MapPost("/reconciliation/location-checks", (RunLocationReconciliationRequest request,
            ClaimsPrincipal user, ReconciliationService service, CancellationToken ct) => Invoke(async () =>
        {
            var runId = await service.RunLocationChecksAsync(request.ConnectionId, Actor(user), ct);
            return await service.DetailAsync(runId, ct)
                ?? throw new InvalidOperationException("The completed reconciliation run was not found.");
        }));
        integration.MapGet("/backfills", (Guid? connectionId, string? entity, string? status,
            string? cursor, int? take, BackfillService service, CancellationToken ct) => Invoke(() =>
            service.ListAsync(new(connectionId, entity, status, cursor, take ?? 50), ct)));
        integration.MapGet("/backfills/{backfillId:guid}", async (Guid backfillId,
            BackfillService service, CancellationToken ct) =>
            await service.DetailAsync(backfillId, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        integration.MapPost("/backfills", (StartBackfillRequest request, ClaimsPrincipal user,
            BackfillService service, CancellationToken ct) => Invoke(async () =>
        {
            var id = await service.StartAsync(request, Actor(user), ct);
            return await service.DetailAsync(id, ct)
                ?? throw new InvalidOperationException("The created backfill was not found.");
        }));
        integration.MapPost("/backfills/{backfillId:guid}/pause", (Guid backfillId,
            ClaimsPrincipal user, BackfillService service, CancellationToken ct) => Invoke(async () =>
        {
            await service.PauseAsync(backfillId, Actor(user), ct);
            return await service.DetailAsync(backfillId, ct)
                ?? throw new InvalidOperationException("The paused backfill was not found.");
        }));
        integration.MapPost("/backfills/{backfillId:guid}/resume", (Guid backfillId,
            ClaimsPrincipal user, BackfillService service, CancellationToken ct) => Invoke(async () =>
        {
            await service.ResumeAsync(backfillId, Actor(user), ct);
            return await service.DetailAsync(backfillId, ct)
                ?? throw new InvalidOperationException("The resumed backfill was not found.");
        }));
        integration.MapPost("/backfills/{backfillId:guid}/cancel", (Guid backfillId,
            ClaimsPrincipal user, BackfillService service, CancellationToken ct) => Invoke(async () =>
        {
            await service.CancelAsync(backfillId, Actor(user), ct);
            return await service.DetailAsync(backfillId, ct)
                ?? throw new InvalidOperationException("The cancelled backfill was not found.");
        }));
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
        catch (ReconciliationRuleException ex)
        {
            var status = ex.Rule.EndsWith("not_found", StringComparison.Ordinal)
                ? StatusCodes.Status404NotFound : StatusCodes.Status422UnprocessableEntity;
            return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: status,
                extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
        }
        catch (Cx01FieldMapRuleException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
        }
        catch (Cx01DocumentRuleException ex)
        {
            return Results.Problem(title: "document_settings_invalid", detail: ex.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (Cx01ApprovalRuleException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
        }
        catch (OnboardingRuleException ex)
        {
            var status = ex.Rule.EndsWith("not_found", StringComparison.Ordinal)
                ? StatusCodes.Status404NotFound : StatusCodes.Status422UnprocessableEntity;
            return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: status,
                extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
        }
        catch (BackfillRuleException ex)
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
