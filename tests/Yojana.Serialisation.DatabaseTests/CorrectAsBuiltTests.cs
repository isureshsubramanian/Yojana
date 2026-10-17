using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P3-T05: atomic serial-list as-built correction, shared preview, interval guard, publication and reversal.</summary>
[Collection("serialisation host")]
public sealed class CorrectAsBuiltTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<Guid> UnitAsync(TenantId tenant, string serial)
    {
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), Guid.NewGuid(), UnitOrigin.Manufactured,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], At, "line", Entity));
        return result.Lines[0].UnitId!.Value;
    }

    private static AsBuiltUnitReference Serial(string value) => new(Serial: value, IdentifierType: IdentifierType.InternalSerial);
    private static CorrectionCommitter Production(string user = "production-1") => new(user, [YojanaRole.ProductionSupervisor]);

    [DatabaseFact]
    public async Task Preview_and_atomic_bulk_write_one_fact_per_parent_and_publish_the_changes()
    {
        var tenant = await fixture.NewTenantAsync("cab");
        var prefix = "CAB" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var parent1Serial = $"{prefix}-P1";
        var parent2Serial = $"{prefix}-P2";
        var child1Serial = $"{prefix}-C1";
        var child2Serial = $"{prefix}-C2";
        var child3Serial = $"{prefix}-C3";
        var parent1 = await UnitAsync(tenant, parent1Serial);
        var parent2 = await UnitAsync(tenant, parent2Serial);
        await UnitAsync(tenant, child1Serial);
        await UnitAsync(tenant, child2Serial);
        await UnitAsync(tenant, child3Serial);
        var correctionId = Guid.NewGuid();
        var effective = At.AddDays(1);
        var command = new CorrectAsBuilt(Guid.NewGuid(),
        [
            new(Serial(parent1Serial), effective,
            [
                new(AsBuiltCorrectionChange.Add, Installed: new(Unit: Serial(child1Serial), Slot: "drive-1")),
                new(AsBuiltCorrectionChange.Add, Installed: new(Unit: Serial(child2Serial), Slot: "drive-2")),
            ]),
            new(Serial(parent2Serial), effective.AddHours(1),
            [new(AsBuiltCorrectionChange.Add, Installed: new(Unit: Serial(child3Serial), Slot: "drive-1"))]),
        ], CorrectAsBuiltReason.ComponentNotCaptured, "production-1", Entity,
            new(correctionId, CorrectAsBuiltReason.ComponentNotCaptured, EvidenceRefs: ["photo:as-built"], InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor), Production());

        var preview = await InvokeAsync<CorrectionPreview>(tenant, new PreviewCorrectAsBuilt(command));
        Assert.Equal(2, preview.Change);
        Assert.Equal(5, preview.UnitEvents.Count);
        Assert.Empty(preview.CertificateVersions);
        using (var scope = fixture.ScopeFor(tenant))
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().AssemblyLinks.Where(l => l.ParentUnitId == parent1 || l.ParentUnitId == parent2).ToListAsync());

        var result = await InvokeAsync<CorrectAsBuiltResult>(tenant, command);
        Assert.Equal(2, result.ParentCount);
        Assert.Equal(3, result.ChangeCount);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var links = await db.AssemblyLinks.Where(l => l.ParentUnitId == parent1 || l.ParentUnitId == parent2).ToListAsync();
            Assert.Equal(3, links.Count);
            Assert.All(links, link => Assert.True(link.RecordedAt > link.InstalledAt));
            var facts = await db.Events.Where(e => (e.UnitId == parent1 || e.UnitId == parent2) && e.EventType == UnitEventType.AsBuiltCorrected).ToListAsync();
            Assert.Equal(2, facts.Count);
            Assert.All(facts, fact => Assert.Equal(correctionId, JsonDocument.Parse(fact.Attributes!).RootElement.GetProperty("correction_id").GetGuid()));
            var correction = await db.Corrections.SingleAsync(c => c.CorrectionId == correctionId);
            Assert.Equal(2, correction.UnitCount);
        }

        var published = await PublishedEventSink.WaitForAsync<UnitAsBuiltCorrected>(e => e.Correction.CorrectionId == correctionId, 2, TimeSpan.FromSeconds(5));
        Assert.Equal(2, published.Count);
        Assert.Equal(2, published.Single(e => e.Header.UnitId == parent1).Changes!.Count);
    }

    [DatabaseFact]
    public async Task Back_dated_overlap_is_refused_by_name_and_writes_nothing()
    {
        var tenant = await fixture.NewTenantAsync("cao");
        var prefix = "CAO" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var parent1 = await UnitAsync(tenant, $"{prefix}-P1");
        var parent2 = await UnitAsync(tenant, $"{prefix}-P2");
        var child = await UnitAsync(tenant, $"{prefix}-C");
        var installed = await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), parent1, [new ChildInput(child)], At.AddHours(1), "line", Entity));
        await InvokeAsync<RecordDisassemblyResult>(tenant, new RecordDisassembly(Guid.NewGuid(), parent1, installed.LinkIds, At.AddDays(3), "line", Entity));
        var correctionId = Guid.NewGuid();
        var command = new CorrectAsBuilt(Guid.NewGuid(),
        [
            new(new(UnitId: parent1), At.AddDays(2), [new(AsBuiltCorrectionChange.Add, Installed: new(ItemId: Guid.NewGuid(), Quantity: 1))]),
            new(new(UnitId: parent2), At.AddDays(2), [new(AsBuiltCorrectionChange.Add, Installed: new(Unit: new(UnitId: child), EffectiveUntil: At.AddDays(4)))]),
        ],
            CorrectAsBuiltReason.WrongComponentRecorded, "quality-1", Entity,
            new(correctionId, CorrectAsBuiltReason.WrongComponentRecorded, InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor),
            new("quality-1", [YojanaRole.Quality]));

        var preview = await InvokeAsync<CorrectionPreview>(tenant, new PreviewCorrectAsBuilt(command));
        Assert.Equal("child_effective_interval_overlap", preview.Refusal!.Rule);
        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CorrectAsBuiltResult>(tenant, command));
        Assert.Equal("child_effective_interval_overlap", refusal.Rule);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.False(await db.Corrections.AnyAsync(c => c.CorrectionId == correctionId));
        Assert.False(await db.Events.AnyAsync(e => e.UnitId == parent1 && e.EventType == UnitEventType.AsBuiltCorrected));
        Assert.False(await db.Events.AnyAsync(e => e.UnitId == parent2 && e.EventType == UnitEventType.AsBuiltCorrected));
    }

    [DatabaseFact]
    public async Task A_shipped_parent_needs_a_distinct_second_approver_from_the_tenant_policy()
    {
        var tenant = await fixture.NewTenantAsync("caa");
        var prefix = "CAA" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var parent = await UnitAsync(tenant, $"{prefix}-P");
        var child = await UnitAsync(tenant, $"{prefix}-C");
        using (var scope = fixture.ScopeFor(tenant))
        {
            var writer = scope.ServiceProvider.GetRequiredService<IUnitEventWriter>();
            await writer.AppendAsync([new UnitEventDraft
            {
                UnitId = parent, EventTime = At.AddHours(2), EventType = UnitEventType.Shipped, LegalEntityId = Entity, ActorRef = "shipping",
                IdempotencyKey = $"test:shipped:{parent}", Attributes = JsonObjectText.Of(new { reason = "test shipment" }),
            }], CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().SaveChangesAsync();
        }

        var correctionId = Guid.NewGuid();
        var metadata = new CorrectionMetadata(correctionId, CorrectAsBuiltReason.ComponentNotCaptured, InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor);
        var command = new CorrectAsBuilt(Guid.NewGuid(),
            [new(new(UnitId: parent), At.AddDays(1), [new(AsBuiltCorrectionChange.Add, Installed: new(Unit: new(UnitId: child)))])],
            CorrectAsBuiltReason.ComponentNotCaptured, "production-1", Entity, metadata, Production());

        var preview = await InvokeAsync<CorrectionPreview>(tenant, new PreviewCorrectAsBuilt(command));
        Assert.Equal("approval_required", preview.Refusal!.Rule);
        Assert.NotEmpty(preview.UnitEvents);
        var refused = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CorrectAsBuiltResult>(tenant, command));
        Assert.Equal("approval_required", refused.Rule);

        var approved = command with { Correction = metadata with { Approvers = [new("quality-2", [YojanaRole.Quality])] } };
        Assert.False((await InvokeAsync<CorrectAsBuiltResult>(tenant, approved)).Replayed);
    }

    [DatabaseFact]
    public async Task A_later_correction_can_reference_and_move_an_earlier_corrections_effective_time_backwards()
    {
        var tenant = await fixture.NewTenantAsync("car");
        var prefix = "CAR" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var parent = await UnitAsync(tenant, $"{prefix}-P");
        var child = await UnitAsync(tenant, $"{prefix}-C");
        var assembled = await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), parent, [new ChildInput(child)], At.AddHours(1), "line", Entity));
        var firstId = Guid.NewGuid();
        var first = new CorrectAsBuilt(Guid.NewGuid(),
            [new(new(UnitId: parent), At.AddDays(5), [new(AsBuiltCorrectionChange.Remove, assembled.LinkIds[0])])],
            CorrectAsBuiltReason.WrongComponentRecorded, "production-1", Entity,
            new(firstId, CorrectAsBuiltReason.WrongComponentRecorded, InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor), Production());
        await InvokeAsync<CorrectAsBuiltResult>(tenant, first);

        var secondId = Guid.NewGuid();
        var second = new CorrectAsBuilt(Guid.NewGuid(),
            [new(new(UnitId: parent), At.AddDays(4), [new(AsBuiltCorrectionChange.Remove, assembled.LinkIds[0])])],
            CorrectAsBuiltReason.WrongComponentRecorded, "production-1", Entity,
            new(secondId, CorrectAsBuiltReason.WrongComponentRecorded, CorrectsCorrectionId: firstId, InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor), Production());
        await InvokeAsync<CorrectAsBuiltResult>(tenant, second);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(At.AddDays(4), (await db.AssemblyLinks.SingleAsync(l => l.LinkId == assembled.LinkIds[0])).RemovedAt);
        Assert.Equal(firstId, (await db.Corrections.SingleAsync(c => c.CorrectionId == secondId)).CorrectsCorrectionId);
    }
}
