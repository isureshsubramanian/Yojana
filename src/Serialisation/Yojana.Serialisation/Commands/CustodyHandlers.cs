using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Commands;

/// <summary>
/// The transition tables of ADR-0001 "Core model: CustodyEpisode" as data. Ownership: us → customer (sale), customer → us
/// (return, credit), customer → customer (resale, succession), customer → insurer (write-off), us → vendor (return to
/// vendor), group entity → group entity (inter-company). Custody: the location-type moves of the custody table plus the
/// in-place moves the cases need (warehouse to warehouse or bin, site to site, case 46). Anything else is refused by name.
/// </summary>
public static class CustodyTransitions
{
    private static readonly HashSet<(string? From, string To)> Ownership =
    [
        (null, PartyType.Us), (null, PartyType.Customer), (null, PartyType.Distributor), (null, PartyType.EndUser), (null, PartyType.GroupEntity), (null, PartyType.Vendor), (null, PartyType.MarketplaceBuyer),
        (PartyType.Us, PartyType.Customer), (PartyType.Us, PartyType.Distributor), (PartyType.Us, PartyType.EndUser), (PartyType.Us, PartyType.MarketplaceBuyer), (PartyType.Us, PartyType.Vendor), (PartyType.Us, PartyType.GroupEntity),
        (PartyType.Customer, PartyType.Us), (PartyType.Distributor, PartyType.Us), (PartyType.EndUser, PartyType.Us), (PartyType.MarketplaceBuyer, PartyType.Us), (PartyType.Vendor, PartyType.Us), (PartyType.GroupEntity, PartyType.Us),
        (PartyType.Customer, PartyType.Customer), (PartyType.Customer, PartyType.EndUser), (PartyType.Distributor, PartyType.Customer), (PartyType.Distributor, PartyType.EndUser), (PartyType.EndUser, PartyType.Customer), (PartyType.EndUser, PartyType.EndUser),
        (PartyType.Customer, PartyType.Insurer), (PartyType.EndUser, PartyType.Insurer), (PartyType.Distributor, PartyType.Insurer),
        (PartyType.GroupEntity, PartyType.GroupEntity), (PartyType.GroupEntity, PartyType.Customer),
    ];

    private static readonly HashSet<(string? From, string To)> Custody =
    [
        (null, LocationKind.Warehouse), (null, LocationKind.Bin), (null, LocationKind.CustomerSite), (null, LocationKind.InTransit), (null, LocationKind.Vendor), (null, LocationKind.At3pl), (null, LocationKind.AtSubcontractor),
        (LocationKind.Warehouse, LocationKind.Warehouse), (LocationKind.Warehouse, LocationKind.Bin), (LocationKind.Bin, LocationKind.Bin), (LocationKind.Bin, LocationKind.Warehouse),
        (LocationKind.Warehouse, LocationKind.InTransit), (LocationKind.Bin, LocationKind.InTransit), (LocationKind.Warehouse, LocationKind.CustomerSite), (LocationKind.Bin, LocationKind.CustomerSite),
        (LocationKind.Warehouse, LocationKind.AtSubcontractor), (LocationKind.Warehouse, LocationKind.At3pl), (LocationKind.Warehouse, LocationKind.Vendor), (LocationKind.Warehouse, LocationKind.InRepair), (LocationKind.Warehouse, LocationKind.Quarantine),
        (LocationKind.InTransit, LocationKind.CustomerSite), (LocationKind.InTransit, LocationKind.At3pl), (LocationKind.InTransit, LocationKind.Warehouse), (LocationKind.InTransit, LocationKind.Bin), (LocationKind.InTransit, LocationKind.Returning), (LocationKind.InTransit, LocationKind.Vendor), (LocationKind.InTransit, LocationKind.AtSubcontractor),
        (LocationKind.CustomerSite, LocationKind.CustomerSite), (LocationKind.CustomerSite, LocationKind.Returning), (LocationKind.CustomerSite, LocationKind.Quarantine), (LocationKind.CustomerSite, LocationKind.InTransit),
        (LocationKind.Returning, LocationKind.Quarantine), (LocationKind.Returning, LocationKind.Warehouse),
        (LocationKind.Quarantine, LocationKind.Warehouse), (LocationKind.Quarantine, LocationKind.Bin), (LocationKind.Quarantine, LocationKind.InRepair), (LocationKind.Quarantine, LocationKind.Vendor), (LocationKind.Quarantine, LocationKind.Scrapped),
        (LocationKind.InRepair, LocationKind.Warehouse), (LocationKind.InRepair, LocationKind.Bin), (LocationKind.InRepair, LocationKind.Quarantine),
        (LocationKind.AtSubcontractor, LocationKind.Warehouse), (LocationKind.AtSubcontractor, LocationKind.InTransit), (LocationKind.At3pl, LocationKind.InTransit), (LocationKind.At3pl, LocationKind.CustomerSite), (LocationKind.At3pl, LocationKind.Warehouse),
        (LocationKind.Vendor, LocationKind.InTransit), (LocationKind.Vendor, LocationKind.Warehouse),
    ];

    public static bool OwnershipAllowed(string? fromPartyType, string toPartyType) => Ownership.Contains((fromPartyType, toPartyType));

    public static bool CustodyAllowed(string? fromKind, string toKind) => Custody.Contains((fromKind, toKind));
}

public static class OpenOwnershipHandler
{
    public static async Task<OpenOwnershipResult> Handle(OpenOwnership command, SerialisationDbContext db, IUnitEventWriter writer, EpisodeService episodes, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UnitId == command.UnitId, cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Unit {command.UnitId} does not exist.");
        var key = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, unit.UnitId, UnitEventType.OwnershipOpened, command.EventTime);
        var replayed = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IdempotencyKey == key && e.EventTime == command.EventTime, cancellationToken);
        if (replayed is not null)
        {
            var opened = await db.Episodes.AsNoTracking().FirstAsync(e => e.TenantId == tenantId && e.OpeningEventId == replayed.EventId, cancellationToken);
            var closedBefore = await db.Episodes.AsNoTracking().Where(e => e.TenantId == tenantId && e.ClosingEventId == replayed.EventId && e.Kind == EpisodeKind.Ownership).Select(e => (Guid?)e.EpisodeId).FirstOrDefaultAsync(cancellationToken);
            return new OpenOwnershipResult(unit.UnitId, opened.EpisodeId, closedBefore);
        }

        var prepared = await episodes.PrepareOwnershipOpenAsync(unit.UnitId,
            new OwnershipTarget(command.PartyRef, command.PartyType, command.Reason, command.Source, command.Confidence, command.CustomerPoRef, command.CustomerItemRef, command.PartyIsGroupMember, command.ExternalOrderRef),
            key, command.EventTime, command.ActorRef, command.LegalEntityId, command.Document, command.ClosingReason, cancellationToken);
        var drafts = new List<UnitEventDraft>();
        if (prepared.CloseDraft is not null) drafts.Add(prepared.CloseDraft);
        drafts.Add(prepared.Draft);
        await writer.AppendAsync(drafts, cancellationToken);
        return new OpenOwnershipResult(unit.UnitId, prepared.Episode.EpisodeId, prepared.Closed?.EpisodeId);
    }
}

public static class CloseOwnershipHandler
{
    public static async Task<CloseOwnershipResult> Handle(CloseOwnership command, SerialisationDbContext db, IUnitEventWriter writer, EpisodeService episodes, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var key = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, command.UnitId, UnitEventType.OwnershipClosed, command.EventTime);
        var replayed = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IdempotencyKey == key && e.EventTime == command.EventTime, cancellationToken);
        if (replayed is not null)
        {
            var closedBefore = await db.Episodes.AsNoTracking().FirstAsync(e => e.TenantId == tenantId && e.ClosingEventId == replayed.EventId, cancellationToken);
            return new CloseOwnershipResult(command.UnitId, closedBefore.EpisodeId);
        }

        var (draft, closed) = await episodes.PrepareOwnershipCloseAsync(command.UnitId, command.Reason, key, command.EventTime, command.ActorRef, command.LegalEntityId, command.Document, cancellationToken);
        await writer.AppendAsync([draft], cancellationToken);
        return new CloseOwnershipResult(command.UnitId, closed.EpisodeId);
    }
}

public static class ChangeCustodyHandler
{
    public static async Task<ChangeCustodyResult> Handle(ChangeCustody command, SerialisationDbContext db, IUnitEventWriter writer, EpisodeService episodes, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UnitId == command.UnitId, cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Unit {command.UnitId} does not exist.");
        var key = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, unit.UnitId, UnitEventType.CustodyChanged, command.EventTime);
        var replayed = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IdempotencyKey == key && e.EventTime == command.EventTime, cancellationToken);
        if (replayed is not null)
        {
            var opened = await db.Episodes.AsNoTracking().FirstAsync(e => e.TenantId == tenantId && e.OpeningEventId == replayed.EventId, cancellationToken);
            var closedBefore = await db.Episodes.AsNoTracking().Where(e => e.TenantId == tenantId && e.ClosingEventId == replayed.EventId && e.Kind == EpisodeKind.Custody).FirstOrDefaultAsync(cancellationToken);
            return new ChangeCustodyResult(unit.UnitId, opened.EpisodeId, closedBefore?.EpisodeId, closedBefore?.LocationKind ?? "(none)", opened.LocationKind!);
        }

        var prepared = await episodes.PrepareCustodyMoveAsync(unit.UnitId,
            new CustodyTarget(command.LocationKind, command.LocationId, command.PartyRef, command.PartyType, command.Reason, command.Source, command.Confidence, command.DueBack, command.ExternalOrderRef),
            UnitEventType.CustodyChanged, key, command.EventTime, command.ActorRef, command.LegalEntityId, command.Document, new Dictionary<string, object?> { ["command_id"] = command.CommandId }, null, true, cancellationToken);
        await writer.AppendAsync([prepared.Draft], cancellationToken);
        return new ChangeCustodyResult(unit.UnitId, prepared.Episode.EpisodeId, prepared.Closed?.EpisodeId, prepared.Closed?.LocationKind ?? "(none)", command.LocationKind);
    }
}
