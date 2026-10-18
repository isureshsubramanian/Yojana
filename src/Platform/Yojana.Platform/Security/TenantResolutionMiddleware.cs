using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Security;

/// <summary>After authentication: the selected tenant on the principal becomes the scope's tenant.</summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantContextSetter setter,
        IPortalPartyContextSetter portalSetter)
    {
        var claim = context.User.FindFirst(YojanaClaim.TenantId)?.Value;
        if (TenantId.TryParse(claim, out var tenantId))
        {
            setter.Set(tenantId);
        }

        // COUPLING (portal endpoint metadata): only an endpoint carrying CustomerPortal policy may put the
        // party claim into SQL SESSION_CONTEXT. Reading the claim for every authenticated endpoint would turn a
        // back-office request into a portal-filtered one. Keep this policy marker aligned with the portal route
        // group declaration in SerialisationEndpoints; removing either side disables the defence-in-depth filter.
        var isPortalEndpoint = context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => string.Equals(metadata.Policy, YojanaPolicy.CustomerPortal,
                StringComparison.Ordinal)) == true;
        if (isPortalEndpoint && Guid.TryParse(
                context.User.FindFirst(YojanaClaim.CustomerPartyRef)?.Value, out var partyRef)
            && partyRef != Guid.Empty)
        {
            portalSetter.SetPortalParty(partyRef);
        }

        await next(context);
    }
}
