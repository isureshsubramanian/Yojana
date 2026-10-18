using System.Net;
using System.Net.Http.Json;
using Xunit;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class TenantSettingsHttpTests(SerialisationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Two_consecutive_saves_round_trip_the_database_precision_version()
    {
        var tenant = await fixture.NewTenantAsync("setting-http");
        using var client = await fixture.AuthenticatedClientAsync(tenant);
        var initial = await client.GetFromJsonAsync<TenantTimeZoneSettingsView>("/api/settings/time-zone");
        Assert.NotNull(initial);

        using var firstResponse = await client.PutAsJsonAsync("/api/settings/time-zone",
            new UpdateTenantTimeZoneSettings("Asia/Kolkata", [], initial.Version));
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<TenantTimeZoneSettingsView>();
        Assert.NotNull(first);

        using var secondResponse = await client.PutAsJsonAsync("/api/settings/time-zone",
            new UpdateTenantTimeZoneSettings("Europe/Paris", [], first.Version));
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = await secondResponse.Content.ReadFromJsonAsync<TenantTimeZoneSettingsView>();
        Assert.NotNull(second);
        Assert.Equal("Europe/Paris", second.TenantTimeZone);
        Assert.NotEqual(first.Version, second.Version);
    }
}
