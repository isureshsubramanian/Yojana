using System.Security.Claims;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Settings;

namespace Yojana.Web.Settings;

public static class SettingsSummaryEndpoints
{
    private static readonly TimeSpan ContributorTimeout = TimeSpan.FromSeconds(1);

    public static IEndpointRouteBuilder MapSettingsSummaryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/settings/summary", ReadAsync)
            .RequireAuthorization(YojanaPolicy.StaffMember);
        return endpoints;
    }

    public static async Task<SettingsSummaryResponse> ReadAsync(
        ClaimsPrincipal user,
        IEnumerable<ISettingsSummaryContributor> contributors,
        ILogger<SettingsSummaryResponse> logger,
        CancellationToken cancellationToken)
    {
        var roles = user.FindAll(YojanaClaim.Role).Select(claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);
        var administrator = roles.Contains(YojanaRole.TenantAdministrator);
        var lines = new List<SettingsSummaryLine>();

        foreach (var contributor in contributors)
        {
            var permittedKeys = contributor.Keys
                .Where(key => administrator || key.Roles.Any(roles.Contains))
                .Select(key => key.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (permittedKeys.Count == 0) continue;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ContributorTimeout);
            try
            {
                var contributed = await contributor.ReadAsync(permittedKeys, timeout.Token);
                lines.AddRange(contributed.Where(line => permittedKeys.Contains(line.Key)));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Settings summary contributor omitted for {SettingsKeys}; {ExceptionType}",
                    string.Join(',', permittedKeys.Order(StringComparer.Ordinal)), exception.GetType().Name);
            }
        }

        return new SettingsSummaryResponse(lines);
    }
}

public sealed record SettingsSummaryResponse(IReadOnlyList<SettingsSummaryLine> Lines);
