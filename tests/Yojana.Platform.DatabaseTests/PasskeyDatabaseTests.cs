using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Security;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.DatabaseTests;

[Collection("platform host")]
public sealed class PasskeyDatabaseTests(PlatformHostFixture fixture) : IAsyncLifetime
{
    private const string Password = "Fictional-Passkey-Password-2026";
    private static readonly DateTime Start = new(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc);

    // Sign-ins here come from the loopback address on a fixed clock; clear its failure count around each test, as the
    // second-step tests do, so a rejected answer in one test cannot lock out the next.
    public Task InitializeAsync() => ResetLoopbackThrottleAsync();

    public Task DisposeAsync() => ResetLoopbackThrottleAsync();

    [DatabaseFact]
    public async Task A_passkey_is_added_with_the_password_and_signs_in_alone_only_once_the_company_allows_it()
    {
        fixture.Clock.UtcNow = Start;
        var (tenant, user) = await AddUserAsync("alone", YojanaRole.ServiceDesk);
        using var device = new SoftwarePasskey();
        using var session = fixture.Client();
        (await session.PostAsJsonAsync("/api/auth/sign-in", new { email = user.Email, password = Password }))
            .EnsureSuccessStatusCode();

        using var wrong = await session.PostAsJsonAsync("/api/auth/passkeys/registration/start",
            new { name = "Work laptop", currentPassword = "not-the-password" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("current_password_wrong", await TitleAsync(wrong));

        var added = await RegisterAsync(session, device, "Work laptop");
        Assert.Equal("Work laptop", added.GetProperty("name").GetString());
        var account = await session.GetFromJsonAsync<JsonElement>("/api/auth/passkeys");
        Assert.True(account.GetProperty("allowed").GetBoolean());
        Assert.False(account.GetProperty("passwordless").GetBoolean());
        Assert.Equal(1, account.GetProperty("passkeys").GetArrayLength());
        await WaitForMailAsync(user.Id, MailKind.SignInMethodAdded);

        // Passwordless is off by default: the answer is checked, then refused by the policy.
        using var visitor = fixture.Client();
        var (refusedStart, refusedAnswer) = await AnswerAsync(visitor, device, PasskeyEndpoints.PasswordlessMode);
        using var refused = await FinishAsync(visitor, refusedStart, refusedAnswer, PasskeyEndpoints.PasswordlessMode);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("passkey_not_allowed", await TitleAsync(refused));

        await SetAsync(tenant, PasskeyPolicy.PasswordlessKey, "true");
        var (ceremony, answer) = await AnswerAsync(visitor, device, PasskeyEndpoints.PasswordlessMode);
        using var signedIn = await FinishAsync(visitor, ceremony, answer, PasskeyEndpoints.PasswordlessMode);
        signedIn.EnsureSuccessStatusCode();
        Assert.Equal(user.Email, (await signedIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user")
            .GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/api/auth/me")).StatusCode);

        // The same signed answer a second time: the ceremony was consumed by the first.
        using var replay = await FinishAsync(fixture.Client(), ceremony, answer, PasskeyEndpoints.PasswordlessMode);
        Assert.Equal(HttpStatusCode.Gone, replay.StatusCode);

        // Alone, the passkey must have checked the person (fingerprint, face or PIN).
        using var unverifiedClient = fixture.Client();
        var start = await StartAsync(unverifiedClient, PasskeyEndpoints.PasswordlessMode);
        using var unverified = await FinishAsync(unverifiedClient, start.GetProperty("ceremonyId").GetGuid(),
            device.Get(start.GetProperty("options"), userVerified: false), PasskeyEndpoints.PasswordlessMode);
        Assert.Equal(HttpStatusCode.BadRequest, unverified.StatusCode);
        Assert.Equal("passkey_rejected", await TitleAsync(unverified));

        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var stored = await db.UserPasskeys.SingleAsync(row => row.UserId == user.Id);
        Assert.Equal(device.CredentialId, stored.CredentialId);
        Assert.Equal(Start, stored.LastUsedAt);
        var reasons = await db.SecurityEvents.Where(row => row.UserId == user.Id).Select(row => row.Kind + ":" + row.Reason)
            .ToListAsync();
        Assert.Contains($"{SecurityEventKind.PasskeyAdded}:passkey_added", reasons);
        Assert.Contains($"{SecurityEventKind.SignInFailed}:passkey_not_allowed", reasons);
        Assert.Contains($"{SecurityEventKind.SignInSucceeded}:passkey", reasons);
        Assert.Contains($"{SecurityEventKind.SignInFailed}:passkey_rejected", reasons);
    }

    [DatabaseFact]
    public async Task A_passkey_completes_a_required_second_step_but_never_for_another_person()
    {
        fixture.Clock.UtcNow = Start;
        var (tenant, user) = await AddUserAsync("second", YojanaRole.TenantAdministrator);
        var (_, other) = await AddUserAsync("other", YojanaRole.ServiceDesk);
        await SetModeAsync(tenant, YojanaRole.TenantAdministrator, SecondStepMode.Optional);
        using var mine = new SoftwarePasskey();
        using var theirs = new SoftwarePasskey();
        using (var session = fixture.Client())
        {
            (await session.PostAsJsonAsync("/api/auth/sign-in", new { email = user.Email, password = Password }))
                .EnsureSuccessStatusCode();
            await RegisterAsync(session, mine, "Phone");
        }
        using (var session = fixture.Client())
        {
            (await session.PostAsJsonAsync("/api/auth/sign-in", new { email = other.Email, password = Password }))
                .EnsureSuccessStatusCode();
            await RegisterAsync(session, theirs, "Their key");
        }
        await SetModeAsync(tenant, YojanaRole.TenantAdministrator, SecondStepMode.Required);

        using var client = fixture.Client();
        using var password = await client.PostAsJsonAsync("/api/auth/sign-in", new { email = user.Email, password = Password });
        var pending = await password.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(pending.GetProperty("enrolmentRequired").GetBoolean());
        Assert.Equal(["passkey"], pending.GetProperty("methods").EnumerateArray().Select(value => value.GetString()!));

        var start = await StartAsync(client, PasskeyEndpoints.SecondStepMode);
        var allowed = start.GetProperty("options").GetProperty("allowCredentials").EnumerateArray()
            .Select(value => value.GetProperty("id").GetString()).ToList();
        Assert.Equal([SoftwarePasskey.Base64Url(mine.CredentialId)], allowed);
        using var foreign = await FinishAsync(client, start.GetProperty("ceremonyId").GetGuid(),
            theirs.Get(start.GetProperty("options")), PasskeyEndpoints.SecondStepMode);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        var (ceremony, answer) = await AnswerAsync(client, mine, PasskeyEndpoints.SecondStepMode);
        (await FinishAsync(client, ceremony, answer, PasskeyEndpoints.SecondStepMode)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        // The only second step of someone whose company requires one stays until an administrator resets it.
        var passkeyId = (await client.GetFromJsonAsync<JsonElement>("/api/auth/passkeys")).GetProperty("passkeys")[0]
            .GetProperty("id").GetGuid();
        using var last = await client.PostAsJsonAsync($"/api/auth/passkeys/{passkeyId}/remove",
            new { currentPassword = Password });
        Assert.Equal(HttpStatusCode.Forbidden, last.StatusCode);
        Assert.Equal("second_step_required", await TitleAsync(last));

        var (_, admin) = await AddUserAsync("passkey-admin", YojanaRole.TenantAdministrator, tenant);
        await SetModeAsync(tenant, YojanaRole.TenantAdministrator, SecondStepMode.Optional);
        using var administrator = fixture.Client();
        (await administrator.PostAsJsonAsync("/api/auth/sign-in", new { email = admin.Email, password = Password }))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.PostAsJsonAsync(
            $"/api/security/users/{user.Id}/second-step-reset", new { reason = "Lost phone" })).StatusCode);
        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.False(await db.UserPasskeys.AnyAsync(row => row.UserId == user.Id));
        Assert.True(await db.UserPasskeys.AnyAsync(row => row.UserId == other.Id));
        Assert.Contains(await db.SecurityEvents.Where(row => row.UserId == user.Id).Select(row => row.Reason).ToListAsync(),
            reason => reason == "password+passkey");
    }

    [DatabaseFact]
    public async Task An_optional_user_removes_a_passkey_with_the_password_and_is_told_by_mail()
    {
        fixture.Clock.UtcNow = Start;
        var (_, user) = await AddUserAsync("remove", YojanaRole.ServiceDesk);
        using var device = new SoftwarePasskey();
        using var session = fixture.Client();
        (await session.PostAsJsonAsync("/api/auth/sign-in", new { email = user.Email, password = Password }))
            .EnsureSuccessStatusCode();
        var id = (await RegisterAsync(session, device, "Old key")).GetProperty("id").GetGuid();
        using var wrong = await session.PostAsJsonAsync($"/api/auth/passkeys/{id}/remove", new { currentPassword = "nope" });
        Assert.Equal("current_password_wrong", await TitleAsync(wrong));
        Assert.Equal(HttpStatusCode.NotFound, (await session.PostAsJsonAsync(
            $"/api/auth/passkeys/{Guid.CreateVersion7()}/remove", new { currentPassword = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await session.PostAsJsonAsync($"/api/auth/passkeys/{id}/remove",
            new { currentPassword = Password })).StatusCode);
        // Removal rotated the security stamp; this session was re-issued and still works.
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/auth/me")).StatusCode);
        Assert.Empty((await session.GetFromJsonAsync<JsonElement>("/api/auth/passkeys")).GetProperty("passkeys")
            .EnumerateArray());
        await WaitForMailAsync(user.Id, MailKind.SignInMethodRemoved);
    }

    [DatabaseFact]
    public async Task Passkey_settings_are_versioned_audited_and_the_strictest_company_wins()
    {
        fixture.Clock.UtcNow = Start;
        var (tenant, admin) = await AddUserAsync("settings", YojanaRole.TenantAdministrator);
        await SetModeAsync(tenant, YojanaRole.TenantAdministrator, SecondStepMode.Optional);
        using var client = fixture.Client();
        (await client.PostAsJsonAsync("/api/auth/sign-in", new { email = admin.Email, password = Password }))
            .EnsureSuccessStatusCode();
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/security/settings/passkeys");
        Assert.True(settings.GetProperty("allowed").GetBoolean());
        Assert.False(settings.GetProperty("passwordless").GetBoolean());
        var version = settings.GetProperty("version").GetString();

        using var invalid = await client.PutAsJsonAsync("/api/security/settings/passkeys",
            new { allowed = false, passwordless = true, version });
        Assert.Equal("passkey_settings_invalid", await TitleAsync(invalid));
        using var saved = await client.PutAsJsonAsync("/api/security/settings/passkeys",
            new { allowed = true, passwordless = true, version });
        saved.EnsureSuccessStatusCode();
        using var stale = await client.PutAsJsonAsync("/api/security/settings/passkeys",
            new { allowed = true, passwordless = false, version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // The same person in a second company that turns passkeys off: the stricter company wins everywhere.
        var (other, _) = await AddUserAsync("settings-other", YojanaRole.ServiceDesk);
        await AddMembershipAsync(admin.Id, other, YojanaRole.ServiceDesk);
        using (var scope = fixture.Services.CreateScope())
        {
            var policy = scope.ServiceProvider.GetRequiredService<PasskeyPolicy>();
            Assert.Equal(new PasskeyPermission(true, false), await policy.EffectiveForUserAsync(admin.Id, default));
            await SetAsync(other, PasskeyPolicy.AllowedKey, "false");
            Assert.Equal(new PasskeyPermission(false, false), await policy.EffectiveForUserAsync(admin.Id, default));
        }
        using var refused = await client.PostAsJsonAsync("/api/auth/passkeys/registration/start",
            new { name = "Phone", currentPassword = Password });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("passkey_not_allowed", await TitleAsync(refused));

        // A ceremony nobody answers is purged once it has expired.
        var abandoned = await StartAsync(fixture.Client(), PasskeyEndpoints.PasswordlessMode);
        fixture.Clock.UtcNow = Start.AddMinutes(10);
        using (var scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SecurityMaintenance>().PurgeAsync(default);
            var ceremonyId = abandoned.GetProperty("ceremonyId").GetGuid();
            Assert.False(await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().PasskeyCeremonies
                .AnyAsync(row => row.Id == ceremonyId));
            var reasons = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SecurityEvents
                .Where(row => row.ActorUserId == admin.Id && row.Kind == SecurityEventKind.SettingsChanged)
                .Select(row => row.Reason).ToListAsync();
            Assert.Equal([PasskeyPolicy.PasswordlessKey], reasons);
        }
    }

    private async Task<JsonElement> RegisterAsync(HttpClient session, SoftwarePasskey device, string name)
    {
        using var started = await session.PostAsJsonAsync("/api/auth/passkeys/registration/start",
            new { name, currentPassword = Password });
        started.EnsureSuccessStatusCode();
        var start = await started.Content.ReadFromJsonAsync<JsonElement>();
        var options = start.GetProperty("options");
        Assert.Equal("localhost", options.GetProperty("rp").GetProperty("id").GetString());
        Assert.Equal("required", options.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());
        using var finished = await session.PostAsJsonAsync("/api/auth/passkeys/registration/finish",
            new JsonObject { ["ceremonyId"] = start.GetProperty("ceremonyId").GetGuid(), ["credential"] = device.Create(options) });
        Assert.True(finished.IsSuccessStatusCode, await finished.Content.ReadAsStringAsync());
        return await finished.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> StartAsync(HttpClient client, string mode)
    {
        using var started = await client.PostAsJsonAsync("/api/auth/passkey/sign-in/start", new { mode });
        Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
        return await started.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<(Guid Ceremony, JsonObject Answer)> AnswerAsync(HttpClient client, SoftwarePasskey device,
        string mode)
    {
        var start = await StartAsync(client, mode);
        return (start.GetProperty("ceremonyId").GetGuid(), device.Get(start.GetProperty("options")));
    }

    private static Task<HttpResponseMessage> FinishAsync(HttpClient client, Guid ceremony, JsonObject answer, string mode) =>
        client.PostAsJsonAsync("/api/auth/passkey/sign-in/finish",
            new JsonObject { ["ceremonyId"] = ceremony, ["mode"] = mode, ["credential"] = answer.DeepClone() });

    private static async Task<string?> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

    private async Task<(TenantId Tenant, AppUser User)> AddUserAsync(string prefix, string role,
        TenantId? existingTenant = null)
    {
        var tenant = existingTenant ?? await AddTenantAsync(prefix);
        var user = new AppUser
        {
            Id = Guid.CreateVersion7(), Email = $"{prefix}-{Guid.NewGuid():N}@example.invalid",
            DisplayName = $"Fictional {prefix} person", PasswordHash = PasswordHasher.Hash(Password),
            CreatedAt = fixture.Clock.UtcNow,
        };
        user.Memberships.Add(new UserTenantMembership { UserId = user.Id, TenantId = tenant, Roles = [role] });
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (tenant, user);
    }

    private async Task<TenantId> AddTenantAsync(string prefix)
    {
        var id = new TenantId(Guid.CreateVersion7());
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Tenants.Add(new Tenant
        {
            Id = id, Code = (prefix + Guid.NewGuid().ToString("N"))[..20], Name = $"Fictional {prefix} company",
            CreatedAt = fixture.Clock.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task AddMembershipAsync(Guid userId, TenantId tenant, string role)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Memberships.Add(new UserTenantMembership { UserId = userId, TenantId = tenant, Roles = [role] });
        await db.SaveChangesAsync();
    }

    private async Task SetModeAsync(TenantId tenant, string role, string mode) =>
        await SetAsync(tenant, SecondStepPolicy.SettingKey, JsonSerializer.Serialize(new Dictionary<string, string> { [role] = mode }));

    private async Task SetAsync(TenantId tenant, string key, string value)
    {
        using var scope = fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        await scope.ServiceProvider.GetRequiredService<ITenantSettings>().SetAsync(key, value, default);
    }

    private async Task WaitForMailAsync(Guid userId, string kind)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            using var scope = fixture.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().MailMessages
                    .AnyAsync(row => row.UserId == userId && row.Kind == kind))
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"The {kind} mail did not reach its delivery log.");
    }

    private async Task ResetLoopbackThrottleAsync()
    {
        var hashes = new[] { "127.0.0.1", "::1", "::ffff:127.0.0.1", "unknown" }
            .Select(address => SHA256.HashData(Encoding.UTF8.GetBytes("address\0" + address))).ToList();
        using var scope = fixture.Services.CreateScope();
        var throttles = scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SignInThrottles;
        foreach (var hash in hashes)
            await throttles.Where(row => row.KeyKind == "address" && row.KeyHash == hash).ExecuteDeleteAsync();
    }
}
