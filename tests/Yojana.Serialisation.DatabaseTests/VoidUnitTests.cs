using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P3-T07: cases 119 and 137, ticket T3, serial re-use and correction reversal.</summary>
[Collection("serialisation host")]
public sealed class VoidUnitTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc);
    private static readonly CorrectionCommitter Supervisor = new("production-supervisor", [YojanaRole.ProductionSupervisor]);

    private InMemoryItemPorts Items => fixture.Services.GetRequiredService<InMemoryItemPorts>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<(Guid UnitId, Guid ItemId, string Serial)> CommissionAsync(TenantId tenant, string marker, string? itemClass = null,
        string origin = UnitOrigin.Manufactured, DocumentRef? document = null)
    {
        var item = Guid.NewGuid();
        var serial = marker + "-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Items.Register(InMemoryItemPorts.DefaultProfile(item) with { ItemClass = itemClass });
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, origin,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], At, "line", Entity, Document: document));
        return (result.Lines[0].UnitId!.Value, item, serial);
    }

    private static VoidUnit Command(Guid unitId, Guid correctionId, DateTime at, Guid? corrects = null) =>
        new(Guid.NewGuid(), unitId, VoidUnitReason.WorkOrderQuantityReduced, at, Supervisor.UserRef, Entity,
            new DocumentRef("production_order", Guid.NewGuid()), "quantity reduced", new(correctionId,
                VoidUnitReason.WorkOrderQuantityReduced, "quantity reduced", ["work-order:change"],
                CorrectionInitiatedVia.Workbench, corrects, CorrectionInitiatingRole.ProductionSupervisor), Supervisor);

    [DatabaseFact]
    public async Task Preview_is_dry_and_components_must_be_reverse_issued_before_void_is_recorded_and_published()
    {
        var tenant = await fixture.NewTenantAsync("vu");
        var parent = await CommissionAsync(tenant, "VU-P");
        var child = await CommissionAsync(tenant, "VU-C");
        var productionOrder = new DocumentRef("production_order", Guid.NewGuid());
        var assembly = await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), parent.UnitId,
            [new ChildInput(child.UnitId, Relation: AssemblyRelation.Component, Slot: "drive-1")], At.AddMinutes(10), "line", Entity, Document: productionOrder));
        var correctionId = Guid.NewGuid();
        var command = Command(parent.UnitId, correctionId, At.AddHours(1)) with { ProductionOrder = productionOrder };

        var blocked = await InvokeAsync<CorrectionPreview>(tenant, new PreviewVoidUnit(command));
        Assert.Equal((0, 1, "components_still_issued"), (blocked.Change, blocked.Block, blocked.Refusal!.Rule));
        Assert.Contains("Manufacturing", blocked.Refusal.UnblockingStep);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(LifecycleStatus.PreRelease, (await db.Units.SingleAsync(u => u.UnitId == parent.UnitId)).LifecycleStatus);
            Assert.False(await db.Corrections.AnyAsync(c => c.CorrectionId == correctionId));
        }

        await InvokeAsync<RecordDisassemblyResult>(tenant, new RecordDisassembly(Guid.NewGuid(), parent.UnitId,
            assembly.LinkIds, At.AddMinutes(30), "manufacturing", Entity, "reverse_issue", productionOrder));
        var preview = await InvokeAsync<CorrectionPreview>(tenant, new PreviewVoidUnit(command));
        Assert.Equal((1, 0, UnitEventType.Voided), (preview.Change, preview.Block, Assert.Single(preview.UnitEvents).EventType));
        var result = await InvokeAsync<VoidUnitResult>(tenant, command);
        Assert.Equal((LifecycleStatus.PreRelease, LifecycleStatus.Void, SerialReuseOutcome.NeverReuse, false),
            (result.PreviousLifecycleStatus, result.LifecycleStatus, result.SerialReuseOutcome, result.Reversed));
        Assert.Single(result.IdentifierIds);

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(LifecycleStatus.Void, (await db.Units.SingleAsync(u => u.UnitId == parent.UnitId)).LifecycleStatus);
            Assert.False(await db.Identifiers.AnyAsync(i => i.UnitId == parent.UnitId && i.ValidTo == null));
            Assert.Equal(2, await db.Units.CountAsync(u => u.UnitId == parent.UnitId || u.UnitId == child.UnitId));
            Assert.Single(await db.Corrections.Where(c => c.CorrectionId == correctionId).ToListAsync());
            Assert.Single(await db.Events.Where(e => e.UnitId == parent.UnitId && e.EventType == UnitEventType.Voided).ToListAsync());
        }

        var published = Assert.Single(await PublishedEventSink.WaitForAsync<UnitVoided>(e => e.Header.UnitId == parent.UnitId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((correctionId, SerialReuseOutcome.NeverReuse, productionOrder.Id),
            (published.Correction.CorrectionId, published.SerialReuseOutcome, published.ProductionOrder!.Id));
        Assert.Equal(result.IdentifierIds, published.ClosedIdentifierIds);
    }

    [DatabaseFact]
    public async Task A_unit_that_ever_shipped_or_appeared_on_an_external_document_is_refused_by_the_named_rule()
    {
        var tenant = await fixture.NewTenantAsync("vx");
        var external = await CommissionAsync(tenant, "VX-E", document: new DocumentRef("invoice", Guid.NewGuid(), 1));
        var externalRefusal = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<VoidUnitResult>(tenant, Command(external.UnitId, Guid.NewGuid(), At.AddHours(1))));
        Assert.Equal("unit_on_external_document", externalRefusal.Rule);

        var shipped = await CommissionAsync(tenant, "VX-S", origin: UnitOrigin.Purchased);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var writer = scope.ServiceProvider.GetRequiredService<IUnitEventWriter>();
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            await writer.AppendAsync([new UnitEventDraft
            {
                UnitId = shipped.UnitId, EventTime = At.AddHours(2), EventType = UnitEventType.Shipped,
                LegalEntityId = Entity, ActorRef = "shipping", DocumentType = "delivery", DocumentId = Guid.NewGuid(), DocumentLine = 1,
                IdempotencyKey = "void-test-shipped-" + shipped.UnitId.ToString("N"), Attributes = JsonObjectText.Of(new { test = true }),
            }], CancellationToken.None);
            await db.SaveChangesAsync();
        }
        var shippedRefusal = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<VoidUnitResult>(tenant, Command(shipped.UnitId, Guid.NewGuid(), At.AddHours(3))));
        Assert.Equal("unit_ever_shipped", shippedRefusal.Rule);
    }

    [DatabaseFact]
    public async Task Reversal_restores_status_with_new_identifier_history_but_is_refused_after_allowed_serial_reuse()
    {
        var tenant = await fixture.NewTenantAsync("vr");
        var ordinary = await CommissionAsync(tenant, "VR-O");
        var firstCorrection = Guid.NewGuid();
        var voided = await InvokeAsync<VoidUnitResult>(tenant, Command(ordinary.UnitId, firstCorrection, At.AddHours(1)));
        var reversalCorrection = Guid.NewGuid();
        var reversed = await InvokeAsync<VoidUnitResult>(tenant, Command(ordinary.UnitId, reversalCorrection, At.AddHours(2), firstCorrection));
        Assert.Equal((LifecycleStatus.PreRelease, true, SerialReuseOutcome.VoidReversed),
            (reversed.LifecycleStatus, reversed.Reversed, reversed.SerialReuseOutcome));
        Assert.NotEqual(Assert.Single(voided.IdentifierIds), Assert.Single(reversed.IdentifierIds));
        var publishedReversal = Assert.Single(await PublishedEventSink.WaitForAsync<UnitVoidReversed>(
            e => e.Header.UnitId == ordinary.UnitId && e.Correction.CorrectionId == reversalCorrection, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((firstCorrection, LifecycleStatus.PreRelease, Assert.Single(reversed.IdentifierIds)),
            (publishedReversal.Correction.CorrectsCorrectionId, publishedReversal.RestoredLifecycleStatus,
                Assert.Single(publishedReversal.RestoredIdentifierIds)));
        Assert.DoesNotContain(PublishedEventSink.Received.OfType<UnitVoided>(), e => e.Correction.CorrectionId == reversalCorrection);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var restored = await db.Identifiers.SingleAsync(i => i.IdentifierId == reversed.IdentifierIds[0]);
            Assert.Equal(voided.IdentifierIds[0], restored.SupersedesIdentifierId);
            Assert.Equal(firstCorrection, (await db.Corrections.SingleAsync(c => c.CorrectionId == reversalCorrection)).CorrectsCorrectionId);
        }

        var reusable = await CommissionAsync(tenant, "VR-R", "accessory");
        using (var scope = fixture.ScopeFor(tenant))
        {
            await scope.ServiceProvider.GetRequiredService<SerialReuseSettings>().SetAsync(new(SerialReusePolicyValue.Never,
                [new("accessory", SerialReusePolicyValue.NeverShippedAndNotExternal)]), CancellationToken.None);
        }
        var reusableVoidId = Guid.NewGuid();
        var eligible = await InvokeAsync<VoidUnitResult>(tenant, Command(reusable.UnitId, reusableVoidId, At.AddHours(3)));
        Assert.Equal(SerialReuseOutcome.EligibleForReuse, eligible.SerialReuseOutcome);
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), reusable.ItemId, UnitOrigin.Manufactured,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, reusable.Serial)])], At.AddHours(4), "line", Entity));
        var derived = commissioned.Lines[0].UnitId!.Value;
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(reusable.UnitId, (await db.Units.SingleAsync(u => u.UnitId == derived)).DerivedFromUnitId);
        }

        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<VoidUnitResult>(tenant,
            Command(reusable.UnitId, Guid.NewGuid(), At.AddHours(5), reusableVoidId)));
        Assert.Equal("serial_reused", refusal.Rule);
    }

    [DatabaseFact]
    public async Task Tenant_default_never_refuses_commissioning_with_a_voided_serial()
    {
        var tenant = await fixture.NewTenantAsync("vn");
        var original = await CommissionAsync(tenant, "VN");
        await InvokeAsync<VoidUnitResult>(tenant, Command(original.UnitId, Guid.NewGuid(), At.AddHours(1)));
        var attempt = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), original.ItemId, UnitOrigin.Manufactured,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, original.Serial)])], At.AddHours(2), "line", Entity,
            SkipInvalidLines: true));
        var line = Assert.Single(attempt.Lines);
        Assert.Equal(CommissionLineResult.Refused, line.Outcome);
        Assert.Contains(line.Reasons, reason => reason.Contains("serial_reuse_forbidden", StringComparison.Ordinal));
    }
}
