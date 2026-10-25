using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Developer.Data;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Portal;

namespace Yojana.Developer.DatabaseTests;

[Collection("developer host")]
public sealed class PublicWriteIdempotencyTests(DeveloperHostFixture fixture)
{
    [DatabaseFact]
    public async Task Installation_write_replays_rejects_mismatch_and_records_the_api_client_actor()
    {
        var setup = await fixture.NewTenantAsync("write-idem");
        var party = Guid.CreateVersion7();
        var unit = await PublicApiTestSupport.SeedUnitAsync(fixture, setup.Tenant, party, "SERVER-WRITE-01");
        await PublicApiTestSupport.SeedEntitlementAsync(fixture, setup.Tenant, unit, party);
        var (apiClient, token) = await PublicApiTestSupport.CreateTokenAsync(fixture, setup.Tenant,
            setup.Administrator, [PublicApiScopes.UnitsWriteInstallation],
            PublicApiDisclosureTier.Customer, party);
        using var client = PublicApiTestSupport.Anonymous(fixture);
        var body = new PublicInstallationDateRequest(null, fixture.Clock.UtcNow.AddDays(-5),
            "Fictional field engineer report");

        var first = await SendAsync(client, unit, token.AccessToken, "installation-one", body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        var replay = await SendAsync(client, unit, token.AccessToken, "installation-one", body);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync());

        var changed = body with { EvidenceReference = "Different service record" };
        var mismatch = await SendAsync(client, unit, token.AccessToken, "installation-one", changed);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        Assert.Equal("idempotency_key_reused", await CodeAsync(mismatch));

        using var scope = fixture.ScopeFor(setup.Tenant);
        var serial = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var registrations = await serial.PortalRegistrations.AsNoTracking().ToListAsync();
        var registration = Assert.Single(registrations);
        Assert.Equal("system:api-client:" + apiClient.Client.Id, registration.SubmittedBy);
        var developer = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
        Assert.Single(await developer.IdempotencyRecords.AsNoTracking().ToListAsync());
    }

    [DatabaseFact]
    public async Task Expired_key_can_start_a_new_command_and_command_refusals_keep_their_stable_code()
    {
        var setup = await fixture.NewTenantAsync("write-exp");
        var party = Guid.CreateVersion7();
        var unit = await PublicApiTestSupport.SeedUnitAsync(fixture, setup.Tenant, party, "STORAGE-WRITE-02");
        await PublicApiTestSupport.SeedEntitlementAsync(fixture, setup.Tenant, unit, party);
        var (_, token) = await PublicApiTestSupport.CreateTokenAsync(fixture, setup.Tenant,
            setup.Administrator, [PublicApiScopes.UnitsWriteInstallation],
            PublicApiDisclosureTier.Customer, party);
        using var client = PublicApiTestSupport.Anonymous(fixture);
        var firstBody = new PublicInstallationDateRequest(null, fixture.Clock.UtcNow.AddDays(-5), "Rack handover");
        (await SendAsync(client, unit, token.AccessToken, "installation-expiring", firstBody))
            .EnsureSuccessStatusCode();

        using (var expiryScope = fixture.ScopeFor(setup.Tenant))
        {
            var db = expiryScope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            var row = await db.IdempotencyRecords.SingleAsync(item =>
                item.IdempotencyKey == "installation-expiring");
            row.ExpiresAt = fixture.Clock.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        var afterExpiry = firstBody with
        {
            EvidenceAt = fixture.Clock.UtcNow.AddDays(-4),
            EvidenceReference = "Commissioning record",
        };
        var newResponse = await SendAsync(client, unit, token.AccessToken, "installation-expiring", afterExpiry);
        newResponse.EnsureSuccessStatusCode();
        Assert.False(newResponse.Headers.Contains("Idempotency-Replayed"));

        var refused = await SendAsync(client, unit, token.AccessToken, "installation-refused",
            firstBody with { EvidenceAt = fixture.Clock.UtcNow.AddDays(1) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("registration_evidence_future", await CodeAsync(refused));

        using var check = fixture.ScopeFor(setup.Tenant);
        var serial = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(2, await serial.PortalRegistrations.CountAsync());
    }

    [DatabaseFact]
    public async Task Rma_claim_with_evidence_and_asset_tag_use_existing_commands_and_api_client_actor()
    {
        var setup = await fixture.NewTenantAsync("writecmd");
        var party = Guid.CreateVersion7();
        var unit = await PublicApiTestSupport.SeedUnitAsync(fixture, setup.Tenant, party, "SERVER-WRITE-ALL");
        await PublicApiTestSupport.SeedEntitlementAsync(fixture, setup.Tenant, unit, party);
        await PrepareCommandInputsAsync(setup.Tenant, unit);
        var (apiClient, token) = await PublicApiTestSupport.CreateTokenAsync(fixture, setup.Tenant,
            setup.Administrator, [PublicApiScopes.RmaWrite, PublicApiScopes.ClaimsWrite,
                PublicApiScopes.UnitsWriteAssetTag], PublicApiDisclosureTier.Customer, party);
        using var client = PublicApiTestSupport.Anonymous(fixture);

        var rma = await SendWriteAsync(client, HttpMethod.Post, "/api/public/v1/rmas", token.AccessToken,
            "rma-command", new PublicRmaRequest(unit, null, new(2026, 8, 20), "GB",
                CoverageFamilyCode.Base, ClaimLineKind.Parts, "Power supply fails under load."));
        Assert.True(rma.StatusCode == HttpStatusCode.Created, await rma.Content.ReadAsStringAsync());
        var rmaResult = await rma.Content.ReadFromJsonAsync<PublicRmaResult>();
        Assert.NotNull(rmaResult);
        Assert.Equal(RmaStage.Requested, rmaResult.Stage);

        var evidence = new PublicEvidenceAttachment("diagnostic.txt", "text/plain",
            "Fictional diagnostic code PSU-17"u8.ToArray());
        var claim = await SendWriteAsync(client, HttpMethod.Post, "/api/public/v1/claims", token.AccessToken,
            "claim-command", new PublicClaimRequest(unit, null, new(2026, 8, 21), "GB",
                [new(ClaimLineKind.Parts, CoverageFamilyCode.Base, 125m, Description: "Power supply")],
                [new(ClaimEvidenceKind.Attachment, "DIAG-PSU-17", "Service diagnostic", evidence)],
                ClaimRemedy.Repair, "CUSTOMER-CHOICE-RECORDED"));
        Assert.True(claim.StatusCode == HttpStatusCode.Created, await claim.Content.ReadAsStringAsync());
        var claimResult = await claim.Content.ReadFromJsonAsync<PublicClaimResult>();
        Assert.NotNull(claimResult);

        var tag = await SendWriteAsync(client, HttpMethod.Post,
            $"/api/public/v1/units/{unit:D}/asset-tags", token.AccessToken, "asset-tag-command",
            new PublicAssetTagRequest(null, "FIC-RACK-0042"));
        Assert.True(tag.StatusCode == HttpStatusCode.Created, await tag.Content.ReadAsStringAsync());

        var actor = "system:api-client:" + apiClient.Client.Id;
        using var scope = fixture.ScopeFor(setup.Tenant);
        var serial = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(actor, (await serial.Rmas.SingleAsync(row => row.RmaId == rmaResult.Id)).RequestedBy);
        Assert.Equal(actor, (await serial.Claims.SingleAsync(row => row.ClaimId == claimResult.Id)).SubmittedBy);
        var storedEvidence = await serial.ClaimEvidence.SingleAsync(row => row.ClaimId == claimResult.Id
            && row.Reference == "DIAG-PSU-17");
        Assert.Equal("text/plain", storedEvidence.AttachmentContentType);
        Assert.Equal(evidence.Content, storedEvidence.AttachmentContent);
        Assert.Equal(actor, (await serial.Identifiers.SingleAsync(row => row.UnitId == unit
            && row.Type == IdentifierType.CustomerAssetTag)).ActorRef);
    }

    [DatabaseFact]
    public async Task Claim_evidence_refuses_unapproved_types_and_oversized_files_with_stable_codes()
    {
        var setup = await fixture.NewTenantAsync("writeev");
        var party = Guid.CreateVersion7();
        var unit = await PublicApiTestSupport.SeedUnitAsync(fixture, setup.Tenant, party, "STORAGE-EVIDENCE-01");
        await PublicApiTestSupport.SeedEntitlementAsync(fixture, setup.Tenant, unit, party);
        await PrepareCommandInputsAsync(setup.Tenant, unit);
        var (_, token) = await PublicApiTestSupport.CreateTokenAsync(fixture, setup.Tenant,
            setup.Administrator, [PublicApiScopes.ClaimsWrite], PublicApiDisclosureTier.Customer, party);
        using var client = PublicApiTestSupport.Anonymous(fixture);

        PublicClaimRequest Request(PublicEvidenceAttachment attachment) => new(unit, null, new(2026, 8, 21),
            "GB", [new(ClaimLineKind.Parts, CoverageFamilyCode.Base, 125m)],
            [new(ClaimEvidenceKind.Attachment, "FAULT-EVIDENCE", Attachment: attachment)],
            ClaimRemedy.Repair, "CUSTOMER-CHOICE-RECORDED");

        var type = await SendWriteAsync(client, HttpMethod.Post, "/api/public/v1/claims", token.AccessToken,
            "claim-type-refusal", Request(new("diagnostic.bin", "application/octet-stream", [1, 2, 3])));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, type.StatusCode);
        Assert.Equal("evidence_attachment_type", await CodeAsync(type));

        var size = await SendWriteAsync(client, HttpMethod.Post, "/api/public/v1/claims", token.AccessToken,
            "claim-size-refusal", Request(new("diagnostic.txt", "text/plain",
                new byte[PortalWriteService.MaximumAttachmentBytes + 1])));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, size.StatusCode);
        Assert.Equal("evidence_attachment_size", await CodeAsync(size));

        using var scope = fixture.ScopeFor(setup.Tenant);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Claims
            .Where(row => row.UnitId == unit).ToListAsync());
    }

    private async Task PrepareCommandInputsAsync(Yojana.Platform.Contracts.Tenancy.TenantId tenant, Guid unit)
    {
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ITenantCurrency>().SetAsync("GBP", CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var ownership = await db.Episodes.SingleAsync(row => row.UnitId == unit
            && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        ownership.OpeningDocumentType = "delivery";
        ownership.OpeningDocumentId = Guid.CreateVersion7();
        ownership.OpeningDocumentLine = 1;
        db.Events.Add(new UnitEvent
        {
            TenantId = tenant, EventTime = fixture.Clock.UtcNow.AddDays(-30), EventId = Guid.CreateVersion7(),
            UnitId = unit, Seq = 1, EventType = UnitEventType.Released, LegalEntityId = Guid.CreateVersion7(),
            ActorRef = "fixture", RecordedTime = fixture.Clock.UtcNow.AddDays(-30),
            IdempotencyKey = Guid.NewGuid().ToString("N"), Attributes = "{}",
        });
        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> SendWriteAsync<T>(HttpClient client, HttpMethod method, string path,
        string token, string key, T body)
    {
        var request = PublicApiTestSupport.Bearer(method, path, token);
        request.Headers.Add("Idempotency-Key", key);
        request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, Guid unitId, string token, string key,
        PublicInstallationDateRequest body)
    {
        var request = PublicApiTestSupport.Bearer(HttpMethod.Put,
            $"/api/public/v1/units/{unitId:D}/installation-date", token);
        request.Headers.Add("Idempotency-Key", key);
        request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }
}
