using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Tests;

public sealed class VoidUnitRulesTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("production_order", false)]
    [InlineData("production_receipt", false)]
    [InlineData("transfer", false)]
    [InlineData("invoice", true)]
    [InlineData("delivery", true)]
    [InlineData("connector_defined_document", true)]
    public void Unknown_document_kinds_are_conservatively_external(string? documentType, bool expected) =>
        Assert.Equal(expected, UnitCorrectionFacts.IsExternalDocument(documentType));

    [Fact]
    public void Voided_ledger_fact_maps_to_the_published_contract()
    {
        var correctionId = Guid.NewGuid();
        var identifierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var fact = new UnitEvent
        {
            TenantId = new(Guid.NewGuid()), EventTime = DateTime.UtcNow, EventId = Guid.NewGuid(), UnitId = Guid.NewGuid(), Seq = 7,
            EventType = UnitEventType.Voided, LegalEntityId = Guid.NewGuid(), ActorRef = "supervisor", DocumentType = "production_order",
            DocumentId = orderId, IdempotencyKey = "void-map", RecordedTime = DateTime.UtcNow,
            Attributes = JsonObjectText.Of(new
            {
                correction_id = correctionId,
                reason_code = VoidUnitReason.WorkOrderQuantityReduced,
                initiated_via = CorrectionInitiatedVia.Workbench,
                corrects_correction_id = (Guid?)null,
                approvers = new[] { new { user_ref = "approver-1", roles = new[] { "production_supervisor" } } },
                closed_identifier_ids = new[] { identifierId },
                serial_reuse_outcome = SerialReuseOutcome.NeverReuse,
            }).Json,
        };

        var published = Assert.IsType<UnitVoided>(PublishedEventMapper.Map(fact));
        Assert.Equal((correctionId, identifierId, SerialReuseOutcome.NeverReuse, orderId),
            (published.Correction.CorrectionId, Assert.Single(published.ClosedIdentifierIds), published.SerialReuseOutcome, published.ProductionOrder!.Id));
        Assert.Equal(["approver-1"], published.Correction.ApproverRefs);
    }

    [Fact]
    public void Void_reversed_ledger_fact_maps_to_its_own_published_contract()
    {
        var correctionId = Guid.NewGuid();
        var voidCorrectionId = Guid.NewGuid();
        var identifierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var fact = new UnitEvent
        {
            TenantId = new(Guid.NewGuid()), EventTime = DateTime.UtcNow, EventId = Guid.NewGuid(), UnitId = Guid.NewGuid(), Seq = 8,
            EventType = UnitEventType.VoidReversed, LegalEntityId = Guid.NewGuid(), ActorRef = "supervisor", DocumentType = "production_order",
            DocumentId = orderId, IdempotencyKey = "void-reversed-map", RecordedTime = DateTime.UtcNow,
            Attributes = JsonObjectText.Of(new
            {
                correction_id = correctionId,
                reason_code = VoidUnitReason.WorkOrderQuantityReduced,
                initiated_via = CorrectionInitiatedVia.Workbench,
                corrects_correction_id = voidCorrectionId,
                approvers = new[] { new { user_ref = "approver-1", roles = new[] { "production_supervisor" } } },
                restored_identifier_ids = new[] { identifierId },
                lifecycle_status = LifecycleStatus.PreRelease,
                serial_reuse_outcome = SerialReuseOutcome.VoidReversed,
            }).Json,
        };

        var published = Assert.IsType<UnitVoidReversed>(PublishedEventMapper.Map(fact));
        Assert.Equal((correctionId, voidCorrectionId, identifierId, LifecycleStatus.PreRelease, orderId),
            (published.Correction.CorrectionId, published.Correction.CorrectsCorrectionId,
                Assert.Single(published.RestoredIdentifierIds), published.RestoredLifecycleStatus, published.ProductionOrder!.Id));
        Assert.Equal(["approver-1"], published.Correction.ApproverRefs);
    }
}
