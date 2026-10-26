using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>ADR-0023: immutable preview, corrected N+1 issue and one audited viewer copy.</summary>
[Collection("serialisation host")]
public sealed class CertificateCorrectionTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Entity = Guid.Parse("c2300000-0000-0000-0000-000000000001");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    [DatabaseFact]
    public async Task Preview_shows_grouped_current_names_and_derived_parts_without_persisting()
    {
        var setup = await ReleasedAsync("correction-preview", componentCount: 8);
        Counts before;
        using (var scope = fixture.ScopeFor(setup.Tenant))
            before = await CountsAsync(scope.ServiceProvider.GetRequiredService<SerialisationDbContext>());

        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(
            new(setup.ItemId, "YD-SRV-2U", "Kestrel Systems 2U storage server"));
        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(
            new(setup.ComponentItemId!.Value, "YD-HDD", "Quill 16 TB hard drive"));

        CertificateCorrectionPreview preview;
        using (var scope = fixture.ScopeFor(setup.Tenant))
            preview = await scope.ServiceProvider.GetRequiredService<CertificateCorrectionService>()
                .PreviewAsync(setup.CertificateId, CancellationToken.None);

        Assert.True(preview.CanCorrect);
        Assert.Null(preview.RefusalCode);
        Assert.Equal((1, 2), (preview.FromVersion, preview.ToVersion));
        Assert.Contains(preview.Changes, row => row.Path == "model.name" && row.Label == "Model"
            && row.After == "Kestrel Systems 2U storage server");
        Assert.Contains(preview.Changes, row => row.Path == "as_maintained.part" && row.Label == "Part"
            && row.Before == "ITEM-97156C8D" && row.After == "Quill 16 TB hard drive · YD-HDD"
            && row.Rows == 8 && !row.Derived);
        Assert.Contains(preview.Changes, row => row.Path == "installed_parts" && row.Derived);
        Assert.True(preview.Changes.Count <= 200);

        using var assertion = fixture.ScopeFor(setup.Tenant);
        Assert.Equal(before, await CountsAsync(assertion.ServiceProvider.GetRequiredService<SerialisationDbContext>()));
    }

    [DatabaseFact]
    public async Task Preview_identical_facts_refuses_superseded_and_warranty_records()
    {
        var setup = await ReleasedAsync("correction-refusals");
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var preview = await scope.ServiceProvider.GetRequiredService<CertificateCorrectionService>()
                .PreviewAsync(setup.CertificateId, CancellationToken.None);
            Assert.False(preview.CanCorrect);
            Assert.Equal(CertificateCorrectionRefusal.NothingToCorrect, preview.RefusalCode);
            Assert.Equal("Nothing to correct: the record matches today's data.", preview.RefusalDetail);
        }

        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(
            new(setup.ItemId, "YD-SRV-EDGE", "Kestrel edge server"));
        var issued = await InvokeAsync<ReissueCertificateResult>(setup.Tenant,
            new ReissueCertificate(Guid.NewGuid(), setup.CertificateId,
                "Correct the model description in the signed record.", "quality.user"));
        Assert.Equal(2, issued.Version);

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var corrections = scope.ServiceProvider.GetRequiredService<CertificateCorrectionService>();
            var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() =>
                corrections.PreviewAsync(setup.CertificateId, CancellationToken.None));
            Assert.Equal(CertificateCorrectionRefusal.NotCurrent, refusal.Rule);
        }

        var owner = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(
            new(owner, "FIC-CUSTOMER", "Fictional Compute Services", PartyType.Customer));
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.UnitId,
            owner, PartyType.Customer, EpisodeReason.Sale, At.AddHours(3), "sales.user", Entity));
        var warranty = await InvokeAsync<IssueCertificateVersionResult>(setup.Tenant,
            new IssueCertificateVersion(Guid.NewGuid(), setup.UnitId,
                CertificateDocumentKind.WarrantyCertificate, CertificateFormType.WarrantyCertificate,
                null, At.AddHours(4), "quality.user", Entity));
        Assert.True(warranty.Issued);
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => scope.ServiceProvider
                .GetRequiredService<CertificateCorrectionService>()
                .PreviewAsync(warranty.CertificateId!.Value, CancellationToken.None));
            Assert.Equal(CertificateCorrectionRefusal.ReleaseRecordsOnly, refusal.Rule);
        }
    }

    [DatabaseFact]
    public async Task Reissue_issues_v2_prints_escaped_reason_supersedes_publishes_and_replays()
    {
        var setup = await ReleasedAsync("correction-reissue");
        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(
            new(setup.ItemId, "YD-STORAGE-2U", "Kestrel 2U storage server"));
        var command = new ReissueCertificate(Guid.NewGuid(), setup.CertificateId,
            "Corrected <part> names from the source record.", "quality.user");

        var result = await InvokeAsync<ReissueCertificateResult>(setup.Tenant, command);

        Assert.False(result.Replayed);
        Assert.Equal((setup.CertificateId, 2), (result.SupersededCertificateId, result.Version));
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var chain = await db.CertificateInstances.AsNoTracking().Where(row => row.UnitId == setup.UnitId
                    && row.DocumentKind == CertificateDocumentKind.ReleaseRecord)
                .OrderBy(row => row.Version).ToListAsync();
            Assert.Equal([CertificateStatus.Superseded, CertificateStatus.Issued], chain.Select(row => row.Status));
            Assert.Equal(CertificateSupersedeReason.Correction, chain[1].SupersedeReason);
            using var payload = JsonDocument.Parse(chain[1].Payload);
            var trigger = payload.RootElement.GetProperty("trigger");
            Assert.Equal(CertificateCorrectionService.ManualCorrectionTrigger,
                trigger.GetProperty("schema").GetString());
            Assert.Equal(command.Reason, trigger.GetProperty("reason").GetString());
            var rendered = await scope.ServiceProvider.GetRequiredService<CertificateQueries>()
                .RenderAsync(result.CertificateId, CancellationToken.None);
            var html = Encoding.UTF8.GetString(rendered!.Bytes);
            Assert.Contains("Verified record correction; unit condition was not re-verified.", html,
                StringComparison.Ordinal);
            Assert.Contains("Corrected &lt;part&gt; names from the source record.", html,
                StringComparison.Ordinal);
            Assert.DoesNotContain(command.Reason, html, StringComparison.Ordinal);
        }
        var published = Assert.Single(await PublishedEventSink.WaitForAsync<CertificateSuperseded>(row =>
            row.Header.UnitId == setup.UnitId && row.Reason == CertificateSupersedeReason.Correction,
            1, Timeout));
        Assert.Equal(setup.CertificateId, published.CertificateId);

        var replay = await InvokeAsync<ReissueCertificateResult>(setup.Tenant, command);
        Assert.True(replay.Replayed);
        Assert.Equal(result.CertificateId, replay.CertificateId);
        using var assertion = fixture.ScopeFor(setup.Tenant);
        Assert.Equal(2, await assertion.ServiceProvider.GetRequiredService<SerialisationDbContext>()
            .CertificateInstances.CountAsync(row => row.UnitId == setup.UnitId
                && row.DocumentKind == CertificateDocumentKind.ReleaseRecord));
    }

    [DatabaseFact]
    public async Task Reissue_guards_reason_unchanged_facts_and_concurrent_current_version()
    {
        var unchanged = await ReleasedAsync("correction-unchanged");
        var tooShort = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ReissueCertificateResult>(
            unchanged.Tenant, new ReissueCertificate(Guid.NewGuid(), unchanged.CertificateId, "too short",
                "quality.user")));
        Assert.Equal(CertificateCorrectionRefusal.ReasonRequired, tooShort.Rule);
        var noChange = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ReissueCertificateResult>(
            unchanged.Tenant, new ReissueCertificate(Guid.NewGuid(), unchanged.CertificateId,
                "The signed record already matches current facts.", "quality.user")));
        Assert.Equal(CertificateCorrectionRefusal.NothingToCorrect, noChange.Rule);

        var setup = await ReleasedAsync("correction-race");
        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(
            new(setup.ItemId, "YD-RACK-4U", "Juniper 4U compute server"));
        async Task<(ReissueCertificateResult? Result, CommandRefusedException? Refusal)> RunAsync(string reason)
        {
            try
            {
                return (await InvokeAsync<ReissueCertificateResult>(setup.Tenant,
                    new ReissueCertificate(Guid.NewGuid(), setup.CertificateId, reason, "quality.user")), null);
            }
            catch (CommandRefusedException exception)
            {
                return (null, exception);
            }
        }

        var attempts = await Task.WhenAll(
            RunAsync("Correct the model name after reference-data repair."),
            RunAsync("Correct the model description after reference-data repair."));

        Assert.Single(attempts, row => row.Result is { Version: 2 });
        Assert.Single(attempts, row => row.Refusal?.Rule == CertificateCorrectionRefusal.NotCurrent);
        using var scope = fixture.ScopeFor(setup.Tenant);
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>()
            .CertificateInstances.CountAsync(row => row.UnitId == setup.UnitId
                && row.DocumentKind == CertificateDocumentKind.ReleaseRecord));
    }

    [DatabaseFact]
    public async Task Endpoints_enforce_issuer_role_and_copy_returns_one_html_fallback_with_one_audit_row()
    {
        var setup = await ReleasedAsync("correction-http");
        using (var administrator = await fixture.AuthenticatedClientAsync(setup.Tenant,
                   YojanaRole.TenantAdministrator))
        {
            using var forbidden = await administrator.GetAsync(
                $"/api/certificates/{setup.CertificateId:D}/correction-preview");
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        var actor = Guid.NewGuid();
        using var quality = await fixture.AuthenticatedClientAsync(setup.Tenant, YojanaRole.Quality, actor);
        var command = new PrintCertificateCopy(Guid.NewGuid(), setup.CertificateId,
            "Customer requested a controlled replacement copy.", "spoofed.actor");
        using var response = await quality.PostAsJsonAsync(
            $"/api/certificates/{setup.CertificateId:D}/copy", command);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("html-fallback", response.Headers.GetValues("X-Yojana-Render").Single());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal($"{setup.Serial}-{setup.TrackingNumber}-v1-copy.html",
            response.Content.Headers.ContentDisposition.FileNameStar
            ?? response.Content.Headers.ContentDisposition.FileName?.Trim('"'));
        Assert.Contains("COPY", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var log = Assert.Single(await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>()
            .ReprintLogs.AsNoTracking().Where(row => row.CertificateId == setup.CertificateId).ToListAsync());
        Assert.Equal(actor.ToString("D"), log.RequestedBy);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>()
            .CertificateInstances.CountAsync(row => row.UnitId == setup.UnitId
                && row.DocumentKind == CertificateDocumentKind.ReleaseRecord));
    }

    private async Task<Setup> ReleasedAsync(string prefix, int componentCount = 0)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        var rootItem = Guid.NewGuid();
        var root = await CommissionAsync(tenant, rootItem, Serial(prefix, "ROOT"));
        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(
            new(rootItem, "ITEM-8C647191", "ITEM-8C647191"));
        Guid? componentItem = null;
        if (componentCount > 0)
        {
            componentItem = Guid.NewGuid();
            fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(
                new(componentItem.Value, "ITEM-97156C8D", "ITEM-97156C8D"));
            var installs = new List<ChildInput>();
            for (var index = 1; index <= componentCount; index++)
            {
                var child = await CommissionAsync(tenant, componentItem.Value, Serial(prefix, $"HDD{index:00}"));
                installs.Add(new(child.UnitId, Slot: $"BAY-{index:00}"));
            }
            await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), root.UnitId,
                installs, At.AddMinutes(30), "production.user", Entity));
        }
        await InvokeAsync<ReleaseUnitResult>(tenant, new ReleaseUnit(Guid.NewGuid(), root.UnitId,
            At.AddHours(1), "quality.user", Entity, "sig:quality"));
        var certificate = await WaitForCertificateAsync(tenant, root.UnitId);
        return new(tenant, root.UnitId, rootItem, componentItem, root.Serial, certificate.CertificateId,
            certificate.TrackingNumber);
    }

    private async Task<UnitSetup> CommissionAsync(TenantId tenant, Guid itemId, string serial)
    {
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(itemId));
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), itemId,
            UnitOrigin.Manufactured, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])],
            At, "production.user", Entity, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        return new(result.Lines.Single().UnitId!.Value, serial);
    }

    private async Task<Yojana.Serialisation.Data.Entities.CertificateInstance> WaitForCertificateAsync(
        TenantId tenant, Guid unitId)
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            using var scope = fixture.ScopeFor(tenant);
            var row = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>()
                .CertificateInstances.AsNoTracking().FirstOrDefaultAsync(item => item.UnitId == unitId
                    && item.DocumentKind == CertificateDocumentKind.ReleaseRecord
                    && item.Status == CertificateStatus.Issued);
            if (row is not null) return row;
            await Task.Delay(100);
        }
        throw new TimeoutException("The release record was not issued in time.");
    }

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private static async Task<Counts> CountsAsync(SerialisationDbContext db) => new(
        await db.CertificateInstances.CountAsync(), await db.CertificateTriggers.CountAsync(),
        await db.CertificateSignatures.CountAsync(), await db.NumberSeries.SumAsync(row => (long)row.LastValue));

    private static string Serial(string prefix, string suffix) =>
        $"{prefix[..Math.Min(prefix.Length, 7)].ToUpperInvariant()}-{suffix}-{Guid.NewGuid():N}"[..20];

    private sealed record UnitSetup(Guid UnitId, string Serial);
    private sealed record Setup(TenantId Tenant, Guid UnitId, Guid ItemId, Guid? ComponentItemId, string Serial,
        Guid CertificateId, string TrackingNumber);
    private sealed record Counts(int Certificates, int Triggers, int Signatures, long NextNumbers);
}
