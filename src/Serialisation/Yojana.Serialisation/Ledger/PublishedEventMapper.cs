using System.Text.Json;
using Wolverine;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Ledger;

/// <summary>
/// Turns the ledger events other contexts care about into the published events (P1-T16): called by the writer after an
/// append with the rows it wrote, and publishes through the message bus. Inside a Wolverine handler the bus is the
/// transactional outbox: the messages leave only when the handler's unit of work commits. The mapping reads the event's
/// attributes (what the command recorded) and never a table, so publishing cannot see uncommitted state of another unit.
/// </summary>
public sealed class PublishedEventMapper(IMessageBus bus, ITenantContext tenant)
{
    public async Task PublishAsync(IReadOnlyList<UnitEvent> written, CancellationToken cancellationToken)
    {
        foreach (var @event in written)
        {
            var message = Map(@event);
            if (message is not null)
            {
                await bus.PublishAsync(message, new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
            }
        }
    }

    public static IPublishedUnitEvent? Map(UnitEvent e)
    {
        using var doc = e.Attributes is null ? null : JsonDocument.Parse(e.Attributes);
        var a = doc?.RootElement;
        var document = e.DocumentType is null || e.DocumentId is null ? null : new DocumentRef(e.DocumentType, e.DocumentId.Value, e.DocumentLine);
        PublishedEventHeader Header(string schema, string version) => new(schema, version, e.EventId, e.UnitId, e.EventTime, e.RecordedTime, e.Seq, e.LegalEntityId, e.ActorRef, document);
        switch (e.EventType)
        {
            case UnitEventType.Commissioned:
            {
                var state = a?.TryGetProperty("_state", out var s) == true ? s : default;
                var create = state.ValueKind == JsonValueKind.Object && state.TryGetProperty("create", out var c) ? c : default;
                return new UnitCommissioned(Header(UnitCommissioned.Schema, UnitCommissioned.Version),
                    create.ValueKind == JsonValueKind.Object ? create.GetProperty("item_id").GetGuid() : Guid.Empty,
                    Str(a, "origin") ?? "", create.ValueKind == JsonValueKind.Object ? create.GetProperty("lifecycle_status").GetString() ?? "" : "",
                    Identifiers(a), GuidOrNull(create, "lot_id"), e.LocationId, state.ValueKind == JsonValueKind.Object ? Optional(state, "location_kind") : null);
            }

            case UnitEventType.Released:
                return new UnitReleased(Header(UnitReleased.Schema, UnitReleased.Version), GuidOrNull(a, "item_id") ?? Guid.Empty, GuidOrNull(a, "build_record_id") ?? Guid.Empty, a?.TryGetProperty("build_record_version", out var v) == true ? v.GetInt32() : 0, Str(a, "payload_hash") ?? "", Identifiers(a));
            case UnitEventType.IdentifierCorrected:
            {
                var before = a?.GetProperty("before");
                var after = a?.GetProperty("after");
                return new UnitIdentifierCorrected(Header(UnitIdentifierCorrected.Schema, UnitIdentifierCorrected.Version), Str(a, "identifier_type") ?? "", Str(a, "reason") ?? "", Str(before, "normalised_value") ?? "", Str(after, "normalised_value") ?? "",
                    GuidOrNull(before, "identifier_id") ?? Guid.Empty, GuidOrNull(after, "identifier_id") ?? Guid.Empty, Str(a, "approver_ref"));
            }

            case UnitEventType.OwnershipOpened or UnitEventType.OwnershipTransferred:
                return new OwnershipOpened(Header(OwnershipOpened.Schema, OwnershipOpened.Version), GuidOrNull(a, "episode_id") ?? Guid.Empty, GuidOrNull(a, "party_ref") ?? Guid.Empty, Str(a, "party_type") ?? "", Str(a, "reason") ?? "", Str(a, "source") ?? "", Str(a, "confidence") ?? "", Str(a, "customer_po_ref"), Str(a, "customer_item_ref"), GuidOrNull(a, "previous_episode_id"));
            case UnitEventType.OwnershipClosed:
                return new OwnershipClosed(Header(OwnershipClosed.Schema, OwnershipClosed.Version), GuidOrNull(a, "episode_id") ?? Guid.Empty, GuidOrNull(a, "party_ref"), Str(a, "party_type"), Str(a, "reason") ?? "", GuidOrNull(a, "replaced_by_episode_id"));
            case UnitEventType.CustodyChanged or UnitEventType.Shipped or UnitEventType.Delivered:
            {
                if (a?.TryGetProperty("to", out var to) != true || to.ValueKind != JsonValueKind.Object) return null;
                var from = a?.TryGetProperty("from", out var f) == true && f.ValueKind == JsonValueKind.Object ? f : (JsonElement?)null;
                return new CustodyChanged(Header(CustodyChanged.Schema, CustodyChanged.Version), GuidOrNull(a, "episode_id") ?? Guid.Empty, Str(to, "location_kind") ?? "", GuidOrNull(to, "location_id"), GuidOrNull(to, "party_ref"), Str(to, "party_type"), Str(from, "location_kind"), GuidOrNull(from, "location_id"), Str(a, "reason") ?? "", e.EventType);
            }

            default:
                return null;
        }
    }

    private static string? Str(JsonElement? element, string name) => element is { ValueKind: JsonValueKind.Object } el && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>Delta members of type Optional&lt;T&gt; serialise as {"value": …}; a plain string is accepted too.</summary>
    private static string? Optional(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Object when p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String => v.GetString(),
            _ => null,
        };
    }

    private static Guid? GuidOrNull(JsonElement? element, string name) => element is { ValueKind: JsonValueKind.Object } el && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && Guid.TryParse(p.GetString(), out var g) ? g : null;

    private static List<PublishedIdentifier> Identifiers(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } el || !el.TryGetProperty("identifiers", out var ids) || ids.ValueKind != JsonValueKind.Array) return [];
        return ids.EnumerateArray().Select(i => new PublishedIdentifier(Str(i, "type") ?? "", Str(i, "scheme") ?? "", Str(i, "value") ?? "", GuidOrNull(i, "issuer_party_ref"))).ToList();
    }
}
