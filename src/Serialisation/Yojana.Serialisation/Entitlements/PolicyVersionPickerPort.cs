using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Entitlements;

public sealed class PolicyVersionPickerPort(SerialisationDbContext db, ITenantContext tenant)
    : IPolicyVersionPickerPort
{
    public async Task<PolicyVersionPickerPage> SearchAsync(string? search, string? cursor, int take,
        CancellationToken cancellationToken)
    {
        if (take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        var after = Decode(cursor);
        var query = db.PolicyVersions.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.Status == PolicyVersionStatus.Published);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Name.Contains(term) || x.PolicyCode.Contains(term));
        }
        if (after is not null)
            query = query.Where(x => x.Name.CompareTo(after.Value.Name) > 0
                || (x.Name == after.Value.Name && x.Version > after.Value.Version)
                || (x.Name == after.Value.Name && x.Version == after.Value.Version
                    && x.PolicyVersionId.CompareTo(after.Value.Id) > 0));
        var rows = await query.OrderBy(x => x.Name).ThenBy(x => x.Version).ThenBy(x => x.PolicyVersionId)
            .Take(take + 1).Select(x => new { x.PolicyVersionId, x.Name, x.Version }).ToListAsync(cancellationToken);
        var page = rows.Take(take).Select(x => new PolicyVersionPickerItem(x.PolicyVersionId,
            $"{x.Name} · version {x.Version}")).ToList();
        return new PolicyVersionPickerPage(page, rows.Count > take && page.Count > 0
            ? Encode(rows[take - 1].Name, rows[take - 1].Version, rows[take - 1].PolicyVersionId) : null);
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(IReadOnlyCollection<Guid> policyVersionIds,
        CancellationToken cancellationToken)
    {
        if (policyVersionIds.Count > 100) throw new ArgumentException("At most 100 policy versions resolve at once.",
            nameof(policyVersionIds));
        return ResolveCoreAsync(policyVersionIds.Distinct().ToList(), cancellationToken);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ResolveCoreAsync(IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken) => await db.PolicyVersions.AsNoTracking()
        .Where(x => x.TenantId == tenant.TenantId && x.Status == PolicyVersionStatus.Published
            && ids.Contains(x.PolicyVersionId))
        .ToDictionaryAsync(x => x.PolicyVersionId, x => x.Name + " · version " + x.Version, cancellationToken);

    private static string Encode(string name, int version, Guid id) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes($"{name}\n{version}\n{id:D}"));

    private static (string Name, int Version, Guid Id)? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\n');
            if (parts.Length != 3) throw new FormatException();
            return (parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                Guid.Parse(parts[2]));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
        {
            throw new ArgumentException("The policy-version cursor is invalid.", nameof(cursor));
        }
    }
}
