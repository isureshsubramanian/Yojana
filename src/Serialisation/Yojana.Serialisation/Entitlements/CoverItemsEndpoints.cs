using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Documents;

namespace Yojana.Serialisation.Entitlements;

public static class CoverItemsEndpoints
{
    public static void MapCoverItemsEndpoints(this IEndpointRouteBuilder app)
    {
        var units = app.MapGroup("/api/units/{unitId:guid}").RequireAuthorization(YojanaPolicy.TenantMember);
        units.MapGet("/whats-covered", async (Guid unitId, [AsParameters] WhatsCoveredRequest request, WhatsCoveredQuery query, CancellationToken ct) =>
        {
            if (request.Type is not (null or "" or "covered" or "excluded")) return InvalidType();
            return await query.ReadAsync(unitId, ct) is { } result
                ? Results.Ok(WhatsCoveredQuery.Page(result, string.IsNullOrWhiteSpace(request.Type) ? null : request.Type, request.Search, request.Skip ?? 0, request.Take ?? 25)) : Results.NotFound();
        });
        units.MapGet("/claim-guidance", async (Guid unitId, [AsParameters] ClaimGuidanceRequest request, WhatsCoveredQuery query, CancellationToken ct) =>
            await query.GuidanceAsync(unitId, request.Cause, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        units.MapGet("/whats-covered/sheet.pdf", async (Guid unitId, [AsParameters] CustomerSheetRequest request, IUnitCustomerDocument document, CancellationToken ct) =>
        {
            var result = await document.CreateAsync(unitId, request.Language ?? "en", ct);
            if (result is null) return Results.NotFound();
            return result.PdfBytes is { } bytes ? Results.File(bytes, "application/pdf") : Results.Content(result.Html, "text/html; charset=utf-8");
        });
        var versions = app.MapGroup("/api/warranty-policies/versions/{policyVersionId:guid}/items").RequireAuthorization(YojanaPolicy.TenantMember);
        versions.MapGet("", async (Guid policyVersionId, [AsParameters] PolicyCoverItemsRequest request, PolicyCoverItems items,
            SerialisationDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (request.Type is not (null or "" or "covered" or "excluded")) return InvalidType();
            var version = await db.PolicyVersions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.PolicyVersionId == policyVersionId, ct);
            if (version is null) return Results.NotFound();
            var rows = (await items.ReadAsync(policyVersionId, ct)).Where(x => string.IsNullOrWhiteSpace(request.Type) || x.Type == request.Type).ToList();
            var offset = Math.Max(0, request.Skip ?? 0); var size = Math.Clamp(request.Take ?? 25, 1, 200);
            return Results.Ok(new PolicyCoverItemPage(rows.Skip(offset).Take(size).ToList(), rows.Count, offset, size, DocumentVersion.Encode(version.RowVersion)));
        });
        versions.MapPut("", (Guid policyVersionId, UpdatePolicyCoverItems command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<PolicyCoverItemPage>(bus, tenant, command with { PolicyVersionId = policyVersionId, ActorRef = Actor(user) }, ct))
            .RequireAuthorization(p => p.RequireClaim(YojanaClaim.Role, YojanaRole.ServiceManager));
        var causes = app.MapGroup("/api/claim-causes").RequireAuthorization(YojanaPolicy.TenantMember);
        causes.MapGet("", async ([AsParameters] ClaimCausesRequest request, ClaimCauseCatalogue catalogue, CancellationToken ct) =>
        {
            var rows = await catalogue.ReadAsync(ct); var offset = Math.Max(0, request.Skip ?? 0); var size = Math.Clamp(request.Take ?? 25, 1, 100);
            return Results.Ok(new ClaimCausePage(rows.Skip(offset).Take(size).ToList(), rows.Count, offset, size));
        });
        causes.MapPut("", (UpdateClaimCauses command, ClaimsPrincipal user, IMessageBus bus, ITenantContext tenant, CancellationToken ct) =>
            Invoke<ClaimCausePage>(bus, tenant, command with { ActorRef = Actor(user) }, ct))
            .RequireAuthorization(p => p.RequireClaim(YojanaClaim.Role, YojanaRole.ServiceManager));
    }

    private static IResult InvalidType() => Results.Problem(statusCode: 400, title: "cover_item_type_invalid", detail: "Choose covered or excluded items.");
    private static string Actor(ClaimsPrincipal user) => user.FindFirstValue(YojanaClaim.UserId) ?? throw new UnauthorizedAccessException();
    private static async Task<IResult> Invoke<T>(IMessageBus bus, ITenantContext tenant, object message, CancellationToken ct) where T : class
    {
        try { return Results.Ok(await bus.InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.TenantId.ToString() }, ct)); }
        catch (CommandRefusedException e) { return Results.Problem(statusCode: e.Rule is "policy_items_published" or "claim_cause_referenced" ? 409 : 400, title: e.Rule, detail: e.Message); }
    }
}
