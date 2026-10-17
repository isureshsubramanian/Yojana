using Xunit;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Tests;

public sealed class CorrectAsBuiltRulesTests
{
    private static readonly DateTime At = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Effective_intervals_overlap_in_the_middle_but_not_at_a_shared_boundary()
    {
        Assert.True(CorrectAsBuiltPlanner.EffectiveIntervalsOverlap(At, At.AddHours(3), At.AddHours(2), At.AddHours(4)));
        Assert.True(CorrectAsBuiltPlanner.EffectiveIntervalsOverlap(At, null, At.AddYears(1), null));
        Assert.False(CorrectAsBuiltPlanner.EffectiveIntervalsOverlap(At, At.AddHours(2), At.AddHours(2), At.AddHours(4)));
    }

    [Fact]
    public void Mapper_publishes_the_complete_multi_component_parent_fact()
    {
        var correctionId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var attributes = CorrectionLedger.Attributes(
            new(correctionId, CorrectAsBuiltReason.ComponentNotCaptured, InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor),
            new
            {
                change = AsBuiltCorrectionChange.Add,
                effective_at = At,
                changes = new[]
                {
                    new { change = AsBuiltCorrectionChange.Add, installed = new { assembly_link_id = first, child_unit_id = Guid.NewGuid(), relation = AssemblyRelation.Component, slot = "1" } },
                    new { change = AsBuiltCorrectionChange.Add, installed = new { assembly_link_id = second, child_unit_id = Guid.NewGuid(), relation = AssemblyRelation.Component, slot = "2" } },
                },
            });
        var fact = new UnitEvent
        {
            TenantId = new(Guid.NewGuid()), EventTime = At, EventId = Guid.NewGuid(), UnitId = Guid.NewGuid(), Seq = 1,
            EventType = UnitEventType.AsBuiltCorrected, LegalEntityId = Guid.NewGuid(), ActorRef = "production-1",
            RecordedTime = At.AddDays(1), IdempotencyKey = "test", Attributes = attributes.Json,
        };

        var published = Assert.IsType<UnitAsBuiltCorrected>(PublishedEventMapper.Map(fact));
        Assert.Equal(correctionId, published.Correction.CorrectionId);
        Assert.Equal([first, second], published.Changes!.Select(c => c.Installed!.AssemblyLinkId));
    }
}
