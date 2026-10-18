using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Queries;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P1-T19: exact lookup, superseded resolution, item disambiguation, prefix search with paging and filters, detail and history.</summary>
[Collection("serialisation host")]
public sealed class UnitQueryTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Lookup_resolves_exact_superseded_and_ambiguous_identifiers_and_search_pages_with_filters()
    {
        var tenant = await fixture.NewTenantAsync("uq");
        var p = "UQ" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var vx = Guid.NewGuid();
        var pm = Guid.NewGuid();
        var vxUnits = (await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), vx, UnitOrigin.Manufactured, Enumerable.Range(1, 12).Select(n => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{p}-{n:D3}")])).ToList(), At, "t", Entity, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse))).Lines.Select(l => l.UnitId!.Value).ToList();
        // the same string as a vendor serial on another item (case 1)
        var pmUnit = (await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), pm, UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.VendorSerial, $"{p}-005", Guid.NewGuid())])], At, "t", Entity))).Lines[0].UnitId!.Value;
        await InvokeAsync<CorrectIdentifierResult>(tenant, new CorrectIdentifier(Guid.NewGuid(), vxUnits[8], IdentifierType.InternalSerial, $"{p}-090", IdentifierCorrectionReason.Typo, At.AddDays(1), "r", Entity));
        await InvokeAsync<ReleaseUnitResult>(tenant, new ReleaseUnit(Guid.NewGuid(), vxUnits[0], At.AddDays(1), "q", Entity, "sig"));

        using var scope = fixture.ScopeFor(tenant);
        var queries = scope.ServiceProvider.GetRequiredService<UnitQueries>();

        var exact = await queries.LookupAsync($"{p}-001", null, CancellationToken.None);
        Assert.Equal(LookupResult.Resolutions.Exact, exact.Resolution);
        Assert.Equal(vxUnits[0], exact.Hits.Single().UnitId);

        var ambiguous = await queries.LookupAsync($"{p}-005", null, CancellationToken.None);
        Assert.Equal(LookupResult.Resolutions.Ambiguous, ambiguous.Resolution);
        Assert.Equal(2, ambiguous.Hits.Count);
        Assert.Contains(ambiguous.Hits, h => h.UnitId == pmUnit && h.IdentifierType == IdentifierType.VendorSerial);
        var byType = await queries.LookupAsync($"{p}-005", IdentifierType.InternalSerial, CancellationToken.None);
        Assert.Equal(LookupResult.Resolutions.Exact, byType.Resolution);

        var superseded = await queries.LookupAsync($"{p}-009", null, CancellationToken.None);
        Assert.Equal(LookupResult.Resolutions.Superseded, superseded.Resolution);
        Assert.Equal(vxUnits[8], superseded.Hits.Single().UnitId);
        Assert.Equal($"{p}090", superseded.Hits.Single().ResolvesTo);
        Assert.Equal(LookupResult.Resolutions.None, (await queries.LookupAsync($"{p}-999", null, CancellationToken.None)).Resolution);

        var page1 = await queries.SearchAsync(new UnitSearch($"{p}-0", Take: 5), CancellationToken.None);
        Assert.Equal(5, page1.Identifiers);
        Assert.True(page1.HasMore);
        Assert.True(page1.TotalIsLowerBound);
        Assert.True(page1.SummaryIsSample);
        Assert.Equal(5, page1.Page.Items.Count);
        Assert.Equal(6, page1.Page.Total);
        Assert.All(page1.Page.Items, row => Assert.NotNull(row.LastEventType));
        var page3 = await queries.SearchAsync(new UnitSearch($"{p}-0", Skip: 10, Take: 5), CancellationToken.None);
        Assert.Equal(4, page3.Page.Items.Count);
        Assert.Equal(14, page3.Identifiers); // 12 internal (one superseded, one corrected successor = 13) + 1 vendor
        Assert.False(page3.HasMore);
        Assert.Equal(14, page3.Page.Total);
        var released = await queries.SearchAsync(new UnitSearch($"{p}-0", LifecycleStatus: LifecycleStatus.Released, ItemId: vx), CancellationToken.None); // the purchased unit is released too
        Assert.Single(released.Page.Items);
        var openOnly = await queries.SearchAsync(new UnitSearch($"{p}-0", IncludeSuperseded: false, ItemId: vx), CancellationToken.None);
        Assert.Equal(12, openOnly.Page.Total);
        Assert.All(openOnly.Page.Items, r => Assert.True(r.Open));
        var supersededRow = (await queries.SearchAsync(new UnitSearch($"{p}-009"), CancellationToken.None)).Page.Items.Single();
        Assert.False(supersededRow.Open);
        Assert.Equal($"{p}090", supersededRow.ResolvesTo);

        var detail = await queries.DetailAsync(vxUnits[8], CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.Identifiers.Count);
        Assert.Equal($"{p}090", detail.Identifiers[0].NormalisedValue); // open first
        Assert.NotNull(detail.Identifiers[1].ValidTo);
        Assert.Single(detail.Episodes);
        Assert.Equal(2, detail.EventCount);
        var history = await queries.HistoryAsync(vxUnits[8], 0, 10, CancellationToken.None);
        Assert.Equal([UnitEventType.IdentifierCorrected, UnitEventType.Commissioned], history.Items.Select(h => h.EventType));
        Assert.Null(await queries.DetailAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
