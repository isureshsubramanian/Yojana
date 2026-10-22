using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Integration.LegacyMigration.Templates;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Reconstruction;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Integration.LegacyMigration.Validation;
using Yojana.Integration.LegacyMigration.Apply;
using Yojana.Integration.LegacyMigration.LinkOverrides;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.LegacyMigration;

public static class MigrationEndpoints
{
    public static IEndpointRouteBuilder MapMigrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/migration");
        var admin = group.MapGroup("").RequireAuthorization(YojanaPolicy.TenantAdministrator);
        var review = group.MapGroup("").RequireAuthorization(YojanaPolicy.MigrationReview);
        admin.MapPost("/sources", (CreateMigrationSourceRequest request, ClaimsPrincipal user,
            MigrationSourceService service, CancellationToken ct) => Invoke(() =>
                service.CreateAsync(request, Actor(user), ct), created: true));
        admin.MapGet("/sources", (string? cursor, int? take, MigrationSourceService service,
            CancellationToken ct) => Invoke(() => service.ListAsync(cursor, take ?? 50, ct)));
        admin.MapPut("/sources", (UpdateMigrationSourceRequest request, ClaimsPrincipal user,
            MigrationSourceService service, CancellationToken ct) => Invoke(() =>
                service.UpdateAsync(request, Actor(user), ct)));
        admin.MapPost("/sources/{sourceId:guid}/licence", (Guid sourceId,
            UpdateMigrationLicenceRequest request, ClaimsPrincipal user, MigrationSourceService service,
            CancellationToken ct) => Invoke(() => service.SetLicenceAsync(sourceId, request, Actor(user), ct)));
        admin.MapPost("/sources/{sourceId:guid}/test", (Guid sourceId, MigrationSourceService service,
            CancellationToken ct) => Invoke(() => service.TestAsync(sourceId, ct)));
        admin.MapPost("/batches", async (StartMigrationBatchRequest request, ClaimsPrincipal user,
            MigrationBatchService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.StartAsync(request, Actor(user), ct);
                return Results.Accepted($"/api/migration/batches/{result.BatchId:D}", result);
            }
            catch (MigrationRuleException ex) { return Problem(ex); }
        });
        review.MapGet("/batches", (string? cursor, int? take, MigrationBatchService service,
            CancellationToken ct) => Invoke(() => service.ListAsync(cursor, take ?? 50, ct)));
        review.MapGet("/batches/{batchId:guid}", async (Guid batchId, MigrationBatchService service,
            CancellationToken ct) => await service.DetailAsync(batchId, ct) is { } result
                ? Results.Ok(result) : Results.NotFound());
        review.MapGet("/templates", (string? state, bool? deletedInSource, string? cursor, int? take,
            TemplateMappingService service, CancellationToken ct) =>
            Invoke(() => service.ListAsync(state, deletedInSource, cursor, take ?? 25, ct)));
        review.MapGet("/templates/summary", (TemplateMappingService service, CancellationToken ct) =>
            Invoke(() => service.SummaryAsync(ct)));
        review.MapGet("/templates/catalogue", (TemplateMappingService service, CancellationToken ct) =>
            Invoke(() => service.CatalogueAsync(ct)));
        review.MapGet("/templates/{templateKey}", async (string templateKey, TemplateMappingService service,
            CancellationToken ct) => await service.DetailAsync(templateKey, ct) is { } result
                ? Results.Ok(result) : Results.NotFound());
        admin.MapPost("/templates/propose", async (ProposeTemplatesRequest request,
            TemplateMappingService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Accepted(value: await service.ProposeAsync(request, ct));
            }
            catch (TemplateMappingRuleException ex) { return Problem(ex); }
        });
        review.MapPut("/templates/{templateKey}", (string templateKey, UpdateTemplateMappingRequest request,
            TemplateMappingService service, CancellationToken ct) =>
            Invoke(() => service.UpdateAsync(templateKey, request, ct)));
        review.MapPost("/templates/{templateKey}/approve", (string templateKey, ClaimsPrincipal user,
            TemplateMappingService service, CancellationToken ct) =>
            Invoke(() => service.ApproveAsync(templateKey, Actor(user), ct)));
        review.MapPost("/templates/approve-pattern", (ApprovePatternRequest request, ClaimsPrincipal user,
            TemplateMappingService service, CancellationToken ct) =>
            Invoke(() => service.ApprovePatternAsync(request.PatternKey, Actor(user), ct)));
        admin.MapGet("/templates/keyword-rules", (TemplateKeywordRules settings, CancellationToken ct) =>
            Invoke(() => settings.GetAsync(ct)));
        admin.MapPut("/templates/keyword-rules", (UpdateTemplateKeywordRulesRequest request,
            TemplateKeywordRules settings, CancellationToken ct) => Invoke(() => settings.UpdateAsync(request, ct)));
        review.MapGet("/batches/{batchId:guid}/profile", async (Guid batchId,
            MigrationProfileService service, CancellationToken ct) =>
            await service.GetAsync(batchId, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        review.MapGet("/batches/{batchId:guid}/profile/{classCode}/samples", async (Guid batchId, string classCode,
            MigrationProfileService service, CancellationToken ct) =>
        {
            try
            {
                return await service.SamplesAsync(batchId, classCode, ct) is { } result
                    ? Results.Ok(result) : Results.NotFound();
            }
            catch (MigrationRuleException ex) { return Problem(ex); }
        });
        admin.MapPost("/batches/{batchId:guid}/profile", async (Guid batchId,
            MigrationProfileService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.EnqueueAsync(batchId, ct);
                return Results.Accepted($"/api/migration/batches/{batchId:D}/profile", result);
            }
            catch (MigrationRuleException ex) { return Problem(ex); }
        });
        admin.MapGet("/profiling/outcomes", (MigrationProfileOutcomeSettings settings, CancellationToken ct) =>
            Invoke(() => settings.GetAsync(ct)));
        admin.MapPut("/profiling/outcomes", async (UpdateMigrationProfileOutcomeSettings request,
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
        admin.MapPost("/batches/{batchId:guid}/canonicalise", async (Guid batchId,
            CanonicalisationService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.EnqueueAsync(batchId, ct);
                return Results.Accepted($"/api/migration/batches/{batchId:D}/canonicalisation", result);
            }
            catch (MigrationRuleException ex) { return Problem(ex); }
        });
        review.MapGet("/batches/{batchId:guid}/canonicalisation", async (Guid batchId,
            CanonicalisationService service, CancellationToken ct) =>
            await service.GetAsync(batchId, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        admin.MapPost("/batches/{batchId:guid}/reconstruct", async (Guid batchId,
            ReconstructionService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.EnqueueAsync(batchId, ct);
                return Results.Accepted($"/api/migration/batches/{batchId:D}/reconstruction", result);
            }
            catch (MigrationRuleException ex) { return Problem(ex); }
        });
        review.MapGet("/batches/{batchId:guid}/reconstruction", async (Guid batchId,
            ReconstructionService service, CancellationToken ct) =>
            await service.GetAsync(batchId, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        review.MapGet("/candidates/{candidateId:guid}/history", async (Guid candidateId, string? cursor,
            int? take, ReconstructionService service, CancellationToken ct) =>
        {
            try
            {
                return await service.HistoryAsync(candidateId, cursor, take ?? 100, ct) is { } result
                    ? Results.Ok(result) : Results.NotFound();
            }
            catch (MigrationRuleException ex) { return Problem(ex); }
        });
        admin.MapPost("/batches/{batchId:guid}/map", async (Guid batchId, MappingService service,
            CancellationToken ct) =>
        {
            try
            {
                var result = await service.EnqueueAsync(batchId, ct);
                return Results.Accepted($"/api/migration/batches/{batchId:D}/map", result);
            }
            catch (MappingRuleException ex) { return Problem(ex); }
        });
        review.MapGet("/batches/{batchId:guid}/map", async (Guid batchId, MappingService service,
            CancellationToken ct) => await service.GetAsync(batchId, ct) is { } result
                ? Results.Ok(result) : Results.NotFound());
        review.MapGet("/batches/{batchId:guid}/map/gaps", (Guid batchId, string? kind, string? cursor,
            int? take, MappingService service, CancellationToken ct) =>
            InvokeMapping(() => service.GapsAsync(batchId, kind, cursor, take ?? 50, ct)));
        review.MapGet("/plan/units/{planUnitId:guid}", async (Guid planUnitId, MappingService service,
            CancellationToken ct) => await service.UnitAsync(planUnitId, ct) is { } result
                ? Results.Ok(result) : Results.NotFound());
        admin.MapPost("/batches/{batchId:guid}/apply", async (Guid batchId, StartApplyRequest request,
            ClaimsPrincipal user, ApplyService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.EnqueueAsync(batchId, request, Actor(user), ct);
                return Results.Accepted($"/api/migration/batches/{batchId:D}/apply", result);
            }
            catch (ApplyRuleException ex) { return Problem(ex); }
        });
        review.MapGet("/batches/{batchId:guid}/apply", async (Guid batchId, ApplyService service,
            CancellationToken ct) => await service.GetAsync(batchId, ct) is { } result
                ? Results.Ok(result) : Results.NotFound());
        review.MapGet("/loads/{loadId:guid}/refusals", async (Guid loadId, string? cursor, int? take,
            ApplyService service, CancellationToken ct) =>
            Results.Ok(await service.RefusalsAsync(loadId, cursor, take ?? 50, ct)));
        admin.MapPost("/loads/{loadId:guid}/rollback", async (Guid loadId, ClaimsPrincipal user,
            ApplyService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.RollBackAsync(loadId, Actor(user), ct)); }
            catch (ApplyRuleException ex) { return Problem(ex); }
        });
        review.MapGet("/mapping/settings", (MappingSettings mappingSettings, CancellationToken ct) =>
            InvokeMapping(() => mappingSettings.GetAsync(ct)));
        admin.MapPut("/mapping/settings", (UpdateMappingSettingsRequest request,
            MappingSettings mappingSettings, CancellationToken ct) =>
            InvokeMapping(() => mappingSettings.SetAsync(request, ct)));
        review.MapGet("/batches/{batchId:guid}/links/export.csv", async (Guid batchId,
            LinkOverrideService service, CancellationToken ct) =>
        {
            try
            {
                return Results.File(await service.ExportAsync(batchId, ct), "text/csv; charset=utf-8",
                    "migration-replacement-links.csv");
            }
            catch (LinkOverrideRuleException ex) { return Problem(ex); }
        });
        review.MapPost("/link-overrides/upload", async (HttpRequest request, ClaimsPrincipal user,
            LinkOverrideService service, CancellationToken ct) =>
        {
            try
            {
                if (!request.HasFormContentType)
                    throw new LinkOverrideRuleException("migration_link_override_multipart_required",
                        "Upload the CSV as multipart form data.");
                var form = await request.ReadFormAsync(ct);
                var file = form.Files.Count == 1 ? form.Files[0] : throw new LinkOverrideRuleException(
                    "migration_link_override_file_required", "Upload exactly one CSV file.");
                await using var stream = file.OpenReadStream();
                return Results.Ok(await service.UploadAsync(stream, file.Length, file.FileName, Actor(user), ct));
            }
            catch (LinkOverrideRuleException ex) { return Problem(ex); }
        }).DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestFormLimitsAttribute
        {
            MultipartBodyLengthLimit = LinkOverrideService.MaxFileBytes + 65_536,
        });
        review.MapGet("/link-overrides", (string? cursor, int? take, LinkOverrideService service,
            CancellationToken ct) => InvokeLinkOverrides(() => service.ListAsync(cursor, take ?? 50, ct)));
        review.MapDelete("/link-overrides", ([Microsoft.AspNetCore.Mvc.FromBody] DeleteLinkOverridesRequest request,
            LinkOverrideService service, CancellationToken ct) =>
            InvokeLinkOverrides(() => service.DeleteAllAsync(request, ct)));
        admin.MapPost("/batches/{batchId:guid}/validate", async (Guid batchId,
            StartValidationRequest request, ValidationService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.EnqueueAsync(batchId, request.Rehearsal, ct);
                return Results.Accepted($"/api/migration/batches/{batchId:D}/validation", result);
            }
            catch (ValidationRuleException ex) { return Problem(ex); }
        });
        review.MapGet("/batches/{batchId:guid}/validation", async (Guid batchId,
            ValidationService service, CancellationToken ct) =>
            await service.GetAsync(batchId, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        review.MapGet("/batches/{batchId:guid}/validation/export.csv", (Guid batchId, HttpContext http,
            ValidationService service, [Microsoft.AspNetCore.Mvc.FromServices] ITabularExporter exporter,
            CancellationToken ct) => ValidationExport(batchId, "csv", http, service, exporter, ct));
        review.MapGet("/batches/{batchId:guid}/validation/export.xlsx", (Guid batchId, HttpContext http,
            ValidationService service, [Microsoft.AspNetCore.Mvc.FromServices] ITabularExporter exporter,
            CancellationToken ct) => ValidationExport(batchId, "xlsx", http, service, exporter, ct));
        review.MapGet("/validation/checks/{checkId:guid}", async (Guid checkId,
            ValidationService service, CancellationToken ct) =>
            await service.CheckAsync(checkId, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        review.MapGet("/validation/checks/{checkId:guid}/differences", (Guid checkId, string? cursor,
            int? take, ValidationService service, CancellationToken ct) =>
            InvokeValidation(() => service.DifferencesAsync(checkId, cursor, take ?? 50, ct)));
        review.MapPost("/validation/checks/{checkId:guid}/explain", (Guid checkId,
            ExplainValidationCheckRequest request, ClaimsPrincipal user, ValidationService service,
            CancellationToken ct) => InvokeValidation(() => service.ExplainAsync(checkId,
                request.Explanation, Actor(user), ct)));
        review.MapPost("/validation/checks/{checkId:guid}/sign", (Guid checkId,
            SignValidationCheckRequest request, ClaimsPrincipal user, ValidationService service,
            CancellationToken ct) => InvokeValidation(() => service.SignAsync(checkId, request.Role,
                Actor(user), user.FindAll(YojanaClaim.Role).Select(x => x.Value).ToList(), ct)));
        review.MapGet("/validation/runs/{runId:guid}/sample", (Guid runId, string? cursor, int? take,
            ValidationService service, CancellationToken ct) => InvokeValidation(() =>
                service.SampleAsync(runId, cursor, take ?? 50, ct)));
        review.MapPost("/validation/sample/{sampleId:guid}/trace", (Guid sampleId,
            TraceValidationSampleRequest request, ClaimsPrincipal user, ValidationService service,
            CancellationToken ct) => InvokeValidation(() => service.TraceAsync(sampleId, request.Outcome,
                request.Note, Actor(user), ct)));
        admin.MapPost("/validation/runs/{runId:guid}/signoff-record", (Guid runId,
            ClaimsPrincipal user, ValidationService service, CancellationToken ct) =>
            InvokeValidation(() => service.CreateSignoffAsync(runId, Actor(user), ct)));
        review.MapGet("/signoff-records/{recordId:guid}", async (Guid recordId,
            ValidationService service, CancellationToken ct) =>
            await service.SignoffAsync(recordId, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        review.MapGet("/signoff-records/{recordId:guid}/export.csv", async (Guid recordId,
            ValidationService service, CancellationToken ct) =>
        {
            try
            {
                return Results.File(await service.ExportCsvAsync(recordId, ct), "text/csv; charset=utf-8",
                    $"migration-signoff-{recordId:D}.csv");
            }
            catch (ValidationRuleException ex) { return Problem(ex); }
        });
        review.MapGet("/validation/settings", (ValidationSettings validationSettings,
            CancellationToken ct) => InvokeValidation(() => validationSettings.GetAsync(ct)));
        admin.MapPut("/validation/settings", (UpdateValidationSettingsRequest request,
            ValidationSettings validationSettings, CancellationToken ct) =>
            InvokeValidation(() => validationSettings.SetAsync(request, ct)));
        review.MapGet("/review", (Guid batchId, string? kind, string? family, string? status, string? find,
            string? cursor, int? take, CanonicalisationService service, CancellationToken ct) =>
            Invoke(() => service.ListAsync(batchId, kind, family, status, find, cursor, take ?? 25, ct)));
        review.MapGet("/review/export.csv", async (Guid batchId, string? kind, string? family, string? status,
            string? find, HttpContext http, CanonicalisationService service,
            [Microsoft.AspNetCore.Mvc.FromServices] ITabularExporter exporter,
            CancellationToken ct) =>
        {
            if (await service.ExportTooLargeAsync(batchId, kind, family, status, find, ct))
                return Results.Problem(title: "migration_review_export_too_large",
                    detail: $"Export exceeds {CanonicalisationService.MaximumExportRows:N0} review items. Narrow the filters.",
                    statusCode: StatusCodes.Status400BadRequest);
            http.Response.Headers["X-Export-Row-Limit"] =
                CanonicalisationService.MaximumExportRows.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Stream(stream => exporter.WriteAsync("csv", new TabularExport(
                ["Serial as stored", "Item code", "Item", "Item family", "Kind", "Rule tried",
                    "Candidates", "Suggested", "Status"],
                service.ExportAsync(batchId, kind, family, status, find, http.RequestAborted)),
                stream, http.RequestAborted), exporter.ContentType("csv"), "migration-review.csv");
        });
        review.MapGet("/review/{itemId:guid}", async (Guid itemId, CanonicalisationService service,
            CancellationToken ct) => await service.DetailAsync(itemId, ct) is { } result
                ? Results.Ok(result) : Results.NotFound());
        review.MapPost("/review/{itemId:guid}/decision", (Guid itemId,
            MigrationReviewDecisionRequest request, ClaimsPrincipal user, CanonicalisationService service,
            CancellationToken ct) => Invoke(() => service.DecideAsync(itemId, request, Actor(user), ct)));
        review.MapGet("/canonicalisation/families/catalogue", (CanonicalisationService service,
            CancellationToken ct) => Invoke(() => service.FamilyCatalogueAsync(ct)));
        admin.MapGet("/canonicalisation/families", (CanonicalFamilySettings settings, CancellationToken ct) =>
            Invoke(() => settings.GetAsync(ct)));
        admin.MapPut("/canonicalisation/families", (UpdateCanonicalFamilySettingsRequest request,
            CanonicalFamilySettings settings, CancellationToken ct) => Invoke(() => settings.SetAsync(request, ct)));
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

    private static async Task<IResult> InvokeMapping<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (MappingRuleException ex) { return Problem(ex); }
    }

    private static async Task<IResult> InvokeValidation<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (ValidationRuleException ex) { return Problem(ex); }
        catch (TenantSettingVersionConflictException ex)
        {
            return Results.Problem(title: "tenant_setting_changed", detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["rule"] = "tenant_setting_changed" });
        }
    }

    private static async Task<IResult> InvokeLinkOverrides<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (LinkOverrideRuleException ex) { return Problem(ex); }
    }

    private static async Task<IResult> ValidationExport(Guid batchId, string format, HttpContext http,
        ValidationService service, ITabularExporter exporter, CancellationToken cancellationToken)
    {
        try
        {
            var table = await service.ExportChecksAsync(batchId, cancellationToken);
            return Results.Stream(stream => exporter.WriteAsync(format, table, stream, http.RequestAborted),
                exporter.ContentType(format), $"migration-validation{exporter.Extension(format)}");
        }
        catch (ValidationRuleException ex) { return Problem(ex); }
    }

    private static IResult Problem(MigrationRuleException ex) => Results.Problem(title: ex.Rule,
        detail: ex.Message, statusCode: ex.Rule.EndsWith("not_found", StringComparison.Ordinal) ? 404 : 422,
        extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });

    private static IResult Problem(TemplateMappingRuleException ex) => Results.Problem(title: ex.Rule,
        detail: ex.Message, statusCode: ex.Rule.EndsWith("not_found", StringComparison.Ordinal) ? 404 : 422,
        extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });

    private static IResult Problem(ApplyRuleException ex) => Results.Problem(title: ex.Rule,
        detail: ex.Message, statusCode: ex.Rule.EndsWith("not_found", StringComparison.Ordinal) ? 404 : 422,
        extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });

    private static IResult Problem(MappingRuleException ex) => Results.Problem(title: ex.Rule,
        detail: ex.Message, statusCode: ex.Rule.EndsWith("not_found", StringComparison.Ordinal) ? 404 : 422,
        extensions: new Dictionary<string, object?>
        {
            ["rule"] = ex.Rule,
            ["waitingReviews"] = ex.WaitingReviews,
        });

    private static IResult Problem(ValidationRuleException ex) => Results.Problem(title: ex.Rule,
        detail: ex.Message, statusCode: ex.Rule.EndsWith("not_found", StringComparison.Ordinal) ? 404 : 422,
        extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });

    private static IResult Problem(LinkOverrideRuleException ex) => Results.Problem(title: ex.Rule,
        detail: ex.Message, statusCode: ex.Rule.EndsWith("not_found", StringComparison.Ordinal) ? 404 : 422,
        extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule });
}
