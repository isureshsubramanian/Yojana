using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Data.Platform;
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

    [DatabaseFact]
    public async Task Import_file_settings_list_named_stores_and_are_tenant_administrator_only()
    {
        var tenant = await fixture.NewTenantAsync("import-setting-http");
        using var administrator = await fixture.AuthenticatedClientAsync(tenant, YojanaRole.TenantAdministrator);
        var initial = await administrator.GetFromJsonAsync<ImportFileStorageSettings>("/api/settings/import-files");
        Assert.NotNull(initial);
        Assert.Equal(["folder-archive", "database-archive"], initial.ArchiveStores.Select(x => x.Name));
        using var savedResponse = await administrator.PutAsJsonAsync("/api/settings/import-files",
            new UpdateImportFileStorageSettings("file", "database-archive", 2, ImportFileArchiveAgeUnit.Years,
                initial.Version));
        Assert.Equal(HttpStatusCode.OK, savedResponse.StatusCode);
        var saved = await savedResponse.Content.ReadFromJsonAsync<ImportFileStorageSettings>();
        Assert.NotNull(saved);
        Assert.Equal(("file", "database-archive", 2, "years"),
            (saved.Storage, saved.ArchiveStore, saved.ArchiveAge, saved.ArchiveAgeUnit));

        using var nonAdministrator = await fixture.AuthenticatedClientAsync(tenant, YojanaRole.SerialAdministrator);
        using var forbidden = await nonAdministrator.GetAsync("/api/settings/import-files");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [DatabaseFact]
    public async Task BOM_setting_changes_write_the_standard_settings_security_events()
    {
        var tenant = await fixture.NewTenantAsync("bom-setting-audit");
        var actor = Guid.CreateVersion7();
        using var client = await fixture.AuthenticatedClientAsync(tenant, YojanaRole.TenantAdministrator, actor);
        var completeness = await client.GetFromJsonAsync<ErpBomCompletenessSettingsView>(
            "/api/settings/as-built-completeness");
        Assert.NotNull(completeness);
        using var completenessResponse = await client.PutAsJsonAsync("/api/settings/as-built-completeness",
            new UpdateErpBomCompletenessSettings(true, completeness.Version));
        Assert.Equal(HttpStatusCode.OK, completenessResponse.StatusCode);

        var certificates = await client.GetFromJsonAsync<ReleaseRecordIssuePointSettingsView>(
            "/api/certificates/settings/release-record-issue-point");
        Assert.NotNull(certificates);
        using var certificateResponse = await client.PutAsJsonAsync(
            "/api/certificates/settings/release-record-issue-point",
            new UpdateReleaseRecordIssuePointSettings(certificates.DefaultIssuePoint,
                certificates.ItemClassOverrides, certificates.Version, ReleaseRecordBomContent.FullBom));
        Assert.Equal(HttpStatusCode.OK, certificateResponse.StatusCode);

        using var scope = fixture.ScopeFor(tenant);
        var events = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SecurityEvents.AsNoTracking()
            .Where(row => row.TenantId == tenant && row.ActorUserId == actor
                && row.Kind == SecurityEventKind.SettingsChanged)
            .Select(row => row.Reason).ToListAsync();
        Assert.Contains("serialisation.erp_bom_completeness", events);
        Assert.Contains("certificates.release_record", events);
    }
}
