using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Commands;

/// <summary>
/// <see cref="RecordAssembly"/> and <see cref="RecordDisassembly"/>: the as-built and as-maintained trees as recorded
/// facts (Identity rule 8), never inferred from costing. Guards: the child exists and is not the parent or one of its
/// ancestors (no cycles); a serialised child has at most one open parent (case 37, also a filtered unique index); a
/// lot or non-serialised child carries item and quantity. Full disassembly parks a released parent as dormant; the next
/// assembly brings it back (Identity rule 9, case 14). Every link change is an event on the parent and, for a serialised
/// child, on the child too, so both histories read correctly (case 36).
/// </summary>
public static class RecordAssemblyHandler
{
    public static async Task<RecordAssemblyResult> Handle(RecordAssembly command, SerialisationDbContext db, IUnitEventWriter writer, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var parent = await db.Units.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UnitId == command.ParentUnitId, cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Parent unit {command.ParentUnitId} does not exist.");
        var key = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, parent.UnitId, UnitEventType.Assembled, command.EventTime);
        var replayed = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IdempotencyKey == key && e.EventTime == command.EventTime, cancellationToken);
        if (replayed is not null)
        {
            var links = await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenantId && l.ParentUnitId == parent.UnitId && l.InstallingEventId == replayed.EventId).Select(l => l.LinkId).ToListAsync(cancellationToken);
            return new RecordAssemblyResult(parent.UnitId, links, [], parent.LifecycleStatus);
        }

        if (parent.LifecycleStatus == LifecycleStatus.Void && command.Installs.Count > 0)
        {
            throw new CommandRefusedException("component_reissue_to_voided_unit", $"Components cannot be issued to voided unit {parent.UnitId}.",
                "reverse the void with a later correction before issuing components");
        }

        if (parent.LifecycleStatus is LifecycleStatus.Void or LifecycleStatus.Scrapped or LifecycleStatus.ScrappedPreRelease or LifecycleStatus.Decommissioned or LifecycleStatus.Merged)
        {
            throw new CommandRefusedException("parent_not_assemblable", $"Parent unit {parent.UnitId} is {parent.LifecycleStatus}.", "a scrapped, voided or decommissioned unit takes no components");
        }

        if (command.Installs.Count == 0 && (command.Removals is null || command.Removals.Count == 0))
        {
            throw new CommandRefusedException("nothing_to_record", "RecordAssembly needs at least one install or removal.");
        }

        var drafts = new List<UnitEventDraft>();
        var closed = new List<Guid>();
        if (command.Removals is { Count: > 0 } removals)
        {
            closed = await AssemblyLinks.CloseAsync(db, writer, tenantId, parent, removals, command.EventTime, command.ActorRef, command.LegalEntityId, command.Document, command.RemovalReason ?? "rework", command.CommandId, drafts, cancellationToken);
        }

        var eventId = TimeOrderedId.New(command.EventTime);
        var linkIds = new List<Guid>();
        var ancestors = await AssemblyLinks.AncestorsAsync(db, tenantId, parent.UnitId, cancellationToken);
        var installedNow = new HashSet<Guid>();
        foreach (var child in command.Installs)
        {
            if (child.ChildUnitId is { } childUnitId)
            {
                if (childUnitId == parent.UnitId || ancestors.Contains(childUnitId))
                {
                    throw new CommandRefusedException("assembly_cycle", $"Unit {childUnitId} is the parent or one of its ancestors.", "check the scanned serials; a unit cannot contain itself");
                }

                if (!installedNow.Add(childUnitId))
                {
                    throw new CommandRefusedException("child_repeated", $"Unit {childUnitId} appears twice in the installs.");
                }

                var childUnit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UnitId == childUnitId, cancellationToken)
                    ?? throw new CommandRefusedException("child_unknown", $"Child unit {childUnitId} does not exist.", "commission the component first (or AssignIdentifiers in P3 for vendor lists)");
                if (childUnit.LifecycleStatus is LifecycleStatus.Void or LifecycleStatus.Scrapped or LifecycleStatus.ScrappedPreRelease or LifecycleStatus.Decommissioned or LifecycleStatus.Merged)
                {
                    throw new CommandRefusedException("child_not_installable", $"Child unit {childUnitId} is {childUnit.LifecycleStatus}.");
                }

                var openElsewhere = await db.AssemblyLinks.AsNoTracking().FirstOrDefaultAsync(l => l.TenantId == tenantId && l.ChildUnitId == childUnitId && l.RemovedAt == null && !closed.Contains(l.LinkId), cancellationToken);
                if (openElsewhere is not null)
                {
                    throw new CommandRefusedException("child_already_installed", $"Unit {childUnitId} is installed in parent {openElsewhere.ParentUnitId} (slot {openElsewhere.Slot ?? "-"}).", "record its removal from that parent first, or use CorrectAsBuilt (P3) when the record is wrong");
                }

                var childItem = await db.ItemAssignments.AsNoTracking().Where(a => a.TenantId == tenantId && a.UnitId == childUnitId && a.ValidTo == null).Select(a => (Guid?)a.ItemId).FirstOrDefaultAsync(cancellationToken);
                var link = NewLink(tenantId, parent.UnitId, child, command.EventTime, eventId, childItem);
                db.AssemblyLinks.Add(link);
                linkIds.Add(link.LinkId);
                drafts.Add(new UnitEventDraft
                {
                    UnitId = childUnitId, EventTime = command.EventTime, EventType = UnitEventType.Installed, BizStep = "installing", LegalEntityId = command.LegalEntityId, ActorRef = command.ActorRef,
                    DocumentType = command.Document?.Type, DocumentId = command.Document?.Id, DocumentLine = command.Document?.Line,
                    IdempotencyKey = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, childUnitId, UnitEventType.Installed, command.EventTime),
                    Attributes = JsonObjectText.Of(new { command_id = command.CommandId, parent_unit_id = parent.UnitId, link_id = link.LinkId, slot = child.Slot, relation = child.Relation }),
                    Delta = new CurrentStateDelta { Disposition = UnitDisposition.InProgress, Attributes = new Dictionary<string, System.Text.Json.JsonElement?> { ["parent_unit_id"] = System.Text.Json.JsonSerializer.SerializeToElement(parent.UnitId) } },
                });
            }
            else
            {
                if (child.ChildItemId is null || child.Quantity is not > 0)
                {
                    throw new CommandRefusedException("child_input_invalid", "A lot or non-serialised child needs ChildItemId and a positive Quantity.");
                }

                var link = NewLink(tenantId, parent.UnitId, child, command.EventTime, eventId, child.ChildItemId);
                db.AssemblyLinks.Add(link);
                linkIds.Add(link.LinkId);
            }
        }

        var wasDormant = parent.LifecycleStatus == LifecycleStatus.Dormant;
        var next = wasDormant && command.Installs.Count > 0 ? UnitLifecycle.Require(parent.LifecycleStatus, UnitEventType.Reassembled, parent.Origin, parent.UnitId) : parent.LifecycleStatus;
        if (command.Installs.Count > 0)
        {
            drafts.Add(new UnitEventDraft
            {
                UnitId = parent.UnitId, EventTime = command.EventTime, EventType = wasDormant ? UnitEventType.Reassembled : UnitEventType.Assembled, PresetEventId = eventId, BizStep = "assembling",
                LegalEntityId = command.LegalEntityId, ActorRef = command.ActorRef, DocumentType = command.Document?.Type, DocumentId = command.Document?.Id, DocumentLine = command.Document?.Line,
                IdempotencyKey = key,
                Attributes = JsonObjectText.Of(new { command_id = command.CommandId, installed = command.Installs.Select(c => new { c.ChildUnitId, c.ChildItemId, c.ChildLotId, c.Quantity, c.Relation, c.Slot, through_phantom_item_id = c.ThroughPhantomItemId }), removed_link_ids = closed }),
                Delta = wasDormant ? new CurrentStateDelta { LifecycleStatus = next, Disposition = UnitLifecycle.DispositionFor(next) } : null,
            });
        }

        if (wasDormant && command.Installs.Count > 0)
        {
            // Identity rule 9: reassembly restores the unit and freezes the new as-built as the next BuildRecord version.
            var assignment = await db.ItemAssignments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.UnitId == parent.UnitId && x.ValidTo == null, cancellationToken)
                ?? throw new CommandRefusedException("no_item_assignment", $"Unit {parent.UnitId} has no current item assignment.");
            await BuildRecords.FreezeAsync(db, tenantId, parent, assignment, eventId, command.EventTime, null, null, null, null, cancellationToken);
        }

        await writer.AppendAsync(drafts, cancellationToken);
        parent.LifecycleStatus = next;
        return new RecordAssemblyResult(parent.UnitId, linkIds, closed, next);
    }

    private static AssemblyLink NewLink(TenantId tenantId, Guid parentUnitId, ChildInput child, DateTime at, Guid eventId, Guid? childItemId) => new()
    {
        TenantId = tenantId, LinkId = TimeOrderedId.New(at), ParentUnitId = parentUnitId, ChildUnitId = child.ChildUnitId,
        ChildItemId = child.ChildUnitId is null ? childItemId : null, ChildLotId = child.ChildUnitId is null ? child.ChildLotId : null, Quantity = child.ChildUnitId is null ? child.Quantity : null,
        Relation = child.Relation, Slot = child.Slot, InstalledAt = at, InstallingEventId = eventId, ThroughPhantomItemId = child.ThroughPhantomItemId, ReleaseStatus = child.ReleaseStatus ?? ReleaseStatus.New, ReleaseDocRef = child.ReleaseDocRef,
    };
}

public static class RecordDisassemblyHandler
{
    public static async Task<RecordDisassemblyResult> Handle(RecordDisassembly command, SerialisationDbContext db, IUnitEventWriter writer, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var parent = await db.Units.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UnitId == command.ParentUnitId, cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Parent unit {command.ParentUnitId} does not exist.");
        var key = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, parent.UnitId, UnitEventType.Disassembled, command.EventTime);
        if (await db.Events.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.IdempotencyKey == key && e.EventTime == command.EventTime, cancellationToken))
        {
            return new RecordDisassemblyResult(parent.UnitId, [], parent.LifecycleStatus);
        }

        var targets = command.LinkIds ?? await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenantId && l.ParentUnitId == parent.UnitId && l.RemovedAt == null).Select(l => l.LinkId).ToListAsync(cancellationToken);
        if (targets.Count == 0)
        {
            throw new CommandRefusedException("nothing_open", $"Parent unit {parent.UnitId} has no open links to close.");
        }

        var drafts = new List<UnitEventDraft>();
        var closed = await AssemblyLinks.CloseAsync(db, writer, tenantId, parent, targets, command.EventTime, command.ActorRef, command.LegalEntityId, command.Document, command.Reason ?? "disassembly", command.CommandId, drafts, cancellationToken);
        var remaining = await db.AssemblyLinks.AsNoTracking().CountAsync(l => l.TenantId == tenantId && l.ParentUnitId == parent.UnitId && l.RemovedAt == null && !closed.Contains(l.LinkId), cancellationToken);
        var next = remaining == 0 && parent.LifecycleStatus == LifecycleStatus.Released ? UnitLifecycle.Require(parent.LifecycleStatus, UnitEventType.Disassembled, parent.Origin, parent.UnitId) : parent.LifecycleStatus;
        drafts.Add(new UnitEventDraft
        {
            UnitId = parent.UnitId, EventTime = command.EventTime, EventType = UnitEventType.Disassembled, BizStep = "disassembling", LegalEntityId = command.LegalEntityId, ActorRef = command.ActorRef,
            DocumentType = command.Document?.Type, DocumentId = command.Document?.Id, DocumentLine = command.Document?.Line, IdempotencyKey = key,
            Attributes = JsonObjectText.Of(new { command_id = command.CommandId, closed_link_ids = closed, reason = command.Reason, remaining_open_links = remaining }),
            Delta = next == parent.LifecycleStatus ? null : new CurrentStateDelta { LifecycleStatus = next, Disposition = UnitLifecycle.DispositionFor(next) },
        });
        await writer.AppendAsync(drafts, cancellationToken);
        parent.LifecycleStatus = next;
        return new RecordDisassemblyResult(parent.UnitId, closed, next);
    }
}

/// <summary>Shared pieces of the two handlers.</summary>
internal static class AssemblyLinks
{
    /// <summary>Closes the given open links of the parent, writing a removal event on each serialised child; returns the ids closed.</summary>
    public static async Task<List<Guid>> CloseAsync(SerialisationDbContext db, IUnitEventWriter writer, TenantId tenantId, Unit parent, IReadOnlyList<Guid> linkIds, DateTime at, string actorRef, Guid legalEntityId, DocumentRef? document, string reason, Guid commandId, List<UnitEventDraft> drafts, CancellationToken cancellationToken)
    {
        var links = await db.AssemblyLinks.Where(l => l.TenantId == tenantId && l.ParentUnitId == parent.UnitId && linkIds.Contains(l.LinkId)).ToListAsync(cancellationToken);
        var missing = linkIds.Except(links.Select(l => l.LinkId)).ToList();
        if (missing.Count > 0)
        {
            throw new CommandRefusedException("link_unknown", $"Link(s) {string.Join(", ", missing)} are not links of parent {parent.UnitId}.");
        }

        var alreadyClosed = links.Where(l => l.RemovedAt is not null).Select(l => l.LinkId).ToList();
        if (alreadyClosed.Count > 0)
        {
            throw new CommandRefusedException("link_already_closed", $"Link(s) {string.Join(", ", alreadyClosed)} were already removed.");
        }

        var removingEventId = TimeOrderedId.New(at);
        foreach (var link in links)
        {
            link.RemovedAt = at;
            link.RemovingEventId = removingEventId;
            if (link.ChildUnitId is { } childUnitId)
            {
                drafts.Add(new UnitEventDraft
                {
                    UnitId = childUnitId, EventTime = at, EventType = UnitEventType.Removed, BizStep = "removing", LegalEntityId = legalEntityId, ActorRef = actorRef,
                    DocumentType = document?.Type, DocumentId = document?.Id, DocumentLine = document?.Line,
                    IdempotencyKey = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, commandId, childUnitId, UnitEventType.Removed, at),
                    Attributes = JsonObjectText.Of(new { command_id = commandId, parent_unit_id = parent.UnitId, link_id = link.LinkId, reason }),
                    // A removed child is a loose unit again; its custody follows the parent's location until a custody command says otherwise (case 36).
                    Delta = new CurrentStateDelta { Disposition = UnitDisposition.Available, Attributes = new Dictionary<string, System.Text.Json.JsonElement?> { ["parent_unit_id"] = null } },
                });
            }
        }

        return links.Select(l => l.LinkId).ToList();
    }

    /// <summary>Every unit above the given one in the open tree (its parent, the parent's parent, …).</summary>
    public static async Task<HashSet<Guid>> AncestorsAsync(SerialisationDbContext db, TenantId tenantId, Guid unitId, CancellationToken cancellationToken)
    {
        var ancestors = new HashSet<Guid>();
        var current = unitId;
        while (true)
        {
            var parent = await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenantId && l.ChildUnitId == current && l.RemovedAt == null).Select(l => (Guid?)l.ParentUnitId).FirstOrDefaultAsync(cancellationToken);
            if (parent is null || !ancestors.Add(parent.Value))
            {
                return ancestors;
            }

            current = parent.Value;
        }
    }
}
