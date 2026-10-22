using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Replacements;

public sealed class CertificateReplacementReadPort(SerialisationDbContext db, ITenantContext tenant) : ICertificateReplacementReadPort
{
    public async Task<CertificateReplacementFact> ReadAsync(Guid unitId, CancellationToken cancellationToken) =>
        (await ReadManyAsync([unitId], cancellationToken))[unitId];

    /// <summary>One read of the links and one of the other units' serials for the whole list; the single-unit read goes through here too.</summary>
    public async Task<IReadOnlyDictionary<Guid, CertificateReplacementFact>> ReadManyAsync(IReadOnlyCollection<Guid> unitIds,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var ids = unitIds.Distinct().ToList();
        var links = await db.ReplacementLinks.AsNoTracking().Where(row => row.TenantId == tenantId
            && (ids.Contains(row.OriginalUnitId) || ids.Contains(row.ReplacementUnitId)) && row.CorrectedByLinkId == null).ToListAsync(cancellationToken);
        var otherIds = links.SelectMany(row => new[] { row.OriginalUnitId, row.ReplacementUnitId }).Distinct().ToList();
        var serials = otherIds.Count == 0 ? new Dictionary<Guid, string>() : await db.Identifiers.AsNoTracking()
            .Where(row => row.TenantId == tenantId && otherIds.Contains(row.UnitId) && row.ValidTo == null && row.Type == IdentifierType.InternalSerial)
            .ToDictionaryAsync(row => row.UnitId, row => row.RawValue, cancellationToken);
        var facts = new Dictionary<Guid, CertificateReplacementFact>(ids.Count);
        foreach (var unitId in ids)
        {
            var replaces = links.Where(row => row.ReplacementUnitId == unitId && serials.ContainsKey(row.OriginalUnitId))
                .ToDictionary(_ => unitId, row => serials[row.OriginalUnitId]);
            var replacedBy = links.Where(row => row.OriginalUnitId == unitId && serials.ContainsKey(row.ReplacementUnitId))
                .ToDictionary(_ => unitId, row => serials[row.ReplacementUnitId]);
            facts[unitId] = new CertificateReplacementFact(replaces, replacedBy);
        }
        return facts;
    }
}
