using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Developer.Data;
using Yojana.Developer.Data.Entities;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Developer.DatabaseTests;

[Collection("developer host")]
public sealed class PublicApiReadTests(DeveloperHostFixture fixture)
{
    [DatabaseFact]
    public async Task Customer_client_gets_only_its_party_and_other_ids_are_not_probeable()
    {
        var setup = await fixture.NewTenantAsync("public-party");
        var party = Guid.CreateVersion7();
        var otherParty = Guid.CreateVersion7();
        var owned = await PublicApiTestSupport.SeedUnitAsync(fixture, setup.Tenant, party, "SRV-PARTY-01");
        var hidden = await PublicApiTestSupport.SeedUnitAsync(fixture, setup.Tenant, otherParty, "SRV-PARTY-02");
        var (_, token) = await PublicApiTestSupport.CreateTokenAsync(fixture, setup.Tenant,
            setup.Administrator, [PublicApiScopes.UnitsRead], PublicApiDisclosureTier.Customer, party);
        using var client = PublicApiTestSupport.Anonymous(fixture);

        using var visibleRequest = PublicApiTestSupport.Bearer(HttpMethod.Get,
            $"/api/public/v1/units/{owned:D}", token.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(visibleRequest)).StatusCode);
        using var hiddenRequest = PublicApiTestSupport.Bearer(HttpMethod.Get,
            $"/api/public/v1/units/{hidden:D}", token.AccessToken);
        var hiddenResponse = await client.SendAsync(hiddenRequest);
        Assert.Equal(HttpStatusCode.NotFound, hiddenResponse.StatusCode);
        Assert.Equal("not_found", await CodeAsync(hiddenResponse));
    }

    [DatabaseFact]
    public async Task Tenant_token_never_reads_another_tenants_unit()
    {
        var a = await fixture.NewTenantAsync("pub-a");
        var b = await fixture.NewTenantAsync("pub-b");
        var unit = await PublicApiTestSupport.SeedUnitAsync(fixture, b.Tenant, Guid.CreateVersion7(),
            "STORAGE-TENANT-B");
        var (_, token) = await PublicApiTestSupport.CreateTokenAsync(fixture, a.Tenant, a.Administrator,
            [PublicApiScopes.UnitsRead]);
        using var client = PublicApiTestSupport.Anonymous(fixture);
        using var request = PublicApiTestSupport.Bearer(HttpMethod.Get,
            $"/api/public/v1/units/{unit:D}", token.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
    }

    [DatabaseFact]
    public async Task Disclosure_tiers_omit_each_field_above_their_rank()
    {
        var setup = await fixture.NewTenantAsync("pub-disc");
        var party = Guid.CreateVersion7();
        var unit = await PublicApiTestSupport.SeedUnitAsync(fixture, setup.Tenant, party, "SWITCH-DISCLOSE-01");
        await PublicApiTestSupport.SeedEntitlementAsync(fixture, setup.Tenant, unit, party);
        var (_, customer) = await PublicApiTestSupport.CreateTokenAsync(fixture, setup.Tenant,
            setup.Administrator, [PublicApiScopes.CoverRead], PublicApiDisclosureTier.Customer, party);
        var (_, backOffice) = await PublicApiTestSupport.CreateTokenAsync(fixture, setup.Tenant,
            setup.Administrator, [PublicApiScopes.CoverRead]);

        using var client = PublicApiTestSupport.Anonymous(fixture);
        using var customerRequest = PublicApiTestSupport.Bearer(HttpMethod.Get,
            $"/api/public/v1/units/{unit:D}/cover", customer.AccessToken);
        var customerResponse = await client.SendAsync(customerRequest);
        customerResponse.EnsureSuccessStatusCode();
        using var customerJson = JsonDocument.Parse(await customerResponse.Content.ReadAsStringAsync());
        var customerCover = customerJson.RootElement.GetProperty("cover")[0];
        Assert.True(customerCover.TryGetProperty("status", out _));
        Assert.True(customerCover.TryGetProperty("startDate", out _));
        Assert.False(customerCover.TryGetProperty("ownerPartyId", out _));
        Assert.False(customerCover.TryGetProperty("obligorEntityId", out _));

        using var internalRequest = PublicApiTestSupport.Bearer(HttpMethod.Get,
            $"/api/public/v1/units/{unit:D}/cover", backOffice.AccessToken);
        var internalResponse = await client.SendAsync(internalRequest);
        internalResponse.EnsureSuccessStatusCode();
        using var internalJson = JsonDocument.Parse(await internalResponse.Content.ReadAsStringAsync());
        var internalCover = internalJson.RootElement.GetProperty("cover")[0];
        Assert.True(internalCover.TryGetProperty("ownerPartyId", out _));
        Assert.True(internalCover.TryGetProperty("obligorEntityId", out _));
    }

    [DatabaseFact]
    public async Task Identifier_results_page_by_stable_opaque_cursor_and_support_strong_etags()
    {
        var setup = await fixture.NewTenantAsync("public-page");
        var party = Guid.CreateVersion7();
        var firstId = await PublicApiTestSupport.SeedUnitAsync(fixture, setup.Tenant, party, "SERVER-PAGE",
            Guid.CreateVersion7());
        var secondId = await PublicApiTestSupport.SeedUnitAsync(fixture, setup.Tenant, party, "SERVER-PAGE",
            Guid.CreateVersion7());
        var (_, token) = await PublicApiTestSupport.CreateTokenAsync(fixture, setup.Tenant,
            setup.Administrator, [PublicApiScopes.UnitsRead]);
        using var client = PublicApiTestSupport.Anonymous(fixture);

        using var first = PublicApiTestSupport.Bearer(HttpMethod.Get,
            "/api/public/v1/units?identifier=SERVER-PAGE&limit=1", token.AccessToken);
        var firstResponse = await client.SendAsync(first);
        firstResponse.EnsureSuccessStatusCode();
        Assert.NotNull(firstResponse.Headers.ETag);
        using var firstJson = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        var firstUnit = firstJson.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();
        var cursor = firstJson.RootElement.GetProperty("next").GetString();
        Assert.NotNull(cursor);
        Assert.DoesNotContain(firstUnit.ToString("N"), cursor, StringComparison.OrdinalIgnoreCase);

        using var second = PublicApiTestSupport.Bearer(HttpMethod.Get,
            $"/api/public/v1/units?identifier=SERVER-PAGE&limit=1&after={Uri.EscapeDataString(cursor)}",
            token.AccessToken);
        using var secondJson = JsonDocument.Parse(await (await client.SendAsync(second)).Content.ReadAsStringAsync());
        var secondUnit = secondJson.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.NotEqual(firstUnit, secondUnit);
        Assert.Equal(new HashSet<Guid> { firstId, secondId }, new HashSet<Guid> { firstUnit, secondUnit });

        using var conditional = PublicApiTestSupport.Bearer(HttpMethod.Get,
            "/api/public/v1/units?identifier=SERVER-PAGE&limit=1", token.AccessToken);
        conditional.Headers.IfNoneMatch.Add(firstResponse.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional)).StatusCode);
    }

    [DatabaseFact]
    public async Task Sandbox_clients_read_only_test_events_and_normal_clients_read_only_real_events()
    {
        var setup = await fixture.NewTenantAsync("sandbox");
        var real = Guid.CreateVersion7();
        var test = Guid.CreateVersion7();
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            db.PublicEvents.AddRange(Event(setup.Tenant, real, false), Event(setup.Tenant, test, true));
            await db.SaveChangesAsync();
        }
        var (_, normalToken) = await PublicApiTestSupport.CreateTokenAsync(fixture, setup.Tenant,
            setup.Administrator, [PublicApiScopes.EventsRead]);
        var (_, sandboxToken) = await PublicApiTestSupport.CreateTokenAsync(fixture, setup.Tenant,
            setup.Administrator, [PublicApiScopes.EventsRead], sandbox: true);
        using var client = PublicApiTestSupport.Anonymous(fixture);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, $"/api/public/v1/events/{real:D}",
            normalToken.AccessToken));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, $"/api/public/v1/events/{test:D}",
            normalToken.AccessToken));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, $"/api/public/v1/events/{real:D}",
            sandboxToken.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, $"/api/public/v1/events/{test:D}",
            sandboxToken.AccessToken));
    }

    [DatabaseFact]
    public async Task Customer_client_cannot_probe_another_partys_service_resources_or_bulk_operation()
    {
        var setup = await fixture.NewTenantAsync("resscope");
        var customer = Guid.CreateVersion7();
        var otherCustomer = Guid.CreateVersion7();
        var otherUnit = await PublicApiTestSupport.SeedUnitAsync(fixture, setup.Tenant, otherCustomer,
            "SERVER-HIDDEN-RESOURCE");
        var entitlement = await PublicApiTestSupport.SeedEntitlementAsync(fixture, setup.Tenant, otherUnit,
            otherCustomer);
        var resources = await SeedRestrictedResourcesAsync(setup.Tenant, otherCustomer, otherUnit, entitlement);
        var (_, token) = await PublicApiTestSupport.CreateTokenAsync(fixture, setup.Tenant,
            setup.Administrator, [PublicApiScopes.ClaimsRead, PublicApiScopes.RmaRead,
                PublicApiScopes.CertificatesRead, PublicApiScopes.EventsRead, PublicApiScopes.UnitsRead],
            PublicApiDisclosureTier.Customer, customer);
        using var client = PublicApiTestSupport.Anonymous(fixture);

        foreach (var path in new[]
        {
            $"/api/public/v1/claims/{resources.ClaimId:D}",
            $"/api/public/v1/rmas/{resources.RmaId:D}",
            $"/api/public/v1/replacements/{resources.ReplacementId:D}",
            $"/api/public/v1/certificates/{resources.CertificateId:D}",
            $"/api/public/v1/certificates/{resources.CertificateId:D}/pdf",
            $"/api/public/v1/events/{resources.EventId:D}",
            $"/api/public/v1/operations/{resources.OperationId:D}/units",
        })
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, path, token.AccessToken));
    }

    private async Task<RestrictedResources> SeedRestrictedResourcesAsync(TenantId tenant, Guid party, Guid unitId,
        Guid entitlementId)
    {
        var claimId = Guid.CreateVersion7();
        var rmaId = Guid.CreateVersion7();
        var replacementId = Guid.CreateVersion7();
        var certificateId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        var operationId = Guid.CreateVersion7();
        using var scope = fixture.ScopeFor(tenant);
        var serial = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var episode = await serial.Episodes.Where(row => row.UnitId == unitId).Select(row => row.EpisodeId)
            .SingleAsync();
        var item = await serial.Current.Where(row => row.UnitId == unitId).Select(row => row.ItemId).SingleAsync();
        var entity = Guid.CreateVersion7();
        await scope.ServiceProvider.GetRequiredService<CertificateTemplateService>()
            .EnsureStartersAsync(CancellationToken.None);
        var templateVersion = await serial.CertificateTemplateVersions.Select(row => row.TemplateVersionId)
            .FirstAsync();
        serial.Claims.Add(new Claim
        {
            TenantId = tenant, ClaimId = claimId, ClaimNumber = "CLM-2026-HIDDEN", CommandId = Guid.CreateVersion7(),
            UnitId = unitId, ClaimDate = new(2026, 8, 10), ClaimantPartyRef = party,
            SettlementPartyRef = party, ServiceEntityId = entity, ObligorEntityId = entity,
            ServiceCountry = "GB", Currency = "GBP", CoverageSource = ClaimCoverageSource.Warranty,
            Remedy = ClaimRemedy.Repair, Status = ClaimStatus.Submitted, SubmittedAt = fixture.Clock.UtcNow,
            SubmittedBy = "fixture", LastEventId = Guid.CreateVersion7(),
        });
        serial.ClaimLines.Add(new ClaimLine
        {
            TenantId = tenant, ClaimLineId = Guid.CreateVersion7(), ClaimId = claimId, UnitId = unitId,
            Sequence = 1, Kind = ClaimLineKind.Parts, CoverageFamily = CoverageFamilyCode.Base,
            RequestedAmount = 100, CoverageSource = ClaimCoverageSource.Warranty,
        });
        serial.Rmas.Add(new Rma
        {
            TenantId = tenant, RmaId = rmaId, CommandId = Guid.CreateVersion7(), RmaNumber = "RMA-2026-HIDDEN",
            Stage = RmaStage.Requested, RequestDate = new(2026, 8, 10), ReturningPartyRef = party,
            SettlementPartyRef = party, ServiceEntityId = entity, ObligorEntityId = entity,
            ServiceCountry = "GB", Currency = "GBP", RequestedAt = fixture.Clock.UtcNow,
            RequestedBy = "fixture", LastEventId = Guid.CreateVersion7(),
        });
        serial.RmaLines.Add(new RmaLine
        {
            TenantId = tenant, RmaLineId = Guid.CreateVersion7(), RmaId = rmaId, Sequence = 1,
            RequestedUnitId = unitId, OwnershipEpisodeId = episode, OriginalSaleDocumentType = "delivery",
            OriginalSaleDocumentId = Guid.CreateVersion7(), CoverageFamily = CoverageFamilyCode.Base,
            ClaimLineKind = ClaimLineKind.Parts,
        });
        var replacementDocumentId = Guid.CreateVersion7();
        serial.WarrantyReplacements.Add(new WarrantyReplacement
        {
            TenantId = tenant, WarrantyReplacementId = replacementDocumentId, CommandId = Guid.CreateVersion7(),
            ReplacementNumber = "REP-2026-HIDDEN", SourceKind = ReplacementSourceKind.Claim,
            SourceId = claimId, SourceNumber = "CLM-2026-HIDDEN", LegalEntityId = entity,
            ServiceCountry = "GB", Status = WarrantyReplacementStatus.Proposed,
            CreatedAt = fixture.Clock.UtcNow, CreatedBy = "fixture",
        });
        serial.ReplacementLinks.Add(new ReplacementLink
        {
            TenantId = tenant, ReplacementLinkId = replacementId, WarrantyReplacementId = replacementDocumentId,
            Direction = ReplacementDirection.CustomerFacing,
            ReplacementReason = ReplacementReason.WarrantyDefect, State = ReplacementLinkState.Proposed,
            SourceKind = ReplacementSourceKind.Claim, SourceId = claimId, SourceNumber = "CLM-2026-HIDDEN",
            OriginalUnitId = unitId, ReplacementUnitId = Guid.CreateVersion7(), OriginalItemId = item,
            ReplacementItemId = item, SourceEntitlementId = entitlementId, CounterpartyPartyRef = party,
            PricePolicy = ReplacementPricePolicy.Zero,
            Currency = "GBP", ReplacementConditionAllowed = ReplacementConditionAllowed.Any,
            CreatedAt = fixture.Clock.UtcNow, CreatedBy = "fixture",
        });
        serial.CertificateInstances.Add(new CertificateInstance
        {
            TenantId = tenant, CertificateId = certificateId, UnitId = unitId,
            DocumentKind = CertificateDocumentKind.WarrantyCertificate,
            FormType = CertificateFormType.WarrantyCertificate, Version = 1, TrackingNumber = "CERT-HIDDEN",
            TemplateVersionId = templateVersion, Payload = "{}", PayloadHash = Guid.NewGuid().ToString("N"),
            FactsHash = Guid.NewGuid().ToString("N"), TriggeringEventId = Guid.CreateVersion7(),
            IssuerEntityId = entity, IssuerSnapshot = "{}", BrandingSnapshot = "{}", Language = "en",
            Provenance = CertificateProvenance.SystemGenerated, IssuedAt = fixture.Clock.UtcNow,
            Status = CertificateStatus.Issued,
        });
        await serial.SaveChangesAsync();

        var developer = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
        developer.PublicEvents.Add(Event(tenant, eventId, false, party));
        developer.BulkOperations.Add(new BulkOperation
        {
            TenantId = tenant, OperationId = operationId, Kind = "unit.lifecycle", UnitCount = 1,
            CompletedAt = fixture.Clock.UtcNow, EventId = eventId,
        });
        developer.BulkOperationUnits.Add(new BulkOperationUnit
        {
            TenantId = tenant, OperationId = operationId, UnitId = unitId,
        });
        await developer.SaveChangesAsync();
        return new(claimId, rmaId, replacementId, certificateId, eventId, operationId);
    }

    private PublicEvent Event(TenantId tenant, Guid eventId, bool test, Guid? party = null) => new()
    {
        TenantId = tenant, EventId = eventId, Type = "unit.shipped", Version = "1", RecordKind = "unit",
        RecordId = Guid.CreateVersion7(), Sequence = 1, OccurredAt = fixture.Clock.UtcNow,
        RecordedAt = fixture.Clock.UtcNow, Body = $"{{\"id\":\"{eventId:D}\",\"test\":{test.ToString().ToLowerInvariant()}}}",
        Test = test, CustomerPartyRef = party,
    };

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string path, string token)
    {
        using var request = PublicApiTestSupport.Bearer(HttpMethod.Get, path, token);
        return (await client.SendAsync(request)).StatusCode;
    }

    private sealed record RestrictedResources(Guid ClaimId, Guid RmaId, Guid ReplacementId,
        Guid CertificateId, Guid EventId, Guid OperationId);

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }
}
