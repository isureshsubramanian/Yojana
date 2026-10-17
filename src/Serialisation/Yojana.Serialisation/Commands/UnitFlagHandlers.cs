using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Commands;

/// <summary>Overlay flags (ADR-0001 PhysicalUnit `overlay_flags`; case 30): a lost or stolen unit that reappears (its flag cleared after a custody fact) raises an investigation task; the events stay.</summary>
public static class UnitFlagHandler
{
    private static readonly string[] Known = [OverlayFlag.Lost, OverlayFlag.Stolen, OverlayFlag.WrittenOff, OverlayFlag.CloneSeen, OverlayFlag.Recalled, OverlayFlag.AuthenticityFailed, OverlayFlag.DocumentMismatch];

    public static Task<UnitFlagResult> Handle(SetUnitFlag command, SerialisationDbContext db, IUnitEventWriter writer, ITenantContext tenant, CancellationToken ct) =>
        ApplyAsync(command.CommandId, command.UnitId, command.Flag, true, command.EventTime, command.ActorRef, command.LegalEntityId, command.Reason, command.Document, db, writer, tenant, ct);

    public static Task<UnitFlagResult> Handle(ClearUnitFlag command, SerialisationDbContext db, IUnitEventWriter writer, ITenantContext tenant, CancellationToken ct) =>
        ApplyAsync(command.CommandId, command.UnitId, command.Flag, false, command.EventTime, command.ActorRef, command.LegalEntityId, command.Reason, command.Document, db, writer, tenant, ct);

    private static async Task<UnitFlagResult> ApplyAsync(Guid commandId, Guid unitId, string flag, bool set, DateTime at, string actorRef, Guid legalEntityId, string? reason, DocumentRef? document, SerialisationDbContext db, IUnitEventWriter writer, ITenantContext tenant, CancellationToken ct)
    {
        if (!Known.Contains(flag)) throw new CommandRefusedException("flag_unknown", $"'{flag}' is not an overlay flag ({string.Join(", ", Known)}).");
        var tenantId = tenant.TenantId;
        var unit = await db.Units.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UnitId == unitId, ct) ?? throw new CommandRefusedException("unit_unknown", $"Unit {unitId} does not exist.");
        var eventType = set ? UnitEventType.FlagSet : UnitEventType.FlagCleared;
        var key = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, commandId, unitId, eventType, at);
        var flags = JsonSerializer.Deserialize<List<string>>(unit.OverlayFlags) ?? [];
        if (await db.Events.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.IdempotencyKey == key && e.EventTime == at, ct))
        {
            return new UnitFlagResult(unitId, flags, false, false);
        }

        var changed = set ? !flags.Contains(flag) : flags.Remove(flag);
        if (set && changed) flags.Add(flag);
        if (!changed) return new UnitFlagResult(unitId, flags, false, false);

        // A lost or stolen unit that comes back is an investigation, not a quiet reappearance (case 30).
        var investigation = false;
        if (!set && flag is OverlayFlag.Lost or OverlayFlag.Stolen)
        {
            var flaggedAt = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId && e.EventType == UnitEventType.FlagSet).OrderByDescending(e => e.EventTime).Select(e => (DateTime?)e.EventTime).FirstOrDefaultAsync(ct) ?? unit.CreatedAt;
            investigation = await db.Events.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.UnitId == unitId && (e.EventType == UnitEventType.CustodyChanged || e.EventType == UnitEventType.Delivered || e.EventType == UnitEventType.Shipped) && e.EventTime >= flaggedAt, ct);
        }

        unit.OverlayFlags = JsonSerializer.Serialize(flags);
        await writer.AppendAsync(
        [
            new UnitEventDraft
            {
                UnitId = unitId, EventTime = at, EventType = eventType, LegalEntityId = legalEntityId, ActorRef = actorRef, DocumentType = document?.Type, DocumentId = document?.Id, DocumentLine = document?.Line, IdempotencyKey = key,
                Attributes = JsonObjectText.Of(new { command_id = commandId, flag, reason, investigation_raised = investigation, flags_after = flags }),
                Delta = new CurrentStateDelta { OverlayFlags = flags },
            },
        ], ct);
        return new UnitFlagResult(unitId, flags, true, investigation);
    }
}
