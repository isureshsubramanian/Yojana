using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Condition;

public sealed class CertificateConditionReadPort(SerialisationDbContext db, ITenantContext tenant) : ICertificateConditionReadPort
{
    public async Task<CertificateConditionFact> ReadAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var grade = await db.ConditionEvents.AsNoTracking().Where(row => row.TenantId == tenantId && row.UnitId == unitId)
            .OrderByDescending(row => row.GradedAt).Select(row => row.Grade).FirstOrDefaultAsync(cancellationToken)
            ?? ConditionGrades.New;
        var tests = await db.TestRecords.AsNoTracking().Where(row => row.TenantId == tenantId && row.UnitId == unitId)
            .OrderBy(row => row.TestedAt).ToListAsync(cancellationToken);
        var snapshots = tests.Select(row => new ReleaseTestSnapshot(row.TestKind, row.Result,
            JsonDocument.Parse(row.MeasurementsJson).RootElement.Clone(), row.Equipment, row.TestedAt)).ToList();
        var sanitisation = await db.SanitisationRecords.AsNoTracking().Where(row => row.TenantId == tenantId && row.UnitId == unitId)
            .OrderByDescending(row => row.SanitisedAt).Select(row => row.CertificateRef).FirstOrDefaultAsync(cancellationToken);
        var firmware = await db.AttributeHistory.AsNoTracking().Where(row => row.TenantId == tenantId && row.UnitId == unitId && row.Key == "firmware")
            .OrderByDescending(row => row.EffectiveFrom).Select(row => row.Value).FirstOrDefaultAsync(cancellationToken);
        return new CertificateConditionFact(grade, snapshots, sanitisation, firmware);
    }
}
