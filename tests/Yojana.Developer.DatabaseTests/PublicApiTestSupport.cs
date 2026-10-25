using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Developer.Authentication;
using Yojana.Developer.Clients;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ports;

namespace Yojana.Developer.DatabaseTests;

internal static class PublicApiTestSupport
{
    internal static HttpClient Anonymous(DeveloperHostFixture fixture) =>
        new() { BaseAddress = new Uri(fixture.Host.Urls.Single()) };

    internal static async Task<(ApiClientCreated Client, TokenResponse Token)> CreateTokenAsync(
        DeveloperHostFixture fixture, TenantId tenant, Guid administrator, IReadOnlyList<string> scopes,
        string tier = PublicApiDisclosureTier.BackOffice, Guid? party = null, string? name = null,
        bool sandbox = false)
    {
        using var admin = await fixture.ClientAsync(tenant, administrator);
        var create = await admin.PostAsJsonAsync("/api/developer/clients", new CreateApiClientRequest(
            name ?? "Fictional hardware integration " + Guid.NewGuid().ToString("N")[..8], scopes, tier,
            party, sandbox));
        create.EnsureSuccessStatusCode();
        var client = (await create.Content.ReadFromJsonAsync<ApiClientCreated>())!;
        using var anonymous = Anonymous(fixture);
        var tokenResponse = await anonymous.PostAsJsonAsync("/api/public/v1/token",
            new TokenRequest("client_credentials", client.Client.Id, client.Secret));
        tokenResponse.EnsureSuccessStatusCode();
        return (client, (await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>())!);
    }

    internal static HttpRequestMessage Bearer(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    internal static async Task<Guid> SeedUnitAsync(DeveloperHostFixture fixture, TenantId tenant, Guid party,
        string serial, Guid? itemId = null)
    {
        var at = fixture.Clock.UtcNow.AddDays(-30);
        var unitId = Guid.CreateVersion7();
        var item = itemId ?? Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        var episodeId = Guid.CreateVersion7();
        using var scope = fixture.ScopeFor(tenant);
        scope.ServiceProvider.GetRequiredService<InMemoryItemNamePort>()
            .Register(new ItemLabel(item, "SERVER-" + item.ToString("N")[..6].ToUpperInvariant(),
                "Fictional rack server"));
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.Units.Add(new Unit
        {
            TenantId = tenant, UnitId = unitId, LifecycleStatus = LifecycleStatus.Released,
            Origin = UnitOrigin.Purchased, SerialisationPoint = SerialisationPoint.Receipt,
            RetentionClass = "standard_10y", CreatedAt = at, CreatedEventId = eventId,
            CreatedEventTime = at,
        });
        db.Current.Add(new UnitCurrent
        {
            TenantId = tenant, UnitId = unitId, ItemId = item, LifecycleStatus = LifecycleStatus.Released,
            Disposition = UnitDisposition.Active, ConditionGrade = "new", OwnerPartyRef = party,
            OpenOwnershipEpisodeId = episodeId, LastEventSeq = 1, LastEventTime = at,
        });
        db.ItemAssignments.Add(new UnitItemAssignment
        {
            TenantId = tenant, AssignmentId = Guid.CreateVersion7(), UnitId = unitId, ItemId = item,
            ValidFrom = at, Reason = ItemAssignmentReason.Initial, EventId = eventId,
        });
        db.Identifiers.Add(new UnitIdentifier
        {
            TenantId = tenant, IdentifierId = Guid.CreateVersion7(), UnitId = unitId,
            Type = IdentifierType.InternalSerial, Scheme = IdentifierScheme.Internal,
            ScopeKey = item.ToString("N"), RawValue = serial, NormalisedValue = serial.ToUpperInvariant(),
            ValidFrom = at, ActorRef = "fixture", RecordedTime = at, EventId = eventId,
        });
        db.Episodes.Add(new CustodyEpisode
        {
            TenantId = tenant, EpisodeId = episodeId, UnitId = unitId, Kind = EpisodeKind.Ownership,
            PartyRef = party, PartyType = PartyType.Customer, Reason = EpisodeReason.Sale,
            Source = EpisodeSource.Delivery, Confidence = Confidence.High, ValidFrom = at,
            OpeningEventId = eventId,
        });
        await db.SaveChangesAsync();
        return unitId;
    }

    internal static async Task<Guid> SeedEntitlementAsync(DeveloperHostFixture fixture, TenantId tenant,
        Guid unit, Guid party)
    {
        var id = Guid.CreateVersion7();
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.Entitlements.Add(new Entitlement
        {
            TenantId = tenant, EntitlementId = id, UnitId = unit, Scope = EntitlementScope.Unit,
            Type = EntitlementType.Manufacturer, CoverageFamily = CoverageFamilyCode.Base,
            Direction = EntitlementDirection.Outbound, OwnerPartyRef = party,
            OwnerPartyType = PartyType.Customer, ObligorEntityId = Guid.CreateVersion7(),
            PolicyVersionId = Guid.CreateVersion7(), StartTrigger = StartTrigger.Deliver,
            StartBasis = StartTrigger.Deliver, StartDate = new DateOnly(2026, 8, 2),
            EndDate = new DateOnly(2029, 8, 1), StartTimeZone = "UTC", DurationValue = 36,
            DurationUnit = "months", EndOfMonthRule = "none", DurationBasis = DurationBasis.Time,
            Transferable = Transferability.Yes, AssuranceOrService = AssuranceOrService.Assurance,
            Status = EntitlementStatus.Active, Confidence = Confidence.High,
            Provenance = EntitlementProvenance.Created, CreatedEventId = Guid.CreateVersion7(),
            CreatedAt = fixture.Clock.UtcNow.AddDays(-30),
        });
        await db.SaveChangesAsync();
        return id;
    }
}
