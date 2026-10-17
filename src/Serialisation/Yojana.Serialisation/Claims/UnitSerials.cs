using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Claims;

/// <summary>The serial a person recognises for each unit: the raw value as written, of the live internal serial, else any live identifier.</summary>
public static class UnitSerials
{
    private const int Chunk = 1000;

    public static async Task<IReadOnlyDictionary<Guid, string>> DisplayAsync(SerialisationDbContext db, TenantId tenantId, IEnumerable<Guid> unitIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var ids in unitIds.Distinct().Chunk(Chunk))
        {
            var rows = await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && ids.Contains(i.UnitId) && i.ValidTo == null)
                .Select(i => new { i.UnitId, i.Type, i.RawValue }).ToListAsync(cancellationToken);
            foreach (var unit in rows.GroupBy(r => r.UnitId))
            {
                result[unit.Key] = (unit.FirstOrDefault(r => r.Type == IdentifierType.InternalSerial) ?? unit.OrderBy(r => r.Type, StringComparer.Ordinal).First()).RawValue;
            }
        }

        return result;
    }
}
