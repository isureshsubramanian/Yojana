using System.Globalization;
using System.Text.Json;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Knowledge.Training;

public sealed class TrainingSettings(ITenantSettings settings)
{
    public const string NewMemberDaysKey = "knowledge.training_new_member_days";
    public const string TeamViewRolesKey = "knowledge.training_team_view_roles";
    public const int DefaultNewMemberDays = 30;
    public static readonly IReadOnlyList<string> DefaultTeamViewRoles =
        [YojanaRole.ServiceSupervisor, YojanaRole.TenantAdministrator];

    public async Task<int> NewMemberDaysAsync(CancellationToken cancellationToken)
    {
        var value = await settings.GetAsync(NewMemberDaysKey, cancellationToken);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            && days is >= 1 and <= 365 ? days : DefaultNewMemberDays;
    }

    public async Task<IReadOnlyList<string>> TeamViewRolesAsync(CancellationToken cancellationToken)
    {
        var value = await settings.GetAsync(TeamViewRolesKey, cancellationToken);
        if (value is null) return DefaultTeamViewRoles;
        try
        {
            var roles = JsonSerializer.Deserialize<List<string>>(value) ?? [];
            return roles.Count > 0 && roles.All(YojanaRole.IsKnown)
                ? roles.Distinct(StringComparer.Ordinal).ToList() : DefaultTeamViewRoles;
        }
        catch (JsonException)
        {
            return DefaultTeamViewRoles;
        }
    }
}
