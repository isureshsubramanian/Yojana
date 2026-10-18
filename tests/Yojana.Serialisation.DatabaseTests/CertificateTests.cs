using System.Text.Json;
using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Refurbishments;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P6-T02..T06: immutable certificate chains, payload boundaries, subscriptions and tenant templates.</summary>
[Collection("serialisation host")]
public sealed class CertificateDatabaseTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Entity = Guid.Parse("c0000000-0000-0000-0000-000000000009");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [DatabaseFact]
    public async Task Release_versions_are_immutable_signed_snapshots_and_preserve_the_whole_unit()
    {
        var setup = await CommissionAsync("cert-chain", missingRequiredComponent: true);
        await ReleaseAsync(setup);
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 1);

        var correctedSerial = setup.Serial + "-FIX";
        await InvokeAsync<CorrectIdentifierResult>(setup.Tenant, new CorrectIdentifier(Guid.NewGuid(), setup.UnitId,
            IdentifierType.InternalSerial, correctedSerial, IdentifierCorrectionReason.Typo, At.AddHours(3),
            "quality.user", Entity));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 2);
        var correctedEvent = Assert.Single(await PublishedEventSink.WaitForAsync<UnitIdentifierCorrected>(
            row => row.Header.UnitId == setup.UnitId && row.AfterValue == correctedSerial.Replace("-", "", StringComparison.Ordinal), 1, Timeout));
        // A duplicate delivery has the same event id and must not allocate another version or tracking number.
        await InvokeEventAsync(setup.Tenant, correctedEvent);
        var published = await PublishedEventSink.WaitForAsync<CertificateIssued>(row => row.Header.UnitId == setup.UnitId, 2, Timeout);
        Assert.Equal(2, published.Count);
        Assert.All(published, row => Assert.Equal((CertificateIssued.Schema, "1.0"), (row.Header.Schema, row.Header.Version)));
        var supersededPublication = Assert.Single(await PublishedEventSink.WaitForAsync<CertificateSuperseded>(
            row => row.Header.UnitId == setup.UnitId, 1, Timeout));
        Assert.Equal(CertificateSuperseded.Schema, supersededPublication.Header.Schema);

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var versions = await db.CertificateInstances.AsNoTracking().Where(row => row.UnitId == setup.UnitId
                    && row.DocumentKind == CertificateDocumentKind.ReleaseRecord).OrderBy(row => row.Version).ToListAsync();
            Assert.Equal([1, 2], versions.Select(row => row.Version));
            Assert.Equal([CertificateStatus.Superseded, CertificateStatus.Issued], versions.Select(row => row.Status));
            Assert.Equal(versions[0].CertificateId, versions[1].SupersedesCertificateId);
            Assert.Equal(CertificateSupersedeReason.IdentifierChange, versions[1].SupersedeReason);
            Assert.NotEqual(versions[0].TrackingNumber, versions[1].TrackingNumber);
            Assert.All(versions, row => Assert.Matches("^CRT-2026-[0-9]{6}$", row.TrackingNumber));
            Assert.All(versions, row => Assert.Equal(CanonicalJson.Hash(row.Payload), row.PayloadHash));
            Assert.All(versions, row => Assert.Contains("\"status\":\"MISSING\"", row.Payload, StringComparison.Ordinal));
            Assert.All(versions, row => Assert.DoesNotContain("\"owner\"", row.Payload, StringComparison.OrdinalIgnoreCase));
            Assert.Contains("\"condition_reverification_required\":false", versions[1].Payload, StringComparison.Ordinal);
            Assert.Contains("unit condition was not re-verified", versions[1].Payload, StringComparison.Ordinal);
            Assert.Contains(UnitIdentifierCorrected.Schema, versions[1].Payload, StringComparison.Ordinal);
            var signatures = await db.CertificateSignatures.AsNoTracking().Where(row => versions.Select(v => v.CertificateId).Contains(row.CertificateId)).ToListAsync();
            Assert.Equal(2, signatures.Count);
            Assert.All(signatures, signature => Assert.Equal(versions.Single(row => row.CertificateId == signature.CertificateId).PayloadHash, signature.HashSigned));

            var queries = scope.ServiceProvider.GetRequiredService<CertificateQueries>();
            var chain = await queries.ChainAsync(setup.UnitId, CancellationToken.None);
            Assert.Equal([1, 2], chain.ReleaseRecords.Select(row => row.Version));
            Assert.Empty(chain.WarrantyCertificates);
            var filtered = await queries.PageAsync(CertificateDocumentKind.ReleaseRecord, null, null,
                correctedSerial[..6], null, null, 0, 20, CancellationToken.None);
            Assert.Contains(filtered.Items, row => row.UnitId == setup.UnitId);

            var current = await db.Current.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId);
            Assert.Equal(setup.LocationId, current.LocationId);
            Assert.Equal(LocationKind.Warehouse, current.LocationKind);
            Assert.Null(current.OwnerPartyRef);
            Assert.Equal("[]", current.LiveEntitlementIds);
            Assert.Single(await db.Identifiers.AsNoTracking().Where(row => row.UnitId == setup.UnitId && row.ValidTo == null).ToListAsync());
            Assert.Equal(2, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == CertificateEventType.Issued));
            Assert.Equal(1, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == CertificateEventType.Superseded));
        }

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var issued = await db.CertificateInstances.SingleAsync(row => row.UnitId == setup.UnitId
                && row.DocumentKind == CertificateDocumentKind.ReleaseRecord && row.Version == 2);
            issued.Payload = "{}";
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var unchanged = await db.CertificateInstances.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId
                && row.DocumentKind == CertificateDocumentKind.ReleaseRecord && row.Version == 2);
            Assert.NotEqual("{}", unchanged.Payload);
            Assert.Equal(CanonicalJson.Hash(unchanged.Payload), unchanged.PayloadHash);
            Assert.Equal(CanonicalJson.HashExcludingRootProperties(unchanged.Payload, "trigger"), unchanged.FactsHash);
        }

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var issued = await db.CertificateInstances.SingleAsync(row => row.UnitId == setup.UnitId
                && row.DocumentKind == CertificateDocumentKind.ReleaseRecord && row.Version == 2);
            issued.FactsHash = new string('0', 64);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var signature = await db.CertificateSignatures.FirstAsync(row => row.CertificateId != Guid.Empty);
            signature.PrintedName = "Transferred signer";
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [DatabaseFact]
    public async Task Release_and_warranty_triggers_never_cross_chains_and_warranty_keeps_owner_snapshot()
    {
        var setup = await CommissionAsync("cert-cross");
        await ReleaseAsync(setup);
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 1);
        var owner = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(new PartyLabel(owner, "NORTH-01", "Northwind Field Services", PartyType.Customer));
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.UnitId, owner,
            PartyType.Customer, EpisodeReason.Sale, At.AddHours(2), "sales.user", Entity));

        var firstWarranty = await InvokeAsync<IssueCertificateVersionResult>(setup.Tenant,
            new IssueCertificateVersion(Guid.NewGuid(), setup.UnitId, CertificateDocumentKind.WarrantyCertificate,
                CertificateFormType.Custom, null, At.AddHours(2), "system:certificates", Entity));
        Assert.True(firstWarranty.Issued);

        var entitlementEventId = Guid.NewGuid();
        var coverage = new EntitlementCoverageChanged(
            Header(EntitlementCoverageChanged.Schema, EntitlementCoverageChanged.Version, setup.UnitId, entitlementEventId, At.AddHours(3)),
            [], [], null, null, EntitlementEventType.Amended);
        await InvokeEventAsync(setup.Tenant, coverage);
        await InvokeEventAsync(setup.Tenant, coverage);
        await Task.Delay(500);

        await AssertCountsAsync(setup.Tenant, setup.UnitId, release: 1, warranty: 1);
        await InvokeAsync<CorrectIdentifierResult>(setup.Tenant, new CorrectIdentifier(Guid.NewGuid(), setup.UnitId,
            IdentifierType.InternalSerial, setup.Serial + "-NEW", IdentifierCorrectionReason.Typo, At.AddHours(4),
            "quality.user", Entity));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 2);
        await AssertCountsAsync(setup.Tenant, setup.UnitId, release: 2, warranty: 1);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var warranty = await db.CertificateInstances.AsNoTracking().Where(row => row.UnitId == setup.UnitId
                && row.DocumentKind == CertificateDocumentKind.WarrantyCertificate).OrderByDescending(row => row.Version).FirstAsync();
        var snapshot = JsonSerializer.Deserialize<CertificateOwnerSnapshot>(warranty.OwnerSnapshot!, CanonicalJson.Options)!;
        Assert.Equal(("NORTH-01", "Northwind Field Services", PartyType.Customer), (snapshot.Code, snapshot.Name, snapshot.Kind));
        Assert.Contains("\"name\":\"Issuing entity\"", warranty.IssuerSnapshot, StringComparison.Ordinal);
        Assert.Contains("\"profile\":\"default\"", warranty.BrandingSnapshot, StringComparison.Ordinal);
        var current = await db.Current.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId);
        Assert.Equal(owner, current.OwnerPartyRef);
        Assert.Equal((setup.LocationId, LocationKind.Warehouse), (current.LocationId, current.LocationKind));
        Assert.Equal("[]", current.LiveEntitlementIds);
        Assert.Single(await db.Identifiers.AsNoTracking().Where(row => row.UnitId == setup.UnitId && row.ValidTo == null).ToListAsync());
        Assert.Equal(3, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == CertificateEventType.Issued));
        Assert.Equal(1, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == CertificateEventType.Superseded));
    }

    [DatabaseFact]
    public async Task Unreleased_units_and_incomplete_reconstruction_provenance_are_typed_refusals()
    {
        var setup = await CommissionAsync("cert-refuse");
        var beforeRelease = await InvokeAsync<IssueCertificateVersionResult>(setup.Tenant,
            new IssueCertificateVersion(Guid.NewGuid(), setup.UnitId, CertificateDocumentKind.ReleaseRecord,
                CertificateFormType.GenericRelease, null, At, "system:certificates", Entity));
        Assert.False(beforeRelease.Issued);
        Assert.Equal("certificate_unit_not_released", beforeRelease.RefusalRule);

        await ReleaseAsync(setup);
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 1);
        var incomplete = await InvokeAsync<IssueCertificateVersionResult>(setup.Tenant,
            new IssueCertificateVersion(Guid.NewGuid(), setup.UnitId, CertificateDocumentKind.ReleaseRecord,
                CertificateFormType.GenericRelease, CertificateSupersedeReason.Correction, At.AddHours(4),
                "system:certificates", Entity, CertificateProvenance.Reconstructed, ["unknown inspection"], []));
        Assert.False(incomplete.Issued);
        Assert.Equal("certificate_provenance_evidence_required", incomplete.RefusalRule);
    }

    [DatabaseFact]
    public async Task Tenant_templates_validate_payload_only_fields_and_activated_versions_cannot_be_edited()
    {
        var setup = await CommissionAsync("cert-template", itemClass: "telemetry");
        await ReleaseAsync(setup);
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 1);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var service = scope.ServiceProvider.GetRequiredService<CertificateTemplateService>();
        var starters = await service.ListAsync(CancellationToken.None);
        Assert.Contains(starters, row => row.BuiltIn && row.FormType == CertificateFormType.GenericRelease);
        Assert.Contains(starters, row => row.BuiltIn && row.FormType == CertificateFormType.CertificateOfConformity);
        Assert.Contains(starters, row => row.BuiltIn && row.DocumentKind == CertificateDocumentKind.WarrantyCertificate);
        Assert.DoesNotContain(service.Catalogue(CertificateDocumentKind.ReleaseRecord), row =>
            row.Path.Contains("party", StringComparison.OrdinalIgnoreCase) || row.Path.Contains("owner", StringComparison.OrdinalIgnoreCase));

        var invalid = service.Validate(CertificateDocumentKind.ReleaseRecord,
            "<p>{{owner.name}} {{live.table.value}}</p>", null, null, ["owner.name", "live.table.value"]);
        Assert.False(invalid.Valid);
        Assert.Contains("owner.name", invalid.PartyFieldViolations);
        Assert.Contains("live.table.value", invalid.UnknownPlaceholders);

        var source = starters.Single(row => row.FormType == CertificateFormType.GenericRelease);
        var copy = await service.CreateAsync(new CreateCertificateTemplateRequest("tenant-release", "Tenant release",
            CertificateDocumentKind.ReleaseRecord, CertificateFormType.GenericRelease, "telemetry", source.TemplateId), "template.admin", CancellationToken.None);
        var draft = Assert.Single(copy.Versions);
        var activated = await service.ActivateAsync(copy.TemplateId, draft.TemplateVersionId, "template.approver", CancellationToken.None);
        Assert.Equal(CertificateTemplateStatus.Active, activated.Status);
        var preview = await service.PreviewAsync(copy.TemplateId, activated.TemplateVersionId, setup.UnitId,
            "template.admin", CancellationToken.None);
        Assert.True(preview.Validation.Valid);
        Assert.Equal("text/html; charset=utf-8", preview.MediaType);
        Assert.Contains("YT-100", System.Text.Encoding.UTF8.GetString(preview.Bytes), StringComparison.Ordinal);

        await InvokeAsync<CorrectIdentifierResult>(setup.Tenant, new CorrectIdentifier(Guid.NewGuid(), setup.UnitId,
            IdentifierType.InternalSerial, setup.Serial + "-LAYOUT", IdentifierCorrectionReason.Typo, At.AddHours(5),
            "quality.user", Entity));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 2);

        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.ChangeTracker.Clear();
        var selected = await db.CertificateInstances.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord && row.Version == 2);
        Assert.Equal(activated.TemplateVersionId, selected.TemplateVersionId);
        var entity = await db.CertificateTemplateVersions.SingleAsync(row => row.TemplateVersionId == activated.TemplateVersionId);
        entity.LayoutBody = "<p>changed after approval</p>";
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [DatabaseFact]
    public async Task Logo_assets_are_tenant_private_sanitised_and_snapshotted_by_certificate_version()
    {
        var setup = await CommissionAsync("cert-logo");
        Guid firstAssetId;
        string firstHash;
        Guid templateId;
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var assets = scope.ServiceProvider.GetRequiredService<TenantAssetService>();
            var hostile = Encoding.UTF8.GetBytes("""
                <svg xmlns="http://www.w3.org/2000/svg" width="120" height="40" onload="alert(1)">
                  <script>alert(2)</script><foreignObject><div>unsafe</div></foreignObject>
                  <image href="https://outside.invalid/logo.png" width="10" height="10"/>
                  <image href="data:text/html;base64,PHNjcmlwdD4=" width="10" height="10"/>
                  <rect width="120" height="40" fill="url(https://outside.invalid/paint)"/>
                  <rect width="120" height="40" stroke="url(#safe) url(https://outside.invalid/bypass)"/>
                  <text x="4" y="24">Fictional Systems</text>
                </svg>
                """);
            var uploadCommand = new UploadTenantLogo(Guid.NewGuid(), Entity, hostile, "template.admin");
            var uploadedResult = await InvokeAsync<TenantAssetUploadResult>(setup.Tenant, uploadCommand);
            var uploaded = uploadedResult.Asset;
            Assert.False(uploadedResult.Replayed);
            var replayed = await InvokeAsync<TenantAssetUploadResult>(setup.Tenant, uploadCommand);
            Assert.True(replayed.Replayed);
            Assert.Equal(uploaded.AssetId, replayed.Asset.AssetId);
            firstAssetId = uploaded.AssetId;
            firstHash = uploaded.Sha256;
            Assert.Equal(("image/svg+xml", 120, 40), (uploaded.MediaType, uploaded.Width, uploaded.Height));
            var stored = await assets.ContentAsync(firstAssetId, CancellationToken.None);
            var clean = Encoding.UTF8.GetString(stored!.Bytes);
            Assert.DoesNotContain("script", clean, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("foreignObject", clean, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onload", clean, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("outside.invalid", clean, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("data:text", clean, StringComparison.OrdinalIgnoreCase);

            var templates = scope.ServiceProvider.GetRequiredService<CertificateTemplateService>();
            var starter = (await templates.ListAsync(CancellationToken.None)).Single(row => row.Code == "builtin-generic-release");
            var created = await templates.CreateAsync(new CreateCertificateTemplateRequest("logo-release", "Logo release",
                CertificateDocumentKind.ReleaseRecord, CertificateFormType.GenericRelease, null, starter.TemplateId),
                "template.admin", CancellationToken.None);
            templateId = created.TemplateId;
            var draft = Assert.Single(created.Versions);
            var saved = await templates.SaveDraftAsync(templateId,
                Request(draft, firstAssetId) with { HeaderBody = "<header>{{model.code}}</header>" },
                "template.admin", CancellationToken.None);
            await templates.ActivateAsync(templateId, saved.Version!.TemplateVersionId, "template.admin", CancellationToken.None);
        }

        await ReleaseAsync(setup);
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 1);

        Guid secondAssetId;
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var replacement = (await InvokeAsync<TenantAssetUploadResult>(setup.Tenant,
                new UploadTenantLogo(Guid.NewGuid(), Entity, Encoding.UTF8.GetBytes("""
                <svg xmlns="http://www.w3.org/2000/svg" width="120" height="40"><text x="4" y="24">New mark</text></svg>
                """), "template.admin"))).Asset;
            secondAssetId = replacement.AssetId;
            var templates = scope.ServiceProvider.GetRequiredService<CertificateTemplateService>();
            var detail = (await templates.GetAsync(templateId, CancellationToken.None))!;
            var active = detail.Versions.Single(row => row.Status == CertificateTemplateStatus.Active);
            var next = await templates.SaveDraftAsync(templateId, Request(active, secondAssetId) with { TemplateVersionId = null },
                "template.admin", CancellationToken.None);
            await templates.ActivateAsync(templateId, next.Version!.TemplateVersionId, "template.admin", CancellationToken.None);
        }

        await InvokeAsync<CorrectIdentifierResult>(setup.Tenant, new CorrectIdentifier(Guid.NewGuid(), setup.UnitId,
            IdentifierType.InternalSerial, setup.Serial + "-BRAND", IdentifierCorrectionReason.Typo, At.AddHours(3),
            "quality.user", Entity));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 2);

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var versions = await db.CertificateInstances.AsNoTracking().Where(row => row.UnitId == setup.UnitId
                    && row.DocumentKind == CertificateDocumentKind.ReleaseRecord).OrderBy(row => row.Version).ToListAsync();
            var firstBranding = JsonSerializer.Deserialize<CertificateBrandingSnapshot>(versions[0].BrandingSnapshot, CanonicalJson.Options)!;
            var secondBranding = JsonSerializer.Deserialize<CertificateBrandingSnapshot>(versions[1].BrandingSnapshot, CanonicalJson.Options)!;
            Assert.Equal((firstAssetId, firstHash), (firstBranding.LogoAssetId, firstBranding.LogoSha256));
            Assert.Equal(secondAssetId, secondBranding.LogoAssetId);
            Assert.NotEqual(firstBranding.LogoSha256, secondBranding.LogoSha256);
            var rendered = await scope.ServiceProvider.GetRequiredService<CertificateQueries>()
                .RenderAsync(versions[0].CertificateId, CancellationToken.None);
            var renderedHtml = Encoding.UTF8.GetString(rendered!.Bytes);
            Assert.Contains(Convert.ToBase64String((await db.TenantAssets.AsNoTracking().SingleAsync(row => row.AssetId == firstAssetId)).Bytes),
                renderedHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("{{model.code}}", renderedHtml, StringComparison.Ordinal);
            var current = await db.Current.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId);
            Assert.Null(current.OwnerPartyRef);
            Assert.Equal((setup.LocationId, LocationKind.Warehouse, "[]"),
                (current.LocationId, current.LocationKind, current.LiveEntitlementIds));
            var openEpisodes = await db.Episodes.AsNoTracking().Where(row => row.UnitId == setup.UnitId && row.ValidTo == null).ToListAsync();
            Assert.DoesNotContain(openEpisodes, row => row.Kind == EpisodeKind.Ownership);
            var custody = Assert.Single(openEpisodes, row => row.Kind == EpisodeKind.Custody);
            Assert.Equal((setup.LocationId, LocationKind.Warehouse), (custody.LocationId, custody.LocationKind));
            Assert.Empty(await db.Entitlements.AsNoTracking().Where(row => row.UnitId == setup.UnitId).ToListAsync());
            var identifier = Assert.Single(await db.Identifiers.AsNoTracking()
                .Where(row => row.UnitId == setup.UnitId && row.ValidTo == null).ToListAsync());
            Assert.Equal(setup.Serial + "-BRAND", identifier.RawValue);
            Assert.Equal(1, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == UnitEventType.IdentifierCorrected));
            Assert.Equal(2, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == CertificateEventType.Issued));
            Assert.Equal(1, await db.Events.CountAsync(row => row.UnitId == setup.UnitId && row.EventType == CertificateEventType.Superseded));
        }

        var otherTenant = await fixture.NewTenantAsync("cert-logo-other");
        using var otherScope = fixture.ScopeFor(otherTenant);
        Assert.Null(await otherScope.ServiceProvider.GetRequiredService<TenantAssetService>()
            .ContentAsync(firstAssetId, CancellationToken.None));

        static SaveCertificateTemplateDraftRequest Request(CertificateTemplateVersionRow row, Guid assetId) => new(
            row.TemplateVersionId, row.Language, row.PayloadSchemaVersion, row.LayoutBody, row.PageSize, row.Orientation,
            row.MarginTopMm, row.MarginRightMm, row.MarginBottomMm, row.MarginLeftMm, row.HeaderBody, row.FooterBody,
            null, new Dictionary<string, Guid> { [Entity.ToString()] = assetId }, row.StaticText, row.Fields);
    }

    [DatabaseFact]
    public async Task Logo_upload_reads_raster_dimensions_from_magic_bytes_and_refuses_unknown_or_oversized_content()
    {
        var tenant = await fixture.NewTenantAsync("cert-logo-raster");
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
            0, 0, 1, 64, 0, 0, 0, 48];
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xc0, 0, 17, 8, 0, 64, 1, 32, 3, 1, 17, 0, 2, 17, 0, 3, 17, 0];

        var pngResult = await InvokeAsync<TenantAssetUploadResult>(tenant,
            new UploadTenantLogo(Guid.NewGuid(), null, png, "template.admin"));
        var jpegResult = await InvokeAsync<TenantAssetUploadResult>(tenant,
            new UploadTenantLogo(Guid.NewGuid(), null, jpeg, "template.admin"));

        Assert.Equal(("image/png", 320, 48), (pngResult.Asset.MediaType, pngResult.Asset.Width, pngResult.Asset.Height));
        Assert.Equal(("image/jpeg", 288, 64), (jpegResult.Asset.MediaType, jpegResult.Asset.Width, jpegResult.Asset.Height));
        var unknown = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<TenantAssetUploadResult>(tenant,
            new UploadTenantLogo(Guid.NewGuid(), null, "not an image"u8.ToArray(), "template.admin")));
        Assert.Equal("tenant_asset_media_type_unknown", unknown.Rule);
        var oversized = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<TenantAssetUploadResult>(tenant,
            new UploadTenantLogo(Guid.NewGuid(), null, new byte[TenantAssetService.MaximumBytes + 1], "template.admin")));
        Assert.Equal("tenant_asset_too_large", oversized.Rule);
    }

    [DatabaseFact]
    public async Task Refurbishment_release_absorbs_overlapping_release_triggers_and_redelivery()
    {
        var setup = await CommissionAsync("cert-refurb");
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.UnitId, Entity,
            PartyType.Us, "company_stock", At.AddMinutes(5), "receiving", Entity));
        await ReleaseAsync(setup);
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 1);
        using (var scope = fixture.ScopeFor(setup.Tenant))
            await scope.ServiceProvider.GetRequiredService<RefurbishmentPolicySettings>()
                .SetAsync(new RefurbishmentPolicy(RefurbishmentIdentity.SameUnitSameSerial, 3, []), CancellationToken.None);
        var repair = Guid.NewGuid();
        var opened = await InvokeAsync<OpenRefurbishmentResult>(setup.Tenant,
            new OpenRefurbishment(Guid.NewGuid(), setup.UnitId, RefurbishmentKind.Refurbish, At.AddHours(2), "quality", Entity, repair));
        await InvokeAsync<RecordTestResult>(setup.Tenant, new RecordTest(Guid.NewGuid(), setup.UnitId,
            RefurbishmentTestKind.KeyFunctions, UnitTestResult.Pass, "{}", "fixture-fictional-cert-1",
            At.AddHours(2.5), "quality", Entity));
        await InvokeAsync<ReleaseRefurbishmentResult>(setup.Tenant, new ReleaseRefurbishment(Guid.NewGuid(),
            opened.WorkOrderId, ConditionGrades.RefurbishedA, "graded_receipt_plus_postings", At.AddHours(3),
            "quality", Entity, setup.LocationId, null, "F2", "C2"));

        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 2);
        var released = Assert.Single(await PublishedEventSink.WaitForAsync<RefurbishmentReleased>(
            row => row.WorkOrderId == opened.WorkOrderId, 1, Timeout));
        await InvokeEventAsync(setup.Tenant, released);
        await Task.Delay(500);

        using var finalScope = fixture.ScopeFor(setup.Tenant);
        var db = finalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var versions = await db.CertificateInstances.AsNoTracking().Where(row => row.UnitId == setup.UnitId
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord).OrderBy(row => row.Version).ToListAsync();
        Assert.Equal([1, 2], versions.Select(row => row.Version));
        Assert.Equal(1, await db.CertificateTriggers.CountAsync(row => row.UnitId == setup.UnitId
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord && row.Outcome == "absorbed"));
        await AssertWholeUnitAsync(db, setup.UnitId, Entity, setup.LocationId);
    }

    [DatabaseFact]
    public async Task Sale_entitlement_events_share_one_warranty_version_when_the_final_facts_are_identical()
    {
        var setup = await CommissionAsync("cert-sale");
        await ReleaseAsync(setup);
        var owner = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(new PartyLabel(owner, "BUYER-01", "Fictional Survey Works", PartyType.Customer));
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.UnitId, owner,
            PartyType.Customer, EpisodeReason.Sale, At.AddHours(2), "sales", Entity));
        var initial = await InvokeAsync<IssueCertificateVersionResult>(setup.Tenant,
            new IssueCertificateVersion(Guid.NewGuid(), setup.UnitId, CertificateDocumentKind.WarrantyCertificate,
                CertificateFormType.Custom, null, At.AddHours(2), "system:certificates", Entity));
        Assert.True(initial.Issued);
        var policies = new List<Guid>();
        fixture.Services.GetRequiredService<InMemoryWarrantyPorts>().RegisterZone(setup.LocationId, "UTC");
        foreach (var family in new[] { "sale_parts", "sale_service" })
        {
            await InvokeAsync<CoverageFamilyRow>(setup.Tenant,
                new DefineCoverageFamily(family, family, false, AssuranceOrService.Service));
            var code = family.ToUpperInvariant() + "-" + Guid.NewGuid().ToString("N")[..6];
            var draft = await InvokeAsync<PolicyDraftResult>(setup.Tenant, new CreatePolicyDraft(Guid.NewGuid(), code,
                code, family, EntitlementType.Contract, new DateOnly(2026, 1, 1),
                PolicyValidatorTestsTerms.Standard24() with { AssuranceOrService = AssuranceOrService.Service }, "policy.admin"));
            policies.Add((await InvokeAsync<PolicyDraftResult>(setup.Tenant,
                new PublishPolicyVersion(draft.PolicyVersionId, "policy.admin"))).PolicyVersionId);
        }
        await InvokeEventAsync(setup.Tenant, new CreateSaleEntitlements(setup.UnitId, owner, policies, At.AddHours(3), Entity));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.WarrantyCertificate, 2);
        var coverage = await PublishedEventSink.WaitForAsync<EntitlementCoverageChanged>(
            row => row.Header.UnitId == setup.UnitId && row.Header.EventTime == At.AddHours(3), 2, Timeout);
        await WaitForTriggerCountAsync(setup.Tenant, coverage.Select(row => row.Header.EventId).ToList(), 2);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(2, await db.Entitlements.CountAsync(row => row.UnitId == setup.UnitId));
        Assert.Equal(2, await db.CertificateInstances.CountAsync(row => row.UnitId == setup.UnitId
            && row.DocumentKind == CertificateDocumentKind.WarrantyCertificate));
        var outcomes = await db.CertificateTriggers.Where(row => coverage.Select(message => message.Header.EventId)
                .Contains(row.TriggeringEventId)).Select(row => row.Outcome).OrderBy(row => row).ToListAsync();
        Assert.Equal(["absorbed", "issued"], outcomes);
        await AssertWholeUnitAsync(db, setup.UnitId, owner, setup.LocationId, expectedEntitlements: 2);
    }

    [DatabaseFact]
    public async Task Concurrent_chain_triggers_complete_without_duplicate_versions_or_gaps()
    {
        var setup = await CommissionAsync("cert-concurrent");
        await ReleaseAsync(setup);
        await WaitForCountAsync(setup.Tenant, setup.UnitId, CertificateDocumentKind.ReleaseRecord, 1);
        var corrected = setup.Serial + "-CONCURRENT";
        await CorrectWithoutPublicationAsync(setup, corrected);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var before = setup.Serial.Replace("-", "", StringComparison.Ordinal);
        var after = corrected.Replace("-", "", StringComparison.Ordinal);
        var first = new UnitIdentifierCorrected(Header(UnitIdentifierCorrected.Schema, UnitIdentifierCorrected.Version,
            setup.UnitId, firstId, At.AddHours(3)), IdentifierType.InternalSerial, IdentifierCorrectionReason.Typo,
            before, after, Guid.NewGuid(), Guid.NewGuid(), null);
        var second = first with { Header = Header(UnitIdentifierCorrected.Schema, UnitIdentifierCorrected.Version,
            setup.UnitId, secondId, At.AddHours(3).AddSeconds(1)) };

        await Task.WhenAll(InvokeEventAsync(setup.Tenant, first), InvokeEventAsync(setup.Tenant, second));
        await WaitForTriggerCountAsync(setup.Tenant, [firstId, secondId], 2);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var versions = await db.CertificateInstances.AsNoTracking().Where(row => row.UnitId == setup.UnitId
                && row.DocumentKind == CertificateDocumentKind.ReleaseRecord).OrderBy(row => row.Version)
            .Select(row => row.Version).ToListAsync();
        Assert.Equal([1, 2], versions);
        var outcomes = await db.CertificateTriggers.Where(row => row.TriggeringEventId == firstId || row.TriggeringEventId == secondId)
            .Select(row => row.Outcome).OrderBy(row => row).ToListAsync();
        Assert.Equal(["absorbed", "issued"], outcomes);
    }

    [DatabaseFact]
    public async Task Concurrent_first_template_reads_create_each_starter_once()
    {
        var tenant = await fixture.NewTenantAsync("cert-starter-race");
        async Task<IReadOnlyList<CertificateTemplateSummary>> LoadAsync()
        {
            using var scope = fixture.ScopeFor(tenant);
            return await scope.ServiceProvider.GetRequiredService<CertificateTemplateService>().ListAsync(CancellationToken.None);
        }

        var lists = await Task.WhenAll(LoadAsync(), LoadAsync());
        Assert.All(lists, list => Assert.Equal(3, list.Count(row => row.BuiltIn)));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(3, await db.CertificateTemplates.CountAsync(row => row.BuiltIn));
        Assert.Equal(3, await db.CertificateTemplateVersions.CountAsync());
    }

    [DatabaseFact]
    public async Task Certificate_template_policy_accepts_certificate_admin_and_rejects_label_admin()
    {
        var tenant = await fixture.NewTenantAsync("cert-role");
        using var scope = fixture.ScopeFor(tenant);
        var authorisation = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        ClaimsPrincipal Actor(string role) => new(new ClaimsIdentity(
            [new Claim(YojanaClaim.TenantId, tenant.ToString()), new Claim(YojanaClaim.Role, role)], "test"));

        Assert.True((await authorisation.AuthorizeAsync(Actor(YojanaRole.CertificateAdministrator), null,
            YojanaPolicy.CertificateAdministrator)).Succeeded);
        Assert.False((await authorisation.AuthorizeAsync(Actor(YojanaRole.LabelAdministrator), null,
            YojanaPolicy.CertificateAdministrator)).Succeeded);
    }

    private async Task CorrectWithoutPublicationAsync(Setup setup, string correctedSerial)
    {
        using var scope = fixture.ScopeFor(setup.Tenant);
        scope.ServiceProvider.GetRequiredService<LedgerPublication>().Suppressed = true;
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await CorrectIdentifierHandler.Handle(new CorrectIdentifier(Guid.NewGuid(), setup.UnitId,
                IdentifierType.InternalSerial, correctedSerial, IdentifierCorrectionReason.Typo, At.AddHours(2.5),
                "quality.user", Entity), db, scope.ServiceProvider.GetRequiredService<IUnitEventWriter>(),
            scope.ServiceProvider.GetRequiredService<IdentifierService>(),
            scope.ServiceProvider.GetRequiredService<CorrectionLedger>(),
            scope.ServiceProvider.GetRequiredService<ITenantContext>(),
            scope.ServiceProvider.GetRequiredService<IClock>(), CancellationToken.None);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task WaitForTriggerCountAsync(TenantId tenant, IReadOnlyList<Guid> eventIds, int count)
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            using var scope = fixture.ScopeFor(tenant);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            if (await db.CertificateTriggers.CountAsync(row => eventIds.Contains(row.TriggeringEventId)) >= count) return;
            await Task.Delay(100);
        }
        using var finalScope = fixture.ScopeFor(tenant);
        var finalDb = finalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(count, await finalDb.CertificateTriggers.CountAsync(row => eventIds.Contains(row.TriggeringEventId)));
    }

    private static async Task AssertWholeUnitAsync(SerialisationDbContext db, Guid unitId, Guid owner,
        Guid location, int expectedEntitlements = 0)
    {
        var ownership = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == unitId
            && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        var custody = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == unitId
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        Assert.Equal(owner, ownership.PartyRef);
        Assert.Equal((location, LocationKind.Warehouse), (custody.LocationId, custody.LocationKind));
        Assert.Equal(expectedEntitlements, await db.Entitlements.CountAsync(row => row.UnitId == unitId
            && row.Status != EntitlementStatus.Closed));
        Assert.Single(await db.Identifiers.Where(row => row.UnitId == unitId && row.ValidTo == null).ToListAsync());
        Assert.True(await db.Events.AnyAsync(row => row.UnitId == unitId && row.EventType == CertificateEventType.Issued));
    }

    private async Task<Setup> CommissionAsync(string prefix, bool missingRequiredComponent = false, string? itemClass = null)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        var item = Guid.NewGuid();
        var location = Guid.NewGuid();
        var serial = prefix.ToUpperInvariant()[..Math.Min(prefix.Length, 10)] + "-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var items = fixture.Services.GetRequiredService<InMemoryItemPorts>();
        items.Register(InMemoryItemPorts.DefaultProfile(item) with
        {
            AsBuiltCompleteness = missingRequiredComponent ? ItemSerialisationProfile.Completeness.Warn : ItemSerialisationProfile.Completeness.Block,
            ItemClass = itemClass,
        });
        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(new ItemLabel(item, "YT-100", "Telemetry controller"));
        if (missingRequiredComponent)
            items.RegisterBom(item, [new ExpectedComponent("BAT", Guid.NewGuid(), "BAT-1", 1)]);
        var command = new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], At,
            "production.user", Entity, LocationId: location, LocationKind: LocationKind.Warehouse);
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, command);
        return new Setup(tenant, result.Lines.Single().UnitId!.Value, item, location, serial);
    }

    private Task<ReleaseUnitResult> ReleaseAsync(Setup setup) => InvokeAsync<ReleaseUnitResult>(setup.Tenant,
        new ReleaseUnit(Guid.NewGuid(), setup.UnitId, At.AddHours(1), "quality.user", Entity, "sig:quality"));

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task InvokeEventAsync(TenantId tenant, object message)
    {
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None, Timeout);
    }

    private async Task WaitForCountAsync(TenantId tenant, Guid unitId, string kind, int count)
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            using var scope = fixture.ScopeFor(tenant);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            if (await db.CertificateInstances.CountAsync(row => row.UnitId == unitId && row.DocumentKind == kind) >= count) return;
            await Task.Delay(100);
        }
        using var finalScope = fixture.ScopeFor(tenant);
        var finalDb = finalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(count, await finalDb.CertificateInstances.CountAsync(row => row.UnitId == unitId && row.DocumentKind == kind));
    }

    private async Task AssertCountsAsync(TenantId tenant, Guid unitId, int release, int warranty)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(release, await db.CertificateInstances.CountAsync(row => row.UnitId == unitId && row.DocumentKind == CertificateDocumentKind.ReleaseRecord));
        Assert.Equal(warranty, await db.CertificateInstances.CountAsync(row => row.UnitId == unitId && row.DocumentKind == CertificateDocumentKind.WarrantyCertificate));
    }

    private static PublishedEventHeader Header(string schema, string version, Guid unitId, Guid eventId, DateTime at) =>
        new(schema, version, eventId, unitId, at, at, 1, Entity, "system:test-event", null);

    private sealed record Setup(TenantId Tenant, Guid UnitId, Guid ItemId, Guid LocationId, string Serial);
}

public sealed record CreateSaleEntitlements(Guid UnitId, Guid OwnerId, IReadOnlyList<Guid> PolicyVersionIds,
    DateTime EventTime, Guid LegalEntityId);

public static class CreateSaleEntitlementsHandler
{
    public static async Task Handle(CreateSaleEntitlements message, SerialisationDbContext db, EntitlementLedger ledger,
        PolicyResolver resolver, PolicyStore store, ITimeZonePort zones, RefurbishedGoodsSettings refurbishedSettings,
        ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        for (var index = 0; index < message.PolicyVersionIds.Count; index++)
        {
            var family = index == 0 ? "sale_parts" : "sale_service";
            await CreateEntitlementHandler.Handle(new CreateEntitlement(Guid.NewGuid(), message.UnitId, family,
                DateOnly.FromDateTime(message.EventTime), message.LegalEntityId, message.EventTime, "sales",
                new Dictionary<string, DateTime> { [StartTrigger.Invoice] = message.EventTime },
                PolicyVersionId: message.PolicyVersionIds[index], OwnerPartyRef: message.OwnerId,
                OwnerPartyType: PartyType.Customer, SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), index + 1)),
                db, ledger, resolver, store, zones, refurbishedSettings, tenant, clock, cancellationToken);
        }
    }
}
