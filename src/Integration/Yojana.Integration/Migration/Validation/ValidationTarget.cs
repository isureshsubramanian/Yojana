using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Validation;

public sealed record ValidationPairTotal(string ItemCode, string Warehouse, decimal Quantity);
public sealed record ValidationBucketTotal(string Bucket, long Count);
public sealed record MigrationValidationTargetSnapshot(long Units, long Events, long UnitsWithEvent,
    long UnitsWithExactlyOneCurrentRow, long ActiveEntitlements, long OpenServiceCalls,
    IReadOnlyList<ValidationPairTotal> InStockByItemWarehouse,
    IReadOnlyList<ValidationBucketTotal> StatusHistogram, long AttachmentsListed, long AttachmentsWithHash);

/// <summary>
/// The only seam between validation and what was loaded. P9-T10 can supply a module implementation without
/// exposing Serialisation persistence or changing the source-side validation rules.
/// </summary>
public interface IMigrationValidationTarget
{
    string Kind { get; }
    Task<MigrationValidationTargetSnapshot> ReadAsync(Guid mapRunId, DateOnly cutover,
        CancellationToken cancellationToken);
}

public sealed class PlanMigrationValidationTarget(IntegrationDbContext db, ITenantContext tenant)
    : IMigrationValidationTarget
{
    public string Kind => ValidationTargetKind.Plan;

    public async Task<MigrationValidationTargetSnapshot> ReadAsync(Guid mapRunId, DateOnly cutover,
        CancellationToken cancellationToken)
    {
        var units = db.MigrationPlanUnits.AsNoTracking().Where(x => x.TenantId == tenant.TenantId &&
            x.MapRunId == mapRunId);
        var events = db.MigrationPlanEvents.AsNoTracking().Where(x => x.TenantId == tenant.TenantId &&
            x.MapRunId == mapRunId);
        var entitlements = db.MigrationPlanEntitlements.AsNoTracking().Where(x =>
            x.TenantId == tenant.TenantId && x.MapRunId == mapRunId);
        var pairs = await units.Where(x => x.LocationState == PlanLocationState.InStock && x.WarehouseCode != null)
            .GroupBy(x => new { x.ItemCode, x.WarehouseCode }).Select(g => new
            {
                g.Key.ItemCode, Warehouse = g.Key.WarehouseCode!, Quantity = (decimal)g.LongCount(),
            }).ToListAsync(cancellationToken);
        var histogram = await units.GroupBy(x => x.ConditionGrade).Select(g => new
            { Bucket = g.Key, Count = g.LongCount() }).ToListAsync(cancellationToken);
        var attachments = db.MigrationPlanAttachments.AsNoTracking().Where(x =>
            x.TenantId == tenant.TenantId && x.MapRunId == mapRunId);
        return new(await units.LongCountAsync(cancellationToken), await events.LongCountAsync(cancellationToken),
            await events.Select(x => x.PlanUnitId).Distinct().LongCountAsync(cancellationToken),
            await units.LongCountAsync(x => x.LocationState != "" || x.CustodyLocationKind != null,
                cancellationToken),
            await entitlements.LongCountAsync(x => (x.StartDate == null || x.StartDate <= cutover) &&
                (x.EndDate == null || x.EndDate >= cutover) &&
                (x.TerminationDate == null || x.TerminationDate >= cutover), cancellationToken), 0,
            pairs.Select(x => new ValidationPairTotal(x.ItemCode, x.Warehouse, x.Quantity)).ToList(),
            histogram.Select(x => new ValidationBucketTotal(x.Bucket, x.Count)).ToList(),
            await attachments.LongCountAsync(cancellationToken),
            await attachments.LongCountAsync(x => x.ContentHash != null, cancellationToken));
    }
}
