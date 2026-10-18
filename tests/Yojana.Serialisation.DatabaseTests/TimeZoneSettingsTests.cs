using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.TimeZones;
using Xunit;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class TimeZoneSettingsDatabaseTests(SerialisationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Persisted_adapter_resolves_entity_tenant_and_utc_and_keeps_audited_history()
    {
        var tenant = await fixture.NewTenantAsync("time-zone-db");
        var otherTenant = await fixture.NewTenantAsync("time-zone-other");
        var issuer = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(
            new PartyLabel(issuer, "LE-FICTION", "Fictional Issuing Works", PartyType.LegalEntity));

        using (var scope = fixture.ScopeFor(tenant))
        {
            var settings = scope.ServiceProvider.GetRequiredService<TenantTimeZoneSettings>();
            var initial = await settings.GetAsync(CancellationToken.None);
            var first = await settings.SetAsync(new UpdateTenantTimeZoneSettings("Asia/Kolkata",
                [new(issuer, "Europe/London")], initial.Version), "administrator-one", CancellationToken.None);
            var adapter = new TenantTimeZonePort(settings,
                fixture.Services.GetRequiredService<InMemoryWarrantyPorts>());
            Assert.Equal("Europe/London", await adapter.ForLegalEntityAsync(issuer,
                TimeZonePurpose.DocumentPresentation, CancellationToken.None));
            Assert.Equal("Asia/Kolkata", await adapter.ForLegalEntityAsync(Guid.NewGuid(),
                TimeZonePurpose.DocumentPresentation, CancellationToken.None));
            Assert.Equal("Asia/Kolkata", await adapter.ForLegalEntityAsync(Guid.NewGuid(),
                TimeZonePurpose.EntitlementCalendar, CancellationToken.None));
            await settings.SetAsync(new UpdateTenantTimeZoneSettings("Europe/Paris",
                first.LegalEntityOverrides, first.Version), "administrator-two", CancellationToken.None);
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            var persisted = await scope.ServiceProvider.GetRequiredService<TenantTimeZoneSettings>()
                .GetAsync(CancellationToken.None);
            Assert.Equal("Europe/Paris", persisted.TenantTimeZone);
            Assert.Equal("administrator-two", persisted.UpdatedBy);
            Assert.Contains(persisted.History, row => row.TenantTimeZone == "Asia/Kolkata"
                && row.UpdatedBy == "administrator-one");
        }

        using (var scope = fixture.ScopeFor(otherTenant))
        {
            var settings = scope.ServiceProvider.GetRequiredService<TenantTimeZoneSettings>();
            var adapter = new TenantTimeZonePort(settings,
                fixture.Services.GetRequiredService<InMemoryWarrantyPorts>());
            Assert.Equal("UTC", await adapter.ForLegalEntityAsync(issuer,
                TimeZonePurpose.DocumentPresentation, CancellationToken.None));
        }
    }
}
