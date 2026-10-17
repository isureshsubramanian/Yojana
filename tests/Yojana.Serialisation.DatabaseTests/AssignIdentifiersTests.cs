using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Queries;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P3-T10 / case 135: partial vendor lists, collisions, supplements, approval and reversal.</summary>
[Collection("serialisation host")]
public sealed class AssignIdentifiersTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Partial_list_reports_collisions_flags_the_exact_remainder_and_a_supplement_completes_it()
    {
        var tenant = await fixture.NewTenantAsync("ai");
        var item = Guid.NewGuid();
        var scheme = RegisterScheme(item);
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var units = await ProvisionalUnitsAsync(tenant, item, order, 3);
        var otherOrder = new DocumentRef("production_order", Guid.NewGuid());
        var collision = scheme.Format(0, 91);
        var other = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured,
            [new CommissionLine([new IdentifierInput(IdentifierType.VendorSerial, collision)])], At, "line", Entity, Document: otherOrder));

        var correctionId = Guid.NewGuid();
        var command = Command(order, correctionId, IdentifierAssignmentMode.AllocationOrder,
            [new(scheme.Format(0, 11)), new(collision), new("BAD")], AssignIdentifiersReason.VendorList, At.AddHours(1));
        var preview = await InvokeAsync<AssignIdentifiersPreview>(tenant, new PreviewAssignIdentifiers(command));
        Assert.Equal(units[0], preview.Lines[0].UnitId);
        Assert.Equal(units[1], preview.Lines[1].UnitId);
        Assert.Equal("identifier_in_use", preview.Lines[1].Reason);
        Assert.Equal(other.Lines[0].UnitId, preview.Lines[1].ConflictingUnitId);
        Assert.Equal("value_not_in_item_serial_scheme", preview.Lines[2].Reason);
        Assert.Equal(2, preview.RemainingCount);

        var result = await InvokeAsync<AssignIdentifiersResult>(tenant, command);
        Assert.Equal(1, result.Assigned);
        Assert.Equal(2, result.RemainingCount);
        Assert.Equal(new[] { units[1], units[2] }.Order().ToList(), result.AwaitingVendorSerialUnitIds.Order().ToList());

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var firstIdentifiers = await db.Identifiers.Where(i => i.UnitId == units[0]).OrderBy(i => i.ValidFrom).ToListAsync();
            Assert.Equal(2, firstIdentifiers.Count);
            Assert.Equal(IdentifierType.Provisional, firstIdentifiers[0].Type);
            Assert.Equal(At.AddHours(1), firstIdentifiers[0].ValidTo);
            Assert.Equal(IdentifierType.VendorSerial, firstIdentifiers[1].Type);
            Assert.Null(firstIdentifiers[1].ValidTo);
            var flagged = await db.Units.Where(u => result.AwaitingVendorSerialUnitIds.Contains(u.UnitId)).ToListAsync();
            Assert.All(flagged, u => Assert.Contains(OverlayFlag.AwaitingVendorSerial, JsonSerializer.Deserialize<List<string>>(u.OverlayFlags)!));
            Assert.Equal(3, (await db.Corrections.SingleAsync(c => c.CorrectionId == correctionId)).UnitCount);
            var facts = await db.Events.Where(e => units.Contains(e.UnitId) && (e.EventType == UnitEventType.IdentifierAssigned || e.EventType == UnitEventType.FlagSet) && e.EventTime == At.AddHours(1)).ToListAsync();
            Assert.Equal(3, facts.Count);
            Assert.All(facts, fact =>
            {
                using var attributes = JsonDocument.Parse(fact.Attributes!);
                Assert.Equal(correctionId, attributes.RootElement.GetProperty("correction_id").GetGuid());
            });
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            var report = await scope.ServiceProvider.GetRequiredService<AwaitingVendorSerialQueries>().ByProductionOrderAsync(order, CancellationToken.None);
            Assert.Equal(2, report.Count);
            Assert.Equal(new[] { units[1], units[2] }.Order().ToList(), report.Units.Select(u => u.UnitId).Order().ToList());
        }

        var supplement = Command(order, Guid.NewGuid(), IdentifierAssignmentMode.ExplicitPairs,
            [new(scheme.Format(0, 12), units[1]), new(scheme.Format(0, 13), units[2])], AssignIdentifiersReason.VendorListSupplement, At.AddHours(2));
        var completed = await InvokeAsync<AssignIdentifiersResult>(tenant, supplement);
        Assert.Equal(2, completed.Assigned);
        Assert.Equal(0, completed.RemainingCount);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var rows = await db.Units.Where(u => units.Contains(u.UnitId)).ToListAsync();
            Assert.All(rows, u => Assert.DoesNotContain(OverlayFlag.AwaitingVendorSerial, JsonSerializer.Deserialize<List<string>>(u.OverlayFlags)!));
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        UnitIdentifiersAssigned? published = null;
        while (DateTime.UtcNow < deadline && published is null)
        {
            published = PublishedEventSink.Summaries.OfType<UnitIdentifiersAssigned>().FirstOrDefault(e => e.Correction.CorrectionId == correctionId);
            if (published is null) await Task.Delay(100);
        }
        Assert.NotNull(published);
        Assert.Single(published.Assignments);
        Assert.Equal(2, published.RemainingCount);
    }

    [DatabaseFact]
    public async Task A_shipped_target_needs_a_distinct_approver_and_a_later_correction_restores_the_provisional_identifier()
    {
        var tenant = await fixture.NewTenantAsync("ar");
        var item = Guid.NewGuid();
        var scheme = RegisterScheme(item);
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var unit = Assert.Single(await ProvisionalUnitsAsync(tenant, item, order, 1));
        using (var scope = fixture.ScopeFor(tenant))
        {
            var writer = scope.ServiceProvider.GetRequiredService<IUnitEventWriter>();
            var at = At.AddMinutes(30);
            await writer.AppendAsync([new UnitEventDraft
            {
                UnitId = unit, EventTime = at, EventType = UnitEventType.Shipped, LegalEntityId = Entity, ActorRef = "shipper",
                IdempotencyKey = IdempotencyKey.ForCommand("test", Guid.NewGuid(), unit, UnitEventType.Shipped, at),
                Delta = new CurrentStateDelta { Disposition = UnitDisposition.InTransit },
            }], CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().SaveChangesAsync();
        }

        var firstId = Guid.NewGuid();
        var unapproved = Command(order, firstId, IdentifierAssignmentMode.AllocationOrder, [new(scheme.Format(0, 21))], AssignIdentifiersReason.LateCapture, At.AddHours(1));
        var blocked = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<AssignIdentifiersResult>(tenant, unapproved));
        Assert.Equal("approval_required", blocked.Rule);
        var approved = unapproved with
        {
            Correction = new(firstId, AssignIdentifiersReason.LateCapture, InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor,
                Approvers: [new("second-supervisor", [YojanaRole.ProductionSupervisor])]),
        };
        var assigned = await InvokeAsync<AssignIdentifiersResult>(tenant, approved);
        Assert.Equal(1, assigned.Assigned);

        var reversalId = Guid.NewGuid();
        var reversal = Command(order, reversalId, IdentifierAssignmentMode.AllocationOrder, [], AssignIdentifiersReason.LateCapture, At.AddHours(2)) with
        {
            Correction = new(reversalId, AssignIdentifiersReason.LateCapture, CorrectsCorrectionId: firstId,
                InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor, Approvers: [new("second-supervisor", [YojanaRole.ProductionSupervisor])]),
        };
        var reversed = await InvokeAsync<AssignIdentifiersResult>(tenant, reversal);
        Assert.True(reversed.Reversal);
        Assert.Equal(1, reversed.RemainingCount);

        using var check = fixture.ScopeFor(tenant);
        var db = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var open = await db.Identifiers.SingleAsync(i => i.UnitId == unit && i.ValidTo == null);
        Assert.Equal(IdentifierType.Provisional, open.Type);
        Assert.Equal(3, await db.Identifiers.CountAsync(i => i.UnitId == unit));
        Assert.Equal(firstId, (await db.Corrections.SingleAsync(c => c.CorrectionId == reversalId)).CorrectsCorrectionId);
        Assert.Contains(OverlayFlag.AwaitingVendorSerial, JsonSerializer.Deserialize<List<string>>((await db.Units.SingleAsync(u => u.UnitId == unit)).OverlayFlags)!);
    }

    private SerialSchemeDefinition RegisterScheme(Guid item)
    {
        var code = "VS" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var scheme = new SerialSchemeDefinition(code, IdentifierType.VendorSerial, code, null, "-", 4);
        fixture.Services.GetRequiredService<InMemorySerialSchemes>().Register(scheme);
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(item) with { SerialScheme = code });
        return scheme;
    }

    private async Task<List<Guid>> ProvisionalUnitsAsync(TenantId tenant, Guid item, DocumentRef order, int count)
    {
        var prefix = "P" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var lines = Enumerable.Range(1, count).Select(i => new CommissionLine([new IdentifierInput(IdentifierType.Provisional, $"{prefix}-{i:0000}")])).ToList();
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured, lines, At, "line", Entity, Document: order));
        return result.Lines.Select(l => l.UnitId!.Value).ToList();
    }

    private static AssignIdentifiers Command(DocumentRef order, Guid correctionId, string mode, IReadOnlyList<IdentifierAssignmentInput> lines, string reason, DateTime at) =>
        new(correctionId, order, mode, lines, reason, at, "supervisor", Entity,
            Correction: new(correctionId, reason, InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor),
            CommittedBy: new("supervisor", [YojanaRole.ProductionSupervisor]));
}
