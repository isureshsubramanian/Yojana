using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Security;

public static class SecondStepMode
{
    public const string Off = "off";
    public const string Optional = "optional";
    public const string Required = "required";
    public static readonly IReadOnlySet<string> All = new HashSet<string>([Off, Optional, Required],
        StringComparer.Ordinal);

    public static string Strictest(IEnumerable<string> modes) => modes.Aggregate(Off, (current, next) =>
        Rank(next) > Rank(current) ? next : current);

    private static int Rank(string mode) => mode == Required ? 2 : mode == Optional ? 1 : 0;
}

public sealed record SecondStepRoleSetting(string Role, string Label, int Users, string Mode);
public sealed record SecondStepPolicyView(IReadOnlyList<SecondStepRoleSetting> Roles, int NotSetUpCount,
    string Version);

public sealed class SecondStepPolicy(
    PlatformDbContext db,
    ITenantContext tenant,
    ITenantSettings settings,
    IServiceScopeFactory scopes)
{
    public const string SettingKey = "security.second_step.by_role";
    public const string CustomerPortalRole = "customer_portal";
    private static readonly List<string> Roles = YojanaRole.All.Append(CustomerPortalRole).ToList();
    private static readonly HashSet<string> RequiredDefaults = new(
        [YojanaRole.TenantAdministrator, YojanaRole.IntegrationOperator, YojanaRole.CertificateAdministrator,
            YojanaRole.LegalReviewer, YojanaRole.ServiceManager], StringComparer.Ordinal);

    public async Task<SecondStepPolicyView> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var setting = await settings.GetVersionedAsync(SettingKey, cancellationToken);
        var modes = Parse(setting?.Value);
        var tenantId = tenant.TenantId;
        var memberships = await db.Memberships.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.DeactivatedAt == null)
            .Select(row => new { row.UserId, row.Roles }).ToListAsync(cancellationToken);
        var memberIds = memberships.Select(row => row.UserId).Distinct().ToList();
        var enrolled = (await SecondStepEnrolment.EnrolledUsers(db).Where(id => memberIds.Contains(id))
            .ToListAsync(cancellationToken)).ToHashSet();
        var roleViews = Roles.Select(role => new SecondStepRoleSetting(role, Label(role),
            memberships.Count(row => row.Roles.Contains(role)), ModeFor(role, modes))).ToList();
        var notSetUp = memberships.Where(row => SecondStepMode.Strictest(row.Roles.Select(role =>
                ModeFor(role, modes))) == SecondStepMode.Required && !enrolled.Contains(row.UserId))
            .Select(row => row.UserId).Distinct().Count();
        return new SecondStepPolicyView(roleViews, notSetUp,
            setting?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<(SecondStepPolicyView View, IReadOnlyList<string> Changed)> UpdateCurrentAsync(
        IReadOnlyList<SecondStepRoleSetting>? requested, string version, CancellationToken cancellationToken)
    {
        var rows = requested ?? [];
        if (rows.Count != Roles.Count || rows.Select(row => row.Role).Distinct(StringComparer.Ordinal).Count() != Roles.Count ||
            rows.Any(row => !Roles.Contains(row.Role, StringComparer.Ordinal) || !SecondStepMode.All.Contains(row.Mode)))
            throw new ArgumentException("Supply one required, optional or off value for every role.", nameof(requested));
        var before = await GetCurrentAsync(cancellationToken);
        var values = rows.ToDictionary(row => row.Role, row => row.Mode, StringComparer.Ordinal);
        var saved = await settings.SetAsync(SettingKey, JsonSerializer.Serialize(values), version,
            cancellationToken);
        var changed = before.Roles.Where(row => values[row.Role] != row.Mode).Select(row => row.Role).ToList();
        var view = await GetCurrentAsync(cancellationToken);
        return (view with { Version = saved.Version }, changed);
    }

    /// <summary>
    /// A user identity is global, so the strictest mode across every role in every active membership wins. Checking
    /// only the tenant auto-selected after sign-in would let a weaker tenant policy bypass a stronger one.
    /// </summary>
    public async Task<string> EffectiveForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var memberships = await db.Memberships.AsNoTracking()
            .Where(row => row.UserId == userId && row.DeactivatedAt == null && row.Tenant!.IsActive)
            .Select(row => new { row.TenantId, row.Roles }).ToListAsync(cancellationToken);
        if (memberships.Count == 0) return SecondStepMode.Optional;
        // Tenant settings are protected by SQL Server RLS. A pre-tenant sign-in therefore reads each tenant in
        // its own scope; a cross-tenant query would silently see no rows and fall back to defaults.
        var parsed = new Dictionary<TenantId, Dictionary<string, string>>();
        foreach (var tenantId in memberships.Select(row => row.TenantId).Distinct())
            parsed[tenantId] = await ForTenantAsync(tenantId, cancellationToken);
        var modes = memberships.SelectMany(membership => membership.Roles.Select(role =>
            ModeFor(role, parsed.GetValueOrDefault(membership.TenantId)))).ToList();
        return modes.Count == 0 ? SecondStepMode.Optional : SecondStepMode.Strictest(modes);
    }

    private async Task<Dictionary<string, string>> ForTenantAsync(TenantId tenantId,
        CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenantId);
        var value = await scope.ServiceProvider.GetRequiredService<ITenantSettings>()
            .GetAsync(SettingKey, cancellationToken);
        return Parse(value);
    }

    public static string DefaultFor(string role) => RequiredDefaults.Contains(role)
        ? SecondStepMode.Required : SecondStepMode.Optional;

    private static string ModeFor(string role, Dictionary<string, string>? values) =>
        values is not null && values.TryGetValue(role, out var mode) && SecondStepMode.All.Contains(mode)
            ? mode : DefaultFor(role);

    private static Dictionary<string, string> Parse(string? value)
    {
        try
        {
            return string.IsNullOrWhiteSpace(value)
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, string>>(value)
                  ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string Label(string role) => role == CustomerPortalRole ? "Customer portal users" :
        string.Join(' ', role.Split('_').Select((part, index) => index == 0
            ? char.ToUpperInvariant(part[0]) + part[1..] : part));
}

/// <summary>One shared answer to whether an actor administers every membership of a global identity.</summary>
public sealed class MembershipAdministration(PlatformDbContext db)
{
    public async Task<bool> AdministersEveryMembershipAsync(Guid actorId, Guid targetId,
        CancellationToken cancellationToken)
    {
        var targetTenants = await db.Memberships.AsNoTracking().Where(row => row.UserId == targetId)
            .Select(row => row.TenantId).ToListAsync(cancellationToken);
        var administered = await db.Memberships.AsNoTracking()
            .Where(row => row.UserId == actorId && row.DeactivatedAt == null &&
                          row.Roles.Contains(YojanaRole.TenantAdministrator))
            .Select(row => row.TenantId).ToListAsync(cancellationToken);
        return targetTenants.All(administered.Contains);
    }
}
