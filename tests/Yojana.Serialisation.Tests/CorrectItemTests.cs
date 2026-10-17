using System.Text.Json;
using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Projections;

namespace Yojana.Serialisation.Tests;

/// <summary>P3-T04: the in-memory coupling seam is idempotent and the ledger fact maps to Unit.ItemCorrected.</summary>
public sealed class CorrectItemTests
{
    [Fact]
    public async Task Default_stock_port_records_one_request_per_correction()
    {
        var port = new InMemoryStockReclassificationPort();
        var correction = Guid.NewGuid();
        var request = new StockReclassificationRequest(Guid.NewGuid(), correction, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), LocationKind.Warehouse, UnitDisposition.Active, DateTime.UtcNow, "inventory", CorrectItemReason.WrongItemAtReceipt);

        var first = await port.ReclassifyAsync(request, CancellationToken.None);
        var replay = await port.ReclassifyAsync(request, CancellationToken.None);

        Assert.Equal(first, replay);
        Assert.Equal([request], port.Requests);
        var conflict = await Assert.ThrowsAsync<CommandRefusedException>(() => port.ReclassifyAsync(request with { CorrectedItemId = Guid.NewGuid() }, CancellationToken.None));
        Assert.Equal("stock_reclassification_conflict", conflict.Rule);
    }

    [Fact]
    public void Item_correction_fact_maps_every_published_field()
    {
        var previousItem = Guid.NewGuid();
        var correctedItem = Guid.NewGuid();
        var previousAssignment = Guid.NewGuid();
        var newAssignment = Guid.NewGuid();
        var correction = Guid.NewGuid();
        var priorCorrection = Guid.NewGuid();
        var @event = new UnitEvent
        {
            TenantId = new(Guid.NewGuid()), EventId = Guid.NewGuid(), UnitId = Guid.NewGuid(), EventTime = DateTime.UtcNow,
            RecordedTime = DateTime.UtcNow, Seq = 12, EventType = UnitEventType.ItemCorrected, LegalEntityId = Guid.NewGuid(),
            ActorRef = "supervisor", IdempotencyKey = "key",
            Attributes = JsonSerializer.Serialize(new
            {
                correction_id = correction,
                reason_code = CorrectItemReason.ConfigurationMismatch,
                initiated_via = CorrectionInitiatedVia.Workbench,
                corrects_correction_id = priorCorrection,
                approvers = new[] { new { user_ref = "supervisor", roles = new[] { "service_supervisor" } } },
                before = new { item_id = previousItem, assignment_id = previousAssignment },
                after = new { item_id = correctedItem, assignment_id = newAssignment },
                document_mismatch = true,
                inventory_reclassification_ref = (string?)null,
                warranty_policy_changed = true,
            }, LedgerJson.Options),
        };

        var mapped = Assert.IsType<UnitItemCorrected>(PublishedEventMapper.Map(@event));
        Assert.Equal((previousItem, correctedItem, previousAssignment, newAssignment),
            (mapped.PreviousItemId, mapped.CorrectedItemId, mapped.PreviousAssignmentId, mapped.NewAssignmentId));
        Assert.True(mapped.DocumentMismatch);
        Assert.True(mapped.WarrantyPolicyChanged);
        Assert.Equal(correction, mapped.Correction.CorrectionId);
        Assert.Equal(priorCorrection, mapped.Correction.CorrectsCorrectionId);
        Assert.Equal(["supervisor"], mapped.Correction.ApproverRefs);
    }

    [Fact]
    public void Item_correction_can_explicitly_clear_the_current_model_version()
    {
        var current = new UnitCurrent
        {
            TenantId = new(Guid.NewGuid()), UnitId = Guid.NewGuid(), ItemId = Guid.NewGuid(), ModelVersionId = Guid.NewGuid(),
            LifecycleStatus = LifecycleStatus.Released, Disposition = UnitDisposition.Active,
        };

        UnitCurrentStateProjector.Fold(current, new CurrentStateDelta { ClearModelVersionId = true });

        Assert.Null(current.ModelVersionId);
    }
}
