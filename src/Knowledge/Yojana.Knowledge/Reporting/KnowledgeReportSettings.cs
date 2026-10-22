using System.Globalization;
using System.Text.Json;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Knowledge.Reports;

public sealed class KnowledgeReportSettings(ITenantSettings settings)
{
    public const string AllowedRolesKey = "knowledge.report_roles";
    public const string SimilarEntryRankKey = "knowledge.similar_entry_minimum_rank";
    public const int DefaultSimilarEntryRank = 100;
    public static readonly IReadOnlyList<string> DefaultAllowedRoles =
        [YojanaRole.ServiceSupervisor, YojanaRole.Quality, YojanaRole.TenantAdministrator];

    public async Task<IReadOnlyList<string>> AllowedRolesAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetAsync(AllowedRolesKey, cancellationToken);
        if (stored is null) return DefaultAllowedRoles;
        try
        {
            var roles = JsonSerializer.Deserialize<List<string>>(stored) ?? [];
            return roles.Count > 0 && roles.All(YojanaRole.IsKnown)
                ? roles.Distinct(StringComparer.Ordinal).ToList()
                : DefaultAllowedRoles;
        }
        catch (JsonException)
        {
            return DefaultAllowedRoles;
        }
    }

    public async Task<int> SimilarEntryRankAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetAsync(SimilarEntryRankKey, cancellationToken);
        return int.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var rank)
            && rank is >= 1 and <= 1000 ? rank : DefaultSimilarEntryRank;
    }
}
