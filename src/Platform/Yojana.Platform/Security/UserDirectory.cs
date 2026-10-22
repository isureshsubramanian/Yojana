using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;

namespace Yojana.Platform.Security;

/// <summary><see cref="IUserDirectory"/> over the registry tables, limited to members of the current tenant.</summary>
public sealed class UserDirectory(PlatformDbContext db, ITenantContext tenant) : IUserDirectory, ITenantStaffDirectory
{
    public async Task<IReadOnlyDictionary<string, string>> DisplayNamesAsync(IReadOnlyCollection<string> references, CancellationToken cancellationToken)
    {
        if (references.Count > IUserDirectory.MaxReferences)
        {
            throw new ArgumentException($"At most {IUserDirectory.MaxReferences} references resolve in one call; {references.Count} were given.", nameof(references));
        }

        var given = references.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();
        var ids = given.Select(r => Guid.TryParseExact(r.Trim(), "D", out var id) ? id : (Guid?)null).OfType<Guid>().Distinct().ToList();
        var emails = given.Where(r => r.Contains('@')).Select(r => r.Trim().ToLowerInvariant()).Distinct().ToList();
        if (ids.Count == 0 && emails.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var tenantId = tenant.TenantId;
        var users = await db.Memberships.AsNoTracking()
            .Where(m => m.TenantId == tenantId && (ids.Contains(m.UserId) || emails.Contains(m.User!.Email)))
            .Select(m => new { m.User!.Id, m.User.Email, m.User.DisplayName })
            .ToListAsync(cancellationToken);

        var names = new Dictionary<string, string>();
        foreach (var reference in given)
        {
            var trimmed = reference.Trim();
            var user = Guid.TryParseExact(trimmed, "D", out var id)
                ? users.FirstOrDefault(u => u.Id == id)
                : users.FirstOrDefault(u => string.Equals(u.Email, trimmed, StringComparison.OrdinalIgnoreCase));
            if (user is not null)
            {
                names[reference] = user.DisplayName;
            }
        }

        return names;
    }

    public Task<bool> ActiveStaffMemberExistsAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Memberships.AsNoTracking().AnyAsync(m => m.TenantId == tenant.TenantId && m.UserId == userId
            && m.DeactivatedAt == null && m.CustomerPartyRef == null && m.User!.IsActive && m.Roles.Count > 0,
            cancellationToken);

    public Task<bool> ActiveStaffRoleExistsAsync(string role, CancellationToken cancellationToken) =>
        db.Memberships.AsNoTracking().AnyAsync(m => m.TenantId == tenant.TenantId && m.DeactivatedAt == null
            && m.CustomerPartyRef == null && m.User!.IsActive && m.Roles.Contains(role), cancellationToken);

    public async Task<TenantStaffChoicePage> ActiveStaffAsync(string? search, string? cursor, int take,
        CancellationToken cancellationToken)
    {
        if (take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        var after = DecodeStaffCursor(cursor);
        var query = db.Memberships.AsNoTracking().Where(m => m.TenantId == tenant.TenantId && m.DeactivatedAt == null
            && m.CustomerPartyRef == null && m.User!.IsActive && m.Roles.Count > 0);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(m => m.User!.DisplayName.Contains(term));
        }
        if (after is not null)
            query = query.Where(m => m.User!.DisplayName.CompareTo(after.Value.Name) > 0 ||
                (m.User.DisplayName == after.Value.Name && m.UserId.CompareTo(after.Value.Id) > 0));
        var rows = await query.OrderBy(m => m.User!.DisplayName).ThenBy(m => m.UserId).Take(take + 1)
            .Select(m => new { m.UserId, m.User!.DisplayName, m.Roles }).ToListAsync(cancellationToken);
        var page = rows.Take(take).Select(row => new TenantStaffChoice(row.UserId, row.DisplayName,
            row.Roles.Order(StringComparer.Ordinal).ToList())).ToList();
        return new TenantStaffChoicePage(page, rows.Count > take && page.Count > 0
            ? EncodeStaffCursor(page[^1].Name, page[^1].UserId) : null);
    }

    public async Task<IReadOnlyList<TenantRoleChoice>> ActiveRolesAsync(CancellationToken cancellationToken)
    {
        var roleLists = await db.Memberships.AsNoTracking().Where(m => m.TenantId == tenant.TenantId
                && m.DeactivatedAt == null && m.CustomerPartyRef == null && m.User!.IsActive && m.Roles.Count > 0)
            .Select(m => m.Roles).ToListAsync(cancellationToken);
        return roleLists.SelectMany(roles => roles).Where(YojanaRole.IsKnown)
            .GroupBy(role => role, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new TenantRoleChoice(group.Key, RoleLabel(group.Key), group.Count())).ToList();
    }

    private static string EncodeStaffCursor(string name, Guid id) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes($"{name}\n{id:D}"));

    private static (string Name, Guid Id)? DecodeStaffCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\n');
            if (parts.Length != 2) throw new FormatException();
            return (parts[0], Guid.Parse(parts[1]));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new ArgumentException("The staff cursor is invalid.", nameof(cursor));
        }
    }

    private static string RoleLabel(string role) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
        role.Replace('_', ' '));
}
