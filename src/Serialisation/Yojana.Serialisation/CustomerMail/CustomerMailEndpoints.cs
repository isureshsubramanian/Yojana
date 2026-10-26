using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PlatformCommandRefusedException = Yojana.Platform.Contracts.CommandRefusedException;
using Wolverine;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Documents;

namespace Yojana.Serialisation.CustomerMail;

// Sent-log endpoints only bind paging inputs. SentMailQueries declares its columns through SortedPage<TRow, TTie>,
// keeping ordering, size validation and cursor compatibility out of this HTTP boundary.

/// <summary>
/// Customer e-mail (P9-T20): templates and their versions, the compose flow from a unit's What's covered page, and the
/// sent log. The sender and every actor are taken from the session's claims, never from the request body.
/// </summary>
public static class CustomerMailEndpoints
{
    public static void MapCustomerMailEndpoints(this IEndpointRouteBuilder app)
    {
        var templates = app.MapGroup("/api/mail-templates").RequireAuthorization(YojanaPolicy.CustomerMailDesigner);
        templates.MapGet("", (string? status, int? skip, int? take, MailTemplateService service, CancellationToken ct) =>
            Invoke(() => service.ListAsync(status, skip ?? 0, take ?? 25, ct)));
        templates.MapGet("/{templateId:guid}", async (Guid templateId, ClaimsPrincipal user, MailTemplateService service, CancellationToken ct) =>
            await service.GetAsync(templateId, Actor(user), ct) is { } found ? Results.Ok(found) : Results.NotFound());
        templates.MapPost("", (CreateMailTemplateRequest request, ClaimsPrincipal user, MailTemplateService service, CancellationToken ct) =>
            Invoke(() => service.CreateAsync(request, Actor(user), ct)));
        templates.MapPut("/{templateId:guid}/draft", async (Guid templateId, SaveMailTemplateDraftRequest request, ClaimsPrincipal user,
            MailTemplateService service, CancellationToken ct) => await Saved(() => service.SaveDraftAsync(templateId, request, Actor(user), ct)));
        templates.MapPost("/{templateId:guid}/versions/{versionId:guid}/publish", async (Guid templateId, Guid versionId, ClaimsPrincipal user,
            MailTemplateService service, CancellationToken ct) => await Saved(() => service.PublishAsync(templateId, versionId, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        templates.MapPost("/{templateId:guid}/retire", (Guid templateId, ClaimsPrincipal user, MailTemplateService service, CancellationToken ct) =>
            Invoke(() => service.RetireAsync(templateId, Actor(user), ct))).RequireAuthorization(YojanaPolicy.TenantAdministrator);
        templates.MapPut("/{templateId:guid}/default", (Guid templateId, SetMailTemplateDefaultRequest request, ClaimsPrincipal user,
            MailTemplateService service, CancellationToken ct) => Invoke(() => service.SetDefaultAsync(templateId, request, Actor(user), ct)))
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        templates.MapPost("/{templateId:guid}/preview", (Guid templateId, MailPreviewRequest request, ClaimsPrincipal user,
            MailTemplateService service, CancellationToken ct) => Invoke(() => service.PreviewAsync(templateId, request, Actor(user), ct)));
        templates.MapPost("/{templateId:guid}/test-send", async (Guid templateId, MailTestSendRequest request, ClaimsPrincipal user,
            MailTemplateService service, CancellationToken ct) =>
        {
            var result = await Invoke(() => service.TestSendAsync(templateId, request, Actor(user), ct));
            return result is IStatusCodeHttpResult { StatusCode: 200 } && result is IValueHttpResult { Value: MailQueuedView queued }
                ? Results.Accepted(value: queued) : result;
        });

        var compose = app.MapGroup("/api/units/{unitId:guid}/coverage-mail").RequireAuthorization(YojanaPolicy.CustomerMailSender);
        compose.MapGet("", async (Guid unitId, ClaimsPrincipal user, CoverageMailCompose service, CancellationToken ct) =>
            await service.GetAsync(unitId, Actor(user), ct) is { } view ? Results.Ok(view) : Results.NotFound());
        compose.MapPost("/preview", (Guid unitId, ComposePreviewRequest request, ClaimsPrincipal user, CoverageMailCompose service,
            CancellationToken ct) => Invoke(async () => await service.PreviewAsync(unitId, request, Actor(user), ct)));
        compose.MapPost("/send", async (Guid unitId, SendCoverageMail command, ClaimsPrincipal user, IMessageBus bus,
            ITenantContext tenant, CancellationToken ct) =>
        {
            var actor = Actor(user);
            if (command.CommandId == Guid.Empty)
                return Results.Problem(title: "command_id_required", detail: "Send a new command id with each compose.", statusCode: 400);
            var result = await SerialisationEndpoints.Invoke<MailQueuedView>(bus, tenant, command with
            {
                UnitId = unitId, ActorRef = actor.Ref, ActorName = actor.Name, ActorEmail = actor.Email, ActorRoles = actor.Roles,
            }, ct);
            return result is IValueHttpResult { Value: MailQueuedView queued } ? Results.Accepted(value: queued) : result;
        });

        var sent = app.MapGroup("/api/customer-mail").RequireAuthorization(YojanaPolicy.CustomerMailSender);
        sent.MapGet("", (string? status, string? kind, string? sentBy, Guid? unitId, string? q, string? sort, string? dir,
            string? cursor, int? take, SentMailQueries queries, CancellationToken ct) =>
            Invoke(() => queries.ListAsync(status, kind, sentBy, unitId, q, sort, dir, cursor, take ?? 25, ct)));
        sent.MapGet("/export", (string? format, string? status, string? kind, string? sentBy, string? q, string? sort, string? dir, HttpContext http, SentMailQueries queries,
            ITabularExporter exporter) => SerialisationEndpoints.DirectExport(format, "sent-log", http, exporter,
                ["Queued (UTC)", "To", "Name", "Cc", "Customer", "Subject", "Template", "Version", "Language", "Kind", "About", "Sent by", "Status", "Attempts", "Last error"],
                ExportRows(queries.AllAsync(status, kind, sentBy, q, sort, dir, http.RequestAborted))));
        sent.MapGet("/{messageId:guid}", async (Guid messageId, SentMailQueries queries, CancellationToken ct) =>
            await queries.GetAsync(messageId, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
    }

    internal static MailActor Actor(ClaimsPrincipal user) => new(
        user.FindFirstValue(YojanaClaim.UserId) ?? throw new UnauthorizedAccessException(),
        user.FindFirstValue(ClaimTypes.Name) ?? "",
        user.FindFirstValue(ClaimTypes.Email) ?? "",
        user.FindAll(YojanaClaim.Role).Select(x => x.Value).ToList());

    private static async IAsyncEnumerable<IReadOnlyList<string?>> ExportRows(IAsyncEnumerable<SentMailRow> rows)
    {
        await foreach (var row in rows)
            yield return [row.QueuedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), row.To, row.ToName,
                row.CcCount.ToString(CultureInfo.InvariantCulture), row.CustomerName, row.Subject, row.TemplateName,
                row.TemplateVersion.ToString(CultureInfo.InvariantCulture), row.Language, row.KindName, row.AboutLabel, row.SentBy,
                row.Status, row.Attempts.ToString(CultureInfo.InvariantCulture), row.LastError];
    }

    private static async Task<IResult> Invoke<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (DocumentChangedException ex)
        {
            return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["changedBy"] = ex.ChangedBy, ["changedAt"] = ex.ChangedAt });
        }
        catch (PlatformCommandRefusedException ex)
        {
            var status = ex.Rule is "sort_not_supported" or "sort_changed" or "page_size_too_large" or "page_size_invalid" or "cursor_invalid"
                ? StatusCodes.Status400BadRequest
                : ex.Rule.EndsWith("_unknown", StringComparison.Ordinal) ? StatusCodes.Status404NotFound
                : ex.Rule.EndsWith("_forbidden", StringComparison.Ordinal) ? StatusCodes.Status403Forbidden : StatusCodes.Status409Conflict;
            return Results.Problem(title: ex.Rule, detail: ex.Message, statusCode: status,
                extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule, ["unblockingStep"] = ex.UnblockingStep });
        }
    }

    /// <summary>A save or publish whose checks failed answers 422 with the checks, so the designer can show them.</summary>
    private static async Task<IResult> Saved(Func<Task<MailTemplateSaveResult>> action)
    {
        var result = await Invoke(action);
        return result is IValueHttpResult { Value: MailTemplateSaveResult { Template: null } failed }
            ? Results.Problem(title: "mail_template_invalid", detail: string.Join(" ", failed.Check.Errors), statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?> { ["rule"] = "mail_template_invalid", ["check"] = failed.Check })
            : result;
    }
}
