using Microsoft.AspNetCore.Http;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Platform.Security;

/// <summary>
/// A temporary-password session may reach only the three account endpoints needed to inspect, change or end that
/// session. This is deliberately a platform middleware rather than a convention repeated by every module endpoint;
/// separating it from the authentication pipeline would let a newly added API accidentally bypass the requirement.
/// </summary>
public sealed class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> AllowedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth/me", "/api/auth/change-password", "/api/auth/sign-out",
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.HasClaim(YojanaClaim.PasswordChangeRequired, "true")
            && context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && !AllowedPaths.Contains(context.Request.Path.Value ?? string.Empty))
        {
            await Results.Problem(title: "password_change_required",
                detail: "Choose a new password before continuing.",
                statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
