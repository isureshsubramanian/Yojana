using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>
/// P1-T24: a document's reads are set-based. A shipment of 500 serials on 500 lines cost about 1,500 statements and
/// 31.7 s over a LAN in P1-T21; it must now cost a bounded number of round trips whatever its size, with the same effects.
/// </summary>
[Collection("serialisation host")]
public sealed class DocumentPostingScaleTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private InMemoryDocumentPorts Ports => fixture.Services.GetRequiredService<InMemoryDocumentPorts>();

    private static ThinEnvelope Env(DateTime at) => new(Guid.NewGuid(), Entity, at, "cx01-test");

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<int> ShipmentStatementsAsync(string prefix, bool requiresCover)
    {
        const int n = 500;
        var tenant = await fixture.NewTenantAsync(prefix);
        var p = prefix.ToUpperInvariant() + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        if (requiresCover) fixture.Services.GetRequiredService<InMemoryWarrantyPorts>().Register(new ItemWarrantyPolicy(item, CoverageFamilyCode.Base, "NONE-" + p, true));
        var serials = Enumerable.Range(1, n).Select(i => $"{p}-{i:D4}").ToList();
        var receipt = new ReceiptDocument(new DocumentRef("receipt", Guid.NewGuid()), Guid.NewGuid(), T0, [new DocumentLine(1, item, n, serials.Select(s => new DocumentSerial(s)).ToList(), Guid.NewGuid(), LocationKind.Warehouse)]);
        Ports.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(T0), receipt.Document));
        var shipment = new ShipmentDocument(new DocumentRef("delivery", Guid.NewGuid()), Guid.NewGuid(), T0.AddDays(1), serials.Select((s, i) => new DocumentLine(i + 1, item, 1, [new DocumentSerial(s)], null, null)).ToList(), Guid.NewGuid(), null);
        Ports.Add(shipment);
        Assert.Equal(n, (await InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(Env(T0.AddDays(1)), shipment.Document))).UnitsAffected);
        // ownership opens at delivery (the default trigger), so that is where the requirement rows are written
        var confirmation = new DeliveryConfirmation(new DocumentRef("pod", Guid.NewGuid()), shipment.Document, T0.AddDays(3), null);
        Ports.Add(confirmation);
        var (delivered, statements) = await StatementCounter.CountAsync(() => InvokeAsync<ConsumedEventResult>(tenant, new SalesDeliveryConfirmed(Env(T0.AddDays(3)), confirmation.Document)));
        Assert.Equal(n, delivered.UnitsAffected);
        using var scope = fixture.ScopeFor(tenant);
        Assert.Equal(requiresCover ? n : 0, await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().EntitlementRequirements.CountAsync());
        return statements;
    }

    // P2-T13: a sale of an item that requires cover writes one entitlement_requirement row per unit in the delivery posting
    // transaction (the resolver runs after commit). The rows ride EF's batches; they must not bring back per-unit trips.
    [DatabaseFact]
    public async Task Requirement_rows_for_a_500_line_delivery_cost_batched_writes_not_round_trips_per_unit()
    {
        var plain = await ShipmentStatementsAsync("dq", requiresCover: false);
        var required = await ShipmentStatementsAsync("dr", requiresCover: true);
        // measured 2026-10-17: 39 → 51 statements (twelve 42-row batches)
        Assert.True(required - plain <= 25, $"requirement rows added {required - plain} statements ({plain} → {required})");
    }

    [DatabaseFact]
    public async Task A_500_line_shipment_and_its_delivery_post_in_a_bounded_number_of_statements()
    {
        const int n = 500;
        var tenant = await fixture.NewTenantAsync("dp");
        var p = "DP" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        var leeds = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var site = Guid.NewGuid();
        var serials = Enumerable.Range(1, n).Select(i => $"{p}-{i:D4}").ToList();
        var receipt = new ReceiptDocument(new DocumentRef("receipt", Guid.NewGuid()), Guid.NewGuid(), T0, [new DocumentLine(1, item, n, serials.Select(s => new DocumentSerial(s)).ToList(), leeds, LocationKind.Warehouse)]);
        Ports.Add(receipt);
        Assert.Equal(n, (await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(T0), receipt.Document))).UnitsAffected);

        var lines = serials.Select((s, i) => new DocumentLine(i + 1, item, 1, [new DocumentSerial(s)], null, null)).ToList();
        lines.Add(new DocumentLine(n + 1, item, 1, [new DocumentSerial($"{p}-UNKNOWN")], null, null));
        var shipment = new ShipmentDocument(new DocumentRef("delivery", Guid.NewGuid()), customer, T0.AddDays(1), lines, site, null);
        Ports.Add(shipment);
        var (shipped, shipStatements) = await StatementCounter.CountAsync(() => InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(Env(T0.AddDays(1)), shipment.Document)));
        Assert.Equal(n, shipped.UnitsAffected);
        Assert.Single(shipped.Exceptions);
        Assert.Contains("unknown serial", shipped.Exceptions[0]);

        var confirmation = new DeliveryConfirmation(new DocumentRef("pod", Guid.NewGuid()), shipment.Document, T0.AddDays(3), site);
        Ports.Add(confirmation);
        var (delivered, deliverStatements) = await StatementCounter.CountAsync(() => InvokeAsync<ConsumedEventResult>(tenant, new SalesDeliveryConfirmed(Env(T0.AddDays(3)), confirmation.Document)));
        Assert.Equal(n, delivered.UnitsAffected);

        // Before P1-T24 these were about three statements per serial. The ceiling leaves room for the batched writes (EF
        // sends 42 rows per batch: events, episodes, closures, current rows and outbox messages), not for per-unit reads.
        Assert.True(shipStatements < 200, $"shipment took {shipStatements} statements");
        Assert.True(deliverStatements < 200, $"delivery took {deliverStatements} statements");

        // Replays are duplicates, read the same set-based way.
        var (again, againStatements) = await StatementCounter.CountAsync(() => InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(Env(T0.AddDays(1)), shipment.Document)));
        Assert.Equal(0, again.UnitsAffected);
        Assert.True(againStatements < 20, $"replay took {againStatements} statements");

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var units = await db.Identifiers.Where(i => i.NormalisedValue.StartsWith(p) && i.ValidTo == null).Select(i => i.UnitId).ToListAsync();
        Assert.Equal(n, units.Count);
        Assert.Equal(n, await db.Current.CountAsync(c => units.Contains(c.UnitId) && c.LocationKind == LocationKind.CustomerSite && c.LocationId == site && c.OwnerPartyRef == customer));
        Assert.Equal(n * 3, await db.Episodes.CountAsync(e => units.Contains(e.UnitId) && e.Kind == EpisodeKind.Custody));
        Assert.Equal(n, await db.Episodes.CountAsync(e => units.Contains(e.UnitId) && e.ValidTo == null && e.Kind == EpisodeKind.Custody && e.LocationKind == LocationKind.CustomerSite));
        Assert.Equal(n, await db.Episodes.CountAsync(e => units.Contains(e.UnitId) && e.ValidTo == null && e.Kind == EpisodeKind.Ownership && e.PartyRef == customer));
        var lineNumbers = await db.Events.Where(e => e.DocumentId == shipment.Document.Id && e.EventType == UnitEventType.Shipped).Select(e => e.DocumentLine).Distinct().CountAsync();
        Assert.Equal(n, lineNumbers);
    }
}

/// <summary>
/// Counts the SQL commands one awaited call sends. SqlClient reports every command to its diagnostic listener on the
/// executing flow; an async-local mark keeps the outbox and search handlers that run in parallel out of the count.
/// </summary>
internal static class StatementCounter
{
    private static readonly AsyncLocal<int[]?> Current = new();
    private static readonly Listener Instance = new();

    static StatementCounter() => DiagnosticListener.AllListeners.Subscribe(Instance);

    public static async Task<(T Result, int Statements)> CountAsync<T>(Func<Task<T>> body)
    {
        var box = new int[1];
        Current.Value = box;
        try
        {
            var result = await body();
            return (result, box[0]);
        }
        finally
        {
            Current.Value = null;
        }
    }

    private sealed class Listener : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == "SqlClientDiagnosticListener") value.Subscribe(this);
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == "Microsoft.Data.SqlClient.WriteCommandBefore" && Current.Value is { } box) Interlocked.Increment(ref box[0]);
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }
    }
}
