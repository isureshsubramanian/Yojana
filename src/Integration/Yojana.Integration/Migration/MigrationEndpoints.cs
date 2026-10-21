using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Integration.LegacyMigration.Templates;
using Yojana.Integration.LegacyMigration.Profiling;

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
        group.MapGet("/templates", (string? cursor, int? take, TemplateMappingService service,
            CancellationToken ct) => Invoke(() => service.ListAsync(cursor, take ?? 25, ct)));
        group.MapGet("/templates/{templateKey}", async (string templateKey, TemplateMappingService service,
            CancellationToken ct) => await service.DetailAsync(templateKey, ct) is { } result
                ? Results.Ok(result) : Results.NotFound());
        group.MapPost("/templates/propose", async (ProposeTemplatesRequest request,
            TemplateMappingService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Accepted(value: await service.ProposeAsync(request, ct));
            }
            catch (TemplateMappingRuleException ex) { return Problem(ex); }
        });
        group.MapPut("/templates/{templateKey}", (string templateKey, UpdateTemplateMappingRequest request,
            TemplateMappingService service, CancellationToken ct) =>
            Invoke(() => service.UpdateAsync(templateKey, request, ct)));
        group.MapPost("/templates/{templateKey}/approve", (string templateKey, ClaimsPrincipal user,
            TemplateMappingService service, CancellationToken ct) =>
            Invoke(() => service.ApproveAsync(templateKey, Actor(user), ct)));
        group.MapPost("/templates/approve-pattern", (ApprovePatternRequest request, ClaimsPrincipal user,
            TemplateMappingService service, CancellationToken ct) =>
            Invoke(() => service.ApprovePatternAsync(request.PatternKey, Actor(user), ct)));
        group.MapGet("/templates/keyword-rules", (TemplateKeywordRules settings, CancellationToken ct) =>
            Invoke(() => settings.GetAsync(ct)));
        group.MapPut("/templates/keyword-rules", (UpdateTemplateKeywordRulesRequest request,
            TemplateKeywordRules settings, CancellationToken ct) => Invoke(() => settings.UpdateAsync(request, ct)));
        group.MapGet("/batches/{batchId:guid}/profile", async (Guid batchId,
            MigrationProfileService service, CancellationToken ct) =>
            await service.GetAsync(batchId, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        group.MapGet("/batches/{batchId:guid}/profile/{classCode}/samples", async (Guid batchId, string classCode,
            MigrationProfileService service, CancellationToken ct) =>
        {
            try
            {
                return await service.SamplesAsync(batchId, classCode, ct) is { } result
                    ? Results.Ok(result) : Results.NotFound();
            }
            catch (MigrationRuleException ex) { return Problem(ex); }
        });
        group.MapPost("/batches/{batchId:guid}/profile", async (Guid batchId,
            MigrationProfileService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.EnqueueAsync(batchId, ct);
                return Results.Accepted($"/api/migration/batches/{batchId:D}/profile", result);
            }
            catch (MigrationRuleException ex) { return Problem(ex); }
        });
        group.MapGet("/profiling/outcomes", (MigrationProfileOutcomeSettings settings, CancellationToken ct) =>
            Invoke(() => settings.GetAsync(ct)));
        group.MapPut("/profiling/outcomes", async (UpdateMigrationProfileOutcomeSettings request,
            MigrationProfileOutcomeSettings settings, CancellationToken ct) =>
        {
            try { return Results.Ok(await settings.SetAsync(request, ct)); }
            catch (MigrationRuleException ex) { return Problem(ex); }
            catch (TenantSettingVersionConflictException ex)
            {
                return Results.Problem(title: "document_changed", detail: ex.Message, statusCode: 409,
                    extensions: new Dictionary<string, object?>
                    {
                        ["rule"] = "document_changed",
                        ["expectedVersion"] = ex.ExpectedVersion,
                        ["currentVersion"] = ex.CurrentVersion,
                    });
            }
        });
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
        catch (TemplateMappingRuleException ex) { return Problem(ex); }
        catch (TenantSettingVersionConflictException ex)
        {
            return Results.Problem(title: "tenant_setting_changed", detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["rule"] = "tenant_setting_changed" });
        }
    }

    private static IResult Problem(MigrationRuleException ex) => Results.Problem(title: ex.Rule,
        detail: ex.Message, statusCode: ex.Rule.EndsWith("not_found", StringComparison.Ordinal) ? 404 : 422,
        extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });

    private static IResult Problem(TemplateMappingRuleException ex) => Results.Problem(title: ex.Rule,
        detail: ex.Message, statusCode: ex.Rule.EndsWith("not_found", StringComparison.Ordinal) ? 404 : 422,
        extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
}
