using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts;
using Yojana.Platform.Contracts.Privacy;
using Yojana.Platform.Contracts.Paging;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Data.Platform.Entities;

namespace Yojana.Platform.Privacy;

public static class PrivacyEndpoints
{
    internal static readonly SortedPage<ErasureRequest, Guid> Pages = new("recorded", row => row.ErasureId,
        new SortColumn<ErasureRequest, DateTime>("recorded", row => row.RequestedAt, SortDirection.Descending),
        new SortColumn<ErasureRequest, string>("reference", row => row.RequestReference),
        new SortColumn<ErasureRequest, DateOnly>("received", row => row.RequestReceivedOn,
            SortDirection.Descending),
        new SortColumn<ErasureRequest, string>("status", row => row.Status));

    public static IEndpointRouteBuilder MapYojanaPrivacyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/privacy/erasures")
            .RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        group.MapPost("/preview", PreviewAsync);
        group.MapPost("/confirm", ConfirmAsync);
        group.MapGet("/export", (string? sort, string? dir, PersonalDataErasureService service,
            CancellationToken cancellationToken) =>
            Results.Stream(stream => service.ExportCsvAsync(stream, sort, dir, cancellationToken),
                "text/csv; charset=utf-8", "personal-data-erasure-log.csv"));
        group.MapGet("/", async (string? sort, string? dir, string? cursor, int? take,
            PersonalDataErasureService service,
            CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await service.ListAsync(sort, dir, cursor, take, cancellationToken)); }
            catch (CommandRefusedException exception) { return Problem(exception); }
        });
        group.MapGet("/{erasureId:guid}", async (Guid erasureId, PersonalDataErasureService service,
            CancellationToken cancellationToken) =>
            await service.DetailAsync(erasureId, cancellationToken) is { } detail
                ? Results.Ok(detail) : Results.NotFound());
        return endpoints;
    }

    private static async Task<IResult> PreviewAsync(PreviewPersonalDataErasure request,
        PersonalDataErasureService service, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        try
        {
            var actor = Guid.Parse(user.FindFirst(YojanaClaim.UserId)!.Value);
            return Results.Ok(await service.PreviewAsync(request, actor, cancellationToken));
        }
        catch (CommandRefusedException exception) { return Problem(exception); }
    }

    private static async Task<IResult> ConfirmAsync(ConfirmPersonalDataErasure request,
        PersonalDataErasureService service, Wolverine.IMessageBus bus, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        try
        {
            var actor = Guid.Parse(user.FindFirst(YojanaClaim.UserId)!.Value);
            return Results.Accepted(value: await service.ConfirmAsync(request, actor, bus, cancellationToken));
        }
        catch (CommandRefusedException exception) { return Problem(exception); }
    }

    private static IResult Problem(CommandRefusedException exception)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["rule"] = exception.Rule,
            ["unblockingStep"] = exception.UnblockingStep,
        };
        if (exception is PersonalDataErasureRefusedException refusal)
            extensions["blockers"] = refusal.Blockers;
        return Results.Problem(title: exception.Rule, detail: exception.Message,
            statusCode: StatusCodes.Status409Conflict, extensions: extensions);
    }
}
