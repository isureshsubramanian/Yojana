using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Condition;

public sealed class ConditionQueries(SerialisationDbContext db, ITenantContext tenant) : IUnitConditionQueryPort
{
    public async Task<string> LatestGradeAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var graded = await db.ConditionEvents.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && e.UnitId == unitId)
            .OrderByDescending(e => e.GradedAt).ThenByDescending(e => e.ConditionEventId).Select(e => e.Grade).FirstOrDefaultAsync(cancellationToken);
        if (graded is not null) return graded;
        return await db.Current.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && row.UnitId == unitId)
            .Select(row => row.ConditionGrade).FirstOrDefaultAsync(cancellationToken) ?? ConditionGrades.New;
    }

    public Task<List<UnitConditionRow>> ConditionAsync(Guid unitId, CancellationToken cancellationToken) =>
        db.ConditionEvents.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && e.UnitId == unitId)
            .OrderByDescending(e => e.GradedAt).ThenByDescending(e => e.ConditionEventId)
            .Select(e => new UnitConditionRow(e.ConditionEventId, e.Grade, e.FCode, e.CCode, e.ValuationRule, e.InspectorRef, e.GradedAt, e.EventId)).ToListAsync(cancellationToken);

    public Task<List<SanitisationRow>> SanitisationAsync(Guid unitId, CancellationToken cancellationToken) =>
        db.SanitisationRecords.AsNoTracking().Where(r => r.TenantId == tenant.TenantId && r.UnitId == unitId)
            .OrderByDescending(r => r.SanitisedAt).ThenByDescending(r => r.SanitisationRecordId)
            .Select(r => new SanitisationRow(r.SanitisationRecordId, r.Manufacturer, r.Model, r.MediaSerial, r.MediaType, r.Method, r.Tool, r.ToolVersion,
                r.VerificationMethod, r.OperatorRef, r.SanitisedAt, r.CertificateRef, r.SignatureRef, r.EventId)).ToListAsync(cancellationToken);

    public Task<List<UnitTestRow>> TestsAsync(Guid unitId, CancellationToken cancellationToken) =>
        db.TestRecords.AsNoTracking().Where(r => r.TenantId == tenant.TenantId && r.UnitId == unitId)
            .OrderByDescending(r => r.TestedAt).ThenByDescending(r => r.TestRecordId)
            .Select(r => new UnitTestRow(r.TestRecordId, r.TestKind, r.Result, r.MeasurementsJson, r.Equipment, r.OperatorRef, r.TestedAt, r.EventId)).ToListAsync(cancellationToken);

    public Task<List<UsageReadingRow>> UsageAsync(Guid unitId, CancellationToken cancellationToken) =>
        db.UsageReadings.AsNoTracking().Where(r => r.TenantId == tenant.TenantId && r.UnitId == unitId)
            .OrderByDescending(r => r.ReadAt).ThenByDescending(r => r.UsageReadingId)
            .Select(r => new UsageReadingRow(r.UsageReadingId, r.MeterKind, r.Value, r.Unit, r.ReadAt, r.MeterReplacement, r.OperatorRef, r.EventId)).ToListAsync(cancellationToken);

    public Task<List<UnitAttributeHistoryRow>> AttributesAsync(Guid unitId, CancellationToken cancellationToken) =>
        db.AttributeHistory.AsNoTracking().Where(r => r.TenantId == tenant.TenantId && r.UnitId == unitId)
            .OrderByDescending(r => r.EffectiveFrom).ThenBy(r => r.Key)
            .Select(r => new UnitAttributeHistoryRow(r.AttributeHistoryId, r.Key, r.Value, r.EffectiveFrom, r.ActorRef, r.EventId)).ToListAsync(cancellationToken);

    public async Task<ConditionRequirementResult> SatisfiesAsync(Guid unitId, string requirement, CancellationToken cancellationToken)
    {
        var normalised = requirement.Trim().ToLowerInvariant();
        if (!ConditionRequirement.IsKnown(normalised))
            throw new CommandRefusedException("condition_requirement_unknown", $"'{requirement}' is not a condition requirement.");
        var grade = await db.Current.AsNoTracking().Where(c => c.TenantId == tenant.TenantId && c.UnitId == unitId).Select(c => c.ConditionGrade).SingleOrDefaultAsync(cancellationToken);
        var satisfied = grade is not null && (normalised == ConditionRequirement.Any || normalised == ConditionRequirement.Refurbished && ConditionGrades.IsRefurbished(grade) || normalised == grade);
        return new(unitId, normalised, grade, satisfied);
    }

    public async Task<ConditionDisclosure?> DisclosureAsync(Guid unitId, ConditionDisclosurePolicy policy, CancellationToken cancellationToken)
    {
        var grade = await db.Current.AsNoTracking().Where(c => c.TenantId == tenant.TenantId && c.UnitId == unitId).Select(c => c.ConditionGrade).SingleOrDefaultAsync(cancellationToken);
        if (grade is null) return null;
        var term = grade switch
        {
            ConditionGrades.RefurbishedA or ConditionGrades.RefurbishedB or ConditionGrades.RefurbishedC => "refurbished",
            ConditionGrades.Remanufactured => "remanufactured",
            ConditionGrades.OpenBox => "open box",
            ConditionGrades.Used => "used",
            _ => grade,
        };
        var disclosed = policy.DiscloseCondition && grade != ConditionGrades.New;
        return new(unitId, grade, disclosed, disclosed ? term : null, disclosed ? policy.RefurbisherPartyRef : null, disclosed ? policy.WarrantyStatement : null);
    }
}
