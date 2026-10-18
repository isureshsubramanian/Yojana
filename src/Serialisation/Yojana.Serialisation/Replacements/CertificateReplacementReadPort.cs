using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Replacements;

public sealed class CertificateReplacementReadPort(SerialisationDbContext db, ITenantContext tenant) : ICertificateReplacementReadPort
{
    public async Task<CertificateReplacementFact> ReadAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var links = await db.ReplacementLinks.AsNoTracking().Where(row => row.TenantId == tenantId
            && (row.OriginalUnitId == unitId || row.ReplacementUnitId == unitId) && row.CorrectedByLinkId == null).ToListAsync(cancellationToken);
        var otherIds = links.Select(row => row.OriginalUnitId == unitId ? row.ReplacementUnitId : row.OriginalUnitId).Distinct().ToList();
        var serials = otherIds.Count == 0 ? new Dictionary<Guid, string>() : await db.Identifiers.AsNoTracking()
            .Where(row => row.TenantId == tenantId && otherIds.Contains(row.UnitId) && row.ValidTo == null && row.Type == IdentifierType.InternalSerial)
            .ToDictionaryAsync(row => row.UnitId, row => row.RawValue, cancellationToken);
        var replaces = links.Where(row => row.ReplacementUnitId == unitId && serials.ContainsKey(row.OriginalUnitId))
            .ToDictionary(_ => unitId, row => serials[row.OriginalUnitId]);
        var replacedBy = links.Where(row => row.OriginalUnitId == unitId && serials.ContainsKey(row.ReplacementUnitId))
            .ToDictionary(_ => unitId, row => serials[row.ReplacementUnitId]);
        return new CertificateReplacementFact(replaces, replacedBy);
    }
}
