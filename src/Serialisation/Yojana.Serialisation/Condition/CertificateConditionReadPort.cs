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
        var batteryKeys = new[] { "battery.chemistry", "battery.rated_capacity_kwh", "battery.state_of_health_percent",
            "battery.carbon_footprint_kg_co2e", "battery.manufactured_at", "battery.responsible_operator" };
        var batteryValues = (await db.AttributeHistory.AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.UnitId == unitId && batteryKeys.Contains(row.Key))
                .OrderByDescending(row => row.EffectiveFrom).ToListAsync(cancellationToken))
            .GroupBy(row => row.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Value);
        string? BatteryValue(string key) => batteryValues.GetValueOrDefault(key);
        var battery = batteryValues.Values.Any(value => !string.IsNullOrWhiteSpace(value))
            ? new BatteryPassportSnapshot(BatteryValue("battery.chemistry"), BatteryValue("battery.rated_capacity_kwh"),
                BatteryValue("battery.state_of_health_percent"), BatteryValue("battery.carbon_footprint_kg_co2e"),
                BatteryValue("battery.manufactured_at"), BatteryValue("battery.responsible_operator"))
            : null;
        return new CertificateConditionFact(grade, snapshots, sanitisation, firmware, battery);
    }
}
