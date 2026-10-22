using System.Text.Json;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Knowledge.Entries;

public sealed class EntryReviewerSettings(ITenantSettings settings)
{
    public const string Key = "knowledge.entry_reviewer_roles";
    public static readonly IReadOnlyList<string> DefaultRoles = [YojanaRole.ServiceSupervisor, YojanaRole.Quality];

    public async Task<IReadOnlyList<string>> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetAsync(Key, cancellationToken);
        if (stored is null) return DefaultRoles;
        try
        {
            var roles = JsonSerializer.Deserialize<List<string>>(stored) ?? [];
            return roles.Count > 0 && roles.All(YojanaRole.IsKnown)
                ? roles.Distinct(StringComparer.Ordinal).ToList()
                : DefaultRoles;
        }
        catch (JsonException)
        {
            return DefaultRoles;
        }
    }
}
