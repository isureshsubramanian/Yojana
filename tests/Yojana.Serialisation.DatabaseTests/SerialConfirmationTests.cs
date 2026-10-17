using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Documents;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class SerialConfirmationTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private InMemoryDocumentPorts Ports => fixture.Services.GetRequiredService<InMemoryDocumentPorts>();

    private static ThinEnvelope Envelope(DateTime at) => new(Guid.NewGuid(), Entity, at, "shipping-test");

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task SetModeAsync(TenantId tenant, string mode)
    {
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<SerialConfirmationSettings>()
            .SetAsync(new(mode), CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Scan_required_refuses_copied_delivery_and_keyed_transfer_lines()
    {
        var tenant = await fixture.NewTenantAsync("sc");
        await SetModeAsync(tenant, SerialConfirmationMode.ScanRequired);
        var prefix = "SC" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var receipt = new ReceiptDocument(new("receipt", Guid.NewGuid()), Guid.NewGuid(), At,
            [new(1, item, 2, [new($"{prefix}-1"), new($"{prefix}-2")], warehouse, LocationKind.Warehouse)]);
        Ports.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Envelope(At), receipt.Document));

        var delivery = new ShipmentDocument(new("delivery", Guid.NewGuid()), Guid.NewGuid(), At.AddDays(1),
            [new(1, item, 1, [new($"{prefix}-1")], SerialCaptureMethod: SerialCaptureMethodValue.Copied)]);
        Ports.Add(delivery);
        var deliveryResult = await InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(Envelope(At.AddDays(1)), delivery.Document));
        Assert.Equal(0, deliveryResult.UnitsAffected);
        Assert.Single(deliveryResult.Exceptions);
        Assert.Contains(SerialConfirmationRules.ScanRequiredRule, deliveryResult.Exceptions[0]);
        Assert.Contains(SerialConfirmationRules.ScanUnblockingStep, deliveryResult.Exceptions[0]);

        var transfer = new TransferDocument(new("transfer", Guid.NewGuid()), At.AddDays(1), warehouse, LocationKind.Warehouse,
            destination, LocationKind.Warehouse,
            [new(1, item, 1, [new($"{prefix}-2")], SerialCaptureMethod: SerialCaptureMethodValue.Keyed)]);
        Ports.Add(transfer);
        var transferResult = await InvokeAsync<ConsumedEventResult>(tenant, new InventoryTransferPosted(Envelope(At.AddDays(1)), transfer.Document));
        Assert.Equal(0, transferResult.UnitsAffected);
        Assert.Single(transferResult.Exceptions);
        Assert.Contains(SerialConfirmationRules.ScanRequiredRule, transferResult.Exceptions[0]);
    }

    [DatabaseFact]
    public async Task Copy_allowed_accepts_the_line_and_records_the_flag_on_unit_shipped()
    {
        var tenant = await fixture.NewTenantAsync("cc");
        await SetModeAsync(tenant, SerialConfirmationMode.CopyAllowed);
        var prefix = "CC" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        var receipt = new ReceiptDocument(new("receipt", Guid.NewGuid()), Guid.NewGuid(), At,
            [new(1, item, 1, [new($"{prefix}-1")], Guid.NewGuid(), LocationKind.Warehouse)]);
        Ports.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Envelope(At), receipt.Document));

        var delivery = new ShipmentDocument(new("delivery", Guid.NewGuid()), Guid.NewGuid(), At.AddDays(1),
            [new(1, item, 1, [new($"{prefix}-1")], SerialCaptureMethod: SerialCaptureMethodValue.Copied)]);
        Ports.Add(delivery);
        var result = await InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(Envelope(At.AddDays(1)), delivery.Document));
        Assert.Equal(1, result.UnitsAffected);
        Assert.Empty(result.Exceptions);

        using var scope = fixture.ScopeFor(tenant);
        var shipped = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Events
            .SingleAsync(e => e.DocumentId == delivery.Document.Id && e.EventType == UnitEventType.Shipped);
        Assert.Contains("\"serial_confirmation_mode\":\"copy_allowed\"", shipped.Attributes);
        Assert.Contains("\"serial_capture_method\":\"copied\"", shipped.Attributes);
        Assert.Contains("\"serials_copied_without_scan\":true", shipped.Attributes);
    }

    [DatabaseFact]
    public async Task Missing_setting_and_capture_marker_default_to_scan_required_and_scanned()
    {
        var tenant = await fixture.NewTenantAsync("sd");
        var prefix = "SD" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        var receipt = new ReceiptDocument(new("receipt", Guid.NewGuid()), Guid.NewGuid(), At,
            [new(1, item, 1, [new($"{prefix}-1")], Guid.NewGuid(), LocationKind.Warehouse)]);
        Ports.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Envelope(At), receipt.Document));

        var delivery = new ShipmentDocument(new("delivery", Guid.NewGuid()), Guid.NewGuid(), At.AddDays(1),
            [new(1, item, 1, [new($"{prefix}-1")])]);
        Ports.Add(delivery);
        var result = await InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(Envelope(At.AddDays(1)), delivery.Document));
        Assert.Equal(1, result.UnitsAffected);

        using var scope = fixture.ScopeFor(tenant);
        var shipped = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Events
            .SingleAsync(e => e.DocumentId == delivery.Document.Id && e.EventType == UnitEventType.Shipped);
        Assert.Contains("\"serial_confirmation_mode\":\"scan_required\"", shipped.Attributes);
        Assert.Contains("\"serial_capture_method\":\"scanned\"", shipped.Attributes);
        Assert.Contains("\"serials_copied_without_scan\":false", shipped.Attributes);
    }
}
