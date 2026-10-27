using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.TimeZones;
using Xunit;

namespace Yojana.Serialisation.Tests;

public sealed class TimeZoneSettingsTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid IndiaEntity = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid UkEntity = Guid.Parse("10000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task Presentation_and_calendar_put_entity_override_before_direct_zone_then_tenant_and_utc()
    {
        var store = new MemorySettings();
        var parties = new PartyPort(
            new PartyLabel(IndiaEntity, "LE-IN", "Fictional India Works", PartyType.LegalEntity),
            new PartyLabel(UkEntity, "LE-UK", "Fictional Britain Works", PartyType.LegalEntity));
        var settings = new TenantTimeZoneSettings(store, parties, new Clock());
        var saved = await settings.SetAsync(new UpdateTenantTimeZoneSettings("Asia/Kolkata",
            [new(UkEntity, "Europe/London")], ITenantSettings.MissingVersion), "administrator", default);
        var source = new ZoneSource();
        var location = Guid.NewGuid();
        source.Locations[location] = "Europe/Berlin";
        source.Entities[UkEntity] = "America/New_York";
        source.Entities[IndiaEntity] = "Asia/Tokyo";
        var port = new TenantTimeZonePort(settings, source);

        Assert.Equal("Europe/London", await port.ForLegalEntityAsync(UkEntity,
            TimeZonePurpose.DocumentPresentation, default));
        Assert.Equal("Asia/Tokyo", await port.ForLegalEntityAsync(IndiaEntity,
            TimeZonePurpose.DocumentPresentation, default));
        Assert.Equal("Europe/Berlin", await port.ForLocationAsync(location,
            TimeZonePurpose.DocumentPresentation, default));
        Assert.Equal("Asia/Kolkata", await port.ForLocationAsync(Guid.NewGuid(),
            TimeZonePurpose.DocumentPresentation, default));
        Assert.Equal("Europe/London", await port.ForLegalEntityAsync(UkEntity,
            TimeZonePurpose.EntitlementCalendar, default));
        Assert.Equal("Asia/Tokyo", await port.ForLegalEntityAsync(IndiaEntity,
            TimeZonePurpose.EntitlementCalendar, default));
        Assert.Equal("Asia/Kolkata", await port.ForLegalEntityAsync(Guid.NewGuid(),
            TimeZonePurpose.EntitlementCalendar, default));
        Assert.Null(await port.ForLocationAsync(Guid.NewGuid(), TimeZonePurpose.EntitlementCalendar, default));
        Assert.Equal("administrator", saved.UpdatedBy);
        Assert.Equal(Now, saved.UpdatedAt);
        Assert.Contains(saved.TimeZones, row => row.Id == "Asia/Kolkata" && row.Offset == "UTC+05:30");

        var withoutDefault = await settings.SetAsync(new UpdateTenantTimeZoneSettings(null, [], saved.Version),
            "administrator", default);
        Assert.Null(withoutDefault.TenantTimeZone);
        // The port is scoped to one request and reads the setting once; a later request sees the change.
        port = new TenantTimeZonePort(settings, source);
        Assert.Equal("UTC", await port.ForLegalEntityAsync(Guid.NewGuid(),
            TimeZonePurpose.DocumentPresentation, default));
    }

    [Fact]
    public async Task New_entitlement_resolution_is_site_then_entity_override_then_entity_then_tenant_then_utc()
    {
        var store = new MemorySettings();
        var settings = new TenantTimeZoneSettings(store,
            new PartyPort(
                new PartyLabel(IndiaEntity, "LE-IN", "Fictional India Works", PartyType.LegalEntity),
                new PartyLabel(UkEntity, "LE-UK", "Fictional Britain Works", PartyType.LegalEntity)),
            new Clock());
        var saved = await settings.SetAsync(new UpdateTenantTimeZoneSettings("Asia/Kolkata",
            [new(UkEntity, "Europe/London")], ITenantSettings.MissingVersion), "administrator", default);
        var site = Guid.NewGuid();
        var source = new ZoneSource();
        source.Locations[site] = "Europe/Berlin";
        source.Entities[UkEntity] = "America/New_York";
        source.Entities[IndiaEntity] = "Asia/Tokyo";
        var port = new TenantTimeZonePort(settings, source);

        Assert.Equal("Europe/Berlin", await CreateEntitlementHandler.ZoneAsync(
            PolicyTerms.TimeZoneCustomerSite, site, UkEntity, port, default));
        Assert.Equal("Europe/London", await CreateEntitlementHandler.ZoneAsync(
            PolicyTerms.TimeZoneCustomerSite, Guid.NewGuid(), UkEntity, port, default));
        Assert.Equal("Asia/Tokyo", await CreateEntitlementHandler.ZoneAsync(
            PolicyTerms.TimeZoneCustomerSite, null, IndiaEntity, port, default));
        Assert.Equal("Asia/Kolkata", await CreateEntitlementHandler.ZoneAsync(
            PolicyTerms.TimeZoneCustomerSite, null, Guid.NewGuid(), port, default));
        Assert.Equal("UTC", await CreateEntitlementHandler.ZoneAsync(
            PolicyTerms.TimeZoneUtc, site, UkEntity, port, default));

        await settings.SetAsync(new UpdateTenantTimeZoneSettings(null, [], saved.Version),
            "administrator", default);
        port = new TenantTimeZonePort(settings, source);
        Assert.Equal("UTC", await CreateEntitlementHandler.ZoneAsync(
            PolicyTerms.TimeZoneCustomerSite, null, Guid.NewGuid(), port, default));
    }

    [Fact]
    public async Task Unknown_and_non_legal_entity_overrides_are_refused()
    {
        var settings = new TenantTimeZoneSettings(new MemorySettings(),
            new PartyPort(new PartyLabel(IndiaEntity, "C-ONE", "Fictional Customer", PartyType.Customer)),
            new Clock());
        var wrongKind = await Assert.ThrowsAsync<CommandRefusedException>(() => settings.SetAsync(
            new UpdateTenantTimeZoneSettings("Asia/Kolkata", [new(IndiaEntity, "Europe/London")],
                ITenantSettings.MissingVersion), "administrator", default));
        Assert.Equal("time_zone_legal_entity_unknown", wrongKind.Rule);

        var unknownZone = await Assert.ThrowsAsync<CommandRefusedException>(() => settings.SetAsync(
            new UpdateTenantTimeZoneSettings("Moon/Tranquillity", [], ITenantSettings.MissingVersion),
            "administrator", default));
        Assert.Equal("time_zone_unknown", unknownZone.Rule);
    }

    [Fact]
    public async Task Unavailable_optional_source_keeps_presentation_fallback_but_refuses_direct_lookup()
    {
        var settings = new TenantTimeZoneSettings(new MemorySettings(), new PartyPort(), new Clock());
        var port = new TenantTimeZonePort(settings, new UnavailableTimeZoneSourcePort());

        Assert.Equal("UTC", await port.ForLegalEntityAsync(IndiaEntity,
            TimeZonePurpose.DocumentPresentation, default));
        Assert.Equal("UTC", await port.ForLocationAsync(Guid.NewGuid(),
            TimeZonePurpose.DocumentPresentation, default));
        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => port.ForLocationAsync(Guid.NewGuid(),
            TimeZonePurpose.EntitlementCalendar, default));
        Assert.Equal(SerialisationUnavailableCodes.TimeZoneUnavailable, refusal.Rule);
    }

    private sealed class Clock : IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class MemorySettings : ITenantSettings
    {
        private string? value;
        private string version = ITenantSettings.MissingVersion;

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(value);

        public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            this.value = value;
            version = "1";
            return Task.CompletedTask;
        }

        public Task<VersionedTenantSetting?> GetVersionedAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(value is null ? null : new VersionedTenantSetting(value, version, Now));

        public Task<VersionedTenantSetting> SetAsync(string key, string value, string expectedVersion,
            CancellationToken cancellationToken)
        {
            if (expectedVersion != version)
                throw new TenantSettingVersionConflictException(key, expectedVersion, version);
            this.value = value;
            version = version == ITenantSettings.MissingVersion ? "1" : (int.Parse(version) + 1).ToString();
            return Task.FromResult(new VersionedTenantSetting(value, version, Now));
        }
    }

    private sealed class PartyPort(params PartyLabel[] labels) : IPartyNamePort
    {
        private readonly Dictionary<Guid, PartyLabel> values = labels.ToDictionary(row => row.PartyRef);

        public Task<IReadOnlyDictionary<Guid, PartyLabel>> NamesAsync(IReadOnlyCollection<Guid> partyRefs,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<Guid, PartyLabel>>(
            partyRefs.Where(values.ContainsKey).ToDictionary(id => id, id => values[id]));

        public Task<IReadOnlyList<PartyLabel>> SearchAsync(string text, int take,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PartyLabel>>(values.Values.Take(take).ToList());
    }

    private sealed class ZoneSource : ITimeZoneSourcePort
    {
        public Dictionary<Guid, string> Locations { get; } = [];
        public Dictionary<Guid, string> Entities { get; } = [];

        public Task<string?> ForLocationAsync(Guid locationId, CancellationToken cancellationToken) =>
            Task.FromResult(Locations.GetValueOrDefault(locationId));

        public Task<string?> ForLegalEntityAsync(Guid legalEntityId, CancellationToken cancellationToken) =>
            Task.FromResult(Entities.GetValueOrDefault(legalEntityId));
    }
}
