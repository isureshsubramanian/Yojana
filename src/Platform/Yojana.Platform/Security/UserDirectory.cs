using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;

namespace Yojana.Platform.Security;

/// <summary><see cref="IUserDirectory"/> over the registry tables, limited to members of the current tenant.</summary>
public sealed class UserDirectory(PlatformDbContext db, ITenantContext tenant) : IUserDirectory
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
}
