using Microsoft.AspNetCore.Authorization;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Platform.Security;

/// <summary>
/// Policy attribute for an ADR role: <c>[RequireRole(YojanaRole.SerialAdministrator)]</c>. The policy
/// requires a signed-in user with a selected tenant and that role in the selected tenant.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireRoleAttribute : AuthorizeAttribute
{
    public RequireRoleAttribute(string role)
    {
        if (!YojanaRole.IsKnown(role))
        {
            throw new ArgumentException($"Unknown role '{role}'. Add it to YojanaRole first.", nameof(role));
        }

        Role = role;
        Policy = YojanaPolicy.ForRole(role);
    }

    public string Role { get; }
}
