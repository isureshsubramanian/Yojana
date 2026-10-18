using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Portal;

internal sealed record PortalIdentifierCandidate(string Type, string Value);

internal static class PortalIdentifierQuery
{
    /// <summary>Matches type/value pairs exactly; independent IN lists would create false cross-pair matches.</summary>
    public static IQueryable<UnitIdentifier> MatchExact(this IQueryable<UnitIdentifier> source, TenantId tenantId,
        IReadOnlyList<PortalIdentifierCandidate> candidates)
    {
        IQueryable<UnitIdentifier>? matched = null;
        foreach (var group in candidates.GroupBy(row => row.Type, StringComparer.Ordinal))
        {
            var type = group.Key;
            var values = group.Select(row => row.Value).Distinct(StringComparer.Ordinal).ToList();
            var branch = source.Where(row => row.TenantId == tenantId && row.Type == type
                && values.Contains(row.NormalisedValue));
            matched = matched is null ? branch : matched.Concat(branch);
        }
        return matched ?? source.Where(_ => false);
    }
}
