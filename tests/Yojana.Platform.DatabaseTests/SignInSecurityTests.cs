using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Security;
using Yojana.Platform.Seeding;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.DatabaseTests;

[Collection("platform host")]
public sealed class SignInSecurityTests(PlatformHostFixture fixture)
{
    private static readonly DateTime Start = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Five_failures_lock_the_account_and_the_right_password_has_the_same_unauthorised_response()
    {
        fixture.Clock.UtcNow = Start;
        var (_, user, password) = await AddAdministratorAsync("lock");
        using var client = fixture.Client();
        string? firstBody = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/sign-in",
                new SignInRequest(user.Email.ToUpperInvariant(), "wrong-password"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            firstBody ??= await response.Content.ReadAsStringAsync();
        }

        using var locked = await client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest(user.Email, password));
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal(firstBody, await locked.Content.ReadAsStringAsync());
        Assert.False(locked.Headers.Contains("Retry-After"));
    }

    [DatabaseFact]
    public async Task Account_lockout_doubles_and_caps_at_thirty_minutes()
    {
        fixture.Clock.UtcNow = Start;
        var email = $"lock-growth-{Guid.NewGuid():N}@example.invalid";
        using var scope = fixture.Services.CreateScope();
        var throttles = scope.ServiceProvider.GetRequiredService<SignInThrottleService>();
        for (var attempt = 0; attempt < 5; attempt++)
            await throttles.RegisterFailureAsync(email, $"198.51.100.{attempt + 1}", true, CancellationToken.None);

        var expected = new[] { 1, 2, 4, 8, 16, 30, 30 };
        for (var lockout = 0; lockout < expected.Length; lockout++)
        {
            var row = await AccountRowAsync(scope, email);
            Assert.Equal(TimeSpan.FromMinutes(expected[lockout]), row.LockedUntil - fixture.Clock.UtcNow);
            fixture.Clock.UtcNow = row.LockedUntil!.Value;
            if (lockout == expected.Length - 1) continue;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await throttles.RegisterFailureAsync(email, $"203.0.{lockout}.{attempt + 1}", true,
                    CancellationToken.None);
                row = await AccountRowAsync(scope, email);
                if (row.LockedUntil > fixture.Clock.UtcNow) break;
            }
            Assert.True(row.LockedUntil > fixture.Clock.UtcNow);
        }
    }

    [DatabaseFact]
    public async Task Unknown_email_locks_identically_and_is_only_stored_as_an_hmac_in_the_audit()
    {
        fixture.Clock.UtcNow = Start;
        var email = $"missing-{Guid.NewGuid():N}@example.invalid";
        var request = new SecurityRequestContext($"unknown-{Guid.NewGuid():N}", "test-agent");
        using var scope = fixture.Services.CreateScope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInService>();
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Null((await signIn.AuthenticateAsync(email.ToUpperInvariant(), "not-the-password", request,
                CancellationToken.None)).User);
        Assert.Null((await signIn.AuthenticateAsync(email, "not-the-password", request,
            CancellationToken.None)).User);

        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var rows = await db.SecurityEvents.Where(row => row.UnknownUserHash != null && row.OccurredAt == Start)
            .ToListAsync();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(64, row.UnknownUserHash!.Length));
        Assert.DoesNotContain(email, JsonSerializer.Serialize(rows), StringComparison.OrdinalIgnoreCase);
    }

    [DatabaseFact]
    public async Task Successful_sign_in_resets_the_account_counter()
    {
        fixture.Clock.UtcNow = Start;
        var (_, user, password) = await AddAdministratorAsync("reset");
        var request = new SecurityRequestContext($"reset-{Guid.NewGuid():N}", "test-agent");
        using var scope = fixture.Services.CreateScope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInService>();
        for (var attempt = 0; attempt < 4; attempt++)
            Assert.Null((await signIn.AuthenticateAsync(user.Email, "wrong", request, CancellationToken.None)).User);
        Assert.NotNull((await signIn.AuthenticateAsync(user.Email, password, request, CancellationToken.None)).User);
        for (var attempt = 0; attempt < 4; attempt++)
            Assert.Null((await signIn.AuthenticateAsync(user.Email, "wrong", request, CancellationToken.None)).User);
        Assert.NotNull((await signIn.AuthenticateAsync(user.Email, password, request, CancellationToken.None)).User);
    }

    [DatabaseFact]
    public async Task Address_failure_limit_refuses_further_attempts_with_retry_after_state()
    {
        fixture.Clock.UtcNow = Start;
        var address = $"address-{Guid.NewGuid():N}";
        using var scope = fixture.Services.CreateScope();
        var throttles = scope.ServiceProvider.GetRequiredService<SignInThrottleService>();
        FailedAttemptResult? last = null;
        for (var attempt = 0; attempt < 50; attempt++)
            last = await throttles.RegisterFailureAsync($"attempt-{attempt}@example.invalid", address, false,
                CancellationToken.None);
        Assert.NotNull(last!.AddressRetryAfter);
        var state = await throttles.LockStateAsync("another@example.invalid", address, CancellationToken.None);
        Assert.InRange(state.AddressRetryAfter!.Value, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
    }

    [DatabaseFact]
    public async Task Authenticated_me_is_not_rate_limited()
    {
        var (_, user, password) = await AddAdministratorAsync("unlimited-me");
        using var client = fixture.Client();
        using var signedIn = await client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest(user.Email, password));
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);

        for (var request = 0; request < 200; request++)
        {
            using var response = await client.GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [DatabaseFact]
    public async Task Sign_in_rate_limiter_returns_retry_after()
    {
        await using var app = await StartIsolatedWebHostAsync(requestLimit: 2);
        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        HttpResponseMessage? rejected = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/sign-in")
            {
                Content = JsonContent.Create(new SignInRequest($"rate-{Guid.NewGuid():N}@example.invalid", "wrong")),
            };
            var response = await client.SendAsync(request);
            if (attempt < 2) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            else rejected = response;
            if (attempt < 2) response.Dispose();
        }
        using (rejected)
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected!.StatusCode);
            Assert.True(rejected.Headers.TryGetValues("Retry-After", out var values));
            Assert.True(int.Parse(Assert.Single(values)) > 0);
        }
    }

    [DatabaseFact]
    public async Task Configured_known_proxy_sets_the_rate_limit_partition_and_throttle_address()
    {
        await using var app = await StartIsolatedWebHostAsync(requestLimit: 1, knownProxy: "127.0.0.1");
        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var userAgent = $"known-proxy-{Guid.NewGuid():N}";

        using var first = await SendFailedSignInAsync(client, "198.51.100.41", userAgent);
        using var second = await SendFailedSignInAsync(client, "198.51.100.42", $"{userAgent}-second");
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var audit = await db.SecurityEvents.AsNoTracking().SingleAsync(row => row.UserAgent == userAgent);
        Assert.Equal("198.51.100.41", audit.ClientAddress);
        Assert.True(await AddressThrottleExistsAsync(db, "198.51.100.41"));
        Assert.True(await AddressThrottleExistsAsync(db, "198.51.100.42"));
    }

    [DatabaseFact]
    public async Task Forwarded_for_is_ignored_when_no_proxy_is_configured()
    {
        await using var app = await StartIsolatedWebHostAsync(requestLimit: 1);
        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var userAgent = $"untrusted-proxy-{Guid.NewGuid():N}";

        using var first = await SendFailedSignInAsync(client, "198.51.100.51", userAgent);
        using var second = await SendFailedSignInAsync(client, "198.51.100.52", $"{userAgent}-second");
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var audit = await db.SecurityEvents.AsNoTracking().SingleAsync(row => row.UserAgent == userAgent);
        Assert.NotNull(audit.ClientAddress);
        Assert.NotEqual("198.51.100.51", audit.ClientAddress);
        Assert.True(await AddressThrottleExistsAsync(db, audit.ClientAddress!));
        Assert.False(await AddressThrottleExistsAsync(db, "198.51.100.51"));
    }

    [DatabaseFact]
    public async Task Stamp_rotation_rejects_another_session_within_the_validation_window()
    {
        fixture.Clock.UtcNow = Start;
        var (tenant, user, _) = await AddAdministratorAsync("stamp");
        using var scope = fixture.Services.CreateScope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInService>();
        var choice = Assert.Single(await signIn.TenantsForAsync(user.Id, CancellationToken.None));
        var principal = SignInService.PrincipalFor(user, choice);
        var validator = scope.ServiceProvider.GetRequiredService<SecurityStampValidator>();
        Assert.True((await validator.ValidateAsync(principal, CancellationToken.None)).Accepted);

        await scope.ServiceProvider.GetRequiredService<ISecurityStampService>()
            .RotateSecurityStampAsync(user.Id, "test_password_changed", CancellationToken.None);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1).AddMilliseconds(1);
        Assert.False((await validator.ValidateAsync(principal, CancellationToken.None)).Accepted);
        Assert.NotEqual(default, tenant.Value);
    }

    [DatabaseFact]
    public async Task Old_cookie_without_a_stamp_is_accepted_once_and_reissued()
    {
        fixture.Clock.UtcNow = Start;
        var (_, user, _) = await AddAdministratorAsync("legacy-cookie");
        var oldIdentity = new ClaimsIdentity([
            new Claim(YojanaClaim.UserId, user.Id.ToString("D")),
            new Claim(ClaimTypes.Name, user.DisplayName),
        ], "Cookies", ClaimTypes.Name, YojanaClaim.Role);
        using var scope = fixture.Services.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<SecurityStampValidator>();
        var upgraded = await validator.ValidateAsync(new ClaimsPrincipal(oldIdentity), CancellationToken.None);
        Assert.True(upgraded.Accepted);
        Assert.True(upgraded.RenewCookie);
        Assert.Equal(user.SecurityStamp.ToString("D"), upgraded.Principal.FindFirstValue(YojanaClaim.SecurityStamp));
        Assert.NotNull(upgraded.Principal.FindFirstValue(YojanaClaim.SessionId));
        var second = await validator.ValidateAsync(upgraded.Principal, CancellationToken.None);
        Assert.True(second.Accepted);
        Assert.False(second.RenewCookie);
    }

    [DatabaseFact]
    public async Task Inactive_user_is_rejected_at_cookie_validation()
    {
        fixture.Clock.UtcNow = Start;
        var (_, user, _) = await AddAdministratorAsync("inactive");
        var principal = SignInService.PrincipalFor(user, null);
        using var scope = fixture.Services.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<SecurityStampValidator>();
        Assert.True((await validator.ValidateAsync(principal, CancellationToken.None)).Accepted);
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Users.Where(row => row.Id == user.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1).AddMilliseconds(1);
        Assert.False((await validator.ValidateAsync(principal, CancellationToken.None)).Accepted);
    }

    [DatabaseFact]
    public async Task Development_seed_password_reset_rotates_the_stamp_without_applying_password_policy()
    {
        fixture.Clock.UtcNow = Start;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = new TenantId(Guid.CreateVersion7());
        var seed = new TenantUserSeed(tenant, "SEED" + suffix, "Fictional seed tenant",
            $"seed-{suffix}.example.invalid", "Fictional seed administrator");
        await DevelopmentSeeder.SeedTenantUsersAsync(fixture.Services, seed, "Tiny1!", CancellationToken.None);
        Guid userId;
        Guid firstStamp;
        using (var firstScope = fixture.Services.CreateScope())
        {
            var first = await firstScope.ServiceProvider.GetRequiredService<PlatformDbContext>().Users.AsNoTracking()
                .SingleAsync(row => row.Email == DevelopmentSeeder.AdministratorEmail(seed.EmailDomain));
            userId = first.Id;
            firstStamp = first.SecurityStamp;
        }

        await DevelopmentSeeder.SeedTenantUsersAsync(fixture.Services, seed, "Tiny2!", CancellationToken.None);
        using var secondScope = fixture.Services.CreateScope();
        var db = secondScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var reset = await db.Users.AsNoTracking().SingleAsync(row => row.Id == userId);
        Assert.NotEqual(firstStamp, reset.SecurityStamp);
        Assert.True(PasswordHasher.Verify("Tiny2!", reset.PasswordHash));
        Assert.True(await db.SecurityEvents.AnyAsync(row => row.UserId == userId &&
            row.Kind == SecurityEventKind.SecurityStampRotated &&
            row.Reason == "development_seed_password_reset"));
    }

    [DatabaseFact]
    public async Task Current_security_events_contain_no_secret_and_cover_every_current_kind()
    {
        fixture.Clock.UtcNow = Start;
        var (tenant, user, password) = await AddAdministratorAsync("events");
        const string secret = "NeverStoreThisSecret!";
        var request = new SecurityRequestContext($"events-{Guid.NewGuid():N}", "test-agent");
        using var scope = fixture.Services.CreateScope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInService>();
        for (var attempt = 0; attempt < 5; attempt++)
            await signIn.AuthenticateAsync(user.Email, secret, request, CancellationToken.None);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1).AddMilliseconds(1);
        var authenticated = await signIn.AuthenticateAsync(user.Email, password, request, CancellationToken.None);
        Assert.NotNull(authenticated.User);
        await signIn.RecordSignInSucceededAsync(user, tenant, new("192.0.2.20", new string('u', 300)),
            CancellationToken.None);
        await signIn.RecordSignOutAsync(user.Id, tenant, new("192.0.2.20", "test-agent"),
            CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<ISecurityStampService>()
            .RotateSecurityStampAsync(user.Id, "test_password_changed", CancellationToken.None);

        var rows = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SecurityEvents
            .Where(row => row.UserId == user.Id).ToListAsync();
        Assert.Contains(rows, row => row.Kind == SecurityEventKind.SignInFailed);
        Assert.Contains(rows, row => row.Kind == SecurityEventKind.LockedOut);
        Assert.Contains(rows, row => row.Kind == SecurityEventKind.SignInSucceeded);
        Assert.Contains(rows, row => row.Kind == SecurityEventKind.SignedOut);
        Assert.Contains(rows, row => row.Kind == SecurityEventKind.SecurityStampRotated);
        Assert.All(rows.Where(row => row.UserAgent is not null), row => Assert.True(row.UserAgent!.Length <= 256));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(rows), StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Tenant_administrator_reads_only_own_tenant_or_member_events_with_names()
    {
        fixture.Clock.UtcNow = Start;
        var (tenant, user, _) = await AddAdministratorAsync("event-read");
        var otherTenant = await AddTenantAsync("event-other");
        using (var writeScope = fixture.Services.CreateScope())
        {
            var events = writeScope.ServiceProvider.GetRequiredService<SecurityEventWriter>();
            events.Add(SecurityEventKind.SignInSucceeded, user.Id, null, null, new(null, null), "member_event");
            events.Add(SecurityEventKind.RolesChanged, null, tenant, user.Id, new(null, null), "tenant_event");
            events.Add(SecurityEventKind.RolesChanged, null, otherTenant, null, new(null, null), "other_event");
            await writeScope.ServiceProvider.GetRequiredService<PlatformDbContext>().SaveChangesAsync();
        }

        using var readScope = ScopeFor(tenant);
        var page = await readScope.ServiceProvider.GetRequiredService<SecurityEventQueries>()
            .PageAsync(null, null, null, null, null, 10, CancellationToken.None);
        Assert.Contains(page.Items, row => row.Reason == "member_event" && row.UserName == user.DisplayName);
        Assert.Contains(page.Items, row => row.Reason == "tenant_event" && row.ActorName == user.DisplayName);
        Assert.DoesNotContain(page.Items, row => row.Reason == "other_event");
        Assert.DoesNotContain(user.Id.ToString("D"), JsonSerializer.Serialize(page), StringComparison.OrdinalIgnoreCase);

        var first = await readScope.ServiceProvider.GetRequiredService<SecurityEventQueries>()
            .PageAsync(null, null, Start, Start.AddDays(1), null, 1, CancellationToken.None);
        Assert.NotNull(first.NextCursor);
        var second = await readScope.ServiceProvider.GetRequiredService<SecurityEventQueries>()
            .PageAsync(null, null, Start, Start.AddDays(1), first.NextCursor, 1, CancellationToken.None);
        Assert.NotEqual(first.Items[0].Reason, Assert.Single(second.Items).Reason);
        var byKind = await readScope.ServiceProvider.GetRequiredService<SecurityEventQueries>()
            .PageAsync(SecurityEventKind.SignInSucceeded, user.Id, Start, Start.AddDays(1), null, 10,
                CancellationToken.None);
        Assert.Equal("member_event", Assert.Single(byKind.Items).Reason);
    }

    [DatabaseFact]
    public async Task Security_event_summary_counts_each_card_over_its_window_in_tenant_scope()
    {
        fixture.Clock.UtcNow = Start;
        var (tenant, user, password) = await AddAdministratorAsync("event-sum");
        var otherTenant = await AddTenantAsync("event-sum-other");
        var (_, outsiderUser, _) = await AddAdministratorAsync("event-sum-out");
        var outsider = outsiderUser.Id;
        var todayFrom = Start.AddHours(-2);
        var weekFrom = todayFrom.AddDays(-6);

        async Task WriteAsync(DateTime at, string kind, Guid? userId, TenantId? tenantId, string reason)
        {
            fixture.Clock.UtcNow = at;
            using var scope = fixture.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<SecurityEventWriter>()
                .Add(kind, userId, tenantId, null, new(null, null), reason);
            await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SaveChangesAsync();
        }

        await WriteAsync(todayFrom, SecurityEventKind.SignInSucceeded, user.Id, tenant, "today_boundary");
        await WriteAsync(todayFrom.AddMilliseconds(-1), SecurityEventKind.SignInSucceeded, user.Id, tenant, "yesterday");
        await WriteAsync(Start, SecurityEventKind.SignInFailed, user.Id, null, "member_tenantless");
        await WriteAsync(Start, SecurityEventKind.SignInFailed, outsider, null, "non_member_tenantless");
        await WriteAsync(Start, SecurityEventKind.LockedOut, null, otherTenant, "other_tenant");
        await WriteAsync(Start, SecurityEventKind.LockedOut, user.Id, tenant, "lockout");
        await WriteAsync(weekFrom, SecurityEventKind.PasswordResetCompleted, user.Id, tenant, "week_boundary");
        await WriteAsync(weekFrom.AddMilliseconds(-1), SecurityEventKind.AdminPasswordReset, user.Id, tenant, "before_week");
        await WriteAsync(Start, SecurityEventKind.AdminPasswordReset, user.Id, tenant, "admin_reset");
        await WriteAsync(Start, SecurityEventKind.PasswordResetRequested, user.Id, tenant, "request_only");
        foreach (var kind in SecurityEventSummaryKinds.MethodChanges)
            await WriteAsync(weekFrom.AddDays(1), kind, user.Id, tenant, "method");
        await WriteAsync(Start, SecurityEventKind.RecoveryCodeUsed, user.Id, tenant, "not_a_change");
        fixture.Clock.UtcNow = Start;

        using (var readScope = ScopeFor(tenant))
        {
            var queries = readScope.ServiceProvider.GetRequiredService<SecurityEventQueries>();
            Assert.Equal(new SecurityEventSummary(1, 1, 1, 2, 5),
                await queries.SummaryAsync(todayFrom, weekFrom, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                queries.SummaryAsync(null, weekFrom, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                queries.SummaryAsync(weekFrom, todayFrom, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                queries.SummaryAsync(todayFrom, Start.AddDays(-9), CancellationToken.None));
        }

        string Query(DateTime today, DateTime week) =>
            $"/api/security/events/summary?todayFrom={today:O}&weekFrom={week:O}";
        using var administrator = fixture.Client();
        (await administrator.PostAsJsonAsync("/api/auth/sign-in", new { email = user.Email, password }))
            .EnsureSuccessStatusCode();
        using (var refused = await administrator.GetAsync(Query(weekFrom, todayFrom)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("security_event_summary_invalid", (await refused.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("title").GetString());
        }

        var staff = new AppUser
        {
            Id = Guid.CreateVersion7(), Email = $"event-sum-staff-{Guid.NewGuid():N}@example.invalid",
            DisplayName = "Fictional service desk user", PasswordHash = PasswordHasher.Hash(password),
            CreatedAt = fixture.Clock.UtcNow,
        };
        staff.Memberships.Add(new UserTenantMembership
        {
            UserId = staff.Id, TenantId = tenant, Roles = [YojanaRole.ServiceDesk],
        });
        using (var scope = fixture.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Users.Add(staff);
            await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SaveChangesAsync();
        }
        using var desk = fixture.Client();
        (await desk.PostAsJsonAsync("/api/auth/sign-in", new { email = staff.Email, password }))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.GetAsync(Query(todayFrom, weekFrom))).StatusCode);
    }

    [DatabaseFact]
    public async Task Retention_purge_deletes_in_configured_batches()
    {
        fixture.Clock.UtcNow = Start;
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        for (var index = 0; index < 5; index++)
        {
            db.SecurityEvents.Add(new SecurityEvent
            {
                Id = Guid.CreateVersion7(), OccurredAt = new DateTime(2024, 1, 1, 0, 0, index, DateTimeKind.Utc),
                Kind = SecurityEventKind.SignInFailed, Reason = "retention_test",
            });
        }
        db.SecurityEvents.Add(new SecurityEvent
        {
            Id = Guid.CreateVersion7(), OccurredAt = Start, Kind = SecurityEventKind.SignInFailed,
            Reason = "retention_keep",
        });
        await db.SaveChangesAsync();

        var result = await scope.ServiceProvider.GetRequiredService<SecurityMaintenance>()
            .PurgeAsync(CancellationToken.None);
        Assert.Equal(5, result.EventRows);
        Assert.Equal(3, result.Batches);
        Assert.True(await db.SecurityEvents.AnyAsync(row => row.Reason == "retention_keep"));
        Assert.False(await db.SecurityEvents.AnyAsync(row => row.Reason == "retention_test"));
    }

    private async Task<(TenantId Tenant, AppUser User, string Password)> AddAdministratorAsync(string prefix)
    {
        const string password = "Fictional-Test-Password-2026";
        var tenant = await AddTenantAsync(prefix);
        var user = new AppUser
        {
            Id = Guid.CreateVersion7(), Email = $"{prefix}-{Guid.NewGuid():N}@example.invalid",
            DisplayName = $"Fictional {prefix} administrator", PasswordHash = PasswordHasher.Hash(password),
            CreatedAt = fixture.Clock.UtcNow,
        };
        user.Memberships.Add(new UserTenantMembership
        {
            UserId = user.Id, TenantId = tenant, Roles = [YojanaRole.TenantAdministrator],
        });
        using var scope = fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        await scope.ServiceProvider.GetRequiredService<ITenantSettings>().SetAsync(SecondStepPolicy.SettingKey,
            "{\"tenant_administrator\":\"optional\"}", CancellationToken.None);
        scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Users.Add(user);
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SaveChangesAsync();
        return (tenant, user, password);
    }

    private async Task<TenantId> AddTenantAsync(string prefix)
    {
        var tenant = new TenantId(Guid.CreateVersion7());
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Tenants.Add(new Tenant
        {
            Id = tenant, Code = (prefix + Guid.NewGuid().ToString("N"))[..20],
            Name = $"Fictional {prefix} company", CreatedAt = fixture.Clock.UtcNow,
        });
        await db.SaveChangesAsync();
        return tenant;
    }

    private IServiceScope ScopeFor(TenantId tenant)
    {
        var scope = fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        return scope;
    }

    private static async Task<SignInThrottle> AccountRowAsync(IServiceScope scope, string email)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("account\0" + email));
        return await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SignInThrottles
            .SingleAsync(row => row.KeyKind == "account" && row.KeyHash == hash);
    }

    private static async Task<bool> AddressThrottleExistsAsync(PlatformDbContext db, string address)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("address\0" + address));
        return await db.SignInThrottles.AsNoTracking()
            .AnyAsync(row => row.KeyKind == "address" && row.KeyHash == hash);
    }

    private static Task<HttpResponseMessage> SendFailedSignInAsync(HttpClient client, string forwardedFor,
        string userAgent)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/sign-in")
        {
            Content = JsonContent.Create(new SignInRequest($"proxy-{Guid.NewGuid():N}@example.invalid", "wrong")),
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return client.SendAsync(request);
    }

    private static async Task<WebApplication> StartIsolatedWebHostAsync(int requestLimit,
        string? knownProxy = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Platform:ConnectionString"] = PlatformHostFixture.ConnectionString;
        builder.Configuration["Platform:MigrateOnStartup"] = "false";
        builder.Configuration["Platform:AutoBuildMessageStorage"] = "false";
        builder.Configuration["Platform:StartScheduler"] = "false";
        builder.Configuration["Platform:ServiceName"] = "yojana-rate-limit-tests";
        builder.Configuration["Platform:SignIn:AuditHmacKey"] = "rate-limit-tests-only-hmac-key-32";
        builder.Configuration["Platform:SignIn:RequestLimitPerMinute"] = requestLimit.ToString();
        if (knownProxy is not null)
            builder.Configuration["Platform:ForwardedHeaders:KnownProxies:0"] = knownProxy;
        builder.AddYojanaPlatform();
        var app = builder.Build();
        app.UseYojanaPlatform();
        app.MapYojanaSecurityEndpoints();
        await app.StartAsync();
        return app;
    }
}
