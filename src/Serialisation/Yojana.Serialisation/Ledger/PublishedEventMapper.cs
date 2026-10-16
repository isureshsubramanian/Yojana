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
            foreach (var message in new[] { Map(@event), CoverageChanged(@event) }.OfType<IPublishedUnitEvent>())
            {
                await bus.PublishAsync(message, new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
            }
        }
    }

    /// <summary>
    /// <c>Entitlement.CoverageChanged</c> beside any entitlement fact that changed the unit's live set or latest warranty
    /// end: the before values are what <see cref="Entitlements.EntitlementLedger"/> recorded under <c>coverage_before</c>,
    /// the after values the event's own delta.
    /// </summary>
    public static EntitlementCoverageChanged? CoverageChanged(UnitEvent e)
    {
        if (e.Attributes is null || !e.EventType.StartsWith("entitlement.", StringComparison.Ordinal)) return null;
        using var doc = JsonDocument.Parse(e.Attributes);
        var a = doc.RootElement;
        if (!a.TryGetProperty("coverage_before", out var before) || !a.TryGetProperty("_state", out var state) || !state.TryGetProperty("live_entitlement_ids", out var afterIds)) return null;
        var idsBefore = Guids(before, "live_entitlement_ids");
        var idsAfter = afterIds.EnumerateArray().Select(i => i.GetGuid()).Order().ToList();
        var endBefore = Date(before, "warranty_end_max");
        var endAfter = state.TryGetProperty("warranty_end_max", out var w) && w.ValueKind == JsonValueKind.Object ? Date(w, "value") : Date(state, "warranty_end_max");
        if (idsBefore.SequenceEqual(idsAfter) && endBefore == endAfter) return null;
        return new EntitlementCoverageChanged(Header(e, EntitlementCoverageChanged.Schema, EntitlementCoverageChanged.Version), idsBefore, idsAfter, endBefore, endAfter, e.EventType);
    }

    public static IPublishedUnitEvent? Map(UnitEvent e)
    {
        using var doc = e.Attributes is null ? null : JsonDocument.Parse(e.Attributes);
        var a = doc?.RootElement;
        var document = e.DocumentType is null || e.DocumentId is null ? null : new DocumentRef(e.DocumentType, e.DocumentId.Value, e.DocumentLine);
        PublishedEventHeader Header(string schema, string version) => PublishedEventMapper.Header(e, schema, version);
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

            case EntitlementEventType.Created:
                if (Str(a, "provenance") == EntitlementProvenance.Backfilled)
                {
                    return new EntitlementBackfilled(Header(EntitlementBackfilled.Schema, EntitlementBackfilled.Version), GuidOrNull(a, "entitlement_id") ?? Guid.Empty,
                        Str(a, "start_basis") ?? "", Date(a, "start_date") ?? default, Date(a, "end_date") ?? default,
                        a?.TryGetProperty("evidence_refs", out var evidence) == true ? evidence.Deserialize<List<string>>(LedgerJson.Options) ?? [] : [],
                        GuidOrNull(a, "accrual_cohort_id"), a?.TryGetProperty("finance_review_required", out var review) == true && review.ValueKind == JsonValueKind.True, document);
                }
                return new EntitlementCreated(Header(EntitlementCreated.Schema, EntitlementCreated.Version), GuidOrNull(a, "entitlement_id") ?? Guid.Empty, GuidOrNull(a, "item_id") ?? Guid.Empty,
                    Str(a, "coverage_family") ?? "", Str(a, "entitlement_type") ?? "", e.LegalEntityId, GuidOrNull(a, "owner_party_ref"), GuidOrNull(a, "policy_version_id") ?? Guid.Empty, Str(a, "policy_code") ?? "",
                    Int(a, "policy_version") ?? 0, Date(a, "start_date"), Date(a, "end_date"), Str(a, "status") ?? "", GuidOrNull(a, "accrual_cohort_id"), Str(a, "assurance_or_service") ?? "",
                    a?.TryGetProperty("accrual_suppressed", out var suppressed) == true && suppressed.ValueKind == JsonValueKind.True, Str(a, "provenance") ?? "", document,
                    a?.TryGetProperty("matched_refurbished_row", out var matched) == true && matched.ValueKind == JsonValueKind.Object
                        ? new RefurbishedEntitlementDisclosure(Int(a, "term_months") ?? Int(matched, "duration_months") ?? 0,
                            a?.TryGetProperty("floor_applied", out var floorApplied) == true && floorApplied.ValueKind == JsonValueKind.True,
                            Int(a, "floor_months"), Str(a, "floor_source"), GuidOrNull(a, "refurbisher_party_ref"), Str(a, "statement_kind") ?? RefurbishedWarrantyStatement.NoOemWarranty,
                            matched.Deserialize<RefurbishedTerm>(LedgerJson.Options)!) : null);
            case EntitlementEventType.Transferred:
                return new EntitlementTransferred(Header(EntitlementTransferred.Schema, EntitlementTransferred.Version), GuidOrNull(a, "source_entitlement_id") ?? Guid.Empty,
                    GuidOrNull(a, "target_entitlement_id") ?? Guid.Empty, GuidOrNull(a, "source_unit_id") ?? Guid.Empty, GuidOrNull(a, "target_unit_id") ?? Guid.Empty,
                    GuidOrNull(a, "replacement_link_id"), Str(a, "policy_applied") ?? "", Date(a, "computed_end") ?? default, GuidOrNull(a, "root_entitlement_id") ?? Guid.Empty,
                    Int(a, "chain_length") ?? 0, a?.TryGetProperty("accrual_suppressed", out var transferSuppressed) == true && transferSuppressed.ValueKind == JsonValueKind.True);
            case EntitlementEventType.ExtendedSold:
                return new EntitlementExtendedSold(Header(EntitlementExtendedSold.Schema, EntitlementExtendedSold.Version), GuidOrNull(a, "entitlement_id") ?? Guid.Empty, GuidOrNull(a, "item_id") ?? Guid.Empty,
                    Str(a, "coverage_family") ?? "", e.LegalEntityId, GuidOrNull(a, "payer_party_ref"), a?.TryGetProperty("price", out var price) == true ? price.GetDecimal() : 0m, Str(a, "currency") ?? "",
                    Int(a, "term_months") ?? 0, Date(a, "start_date") ?? default, Date(a, "end_date") ?? default,
                    a?.TryGetProperty("revenue_schedule", out var schedule) == true ? schedule.Deserialize<List<EntitlementRevenueSchedulePeriod>>(LedgerJson.Options) ?? [] : [],
                    document ?? new DocumentRef("", Guid.Empty, null));
            // The first start of a pending entitlement is published as a rebase from no start, so Finance sees one shape for every start change.
            case EntitlementEventType.Rebased or EntitlementEventType.EvidenceAttached:
            {
                var before = a?.TryGetProperty("before", out var b) == true ? b : (JsonElement?)null;
                var after = a?.TryGetProperty("after", out var f) == true ? f : (JsonElement?)null;
                return new EntitlementRebased(Header(EntitlementRebased.Schema, EntitlementRebased.Version), GuidOrNull(a, "entitlement_id") ?? Guid.Empty, Date(before, "start"), Date(after, "start") ?? default,
                    Date(before, "end"), Date(after, "end") ?? default, Str(after, "basis") ?? "", Str(a, "evidence_ref"), a?.TryGetProperty("cap_applied", out var cap) == true && cap.ValueKind == JsonValueKind.True);
            }

            case EntitlementEventType.Amended:
                return new EntitlementAmended(Header(EntitlementAmended.Schema, EntitlementAmended.Version), GuidOrNull(a, "entitlement_id") ?? Guid.Empty, Str(a, "reason_code") ?? "", Int(a, "days_changed"),
                    Str(a, "approved_by"), Str(a, "customer_notification_ref"),
                    a?.TryGetProperty("changes", out var changes) == true ? changes.Deserialize<List<EntitlementFieldChange>>(LedgerJson.Options) ?? [] : [], document);

            // A void is a close with a void reason (wrong unit, duplicate, fraud): Finance reverses on the reason, not on a separate schema.
            case EntitlementEventType.Closed or EntitlementEventType.Voided:
                return new EntitlementClosed(Header(EntitlementClosed.Schema, EntitlementClosed.Version), GuidOrNull(a, "entitlement_id") ?? Guid.Empty, Str(a, "coverage_family") ?? "", Str(a, "reason") ?? "",
                    Date(a, "effective") ?? DateOnly.FromDateTime(e.EventTime), GuidOrNull(a, "successor_entitlement_id"), GuidOrNull(a, "successor_unit_id"));
            default:
                return null;
        }
    }

    private static PublishedEventHeader Header(UnitEvent e, string schema, string version) =>
        new(schema, version, e.EventId, e.UnitId, e.EventTime, e.RecordedTime, e.Seq, e.LegalEntityId, e.ActorRef, e.DocumentType is null || e.DocumentId is null ? null : new DocumentRef(e.DocumentType, e.DocumentId.Value, e.DocumentLine));

    private static int? Int(JsonElement? element, string name) => element is { ValueKind: JsonValueKind.Object } el && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;

    private static DateOnly? Date(JsonElement? element, string name) => Str(element, name) is { } text && DateOnly.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var date) ? date : null;

    private static List<Guid> Guids(JsonElement element, string name) => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().Select(i => i.GetGuid()).Order().ToList() : [];

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
