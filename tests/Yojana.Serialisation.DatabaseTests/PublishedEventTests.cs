using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P1-T16: the six published events leave through the outbox after commit, with their schema name and version.</summary>
[Collection("serialisation host")]
public sealed class PublishedEventTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Commission_release_correction_ownership_and_custody_are_published_after_commit()
    {
        var tenant = await fixture.NewTenantAsync("pe");
        var p = "PE" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var unit = (await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{p}-001")])], At, "t", Entity, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse))).Lines[0].UnitId!.Value;
        await InvokeAsync<ReleaseUnitResult>(tenant, new ReleaseUnit(Guid.NewGuid(), unit, At.AddHours(1), "q", Entity, "sig"));
        await InvokeAsync<CorrectIdentifierResult>(tenant, new CorrectIdentifier(Guid.NewGuid(), unit, IdentifierType.InternalSerial, $"{p}-010", IdentifierCorrectionReason.Typo, At.AddHours(2), "r", Entity));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit, customer, PartyType.Customer, EpisodeReason.Sale, At.AddHours(3), "s", Entity, CustomerPoRef: "PO-1"));
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unit, LocationKind.InTransit, "ship", At.AddHours(4), "s", Entity));
        await InvokeAsync<CloseOwnershipResult>(tenant, new CloseOwnership(Guid.NewGuid(), unit, "settled", At.AddHours(5), "s", Entity));

        var timeout = TimeSpan.FromSeconds(20);
        var commissioned = Assert.Single(await PublishedEventSink.WaitForAsync<UnitCommissioned>(e => e.Header.UnitId == unit, 1, timeout));
        Assert.Equal(UnitCommissioned.Schema, commissioned.Header.Schema);
        Assert.Equal("1.1", commissioned.Header.Version); // 1.1 adds commissioned_late (P3-T06)
        Assert.Equal(item, commissioned.ItemId);
        Assert.Equal($"{p}001", commissioned.Identifiers.Single().Value);
        var released = Assert.Single(await PublishedEventSink.WaitForAsync<UnitReleased>(e => e.Header.UnitId == unit, 1, timeout));
        Assert.Equal(1, released.BuildRecordVersion);
        Assert.Equal(64, released.PayloadHash.Length);
        var corrected = Assert.Single(await PublishedEventSink.WaitForAsync<UnitIdentifierCorrected>(e => e.Header.UnitId == unit, 1, timeout));
        Assert.Equal($"{p}001", corrected.BeforeValue);
        Assert.Equal($"{p}010", corrected.AfterValue);
        var opened = Assert.Single(await PublishedEventSink.WaitForAsync<OwnershipOpened>(e => e.Header.UnitId == unit, 1, timeout));
        Assert.Equal(customer, opened.PartyRef);
        Assert.Equal("PO-1", opened.CustomerPoRef);
        // The opening custody travels inside Unit.Commissioned (location fields); only moves publish Custody.Changed.
        var custody = Assert.Single(await PublishedEventSink.WaitForAsync<CustodyChanged>(e => e.Header.UnitId == unit, 1, timeout));
        Assert.Equal(LocationKind.InTransit, custody.ToLocationKind);
        Assert.Equal(LocationKind.Warehouse, custody.FromLocationKind);
        Assert.Equal(UnitEventType.CustodyChanged, custody.TriggerEventType);
        Assert.Equal(LocationKind.Warehouse, commissioned.LocationKind);
        var closed = Assert.Single(await PublishedEventSink.WaitForAsync<OwnershipClosed>(e => e.Header.UnitId == unit, 1, timeout));
        Assert.Equal("settled", closed.Reason);
    }

    [Fact]
    public void Every_published_event_has_an_embedded_schema_whose_required_fields_match_the_record()
    {
        foreach (var (schema, version, type) in new[] { (UnitCommissioned.Schema, UnitCommissioned.Version, typeof(UnitCommissioned)), (UnitReleased.Schema, UnitReleased.Version, typeof(UnitReleased)), (UnitIdentifierCorrected.Schema, UnitIdentifierCorrected.Version, typeof(UnitIdentifierCorrected)), (OwnershipOpened.Schema, OwnershipOpened.Version, typeof(OwnershipOpened)), (OwnershipClosed.Schema, OwnershipClosed.Version, typeof(OwnershipClosed)), (CustodyChanged.Schema, CustodyChanged.Version, typeof(CustodyChanged)), (UnitGraded.Schema, UnitGraded.Version, typeof(UnitGraded)), (UnitPartInstalled.Schema, UnitPartInstalled.Version, typeof(UnitPartInstalled)), (CoverageDecisionChanged.Schema, CoverageDecisionChanged.Version, typeof(CoverageDecisionChanged)) })
        {
            using var doc = System.Text.Json.JsonDocument.Parse(PublishedEventSchemas.Read(schema, version));
            var properties = doc.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
            var recordProperties = type.GetProperties().Select(p => string.Concat(p.Name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()))).ToHashSet();
            Assert.True(recordProperties.IsSubsetOf(properties), $"{schema}: record properties missing from the schema: {string.Join(", ", recordProperties.Except(properties))}");
            Assert.Equal(schema, doc.RootElement.GetProperty("title").GetString());
        }
    }
}
