using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Queries;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P3-T09: exact merge, readable alias, published facts and a correction-linked split.</summary>
[Collection("serialisation host")]
public sealed class MergeUnitsTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Generated_absorbed_histories_are_reachable_from_the_survivor_and_the_alias_resolves_then_splits_back()
    {
        var tenant = await fixture.NewTenantAsync("merge");
        var prefix = "MG" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), Guid.NewGuid(), UnitOrigin.Manufactured,
            [
                new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{prefix}-S")]),
                new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{prefix}-A")]),
            ], At, "line", Entity));
        var survivor = commissioned.Lines[0].UnitId!.Value;
        var absorbed = commissioned.Lines[1].UnitId!.Value;

        // Property style without another package: deterministic generated history lengths and serial values.
        var random = new Random(13134);
        var historyLength = random.Next(12, 25);
        var absorbedSerials = new List<string> { $"{prefix}-A" };
        for (var i = 0; i < historyLength; i++)
        {
            var next = $"{prefix}-{random.Next(100000, 999999)}";
            absorbedSerials.Add(next);
            await InvokeAsync<CorrectIdentifierResult>(tenant, new CorrectIdentifier(Guid.NewGuid(), absorbed, IdentifierType.InternalSerial, next,
                IdentifierCorrectionReason.Typo, At.AddMinutes(i + 1), "serial-admin", Entity));
        }

        List<Guid> absorbedFacts;
        using (var beforeScope = fixture.ScopeFor(tenant))
        {
            var db = beforeScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            absorbedFacts = await db.Events.Where(e => e.UnitId == absorbed).Select(e => e.EventId).ToListAsync();
        }

        var mergeId = Guid.NewGuid();
        var approval = new CorrectionMetadata(mergeId, MergeUnitsReason.DuplicateRecord, Approvers:
            [new("second-serial-admin", [YojanaRole.SerialAdministrator])]);
        var merged = await InvokeAsync<MergeUnitsResult>(tenant, new MergeUnits(Guid.NewGuid(), survivor, absorbed, MergeUnitsReason.DuplicateRecord,
            At.AddHours(2), "serial-admin", Entity, Correction: approval, CommittedBy: new("serial-admin", [YojanaRole.SerialAdministrator])));
        Assert.False(merged.Reversed);
        var published = await PublishedEventSink.WaitForAsync<UnitMerged>(e => e.Correction.CorrectionId == mergeId, 2, TimeSpan.FromSeconds(20));
        Assert.Equal(["absorbed", "survivor"], published.Select(e => e.Role).Order());

        using (var mergedScope = fixture.ScopeFor(tenant))
        {
            var db = mergedScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var moved = await db.Events.Where(e => absorbedFacts.Contains(e.EventId)).ToListAsync();
            Assert.Equal(absorbedFacts.Count, moved.Count);
            Assert.All(moved, e => { Assert.Equal(survivor, e.UnitId); Assert.Equal(absorbed, e.MergedFromUnitId); });
            var alias = await mergedScope.ServiceProvider.GetRequiredService<UnitQueries>().DetailAsync(absorbed, CancellationToken.None);
            Assert.Equal(LifecycleStatus.Merged, alias!.LifecycleStatus);
            Assert.Equal(survivor, alias.MergedIntoUnitId);
            var lookup = await mergedScope.ServiceProvider.GetRequiredService<UnitQueries>().LookupAsync(absorbedSerials[0], IdentifierType.InternalSerial, CancellationToken.None);
            Assert.All(lookup.Hits, hit => Assert.Equal(survivor, hit.UnitId));
        }

        var reversalId = Guid.NewGuid();
        var reversed = await InvokeAsync<MergeUnitsResult>(tenant, new MergeUnits(Guid.NewGuid(), survivor, absorbed, MergeUnitsReason.DuplicateRecord,
            At.AddHours(3), "serial-admin", Entity, Correction: new(reversalId, MergeUnitsReason.DuplicateRecord, CorrectsCorrectionId: mergeId,
                Approvers: [new("second-serial-admin", [YojanaRole.SerialAdministrator])]), CommittedBy: new("serial-admin", [YojanaRole.SerialAdministrator])));
        Assert.True(reversed.Reversed);

        using var afterScope = fixture.ScopeFor(tenant);
        var afterDb = afterScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.All(await afterDb.Events.Where(e => absorbedFacts.Contains(e.EventId)).ToListAsync(), e => { Assert.Equal(absorbed, e.UnitId); Assert.Null(e.MergedFromUnitId); });
        var restored = await afterScope.ServiceProvider.GetRequiredService<UnitQueries>().DetailAsync(absorbed, CancellationToken.None);
        Assert.NotEqual(LifecycleStatus.Merged, restored!.LifecycleStatus);
        Assert.Null(restored.MergedIntoUnitId);
    }

    [DatabaseFact]
    public async Task Genuine_vendor_serial_collision_keeps_both_units_and_records_the_acknowledgement()
    {
        var tenant = await fixture.NewTenantAsync("vendor-duplicate");
        var prefix = "VD" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var vendorSerial = $"{prefix}-OEM";
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), Guid.NewGuid(), UnitOrigin.Purchased,
            [
                new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{prefix}-1"), new IdentifierInput(IdentifierType.VendorSerial, vendorSerial, Guid.NewGuid())]),
                new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{prefix}-2")]),
            ], At, "receiving", Entity));
        var first = commissioned.Lines[0].UnitId!.Value;
        var second = commissioned.Lines[1].UnitId!.Value;
        var correctionId = Guid.NewGuid();

        var result = await InvokeAsync<MergeUnitsResult>(tenant, new MergeUnits(Guid.NewGuid(), first, second, MergeUnitsReason.VendorSerialKeyedTwice,
            At.AddHours(1), "serial-admin", Entity, MergeUnitsResolution.AcknowledgeVendorSerialDuplicate, vendorSerial,
            Correction: new(correctionId, MergeUnitsReason.VendorSerialKeyedTwice, Approvers: [new("second-serial-admin", [YojanaRole.SerialAdministrator])]),
            CommittedBy: new("serial-admin", [YojanaRole.SerialAdministrator])));

        Assert.Equal(MergeUnitsResolution.AcknowledgeVendorSerialDuplicate, result.Resolution);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var units = await db.Units.Where(u => u.UnitId == first || u.UnitId == second).ToListAsync();
        Assert.All(units, unit => Assert.NotEqual(LifecycleStatus.Merged, unit.LifecycleStatus));
        var duplicate = await db.Identifiers.Where(i => (i.UnitId == first || i.UnitId == second) && i.Type == IdentifierType.VendorSerial && i.ValidTo == null).ToListAsync();
        Assert.Equal(2, duplicate.Count);
        Assert.All(duplicate, row => Assert.True(row.DuplicateAcknowledged));
        var lookup = await scope.ServiceProvider.GetRequiredService<UnitQueries>().LookupAsync(vendorSerial, IdentifierType.VendorSerial, CancellationToken.None);
        Assert.Equal(LookupResult.Resolutions.Ambiguous, lookup.Resolution);
        Assert.Equal(new[] { first, second }.Order(), lookup.Hits.Select(h => h.UnitId).Distinct().Order());
    }
}
