using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Commands;

/// <summary>Connector boundary for attribute history; firmware campaigns read the resulting ledger-backed state.</summary>
public static class RecordUnitAttributesHandler
{
    public static async Task<RecordUnitAttributesResult> Handle(
        RecordUnitAttributes command,
        SerialisationDbContext db,
        IUnitEventWriter writer,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        if (command.Attributes.Count == 0)
            throw new CommandRefusedException("unit_attributes_required", "At least one unit attribute is required.");
        if (command.Attributes.Keys.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 100))
            throw new CommandRefusedException("unit_attribute_key_invalid", "Unit attribute keys must contain 1 to 100 non-blank characters.");
        var normalised = command.Attributes.Select(pair => new KeyValuePair<string, string?>(pair.Key.Trim(), pair.Value)).ToList();
        if (normalised.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Count() != normalised.Count)
            throw new CommandRefusedException("unit_attribute_key_duplicate", "Unit attribute keys must be unique after surrounding whitespace is removed.");
        if (normalised.Any(pair => pair.Value?.Length > 2000))
            throw new CommandRefusedException("unit_attribute_value_invalid", "Unit attribute values cannot exceed 2000 characters.");

        var tenantId = tenant.TenantId;
        if (!await db.Units.AsNoTracking().AnyAsync(unit => unit.TenantId == tenantId && unit.UnitId == command.UnitId, cancellationToken))
            throw new CommandRefusedException("unit_unknown", $"Unit {command.UnitId} does not exist.");

        var key = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, command.UnitId, UnitEventType.AttributeChanged, command.EventTime);
        var replay = await db.Events.AsNoTracking().FirstOrDefaultAsync(
            @event => @event.TenantId == tenantId && @event.IdempotencyKey == key && @event.EventTime == command.EventTime,
            cancellationToken);
        if (replay is not null)
            return new(command.UnitId, command.Attributes, replay.EventId, true);

        var eventId = TimeOrderedId.New(command.EventTime);
        var changes = normalised.ToDictionary(
            pair => pair.Key,
            pair => pair.Value is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(pair.Value),
            StringComparer.Ordinal);
        db.AttributeHistory.AddRange(normalised.Select(pair => new UnitAttributeHistory
        {
            TenantId = tenantId,
            AttributeHistoryId = TimeOrderedId.New(command.EventTime),
            CommandId = command.CommandId,
            UnitId = command.UnitId,
            Key = pair.Key,
            Value = pair.Value,
            EffectiveFrom = command.EventTime,
            ActorRef = command.ActorRef,
            EventId = eventId,
        }));
        await writer.AppendAsync(
        [
            new UnitEventDraft
            {
                UnitId = command.UnitId,
                EventTime = command.EventTime,
                EventType = UnitEventType.AttributeChanged,
                PresetEventId = eventId,
                LegalEntityId = command.LegalEntityId,
                ActorRef = command.ActorRef,
                DocumentType = command.Document?.Type,
                DocumentId = command.Document?.Id,
                DocumentLine = command.Document?.Line,
                IdempotencyKey = key,
                Attributes = JsonObjectText.Of(new { command_id = command.CommandId, attributes = command.Attributes }),
                Delta = new CurrentStateDelta { Attributes = changes },
            },
        ], cancellationToken);
        return new(command.UnitId, command.Attributes, eventId, false);
    }
}
