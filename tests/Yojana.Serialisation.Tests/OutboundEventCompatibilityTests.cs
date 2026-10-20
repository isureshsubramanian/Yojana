using System.Text.Json;
using Xunit;
using Yojana.Serialisation.Replacements;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Tests;

public sealed class OutboundEventCompatibilityTests
{
    [Theory]
    [InlineData(ClaimAdjudicated.Schema, "1.2", "1.3", "currency")]
    [InlineData(ReplacementShipped.Schema, "1.0", "1.1", "customer_party_ref")]
    [InlineData(ReplacementReturnOverdue.Schema, "1.0", "1.1", "non_return_amount")]
    [InlineData(RmaAuthorised.Schema, "1.0", "1.1", "customer_party_ref")]
    [InlineData(RmaDispositioned.Schema, "1.0", "1.1", "currency")]
    [InlineData(RmaReceived.Schema, "1.0", "1.1", "source_location_id")]
    [InlineData(RmaDispositioned.Schema, "1.1", "1.2", "source_location_id")]
    [InlineData(RefurbishmentReleased.Schema, "1.0", "1.1", "source_location_id")]
    public void Added_fields_are_optional_and_previous_schemas_remain_unchanged(string schema, string previous, string current, string field)
    {
        using var old = JsonDocument.Parse(PublishedEventSchemas.Read(schema, previous));
        using var added = JsonDocument.Parse(PublishedEventSchemas.Read(schema, current));
        Assert.False(old.RootElement.GetProperty("properties").TryGetProperty(field, out _));
        Assert.True(added.RootElement.GetProperty("properties").TryGetProperty(field, out _));
        Assert.DoesNotContain(field, added.RootElement.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void Document_content_reference_has_one_shared_format_contract()
    {
        var id = Guid.NewGuid();
        Assert.True(OutboundDocumentContentReference.TryDocumentId(OutboundDocumentContentReference.ForDocument(id), out var read));
        Assert.Equal(id, read);
        Assert.False(OutboundDocumentContentReference.TryDocumentId("other/" + id, out _));
        Assert.False(OutboundDocumentContentReference.TryDocumentId(null, out _));
    }

    [Fact]
    public void Previous_payloads_without_the_optional_fields_still_deserialise()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var id = Guid.NewGuid();
        var header = new PublishedEventHeader("test", "1.0", id, id, new(2026, 9, 1), new(2026, 9, 1), 1, id, "test", null);
        T Previous<T>(T value, params string[] added)
        {
            var payload = JsonSerializer.SerializeToNode(value, options)!.AsObject();
            foreach (var field in added) payload.Remove(field);
            var result = JsonSerializer.Deserialize<T>(payload.ToJsonString(), options)!;
            Assert.Equal(id, ((IPublishedUnitEvent)result!).Header.EventId);
            return result;
        }
        Assert.Null(Previous(new RmaReceived(header, id, id, id, id, id, null, "accepted", "new", "standard", "2026-09"), "source_location_id").SourceLocationId);
        Assert.Null(Previous(new RefurbishmentReleased(header, id, "REF-1", id, id, "repair", "new", 1, "same", 0, "GBP"), "source_location_id").SourceLocationId);
        var claim = Previous(new ClaimAdjudicated(header, id, null, "warranty", [new(id, 10, 10, 100, "warranty", null)], id, id, id, id), "currency");
        Assert.Null(claim.Currency);
        Assert.Equal(10, Assert.Single(claim.ApprovedAmountsByLine).ApprovedAmount);
        var replacement = Previous(new ReplacementShipped(header, id, "REP-1", id, id, id, id, id, id, id, "claim", id, "CLAIM-1", "warranty",
            "zero", 0, "GBP", "standard", new(2027, 9, 1), "new", false, 0), "customer_party_ref");
        Assert.Null(replacement.CustomerPartyRef);
        Assert.Equal("REP-1", replacement.ReplacementNumber);
        var overdue = Previous(new ReplacementReturnOverdue(header, id, id, id, id, new(2026, 9, 1), "full", 1), "non_return_amount", "currency");
        Assert.Null(overdue.NonReturnAmount);
        Assert.Null(overdue.Currency);
        Assert.Null(Previous(new RmaAuthorised(header, id, id, "RMA-1", id, id, new("invoice", id), 100, "warranty", null), "customer_party_ref").CustomerPartyRef);
        Assert.Null(Previous(new RmaDispositioned(header, id, id, id, "credit", null, null), "currency", "customer_party_ref").Currency);
    }
}

public sealed class OutboundEventMappingTests
{
    [Fact]
    public void Ledger_publication_preserves_known_money_and_customer_facts()
    {
        var customer = Guid.NewGuid();
        var unit = Guid.NewGuid();
        Yojana.Serialisation.Data.Entities.UnitEvent Event(string type, object facts) => new()
        {
            EventType = type, EventId = Guid.NewGuid(), UnitId = unit, ActorRef = "test", IdempotencyKey = "test",
            EventTime = new(2026, 9, 1), RecordedTime = new(2026, 9, 1),
            Attributes = JsonSerializer.Serialize(facts),
        };
        var claim = Assert.IsType<ClaimAdjudicated>(Yojana.Serialisation.Ledger.PublishedEventMapper.Map(
            Event(ClaimEventType.Adjudicated, new { currency = "GBP" })));
        Assert.Equal("GBP", claim.Currency);
        Assert.Equal("1.3", claim.Header.Version);
        var overdue = Assert.IsType<ReplacementReturnOverdue>(Yojana.Serialisation.Ledger.PublishedEventMapper.Map(
            Event(ReplacementEventType.ReturnOverdue, new { non_return_amount = 123.45m, currency = "GBP" })));
        Assert.Equal(123.45m, overdue.NonReturnAmount);
        Assert.Equal("GBP", overdue.Currency);
        var replacement = Assert.IsType<ReplacementShipped>(Yojana.Serialisation.Ledger.PublishedEventMapper.Map(
            Event(ReplacementEventType.Shipped, new { replacement_unit_id = unit, customer_party_ref = customer })));
        Assert.Equal(customer, replacement.CustomerPartyRef);
        var rma = Assert.IsType<RmaDispositioned>(Yojana.Serialisation.Ledger.PublishedEventMapper.Map(
            Event(RmaEventType.Dispositioned, new { currency = "GBP", customer_party_ref = customer })));
        Assert.Equal("GBP", rma.Currency);
        Assert.Equal(customer, rma.CustomerPartyRef);
        var authorised = Assert.IsType<RmaAuthorised>(Yojana.Serialisation.Ledger.PublishedEventMapper.Map(
            Event(RmaEventType.Authorised, new { customer_party_ref = customer })));
        Assert.Equal(customer, authorised.CustomerPartyRef);
    }
}
