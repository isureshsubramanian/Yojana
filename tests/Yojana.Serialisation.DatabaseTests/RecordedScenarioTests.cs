using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P1-T17 and the phase exit criterion: reference scenario steps 0 and 1 run from recorded messages, no ERP involved.</summary>
[Collection("serialisation host")]
public sealed class RecordedScenarioTests(SerialisationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Reference_scenario_steps_0_and_1_replay_from_recorded_messages()
    {
        var tenant = await fixture.NewTenantAsync("rs");
        var prefix = "RS" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var scenario = RecordedScenario.Load("reference-scenario-steps-0-1.json", prefix);
        var results = await scenario.ReplayAsync(fixture, tenant, CancellationToken.None);
        Assert.Equal(scenario.Messages.Count, results.Count);
        foreach (var consumed in results.OfType<ConsumedEventResult>())
        {
            Assert.Empty(consumed.Exceptions);
        }

        var expect = scenario.Expect;
        var unit = await RecordedScenario.UnitBySerialAsync(fixture, tenant, expect.GetProperty("serial").GetString()!, CancellationToken.None);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var current = await db.Current.AsNoTracking().SingleAsync(c => c.UnitId == unit);
        Assert.Equal(expect.GetProperty("lifecycle").GetString(), current.LifecycleStatus);
        Assert.Equal(expect.GetProperty("ownerPartyRef").GetGuid(), current.OwnerPartyRef);
        Assert.Equal(expect.GetProperty("locationKind").GetString(), current.LocationKind);
        Assert.Equal(expect.GetProperty("locationId").GetGuid(), current.LocationId);
        var events = await db.Events.AsNoTracking().Where(e => e.UnitId == unit).OrderBy(e => e.Seq).Select(e => e.EventType).ToListAsync();
        Assert.Equal(expect.GetProperty("events").EnumerateArray().Select(e => e.GetString()!).ToList(), events);
        var record = await db.BuildRecords.AsNoTracking().SingleAsync(b => b.UnitId == unit);
        Assert.Equal(expect.GetProperty("buildRecordVersion").GetInt32(), record.Version);
        Assert.Contains(expect.GetProperty("componentSerial").GetString()!.Replace("-", "", StringComparison.Ordinal), record.Payload);
        var custody = await db.Episodes.AsNoTracking().Where(e => e.UnitId == unit && e.Kind == EpisodeKind.Custody).OrderBy(e => e.ValidFrom).Select(e => e.LocationKind).ToListAsync();
        Assert.Equal([LocationKind.Warehouse, LocationKind.InTransit, LocationKind.CustomerSite], custody);
    }
}
