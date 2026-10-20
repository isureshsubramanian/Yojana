using System.Text.Json;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Replacements;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P5-T16: keeps every MOD-08 publication wired through the same four contract surfaces.</summary>
public sealed class P5PublishedEventContractTests
{
    public static TheoryData<string, string, Type, string, string> Contracts => new()
    {
        { RmaAuthorised.Schema, RmaAuthorised.Version, typeof(RmaAuthorised), RmaEventType.Authorised,
            """{"rma_id":"10000000-0000-0000-0000-000000000001","rma_line_id":"10000000-0000-0000-0000-000000000002","rma_number":"RMA-FICTIONAL-1","authorised_unit_id":"10000000-0000-0000-0000-000000000003","ownership_episode_id":"10000000-0000-0000-0000-000000000004","original_sale_ref":{"type":"delivery","id":"10000000-0000-0000-0000-000000000005","line":1},"coverage_pct":100,"coverage_source":"warranty","entitlement_id":"10000000-0000-0000-0000-000000000006"}""" },
        { RmaReceived.Schema, RmaReceived.Version, typeof(RmaReceived), RmaEventType.Received,
            """{"rma_id":"20000000-0000-0000-0000-000000000001","rma_line_id":"20000000-0000-0000-0000-000000000002","return_receipt_id":"20000000-0000-0000-0000-000000000003","authorised_unit_id":"20000000-0000-0000-0000-000000000004","received_unit_id":"20000000-0000-0000-0000-000000000004","outcome":"accepted","condition_grade":"defective","valuation_rule":"graded_value","posting_period":"2026-09"}""" },
        { RmaDispositioned.Schema, RmaDispositioned.Version, typeof(RmaDispositioned), RmaEventType.Dispositioned,
            """{"rma_id":"30000000-0000-0000-0000-000000000001","rma_line_id":"30000000-0000-0000-0000-000000000002","unit_id":"30000000-0000-0000-0000-000000000003","disposition":"repair_and_return"}""" },
        { ReplacementShipped.Schema, ReplacementShipped.Version, typeof(ReplacementShipped), ReplacementEventType.Shipped,
            """{"warranty_replacement_id":"40000000-0000-0000-0000-000000000001","replacement_number":"WR-FICTIONAL-1","replacement_link_id":"40000000-0000-0000-0000-000000000002","original_unit_id":"40000000-0000-0000-0000-000000000003","replacement_unit_id":"40000000-0000-0000-0000-000000000004","original_item_id":"40000000-0000-0000-0000-000000000005","replacement_item_id":"40000000-0000-0000-0000-000000000006","source_entitlement_id":"40000000-0000-0000-0000-000000000007","target_entitlement_id":"40000000-0000-0000-0000-000000000008","source_kind":"claim","source_id":"40000000-0000-0000-0000-000000000009","source_number":"CLM-FICTIONAL-1","reason":"warranty_defect","price_policy":"zero","difference_amount":0,"currency":"EUR","policy_applied":"STD-90","computed_end":"2027-09-01","replacement_condition":"new","accrual_suppressed":true,"restocking_fee_amount":0}""" },
        { ReplacementReturnOverdue.Schema, ReplacementReturnOverdue.Version, typeof(ReplacementReturnOverdue), ReplacementEventType.ReturnOverdue,
            """{"replacement_link_id":"50000000-0000-0000-0000-000000000001","original_unit_id":"50000000-0000-0000-0000-000000000002","replacement_unit_id":"50000000-0000-0000-0000-000000000003","party_ref":"50000000-0000-0000-0000-000000000004","return_due":"2026-09-01","non_return_price_basis":"list","days_overdue":2}""" },
        { RefurbishmentReleased.Schema, RefurbishmentReleased.Version, typeof(RefurbishmentReleased), RefurbishmentEventType.Released,
            """{"work_order_id":"60000000-0000-0000-0000-000000000001","work_order_number":"RFW-FICTIONAL-1","source_unit_id":"60000000-0000-0000-0000-000000000002","released_unit_id":"60000000-0000-0000-0000-000000000002","kind":"refurbish","grade":"refurbished_a","cycle_number":1,"identity_policy":"same_unit_same_serial","total_cost":900,"currency":"EUR"}""" },
        { UnitGraded.Schema, UnitGraded.Version, typeof(UnitGraded), UnitEventType.Graded,
            """{"grade":"refurbished_a","f_code":"F2","c_code":"C3","valuation_rule":"graded_receipt_40pct"}""" },
        { ReplacementLinkCorrected.Schema, ReplacementLinkCorrected.Version, typeof(ReplacementLinkCorrected), ReplacementEventType.LinkCorrected,
            """{"correction_id":"70000000-0000-0000-0000-000000000001","reason_code":"wrong_replacement_unit","initiated_via":"api","approvers":[{"user_ref":"service.manager"}],"warranty_replacement_id":"70000000-0000-0000-0000-000000000002","previous_replacement_link_id":"70000000-0000-0000-0000-000000000003","corrected_replacement_link_id":"70000000-0000-0000-0000-000000000004","original_unit_id":"70000000-0000-0000-0000-000000000005","recorded_replacement_unit_id":"70000000-0000-0000-0000-000000000006","actual_replacement_unit_id":"70000000-0000-0000-0000-000000000007","voided_entitlement_id":"70000000-0000-0000-0000-000000000008","target_entitlement_id":"70000000-0000-0000-0000-000000000009","replacement_reason":"warranty_defect","policy_applied":"STD-90","computed_end":"2027-09-01","asset_tag_repointed":true}""" },
    };

    [Theory]
    [MemberData(nameof(Contracts))]
    public void Mod_08_contract_has_schema_mapper_record_and_sink(string schema, string version, Type contractType,
        string eventType, string attributes)
    {
        using var document = JsonDocument.Parse(PublishedEventSchemas.Read(schema, version));
        Assert.Equal(schema, document.RootElement.GetProperty("title").GetString());
        var expectedVersion = schema == RmaDispositioned.Schema ? "1.2"
            : schema is RmaAuthorised.Schema or RmaReceived.Schema or RefurbishmentReleased.Schema or ReplacementShipped.Schema or ReplacementReturnOverdue.Schema ? "1.1" : "1.0";
        Assert.Equal(expectedVersion, version);

        // Replacement.Shipped leaves only from the replacement unit's copy of the fact (the original unit records it too),
        // so the audit event carries the replacement unit wherever the attributes name one.
        using var body = JsonDocument.Parse(attributes);
        var unitId = body.RootElement.TryGetProperty("replacement_unit_id", out var replacementUnit) ? replacementUnit.GetGuid() : Guid.NewGuid();
        var mapped = PublishedEventMapper.Map(new UnitEvent
        {
            TenantId = new TenantId(Guid.NewGuid()), EventId = Guid.NewGuid(), UnitId = unitId, Seq = 1,
            EventType = eventType, EventTime = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
            RecordedTime = new(2026, 9, 1, 9, 0, 1, DateTimeKind.Utc), LegalEntityId = Guid.NewGuid(),
            ActorRef = "contract-audit", IdempotencyKey = Guid.NewGuid().ToString("N"), Attributes = attributes,
        });
        Assert.IsType(contractType, mapped);
        Assert.Equal(schema, mapped!.Header.Schema);
        Assert.Equal(version, mapped.Header.Version);
        Assert.NotNull(typeof(PublishedEventSinkHandler).GetMethod(nameof(PublishedEventSinkHandler.Handle), [contractType]));
    }
}
