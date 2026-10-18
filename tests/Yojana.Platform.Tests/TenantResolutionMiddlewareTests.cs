using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Security;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Tests;

public sealed class TenantResolutionMiddlewareTests
{
    [Fact]
    public async Task Customer_portal_policy_sets_the_claimed_party_but_back_office_policy_never_does()
    {
        var tenantId = Guid.NewGuid();
        var partyRef = Guid.NewGuid();

        var portalContext = Context(tenantId, partyRef, YojanaPolicy.CustomerPortal);
        var portalScope = new TenantContext();
        await new TenantResolutionMiddleware(_ => Task.CompletedTask).InvokeAsync(portalContext,
            portalScope, portalScope);
        Assert.Equal(new TenantId(tenantId), portalScope.TenantId);
        Assert.Equal(partyRef, portalScope.PortalPartyRef);

        var backOfficeContext = Context(tenantId, partyRef, YojanaPolicy.TenantMember);
        var backOfficeScope = new TenantContext();
        await new TenantResolutionMiddleware(_ => Task.CompletedTask).InvokeAsync(backOfficeContext,
            backOfficeScope, backOfficeScope);
        Assert.Equal(new TenantId(tenantId), backOfficeScope.TenantId);
        Assert.False(backOfficeScope.HasPortalParty);
    }

    private static DefaultHttpContext Context(Guid tenantId, Guid partyRef, string policy)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(YojanaClaim.TenantId, tenantId.ToString("D")),
                new Claim(YojanaClaim.CustomerPartyRef, partyRef.ToString("D")),
            ], "test")),
        };
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute(policy)), "test"));
        return context;
    }
}
