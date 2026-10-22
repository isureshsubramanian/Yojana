using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Condition;

public sealed class CertificateConditionReadPort(SerialisationDbContext db, ITenantContext tenant) : ICertificateConditionReadPort
{
    private static readonly string[] BatteryKeys = ["battery.chemistry", "battery.rated_capacity_kwh",
        "battery.state_of_health_percent", "battery.carbon_footprint_kg_co2e", "battery.manufactured_at",
        "battery.responsible_operator"];

    public async Task<CertificateConditionFact> ReadAsync(Guid unitId, CancellationToken cancellationToken) =>
        (await ReadManyAsync([unitId], cancellationToken))[unitId];

    /// <summary>
    /// One query per source table for the whole list (a migration chunk); the single-unit read goes through here too.
    /// Rows are ordered in SQL and grouped in memory, so "latest" and test order are what the per-unit query gave.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, CertificateConditionFact>> ReadManyAsync(IReadOnlyCollection<Guid> unitIds,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var ids = unitIds.Distinct().ToList();
        var grades = (await db.ConditionEvents.AsNoTracking().Where(row => row.TenantId == tenantId && ids.Contains(row.UnitId))
                .OrderByDescending(row => row.GradedAt).Select(row => new { row.UnitId, row.Grade }).ToListAsync(cancellationToken))
            .GroupBy(row => row.UnitId).ToDictionary(group => group.Key, group => group.First().Grade);
        var tests = (await db.TestRecords.AsNoTracking().Where(row => row.TenantId == tenantId && ids.Contains(row.UnitId))
                .OrderBy(row => row.TestedAt).ToListAsync(cancellationToken))
            .ToLookup(row => row.UnitId);
        var sanitisations = (await db.SanitisationRecords.AsNoTracking().Where(row => row.TenantId == tenantId && ids.Contains(row.UnitId))
                .OrderByDescending(row => row.SanitisedAt).Select(row => new { row.UnitId, row.CertificateRef }).ToListAsync(cancellationToken))
            .GroupBy(row => row.UnitId).ToDictionary(group => group.Key, group => group.First().CertificateRef);
        var firmware = (await db.AttributeHistory.AsNoTracking().Where(row => row.TenantId == tenantId && ids.Contains(row.UnitId) && row.Key == "firmware")
                .OrderByDescending(row => row.EffectiveFrom).Select(row => new { row.UnitId, row.Value }).ToListAsync(cancellationToken))
            .GroupBy(row => row.UnitId).ToDictionary(group => group.Key, group => group.First().Value);
        var batteries = (await db.AttributeHistory.AsNoTracking()
                .Where(row => row.TenantId == tenantId && ids.Contains(row.UnitId) && BatteryKeys.Contains(row.Key))
                .OrderByDescending(row => row.EffectiveFrom).ToListAsync(cancellationToken))
            .ToLookup(row => row.UnitId);

        var facts = new Dictionary<Guid, CertificateConditionFact>(ids.Count);
        foreach (var unitId in ids)
        {
            var snapshots = tests[unitId].Select(row => new ReleaseTestSnapshot(row.TestKind, row.Result,
                JsonDocument.Parse(row.MeasurementsJson).RootElement.Clone(), row.Equipment, row.TestedAt)).ToList();
            var batteryValues = batteries[unitId].GroupBy(row => row.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Value);
            string? BatteryValue(string key) => batteryValues.GetValueOrDefault(key);
            var battery = batteryValues.Values.Any(value => !string.IsNullOrWhiteSpace(value))
                ? new BatteryPassportSnapshot(BatteryValue("battery.chemistry"), BatteryValue("battery.rated_capacity_kwh"),
                    BatteryValue("battery.state_of_health_percent"), BatteryValue("battery.carbon_footprint_kg_co2e"),
                    BatteryValue("battery.manufactured_at"), BatteryValue("battery.responsible_operator"))
                : null;
            facts[unitId] = new CertificateConditionFact(grades.GetValueOrDefault(unitId) ?? ConditionGrades.New, snapshots,
                sanitisations.GetValueOrDefault(unitId), firmware.GetValueOrDefault(unitId), battery);
        }
        return facts;
    }
}
