using Microsoft.AspNetCore.Http;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Security;

/// <summary>After authentication: the selected tenant on the principal becomes the scope's tenant.</summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantContextSetter setter)
    {
        var claim = context.User.FindFirst(YojanaClaim.TenantId)?.Value;
        if (TenantId.TryParse(claim, out var tenantId))
        {
            setter.Set(tenantId);
        }

        await next(context);
    }
}
