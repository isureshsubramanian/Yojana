using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Commands;

/// <summary>Set-based persistence seam for a serial's effective production-order reservation.</summary>
public sealed class SerialAllocationReservationStore(SerialisationDbContext db, ITenantContext tenant)
{
    public sealed record Reservation(SerialRange Range, SerialRangeMember? Member, SerialAllocationOverride? Override)
    {
        public DocumentRef? EffectiveOrder => Override is not null
            ? new(Override.ProductionOrderType, Override.ProductionOrderId, Override.ProductionOrderLine)
            : Range.AllocatedToDocumentType is null || Range.AllocatedToDocumentId is null
                ? null
                : new(Range.AllocatedToDocumentType, Range.AllocatedToDocumentId.Value);

        public bool Commissioned => Member?.CommissionedUnitId is not null || Override?.CommissionedUnitId is not null;
    }

    public async Task<IReadOnlyDictionary<long, Reservation>> ResolveAsync(
        string scheme,
        Guid itemId,
        IReadOnlyCollection<long> ordinals,
        bool trackOverrides,
        CancellationToken cancellationToken)
    {
        if (ordinals.Count == 0) return new Dictionary<long, Reservation>();
        var tenantId = tenant.TenantId;
        var min = ordinals.Min();
        var max = ordinals.Max();
        var ranges = await db.SerialRanges.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Scheme == scheme && r.ItemId == itemId && r.StartOrdinal != null && r.StartOrdinal <= max && r.EndOrdinal >= min)
            .ToListAsync(cancellationToken);

        var members = new Dictionary<long, SerialRangeMember>();
        foreach (var chunk in ordinals.Distinct().Chunk(2000))
        {
            var rows = await (from member in db.SerialRangeMembers.AsNoTracking()
                              join range in db.SerialRanges.AsNoTracking() on new { member.TenantId, member.RangeId } equals new { range.TenantId, range.RangeId }
                              where member.TenantId == tenantId && range.Scheme == scheme && range.ItemId == itemId && chunk.Contains(member.Ordinal)
                              select member).ToListAsync(cancellationToken);
            foreach (var member in rows) members[member.Ordinal] = member;
        }

        var randomRangeIds = members.Values.Select(m => m.RangeId).Distinct().Except(ranges.Select(r => r.RangeId)).ToList();
        if (randomRangeIds.Count > 0)
        {
            ranges.AddRange(await db.SerialRanges.AsNoTracking()
                .Where(r => r.TenantId == tenantId && randomRangeIds.Contains(r.RangeId))
                .ToListAsync(cancellationToken));
        }

        var rangeById = ranges.ToDictionary(r => r.RangeId);
        var overrides = new Dictionary<long, SerialAllocationOverride>();
        foreach (var chunk in ordinals.Distinct().Chunk(2000))
        {
            var query = db.AllocationOverrides.Where(r => r.TenantId == tenantId && r.Scheme == scheme && chunk.Contains(r.SerialOrdinal));
            var rows = await (trackOverrides ? query : query.AsNoTracking()).ToListAsync(cancellationToken);
            foreach (var row in rows) overrides[row.SerialOrdinal] = row;
        }

        var resolved = new Dictionary<long, Reservation>();
        foreach (var ordinal in ordinals.Distinct())
        {
            SerialRange? range;
            SerialRangeMember? member = null;
            if (members.TryGetValue(ordinal, out var randomMember))
            {
                member = randomMember;
                range = rangeById.GetValueOrDefault(randomMember.RangeId);
            }
            else
            {
                range = ranges.SingleOrDefault(r => r.StartOrdinal <= ordinal && r.EndOrdinal >= ordinal);
            }

            if (range is not null) resolved[ordinal] = new(range, member, overrides.GetValueOrDefault(ordinal));
        }

        return resolved;
    }

    public async Task<int> EffectiveCountAsync(DocumentRef order, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var baseCount = await db.SerialRanges.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.AllocatedToDocumentType == order.Type && r.AllocatedToDocumentId == order.Id)
            .SumAsync(r => (int?)r.Count, cancellationToken) ?? 0;
        var leaving = await (from current in db.AllocationOverrides.AsNoTracking()
                             join range in db.SerialRanges.AsNoTracking() on new { current.TenantId, RangeId = current.SerialRangeId } equals new { range.TenantId, range.RangeId }
                             where current.TenantId == tenantId
                                   && range.AllocatedToDocumentType == order.Type && range.AllocatedToDocumentId == order.Id
                                   && (current.ProductionOrderType != order.Type || current.ProductionOrderId != order.Id)
                             select current).CountAsync(cancellationToken);
        var entering = await (from current in db.AllocationOverrides.AsNoTracking()
                              join range in db.SerialRanges.AsNoTracking() on new { current.TenantId, RangeId = current.SerialRangeId } equals new { range.TenantId, range.RangeId }
                              where current.TenantId == tenantId
                                    && current.ProductionOrderType == order.Type && current.ProductionOrderId == order.Id
                                    && (range.AllocatedToDocumentType != order.Type || range.AllocatedToDocumentId != order.Id)
                              select current).CountAsync(cancellationToken);
        return baseCount - leaving + entering;
    }

    public SerialAllocationOverride SetEffectiveOrder(
        Reservation reservation,
        string scheme,
        long ordinal,
        string serial,
        DocumentRef order,
        Guid correctionId,
        DateTime at)
    {
        var row = reservation.Override;
        if (row is null)
        {
            row = new SerialAllocationOverride
            {
                TenantId = tenant.TenantId,
                Scheme = scheme,
                SerialOrdinal = ordinal,
                SerialValue = serial,
                SerialRangeId = reservation.Range.RangeId,
                ProductionOrderType = order.Type,
                ProductionOrderId = order.Id,
                ProductionOrderLine = order.Line,
                LastCorrectionId = correctionId,
                UpdatedAt = at,
            };
            db.AllocationOverrides.Add(row);
        }
        else
        {
            row.ProductionOrderType = order.Type;
            row.ProductionOrderId = order.Id;
            row.ProductionOrderLine = order.Line;
            row.LastCorrectionId = correctionId;
            row.UpdatedAt = at;
        }

        return row;
    }

    public Task LockForReassignmentAsync(string scheme, CancellationToken cancellationToken) =>
        LockAsync(scheme, "Exclusive", cancellationToken);

    public Task LockForCommissioningAsync(string scheme, CancellationToken cancellationToken) =>
        LockAsync(scheme, "Shared", cancellationToken);

    private async Task LockAsync(string scheme, string mode, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Allocation reassignment must run in Wolverine's SerialisationDbContext transaction.");
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
        command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @resource, @LockMode = @lock_mode, @LockOwner = N'Transaction', @LockTimeout = 15000; SELECT @r;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = $"serial_allocation:{tenant.TenantId}:{scheme}";
        command.Parameters.Add(parameter);
        var modeParameter = command.CreateParameter();
        modeParameter.ParameterName = "@lock_mode";
        modeParameter.Value = mode;
        command.Parameters.Add(modeParameter);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) < 0)
            throw new CommandRefusedException("allocation_busy", "Another allocation reassignment is in progress for this serial scheme.", "retry in a moment");
    }
}
