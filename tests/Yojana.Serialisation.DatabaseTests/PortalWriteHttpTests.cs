using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Security;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Web;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P7-T10: the authenticated HTTP boundary owns actor stamping, portal scoping and command replay.</summary>
[Collection("serialisation host")]
public sealed class PortalWriteHttpTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.Parse("f601a1c3-838b-4876-95ea-aebd95938969");
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    // Unique per run so the test can run again against a reused test database.
    private readonly string email = $"portal.writer.{Guid.NewGuid():N}@tests.local";

    [DatabaseFact]
    public async Task Registration_and_RMA_are_claim_stamped_replayable_and_hide_another_owners_unit()
    {
        var tenant = await fixture.NewTenantAsync("p7writehttp", "EUR", "GBP");
        var owner = Guid.NewGuid();
        var otherOwner = Guid.NewGuid();
        var ownedUnit = await UnitAsync(tenant, "HTTP-OWNED");
        var hiddenUnit = await UnitAsync(tenant, "HTTP-HIDDEN");
        await OwnAsync(tenant, ownedUnit, owner, At.AddDays(1), new("invoice", Guid.NewGuid()));
        await OwnAsync(tenant, hiddenUnit, otherOwner, At.AddDays(1), new("invoice", Guid.NewGuid()));
        await AddEntitlementAsync(tenant, ownedUnit, owner);
        await AddEntitlementAsync(tenant, hiddenUnit, otherOwner);
        var userId = await AddPortalUserAsync(tenant, owner);

        await using var host = await WebProcess.StartAsync();
        using var cookies = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(cookies) { BaseAddress = host.BaseAddress };
        using (var signIn = await client.PostAsJsonAsync("/api/auth/sign-in",
                   new { email, password = "Portal-test-password-42" }))
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        var registrationCommand = Guid.NewGuid();
        var registrationRequest = new PortalRegistrationRequest(registrationCommand,
            PortalRegistrationEvidenceKind.Installation, At.AddDays(10), "installation-note-4401");
        using var registrationResponse = await client.PostAsJsonAsync(
            $"/api/portal/units/{ownedUnit:D}/registrations", registrationRequest);
        Assert.Equal(HttpStatusCode.OK, registrationResponse.StatusCode);
        var registration = await registrationResponse.Content.ReadFromJsonAsync<PortalRegistrationResult>();
        Assert.NotNull(registration);
        Assert.Equal(PortalRegistrationOutcome.RegistrationRecorded, registration.Outcome);
        Assert.False(registration.Replayed);

        using var registrationReplayResponse = await client.PostAsJsonAsync(
            $"/api/portal/units/{ownedUnit:D}/registrations", registrationRequest);
        Assert.Equal(HttpStatusCode.OK, registrationReplayResponse.StatusCode);
        var registrationReplay = await registrationReplayResponse.Content.ReadFromJsonAsync<PortalRegistrationResult>();
        Assert.NotNull(registrationReplay);
        Assert.Equal(registration.RegistrationId, registrationReplay.RegistrationId);
        Assert.True(registrationReplay.Replayed);

        using var hidden = await client.PostAsJsonAsync(
            $"/api/portal/units/{hiddenUnit:D}/registrations",
            registrationRequest with { CommandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        // The API boundary answers every error with a generic problem body; it must not reveal the other owner's unit.
        var hiddenBody = await hidden.Content.ReadAsStringAsync();
        Assert.Contains("\"title\":\"Not found\"", hiddenBody);
        Assert.DoesNotContain(hiddenUnit.ToString("D"), hiddenBody, StringComparison.OrdinalIgnoreCase);

        var rmaCommand = Guid.NewGuid();
        var rmaRequest = new PortalRmaRequest(rmaCommand, new DateOnly(2026, 9, 12), "GB",
            "base", ClaimLineKind.Parts, "Intermittent power loss after warm-up.");
        using var rmaResponse = await client.PostAsJsonAsync($"/api/portal/units/{ownedUnit:D}/rmas", rmaRequest);
        Assert.Equal(HttpStatusCode.OK, rmaResponse.StatusCode);
        var rma = await rmaResponse.Content.ReadFromJsonAsync<PortalRmaResult>();
        Assert.NotNull(rma);
        Assert.Equal(RmaStage.Requested, rma.Stage);
        Assert.False(string.IsNullOrWhiteSpace(rma.RmaNumber));
        Assert.False(rma.Replayed);

        using var rmaReplayResponse = await client.PostAsJsonAsync($"/api/portal/units/{ownedUnit:D}/rmas", rmaRequest);
        Assert.Equal(HttpStatusCode.OK, rmaReplayResponse.StatusCode);
        var rmaReplay = await rmaReplayResponse.Content.ReadFromJsonAsync<PortalRmaResult>();
        Assert.NotNull(rmaReplay);
        Assert.Equal(rma.RmaId, rmaReplay.RmaId);
        Assert.Equal(rma.RmaNumber, rmaReplay.RmaNumber);
        Assert.True(rmaReplay.Replayed);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(userId.ToString("D"), (await db.PortalRegistrations.SingleAsync(
            row => row.CommandId == registrationCommand)).SubmittedBy);
        Assert.Equal(userId.ToString("D"), (await db.Rmas.SingleAsync(
            row => row.CommandId == rmaCommand)).RequestedBy);
        Assert.Equal(1, await db.PortalRegistrations.CountAsync(row => row.CommandId == registrationCommand));
        Assert.Equal(1, await db.Rmas.CountAsync(row => row.CommandId == rmaCommand));
    }

    private async Task<Guid> UnitAsync(TenantId tenant, string serial)
    {
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), Guid.NewGuid(),
            UnitOrigin.Manufactured, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])],
            At, "test:production", Entity, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        return result.Lines.Single().UnitId!.Value;
    }

    private Task<OpenOwnershipResult> OwnAsync(TenantId tenant, Guid unitId, Guid partyRef, DateTime at,
        DocumentRef document) => InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unitId,
            partyRef, PartyType.Customer, EpisodeReason.Sale, at, "test:sales", Entity, Document: document));

    private async Task AddEntitlementAsync(TenantId tenant, Guid unitId, Guid owner)
    {
        using var scope = fixture.ScopeFor(tenant);
        scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements.Add(new Entitlement
        {
            TenantId = tenant,
            EntitlementId = Guid.NewGuid(),
            UnitId = unitId,
            Scope = EntitlementScope.Unit,
            Type = EntitlementType.Manufacturer,
            CoverageFamily = "base",
            Direction = EntitlementDirection.Outbound,
            OwnerPartyRef = owner,
            OwnerPartyType = PartyType.Customer,
            ObligorEntityId = Entity,
            PolicyVersionId = Guid.NewGuid(),
            StartTrigger = StartTrigger.Deliver,
            StartBasis = StartTrigger.Deliver,
            StartDate = new DateOnly(2026, 9, 2),
            StartTimeZone = "Etc/UTC",
            EndDate = new DateOnly(2027, 9, 1),
            DurationValue = 12,
            DurationUnit = DurationRule.Months,
            EndOfMonthRule = DurationRule.Clamp,
            DurationBasis = DurationBasis.Time,
            Transferable = Transferability.Yes,
            AssuranceOrService = AssuranceOrService.Assurance,
            Status = EntitlementStatus.Active,
            Confidence = Confidence.High,
            Provenance = EntitlementProvenance.Created,
            CreatedEventId = Guid.NewGuid(),
            CreatedAt = At.AddDays(1),
        });
        await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().SaveChangesAsync();
    }

    private async Task<Guid> AddPortalUserAsync(TenantId tenant, Guid partyRef)
    {
        using var scope = fixture.ScopeFor(null);
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = "Fictional Portal Writer",
            PasswordHash = PasswordHasher.Hash("Portal-test-password-42"),
            CreatedAt = DateTime.UtcNow,
        };
        user.Memberships.Add(new UserTenantMembership
        {
            UserId = user.Id,
            TenantId = tenant,
            CustomerPartyRef = partyRef,
        });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private sealed class WebProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly List<string> output;
        public Uri BaseAddress { get; }

        private WebProcess(Process process, List<string> output, Uri baseAddress)
        {
            this.process = process;
            this.output = output;
            BaseAddress = baseAddress;
        }

        public static async Task<WebProcess> StartAsync()
        {
            var port = FreePort();
            var output = new List<string>();
            var start = new ProcessStartInfo("dotnet", $"\"{typeof(WebHost).Assembly.Location}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            start.Environment["Platform__ConnectionString"] = SerialisationHostFixture.ConnectionString;
            start.Environment["Serialisation__ImportFiles__DatabaseConnectionString"] = SerialisationHostFixture.ImportFilesConnectionString;
            start.Environment["Serialisation__ImportFiles__FileRoot"] = SerialisationHostFixture.ImportFileRoot;
            start.Environment["Serialisation__ImportFiles__ArchiveStores__0__Name"] = "folder-archive";
            start.Environment["Serialisation__ImportFiles__ArchiveStores__0__Kind"] = "folder";
            start.Environment["Serialisation__ImportFiles__ArchiveStores__0__Path"] = SerialisationHostFixture.ImportFileArchiveRoot;
            start.Environment["Serialisation__ImportFiles__ArchiveStores__1__Name"] = "database-archive";
            start.Environment["Serialisation__ImportFiles__ArchiveStores__1__Kind"] = "database";
            start.Environment["Serialisation__ImportFiles__ArchiveStores__1__ConnectionString"] = SerialisationHostFixture.ImportFilesArchiveConnectionString;
            // The production host composes both module databases, so this process must migrate both before a
            // Serialisation handler resolves an Integration-backed document port.
            start.Environment["Platform__MigrateOnStartup"] = "true";
            start.Environment["Platform__AutoBuildMessageStorage"] = "false";
            start.Environment["Platform__ServiceName"] = "yojana-portal-write-http-test";
            var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, args) => { if (args.Data is { } line) lock (output) output.Add(line); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is { } line) lock (output) output.Add(line); };
            Assert.True(process.Start());
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var host = new WebProcess(process, output, new Uri($"http://127.0.0.1:{port}"));
            using var probe = new HttpClient { BaseAddress = host.BaseAddress, Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                try
                {
                    using var response = await probe.GetAsync("/health");
                    if (response.StatusCode == HttpStatusCode.OK) return host;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(100);
            }
            await host.DisposeAsync();
            string details;
            lock (output) details = string.Join(Environment.NewLine, output.TakeLast(80));
            throw new Xunit.Sdk.XunitException("The portal HTTP test host did not start." + Environment.NewLine + details);
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process.Dispose();
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
