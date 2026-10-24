using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Settings;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class ModelNumberCaptureTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Entity = Guid.Parse("51000000-0000-0000-0000-000000000001");

    [DatabaseFact]
    public async Task Item_code_and_model_numbers_are_refused_until_a_reasoned_confirmation_is_recorded()
    {
        var tenant = await fixture.NewTenantAsync("model-guard");
        var item = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(item) with
        {
            ItemCode = "HD-20T",
            ModelNumbers = ["HX20PR-64P6ZY0", "ST-4200"],
        });

        foreach (var captured in new[] { "h d-20t", "hx20pr-64p6zy0", "s t-4200" })
        {
            var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CommissionUnitsResult>(tenant,
                Commission(item, captured)));
            Assert.Equal(ModelNumberRules.Rule, refusal.Rule);
            Assert.Equal(ModelNumberRules.Message, refusal.Message);
        }

        var noReason = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CommissionUnitsResult>(tenant,
            Commission(item, "HX20PR-64P6ZY0") with { ConfirmedNotModelNumber = true }));
        Assert.Equal(ModelNumberRules.Rule, noReason.Rule);

        const string reason = "The chassis and packing list both identify this as the unit serial.";
        var confirmed = await InvokeAsync<CommissionUnitsResult>(tenant, Commission(item, "HX20PR-64P6ZY0") with
        {
            ConfirmedNotModelNumber = true,
            ModelNumberConfirmationReason = reason,
        });
        var unitId = Assert.Single(confirmed.Lines).UnitId!.Value;
        using var scope = fixture.ScopeFor(tenant);
        var attributes = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Events.AsNoTracking()
            .Where(row => row.UnitId == unitId && row.EventType == UnitEventType.Commissioned)
            .Select(row => row.Attributes).SingleAsync();
        Assert.Contains(reason, attributes, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Duplicate_refusal_adds_the_model_hint_only_when_the_holder_has_the_same_item()
    {
        var tenant = await fixture.NewTenantAsync("model-duplicate");
        var firstItem = Guid.NewGuid();
        var secondItem = Guid.NewGuid();
        var ports = fixture.Services.GetRequiredService<InMemoryItemPorts>();
        ports.Register(InMemoryItemPorts.DefaultProfile(firstItem));
        ports.Register(InMemoryItemPorts.DefaultProfile(secondItem));
        const string imei = "356938035643809";
        await InvokeAsync<CommissionUnitsResult>(tenant, Commission(firstItem, imei, IdentifierType.Imei));

        var sameItem = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<CommissionUnitsResult>(tenant, Commission(firstItem, imei, IdentifierType.Imei)));
        Assert.Contains(ModelNumberRules.SameItemDuplicateHint, sameItem.Message, StringComparison.Ordinal);

        var otherItem = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<CommissionUnitsResult>(tenant, Commission(secondItem, imei, IdentifierType.Imei)));
        Assert.DoesNotContain(ModelNumberRules.SameItemDuplicateHint, otherItem.Message, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Tenant_administrator_item_settings_validate_and_replace_the_model_number_list()
    {
        var tenant = await fixture.NewTenantAsync("model-settings");
        var item = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(item) with { ItemCode = "HD-20T" });
        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<ItemSerialisationSettingsService>();
        var saved = await service.SetAsync(item, new(["HX20PR-64P6ZY0", "ST-4200"]), "admin.user", CancellationToken.None);
        Assert.Equal(["HX20PR-64P6ZY0", "ST-4200"], saved.ModelNumbers);
        var reloaded = await service.GetAsync(item, CancellationToken.None);
        Assert.Equal(saved.ItemId, reloaded.ItemId);
        Assert.Equal(saved.ItemCode, reloaded.ItemCode);
        Assert.Equal(saved.ModelNumbers, reloaded.ModelNumbers);
        var tooMany = Enumerable.Range(1, 21).Select(number => $"MODEL-{number}").ToList();
        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            service.SetAsync(item, new(tooMany), "admin.user", CancellationToken.None));
        Assert.Equal("item_model_numbers_too_many", refusal.Rule);
    }

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private static CommissionUnits Commission(Guid itemId, string value, string type = IdentifierType.InternalSerial) =>
        new(Guid.NewGuid(), itemId, UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(type, value)])],
            At, "warehouse.user", Entity, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse);
}
