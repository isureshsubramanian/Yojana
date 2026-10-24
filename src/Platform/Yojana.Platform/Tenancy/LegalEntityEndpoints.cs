using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Platform.Tenancy;

public static class LegalEntityEndpoints
{
    public static IEndpointRouteBuilder MapYojanaLegalEntityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/settings/legal-entities", async (IActingLegalEntity legalEntities,
                CancellationToken cancellationToken) => Results.Ok(await legalEntities.GetAsync(cancellationToken)))
            .RequireAuthorization(YojanaPolicy.StaffMember);
        endpoints.MapPut("/api/settings/legal-entities/default", PutDefaultAsync)
            .RequireAuthorization(YojanaPolicy.TenantAdministrator);
        return endpoints;
    }

    private static async Task<IResult> PutDefaultAsync(SetDefaultLegalEntityRequest request,
        IActingLegalEntity legalEntities, CancellationToken cancellationToken)
    {
        var result = await legalEntities.SetDefaultAsync(request.PartyId, cancellationToken);
        return result.Accepted
            ? Results.Ok(await legalEntities.GetAsync(cancellationToken))
            : Results.Problem(title: result.RefusalRule, detail: result.RefusalMessage,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?> { ["rule"] = result.RefusalRule });
    }
}

public sealed record SetDefaultLegalEntityRequest(Guid PartyId);
