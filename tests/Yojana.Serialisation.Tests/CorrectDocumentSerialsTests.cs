using System.Text.Json;
using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.Tests;

/// <summary>P3-T12 rules that do not need the database.</summary>
public sealed class CorrectDocumentSerialsTests
{
    [Fact]
    public void Correction_metadata_wraps_prepared_episode_attributes_without_losing_them()
    {
        var correctionId = Guid.NewGuid();
        var original = JsonObjectText.Of(new { episode_id = Guid.NewGuid(), to = new { location_kind = LocationKind.Warehouse } });
        var merged = CorrectionLedger.Attributes(new(correctionId, CorrectDocumentSerialsReason.ShippedWrongUnits), original);

        using var json = JsonDocument.Parse(merged.Json);
        Assert.Equal(LocationKind.Warehouse, json.RootElement.GetProperty("to").GetProperty("location_kind").GetString());
        Assert.Equal(correctionId, json.RootElement.GetProperty("correction_id").GetGuid());
    }

    [Fact]
    public async Task The_coupling_port_accepts_only_a_zero_quantity_delta()
    {
        var port = new InMemoryStockUnitEventPort();
        var correction = new StockPostingSerialCorrection(new("delivery", Guid.NewGuid()), Guid.NewGuid(), 0,
            [Guid.NewGuid()], [Guid.NewGuid()], Guid.NewGuid());
        await port.RecordCorrectionForPosting(correction, CancellationToken.None);
        Assert.Same(correction, Assert.Single(port.Corrections));

        var refused = await Assert.ThrowsAsync<CommandRefusedException>(() => port.RecordCorrectionForPosting(correction with { QuantityDelta = 1 }, CancellationToken.None));
        Assert.Equal("document_correction_quantity_delta", refused.Rule);
        Assert.NotNull(refused.UnblockingStep);
    }

    [Fact]
    public void Shipment_reversal_is_published_as_the_existing_per_unit_custody_contract()
    {
        var unitId = Guid.NewGuid();
        var document = new DocumentRef("delivery", Guid.NewGuid(), 3);
        var row = new UnitEvent
        {
            EventId = Guid.NewGuid(), UnitId = unitId, EventTime = DateTime.UtcNow, RecordedTime = DateTime.UtcNow,
            EventType = UnitEventType.ShipmentReversed, Seq = 4, LegalEntityId = Guid.NewGuid(), ActorRef = "shipping",
            DocumentType = document.Type, DocumentId = document.Id, DocumentLine = document.Line, IdempotencyKey = "key",
            Attributes = JsonObjectText.Of(new
            {
                episode_id = Guid.NewGuid(), reason = EpisodeReason.DocumentCorrection,
                from = new { location_kind = LocationKind.CustomerSite, location_id = Guid.NewGuid() },
                to = new { location_kind = LocationKind.Warehouse, location_id = Guid.NewGuid() },
                correction_of_document_id = document.Id,
            }).Json,
        };

        var published = Assert.IsType<CustodyChanged>(PublishedEventMapper.Map(row));
        Assert.Equal(UnitEventType.ShipmentReversed, published.TriggerEventType);
        Assert.Equal(EpisodeReason.DocumentCorrection, published.Reason);
        Assert.Equal(document, published.Header.Document);
    }
}
