using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P3-T03 / case 117 / tickets T1 and T67: atomic pairwise swap, outbox facts and a later reversing swap.</summary>
[Collection("serialisation host")]
public sealed class SwapIdentifiersTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Preview_swap_publish_print_replay_and_later_swap_restore_the_original_identifiers()
    {
        var tenant = await fixture.NewTenantAsync("sw");
        var item = Guid.NewGuid();
        var prefix = "SW" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured,
            [
                new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{prefix}-001")]),
                new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{prefix}-002")]),
            ], At, "line", Entity));
        var firstUnit = commissioned.Lines[0].UnitId!.Value;
        var secondUnit = commissioned.Lines[1].UnitId!.Value;
        await InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), firstUnit,
            new IdentifierInput(IdentifierType.CustomerAssetTag, $"TAG-{prefix}-1"), At.AddMinutes(1), "line", Entity));
        await InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), secondUnit,
            new IdentifierInput(IdentifierType.CustomerAssetTag, $"TAG-{prefix}-2"), At.AddMinutes(1), "line", Entity));

        var firstCorrection = Guid.NewGuid();
        var firstSwapId = Guid.NewGuid();
        var firstCommand = new SwapIdentifiers(Guid.NewGuid(), firstUnit, secondUnit, IdentifierType.InternalSerial,
            SwapIdentifiersReason.CrossRecordedAtBuild, At.AddHours(1), "serial-admin", Entity,
            Correction: new(firstCorrection, SwapIdentifiersReason.CrossRecordedAtBuild, "labels crossed", ["photo:pair"],
                CorrectionInitiatedVia.Workbench, InitiatingRole: CorrectionInitiatingRole.SerialAdministrator),
            CommittedBy: new("serial-admin", [YojanaRole.SerialAdministrator]), SwapId: firstSwapId);

        var preview = await InvokeAsync<CorrectionPreview>(tenant, new PreviewSwapIdentifiers(firstCommand));
        Assert.Equal(2, preview.Change);
        Assert.Equal(2, preview.UnitEvents.Count);
        Assert.Equal(2, preview.PrintJobs.Count);
        Assert.Empty(preview.CertificateVersions); // P6 consumes Unit.IdentifiersSwapped; P3 emits no certificate command.
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Empty(await db.Corrections.Where(c => c.CorrectionId == firstCorrection).ToListAsync());
            Assert.Empty(await db.Events.Where(e => e.EventType == UnitEventType.IdentifiersSwapped).ToListAsync());
        }

        var swapped = await InvokeAsync<SwapIdentifiersResult>(tenant, firstCommand);
        Assert.False(swapped.Replayed);
        Assert.Equal(firstSwapId, swapped.SwapId);
        Assert.Equal(2, swapped.AssetTagPrintJobsRequested);

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var rows = await db.Identifiers.Where(i => i.UnitId == firstUnit || i.UnitId == secondUnit).ToListAsync();
            var firstOpen = Assert.Single(rows, i => i.UnitId == firstUnit && i.Type == IdentifierType.InternalSerial && i.ValidTo == null);
            var secondOpen = Assert.Single(rows, i => i.UnitId == secondUnit && i.Type == IdentifierType.InternalSerial && i.ValidTo == null);
            Assert.Equal($"{prefix}002", firstOpen.NormalisedValue);
            Assert.Equal($"{prefix}001", secondOpen.NormalisedValue);
            Assert.Equal(secondOpen.IdentifierId, firstOpen.SwappedWithIdentifierId);
            Assert.Equal(firstOpen.IdentifierId, secondOpen.SwappedWithIdentifierId);
            Assert.Equal(rows.Single(i => i.UnitId == secondUnit && i.Type == IdentifierType.InternalSerial && i.ValidTo != null).IdentifierId, firstOpen.SupersedesIdentifierId);
            Assert.Equal(rows.Single(i => i.UnitId == firstUnit && i.Type == IdentifierType.InternalSerial && i.ValidTo != null).IdentifierId, secondOpen.SupersedesIdentifierId);

            var correction = Assert.Single(await db.Corrections.Where(c => c.CorrectionId == firstCorrection).ToListAsync());
            Assert.Equal(2, correction.UnitCount);
            var facts = await db.Events.Where(e => e.EventType == UnitEventType.IdentifiersSwapped).ToListAsync();
            Assert.Equal(2, facts.Count);
            Assert.All(facts, fact =>
            {
                using var attributes = JsonDocument.Parse(fact.Attributes!);
                Assert.Equal(firstSwapId, attributes.RootElement.GetProperty("swap_id").GetGuid());
                Assert.Equal(firstCorrection, attributes.RootElement.GetProperty("correction_id").GetGuid());
            });
        }

        var published = await PublishedEventSink.WaitForAsync<UnitIdentifiersSwapped>(e => e.SwapId == firstSwapId, 2, TimeSpan.FromSeconds(20));
        Assert.Equal(2, published.Count);
        Assert.Equal([firstUnit, secondUnit], published.Select(e => e.Header.UnitId).Order().ToArray());
        Assert.All(published, e => Assert.Equal(firstCorrection, e.Correction.CorrectionId));
        Assert.Contains(published, e => e.Header.UnitId == firstUnit && e.GivenIdentifier.Value == $"{prefix}001" && e.ReceivedIdentifier.Value == $"{prefix}002");
        Assert.Contains(published, e => e.Header.UnitId == secondUnit && e.GivenIdentifier.Value == $"{prefix}002" && e.ReceivedIdentifier.Value == $"{prefix}001");
        var printRequests = await WaitForPrintRequestsAsync(r => r.SwapId == firstSwapId, 2, TimeSpan.FromSeconds(20));
        Assert.Equal(2, printRequests.Count);
        Assert.Equal([firstUnit, secondUnit], printRequests.Select(r => r.UnitId).Order().ToArray());

        var replay = await InvokeAsync<SwapIdentifiersResult>(tenant, firstCommand);
        Assert.True(replay.Replayed);
        Assert.Equal(firstSwapId, replay.SwapId);

        var reversalCorrection = Guid.NewGuid();
        var reversalSwapId = Guid.NewGuid();
        var reversal = new SwapIdentifiers(Guid.NewGuid(), firstUnit, secondUnit, IdentifierType.InternalSerial,
            SwapIdentifiersReason.LabelAppliedToWrongUnit, At.AddHours(2), "serial-admin", Entity,
            Correction: new(reversalCorrection, SwapIdentifiersReason.LabelAppliedToWrongUnit, CorrectsCorrectionId: firstCorrection,
                InitiatingRole: CorrectionInitiatingRole.SerialAdministrator),
            CommittedBy: new("serial-admin", [YojanaRole.SerialAdministrator]), SwapId: reversalSwapId);
        await InvokeAsync<SwapIdentifiersResult>(tenant, reversal);

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal($"{prefix}001", (await db.Identifiers.SingleAsync(i => i.UnitId == firstUnit && i.Type == IdentifierType.InternalSerial && i.ValidTo == null)).NormalisedValue);
            Assert.Equal($"{prefix}002", (await db.Identifiers.SingleAsync(i => i.UnitId == secondUnit && i.Type == IdentifierType.InternalSerial && i.ValidTo == null)).NormalisedValue);
            Assert.Equal(4, await db.Events.CountAsync(e => e.EventType == UnitEventType.IdentifiersSwapped));
            Assert.Equal(firstCorrection, (await db.Corrections.SingleAsync(c => c.CorrectionId == reversalCorrection)).CorrectsCorrectionId);
            Assert.Equal(2, await db.Corrections.CountAsync(c => c.Command == CorrectionCommand.SwapIdentifiers));
        }

        Assert.Equal(2, (await PublishedEventSink.WaitForAsync<UnitIdentifiersSwapped>(e => e.SwapId == reversalSwapId, 2, TimeSpan.FromSeconds(20))).Count);
        Assert.Equal(2, (await WaitForPrintRequestsAsync(r => r.SwapId == reversalSwapId, 2, TimeSpan.FromSeconds(20))).Count);
    }

    private static Task<IReadOnlyList<AssetTagPrintJobRequested>> WaitForPrintRequestsAsync(
        Func<AssetTagPrintJobRequested, bool> match,
        int count,
        TimeSpan timeout) => Task.Run(async () =>
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hits = PublishedEventSink.PrintJobRequests.Where(match).ToList();
            if (hits.Count >= count) return (IReadOnlyList<AssetTagPrintJobRequested>)hits;
            await Task.Delay(100);
        }

        return PublishedEventSink.PrintJobRequests.Where(match).ToList();
    });
}
