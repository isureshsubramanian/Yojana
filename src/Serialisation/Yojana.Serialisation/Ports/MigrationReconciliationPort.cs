using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Ports;

public sealed class MigrationReconciliationPort(
    SerialisationDbContext db,
    ITenantContext tenant,
    IMessageBus bus) : IMigrationReconciliationPort
{
    public async Task<IReadOnlyList<MigrationMasterReferenceUsage>> MasterReferenceUsageAsync(
        CancellationToken cancellationToken)
    {
        var excluded = SerialisedOnHandCustodyPolicy.ExcludedLifecycleStatuses;
        var items = await db.Current.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && !excluded.Contains(row.LifecycleStatus))
            .GroupBy(row => row.ItemId)
            .Select(group => new UsageProjection
            {
                ReferenceId = group.Key,
                Kind = "item",
                LiveUnits = group.Count(),
            }).ToListAsync(cancellationToken);
        var parties = await db.Current.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && !excluded.Contains(row.LifecycleStatus)
                && row.OwnerPartyRef != null)
            .GroupBy(row => row.OwnerPartyRef)
            .Select(group => new UsageProjection
            {
                ReferenceId = group.Key!.Value,
                Kind = "party",
                LiveUnits = group.Count(),
            }).ToListAsync(cancellationToken);
        return items.Concat(parties)
            .Select(row => new MigrationMasterReferenceUsage(row.ReferenceId, row.Kind, row.LiveUnits))
            .ToList();
    }

    public async Task<MigrationUnitPage> InStockUnitsAsync(Guid? afterUnitId, int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 1000);
        var included = SerialisedOnHandCustodyPolicy.IncludedLocationKinds;
        var excluded = SerialisedOnHandCustodyPolicy.ExcludedLifecycleStatuses;
        var query = db.Current.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.LocationId != null && included.Contains(row.LocationKind!)
            && !excluded.Contains(row.LifecycleStatus)
            && !db.AssemblyLinks.Any(link => link.TenantId == row.TenantId
                && link.ChildUnitId == row.UnitId && link.RemovedAt == null));
        if (afterUnitId is { } cursor) query = query.Where(row => row.UnitId.CompareTo(cursor) > 0);
        var current = await query.OrderBy(row => row.UnitId).Take(take + 1)
            .Select(row => new UnitProjection
            {
                UnitId = row.UnitId,
                ItemId = row.ItemId,
                LifecycleStatus = row.LifecycleStatus,
                Disposition = row.Disposition,
                LocationId = row.LocationId,
                LocationKind = row.LocationKind,
                OwnerPartyRef = row.OwnerPartyRef,
                LiveEntitlementIds = row.LiveEntitlementIds,
                WarrantyEnd = row.WarrantyEndMax,
            }).ToListAsync(cancellationToken);
        var page = current.Take(take).ToList();
        var ids = page.Select(row => row.UnitId).ToList();
        var identifiers = await db.Identifiers.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && ids.Contains(row.UnitId) && row.ValidTo == null)
            .OrderBy(row => row.Type == IdentifierType.InternalSerial ? 0 : 1)
            .ThenBy(row => row.IdentifierId)
            .Select(row => new IdentifierProjection
            {
                UnitId = row.UnitId,
                Type = row.Type,
                RawValue = row.RawValue,
                NormalisedValue = row.NormalisedValue,
            }).ToListAsync(cancellationToken);
        var legalEntities = await db.Events.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && ids.Contains(row.UnitId))
            .GroupBy(row => row.UnitId)
            .Select(group => new LegalEntityProjection
            {
                UnitId = group.Key,
                LegalEntityId = group.OrderByDescending(row => row.Seq)
                    .Select(row => row.LegalEntityId).First(),
            }).ToDictionaryAsync(row => row.UnitId, row => row.LegalEntityId, cancellationToken);
        var firstIdentifiers = identifiers.GroupBy(row => row.UnitId).ToDictionary(group => group.Key,
            group => group.First());
        var result = page.Where(row => firstIdentifiers.ContainsKey(row.UnitId))
            .Select(row =>
            {
                var identifier = firstIdentifiers[row.UnitId];
                return new MigrationReconciliationUnit(row.UnitId, row.ItemId, identifier.Type,
                    identifier.RawValue, identifier.NormalisedValue, row.LifecycleStatus, row.Disposition,
                    row.LocationId, row.LocationKind, row.OwnerPartyRef,
                    row.LiveEntitlementIds != "[]", row.WarrantyEnd,
                    legalEntities.GetValueOrDefault(row.UnitId));
            }).ToList();
        return new(result, current.Count > take && page.Count != 0 ? page[^1].UnitId : null);
    }

    public async Task<MigrationResolutionResult> CorrectIdentifierAsync(MigrationCorrectIdentifier request,
        CancellationToken cancellationToken)
    {
        await bus.InvokeAsync<CorrectIdentifierResult>(new CorrectIdentifier(request.CommandId, request.UnitId,
            request.IdentifierType, request.NewRawValue, MigrationReconciliationReason.Value,
            request.EventTime, request.ActorRef, request.LegalEntityId, Note: request.Note,
            Correction: Metadata(request.CommandId, request.Note),
            CommittedBy: new(request.ActorRef, [YojanaRole.InventoryController])), Options(), cancellationToken);
        return new(CorrectionCommand.CorrectIdentifier, request.CommandId, request.UnitId);
    }

    public async Task<MigrationResolutionResult> MergeUnitsAsync(MigrationMergeUnits request,
        CancellationToken cancellationToken)
    {
        await bus.InvokeAsync<MergeUnitsResult>(new MergeUnits(request.CommandId, request.SurvivorUnitId,
            request.AbsorbedUnitId, MigrationReconciliationReason.Value, request.EventTime, request.ActorRef,
            request.LegalEntityId, Note: request.Note, Correction: Metadata(request.CommandId, request.Note),
            CommittedBy: new(request.ActorRef, [YojanaRole.InventoryController])), Options(), cancellationToken);
        return new(CorrectionCommand.MergeUnits, request.CommandId, request.SurvivorUnitId);
    }

    public async Task<MigrationResolutionResult> ChangeCustodyAsync(MigrationChangeCustody request,
        CancellationToken cancellationToken)
    {
        await bus.InvokeAsync<ChangeCustodyResult>(new ChangeCustody(request.CommandId, request.UnitId,
            request.LocationKind, MigrationReconciliationReason.Value, request.EventTime, request.ActorRef,
            request.LegalEntityId, request.LocationId, Source: CorrectionInitiatedVia.Integration,
            CommittedBy: new(request.ActorRef, [YojanaRole.InventoryController])),
            Options(), cancellationToken);
        return new(nameof(ChangeCustody), request.CommandId, request.UnitId);
    }

    private DeliveryOptions Options() => new() { TenantId = tenant.TenantId.ToString() };

    private static CorrectionMetadata Metadata(Guid commandId, string? note) => new(commandId,
        MigrationReconciliationReason.Value, note, InitiatedVia: CorrectionInitiatedVia.Integration,
        InitiatingRole: CorrectionInitiatingRole.InventoryController);

    private sealed class UsageProjection
    {
        public Guid ReferenceId { get; init; }
        public required string Kind { get; init; }
        public int LiveUnits { get; init; }
    }

    private sealed class UnitProjection
    {
        public Guid UnitId { get; init; }
        public Guid ItemId { get; init; }
        public required string LifecycleStatus { get; init; }
        public required string Disposition { get; init; }
        public Guid? LocationId { get; init; }
        public string? LocationKind { get; init; }
        public Guid? OwnerPartyRef { get; init; }
        public required string LiveEntitlementIds { get; init; }
        public DateOnly? WarrantyEnd { get; init; }
    }

    private sealed class IdentifierProjection
    {
        public Guid UnitId { get; init; }
        public required string Type { get; init; }
        public required string RawValue { get; init; }
        public required string NormalisedValue { get; init; }
    }

    private sealed class LegalEntityProjection
    {
        public Guid UnitId { get; init; }
        public Guid LegalEntityId { get; init; }
    }
}
