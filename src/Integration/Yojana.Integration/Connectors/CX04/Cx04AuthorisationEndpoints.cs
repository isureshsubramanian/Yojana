using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Paging;

namespace Yojana.Integration.Connectors.CX04;

public static class Cx04AuthorisationEndpoints
{
    internal static readonly SortedPage<Cx04HistoryProjection, long> HistoryPages = new("when",
        row => row.Sequence,
        new SortColumn<Cx04HistoryProjection, DateTime>("when", row => row.OccurredAt,
            SortDirection.Descending));

    public static IEndpointRouteBuilder MapCx04AuthorisationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integration/connections/{connectionId:guid}/cx04")
            .RequireAuthorization(YojanaPolicy.IntegrationOperations);
        group.MapGet("/authorisation", (Guid connectionId, string? sort, string? dir, string? cursor, int? take,
            Cx04Authorisation service, CancellationToken ct) =>
            Invoke(() => service.ReadAsync(connectionId, sort, dir, cursor, take, ct)));
        group.MapPost("/consent", (Guid connectionId, Cx04Authorisation service, CancellationToken ct) =>
            Invoke(() => service.BeginAsync(connectionId, ct)));
        group.MapPost("/callback", (Guid connectionId, Cx04ConsentCallback request, ClaimsPrincipal user,
            Cx04Authorisation service, CancellationToken ct) =>
            Invoke(() => service.CompleteAsync(connectionId, request, Actor(user), ct)));
        group.MapPost("/disable", (Guid connectionId, Cx04DisableRequest request, ClaimsPrincipal user,
            Cx04Authorisation service, CancellationToken ct) =>
            Invoke(() => service.DisableAsync(connectionId, request, Actor(user), ct)));
        group.MapGet("/reminder-settings", (Guid connectionId, Cx04ReminderSettings settings,
            CancellationToken ct) => Invoke(() => settings.GetForConnectionAsync(connectionId, ct)));
        group.MapPut("/reminder-settings", (Guid connectionId, UpdateCx04ReminderSettings request,
            ClaimsPrincipal user, Cx04ReminderSettings settings, CancellationToken ct) =>
            Invoke(() => settings.SetAsync(connectionId, request, Actor(user), ct)));
        return app;
    }

    internal static async Task<IResult> Invoke<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (Cx04AuthorisationException exception)
        {
            var status = exception.Rule.EndsWith("not_found", StringComparison.Ordinal) ? 404
                : exception.Rule is "disable_confirmation_required" or "disable_reason_invalid"
                    or "reminder_days_invalid" or "reminder_recipients_required" or "reminder_recipient_invalid"
                    or "cursor_invalid" ? 400 : 422;
            return Results.Problem(title: exception.Rule, detail: exception.Message, statusCode: status,
                extensions: new Dictionary<string, object?> { ["rule"] = exception.Rule });
        }
        catch (Yojana.Platform.Contracts.Tenancy.TenantSettingVersionConflictException exception)
        {
            return Results.Problem(title: "tenant_setting_changed", detail: exception.Message, statusCode: 409,
                extensions: new Dictionary<string, object?> { ["rule"] = "tenant_setting_changed" });
        }
        catch (Yojana.Platform.Contracts.CommandRefusedException exception)
        {
            return Results.Problem(title: exception.Rule, detail: exception.Message, statusCode: 400,
                extensions: new Dictionary<string, object?>
                {
                    ["rule"] = exception.Rule,
                    ["unblockingStep"] = exception.UnblockingStep,
                });
        }
    }

    private static string Actor(ClaimsPrincipal user) => user.FindFirst(YojanaClaim.UserId)?.Value
        ?? throw new Cx04AuthorisationException("audit_actor_required",
            "The authenticated user has no actor id.");
}
