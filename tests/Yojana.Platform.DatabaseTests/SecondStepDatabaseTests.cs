using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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
public sealed class SecondStepDatabaseTests(PlatformHostFixture fixture)
{
    private const string Password = "Fictional-Second-Step-Password-2026";
    private static readonly DateTime Start = new(2026, 9, 1, 14, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Required_user_enrols_before_a_session_and_then_completes_sign_in_with_totp()
    {
        fixture.Clock.UtcNow = Start;
        var (_, user) = await AddUserAsync("enrol", YojanaRole.TenantAdministrator);
        using var client = fixture.Client();
        using var password = await client.PostAsJsonAsync("/api/auth/sign-in", new { email = user.Email, password = Password });
        Assert.Equal(HttpStatusCode.OK, password.StatusCode);
        var challenge = await password.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(challenge.GetProperty("secondStepRequired").GetBoolean());
        Assert.True(challenge.GetProperty("enrolmentRequired").GetBoolean());
        Assert.DoesNotContain(password.Headers.GetValues("Set-Cookie"), value => value.StartsWith("yojana.session=", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        var started = await (await client.PostAsJsonAsync("/api/auth/second-step/enrolment/start", new { }))
            .Content.ReadFromJsonAsync<SecondStepEnrolmentStart>();
        Assert.NotNull(started);
        var secret = Base32(started.ManualKey.Replace(" ", string.Empty));
        var code = AuthenticatorCodes.Totp(secret, new DateTimeOffset(fixture.Clock.UtcNow).ToUnixTimeSeconds());
        using var confirmed = await client.PostAsJsonAsync("/api/auth/second-step/enrolment/confirm",
            new { enrolmentId = started.EnrolmentId, code });
        confirmed.EnsureSuccessStatusCode();
        var confirmation = await confirmed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10, confirmation.GetProperty("recoveryCodes").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(30);
        using var next = fixture.Client();
        var pending = await next.PostAsJsonAsync("/api/auth/sign-in", new { email = user.Email, password = Password });
        var pendingBody = await pending.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(pendingBody.GetProperty("enrolmentRequired").GetBoolean());
        Assert.Equal(["authenticator", "recovery_code"], pendingBody.GetProperty("methods").EnumerateArray()
            .Select(value => value.GetString()!).ToArray());
        var nextCode = AuthenticatorCodes.Totp(secret,
            new DateTimeOffset(fixture.Clock.UtcNow).ToUnixTimeSeconds());
        (await next.PostAsJsonAsync("/api/auth/second-step", new { method = "authenticator", code = nextCode }))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await next.GetAsync("/api/auth/me")).StatusCode);
    }

    [DatabaseFact]
    public async Task Pending_cookie_expires_and_stamp_rotation_invalidates_it()
    {
        fixture.Clock.UtcNow = Start;
        var (_, user) = await AddUserAsync("pending-expiry", YojanaRole.TenantAdministrator);
        using var expired = fixture.Client();
        (await expired.PostAsJsonAsync("/api/auth/sign-in", new { email = user.Email, password = Password }))
            .EnsureSuccessStatusCode();
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(6);
        var expiredResponse = await expired.PostAsJsonAsync("/api/auth/second-step/enrolment/start", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, expiredResponse.StatusCode);
        Assert.Equal("second_step_expired", (await expiredResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("title").GetString());

        fixture.Clock.UtcNow = Start;
        using var rotated = fixture.Client();
        (await rotated.PostAsJsonAsync("/api/auth/sign-in", new { email = user.Email, password = Password }))
            .EnsureSuccessStatusCode();
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISecurityStampService>()
                .RotateSecurityStampAsync(user.Id, "test_pending_rotation", default);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await rotated.PostAsJsonAsync("/api/auth/second-step/enrolment/start", new { })).StatusCode);
    }

    [DatabaseFact]
    public async Task Policy_is_versioned_and_strictest_across_active_memberships()
    {
        fixture.Clock.UtcNow = Start;
        var (first, user) = await AddUserAsync("policy", YojanaRole.ServiceDesk);
        var second = await AddTenantAsync("policy-other");
        await AddMembershipAsync(user.Id, second, YojanaRole.LegalReviewer);
        using var scope = ScopeFor(first);
        var policy = scope.ServiceProvider.GetRequiredService<SecondStepPolicy>();
        Assert.Equal(SecondStepMode.Required, await policy.EffectiveForUserAsync(user.Id, default));
        var initial = await policy.GetCurrentAsync(default);
        var changedRows = initial.Roles.Select(row => row.Role == YojanaRole.ServiceDesk
            ? row with { Mode = SecondStepMode.Off } : row).ToList();
        var saved = await policy.UpdateCurrentAsync(changedRows, initial.Version, default);
        Assert.Contains(YojanaRole.ServiceDesk, saved.Changed);
        await Assert.ThrowsAsync<TenantSettingVersionConflictException>(() =>
            policy.UpdateCurrentAsync(changedRows, initial.Version, default));
        Assert.Equal(SecondStepMode.Required, await policy.EffectiveForUserAsync(user.Id, default));
    }

    [DatabaseFact]
    public async Task Totp_replay_and_recovery_reuse_are_refused_and_low_codes_are_reported_without_secret_leaks()
    {
        fixture.Clock.UtcNow = Start;
        var (_, user) = await AddUserAsync("single-use", YojanaRole.TenantAdministrator);
        using var enrolled = fixture.Client();
        var setup = await EnrolRequiredAsync(enrolled, user);

        fixture.Clock.UtcNow = Start.AddSeconds(30);
        var code = AuthenticatorCodes.Totp(setup.Secret,
            new DateTimeOffset(fixture.Clock.UtcNow).ToUnixTimeSeconds());
        using (var first = fixture.Client())
        {
            await StartPendingAsync(first, user);
            (await first.PostAsJsonAsync("/api/auth/second-step", new { method = "authenticator", code }))
                .EnsureSuccessStatusCode();
        }
        using (var replay = fixture.Client())
        {
            await StartPendingAsync(replay, user);
            var response = await replay.PostAsJsonAsync("/api/auth/second-step",
                new { method = "authenticator", code });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        string recovery;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var rows = await db.UserRecoveryCodes.Where(row => row.UserId == user.Id).ToListAsync();
            foreach (var row in rows.Take(7)) row.UsedAt = fixture.Clock.UtcNow;
            await db.SaveChangesAsync();
            recovery = setup.RecoveryCodes.First(value => rows.Skip(7).Any(row =>
                RecoveryCodes.Verify(RecoveryCodes.Normalise(value)!, row.CodeHash)));
        }

        using (var recovered = fixture.Client())
        {
            await StartPendingAsync(recovered, user);
            var response = await recovered.PostAsJsonAsync("/api/auth/second-step",
                new { method = "recovery_code", code = recovery.ToLowerInvariant().Replace("-", "") });
            response.EnsureSuccessStatusCode();
            Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("recoveryCodesLow").GetBoolean());
        }
        using (var reused = fixture.Client())
        {
            await StartPendingAsync(reused, user);
            Assert.Equal(HttpStatusCode.BadRequest, (await reused.PostAsJsonAsync("/api/auth/second-step",
                new { method = "recovery_code", code = recovery })).StatusCode);
        }

        await WaitForMailAsync(user.Id);
        using var check = fixture.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var auditAndMail = JsonSerializer.Serialize(new
        {
            Events = await checkDb.SecurityEvents.Where(row => row.UserId == user.Id).ToListAsync(),
            Mail = await checkDb.MailMessages.Where(row => row.UserId == user.Id).ToListAsync(),
        });
        Assert.DoesNotContain(AuthenticatorCodes.Base32Encode(setup.Secret), auditAndMail, StringComparison.Ordinal);
        Assert.DoesNotContain(setup.ManualKey, auditAndMail, StringComparison.Ordinal);
        Assert.All(setup.RecoveryCodes, value => Assert.DoesNotContain(value, auditAndMail, StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task Five_wrong_second_step_codes_lock_the_account_until_the_throttle_expires()
    {
        fixture.Clock.UtcNow = Start;
        var (_, user) = await AddUserAsync("code-lock", YojanaRole.TenantAdministrator);
        using var enrolled = fixture.Client();
        var setup = await EnrolRequiredAsync(enrolled, user);
        fixture.Clock.UtcNow = Start.AddSeconds(30);
        using var pending = fixture.Client();
        await StartPendingAsync(pending, user);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var response = await pending.PostAsJsonAsync("/api/auth/second-step",
                new { method = "authenticator", code = "000000" });
            Assert.Equal(attempt == 5 ? HttpStatusCode.Unauthorized : HttpStatusCode.BadRequest,
                response.StatusCode);
        }
        var valid = AuthenticatorCodes.Totp(setup.Secret,
            new DateTimeOffset(fixture.Clock.UtcNow).ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.Unauthorized, (await pending.PostAsJsonAsync("/api/auth/second-step",
            new { method = "authenticator", code = valid })).StatusCode);

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(2);
        using var after = fixture.Client();
        await StartPendingAsync(after, user);
        var next = AuthenticatorCodes.Totp(setup.Secret,
            new DateTimeOffset(fixture.Clock.UtcNow).ToUnixTimeSeconds());
        (await after.PostAsJsonAsync("/api/auth/second-step",
            new { method = "authenticator", code = next })).EnsureSuccessStatusCode();
    }

    [DatabaseFact]
    public async Task Enrolment_returns_service_unavailable_when_the_host_has_no_secret_protector()
    {
        fixture.Clock.UtcNow = Start;
        var (_, user) = await AddUserAsync("protector-missing", YojanaRole.TenantAdministrator);
        await using var host = await StartHostWithoutSecondStepAdaptersAsync();
        var address = host.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        (await client.PostAsJsonAsync("/api/auth/sign-in", new { email = user.Email, password = Password }))
            .EnsureSuccessStatusCode();
        var response = await client.PostAsJsonAsync("/api/auth/second-step/enrolment/start", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("second_step_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("title").GetString());
    }

    [DatabaseFact]
    public async Task Replacement_keeps_the_old_app_until_confirmation_then_rotates_other_sessions()
    {
        fixture.Clock.UtcNow = Start;
        var (_, user) = await AddUserAsync("replace", YojanaRole.ServiceDesk);
        using var account = fixture.Client();
        var original = await EnrolFromAccountAsync(account, user);
        fixture.Clock.UtcNow = Start.AddSeconds(30);

        var replacement = await (await account.PostAsJsonAsync("/api/auth/second-step/enrolment/start", new { }))
            .Content.ReadFromJsonAsync<SecondStepEnrolmentStart>();
        Assert.NotNull(replacement);
        var replacementSecret = Base32(replacement.ManualKey.Replace(" ", string.Empty));
        using var otherSession = fixture.Client();
        await StartPendingAsync(otherSession, user);
        var oldCode = AuthenticatorCodes.Totp(original.Secret,
            new DateTimeOffset(fixture.Clock.UtcNow).ToUnixTimeSeconds());
        (await otherSession.PostAsJsonAsync("/api/auth/second-step",
            new { method = "authenticator", code = oldCode })).EnsureSuccessStatusCode();

        var newCode = AuthenticatorCodes.Totp(replacementSecret,
            new DateTimeOffset(fixture.Clock.UtcNow).ToUnixTimeSeconds());
        (await account.PostAsJsonAsync("/api/auth/second-step/enrolment/confirm",
            new { enrolmentId = replacement.EnrolmentId, code = newCode })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await account.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherSession.GetAsync("/api/auth/me")).StatusCode);

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(30);
        using var oldAttempt = fixture.Client();
        await StartPendingAsync(oldAttempt, user);
        var nextOld = AuthenticatorCodes.Totp(original.Secret,
            new DateTimeOffset(fixture.Clock.UtcNow).ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.BadRequest, (await oldAttempt.PostAsJsonAsync("/api/auth/second-step",
            new { method = "authenticator", code = nextOld })).StatusCode);
        using var newAttempt = fixture.Client();
        await StartPendingAsync(newAttempt, user);
        var nextNew = AuthenticatorCodes.Totp(replacementSecret,
            new DateTimeOffset(fixture.Clock.UtcNow).ToUnixTimeSeconds());
        (await newAttempt.PostAsJsonAsync("/api/auth/second-step",
            new { method = "authenticator", code = nextNew })).EnsureSuccessStatusCode();
    }

    [DatabaseFact]
    public async Task Optional_user_can_remove_with_password_and_admin_reset_obeys_global_membership_rule()
    {
        fixture.Clock.UtcNow = Start;
        var (tenant, target) = await AddUserAsync("remove-reset", YojanaRole.ServiceDesk);
        using var targetSession = fixture.Client();
        await EnrolFromAccountAsync(targetSession, target);
        using var otherTargetSession = fixture.Client();
        await StartPendingAsync(otherTargetSession, target);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(30);
        using (var scope = fixture.Services.CreateScope())
        {
            var authenticator = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>()
                .UserAuthenticators.SingleAsync(row => row.UserId == target.Id &&
                    row.Status == UserAuthenticatorStatus.Active);
            var secret = await scope.ServiceProvider.GetRequiredService<IUserSecretProtector>()
                .UnprotectAsync(authenticator.SealedSecret, "user-authenticator-totp", default);
            var code = AuthenticatorCodes.Totp(secret,
                new DateTimeOffset(fixture.Clock.UtcNow).ToUnixTimeSeconds());
            (await otherTargetSession.PostAsJsonAsync("/api/auth/second-step",
                new { method = "authenticator", code })).EnsureSuccessStatusCode();
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await targetSession.PostAsJsonAsync("/api/auth/second-step/remove",
            new { currentPassword = "wrong" })).StatusCode);
        (await targetSession.PostAsJsonAsync("/api/auth/second-step/remove",
            new { currentPassword = Password })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await targetSession.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherTargetSession.GetAsync("/api/auth/me")).StatusCode);

        await EnrolFromAccountAsync(targetSession, target);
        var (_, actor) = await AddUserAsync("reset-actor", YojanaRole.TenantAdministrator, tenant);
        await SetModeAsync(tenant, YojanaRole.TenantAdministrator, SecondStepMode.Optional);
        using var administrator = fixture.Client();
        (await administrator.PostAsJsonAsync("/api/auth/sign-in",
            new { email = actor.Email, password = Password })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await administrator.PostAsJsonAsync(
            $"/api/security/users/{actor.Id}/second-step-reset", new { reason = "Lost managed phone" })).StatusCode);

        var otherTenant = await AddTenantAsync("reset-other");
        await AddMembershipAsync(target.Id, otherTenant, YojanaRole.ServiceDesk);
        var forbidden = await administrator.PostAsJsonAsync(
            $"/api/security/users/{target.Id}/second-step-reset", new { reason = "Lost managed phone" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("second_step_reset_not_allowed", (await forbidden.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("title").GetString());
        await AddMembershipAsync(actor.Id, otherTenant, YojanaRole.TenantAdministrator);
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.PostAsJsonAsync(
            $"/api/security/users/{target.Id}/second-step-reset", new { reason = "Lost managed phone" })).StatusCode);

        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.False(await db.UserAuthenticators.AnyAsync(row => row.UserId == target.Id));
        Assert.False(await db.UserRecoveryCodes.AnyAsync(row => row.UserId == target.Id));
        Assert.Contains(await db.SecurityEvents.Where(row => row.UserId == target.Id).ToListAsync(), row =>
            row.Kind == SecurityEventKind.SecondStepRemoved && row.ActorUserId == actor.Id &&
            row.Reason == "Lost managed phone");
    }

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
        db.Users.Add(user); await db.SaveChangesAsync();
        return (tenant, user);
    }

    private async Task<Enrolment> EnrolRequiredAsync(HttpClient client, AppUser user)
    {
        await StartPendingAsync(client, user);
        var started = await (await client.PostAsJsonAsync("/api/auth/second-step/enrolment/start", new { }))
            .Content.ReadFromJsonAsync<SecondStepEnrolmentStart>();
        Assert.NotNull(started);
        var secret = Base32(started.ManualKey.Replace(" ", string.Empty));
        var code = AuthenticatorCodes.Totp(secret,
            new DateTimeOffset(fixture.Clock.UtcNow).ToUnixTimeSeconds());
        var response = await client.PostAsJsonAsync("/api/auth/second-step/enrolment/confirm",
            new { enrolmentId = started.EnrolmentId, code });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Enrolment(secret, started.ManualKey, body.GetProperty("recoveryCodes").EnumerateArray()
            .Select(value => value.GetString()!).ToList());
    }

    private async Task<Enrolment> EnrolFromAccountAsync(HttpClient client, AppUser user)
    {
        var signedIn = await client.PostAsJsonAsync("/api/auth/sign-in",
            new { email = user.Email, password = Password });
        signedIn.EnsureSuccessStatusCode();
        var started = await (await client.PostAsJsonAsync("/api/auth/second-step/enrolment/start", new { }))
            .Content.ReadFromJsonAsync<SecondStepEnrolmentStart>();
        Assert.NotNull(started);
        var secret = Base32(started.ManualKey.Replace(" ", string.Empty));
        var code = AuthenticatorCodes.Totp(secret,
            new DateTimeOffset(fixture.Clock.UtcNow).ToUnixTimeSeconds());
        var response = await client.PostAsJsonAsync("/api/auth/second-step/enrolment/confirm",
            new { enrolmentId = started.EnrolmentId, code });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Enrolment(secret, started.ManualKey, body.GetProperty("recoveryCodes").EnumerateArray()
            .Select(value => value.GetString()!).ToList());
    }

    private async Task StartPendingAsync(HttpClient client, AppUser user)
    {
        var response = await client.PostAsJsonAsync("/api/auth/sign-in",
            new { email = user.Email, password = Password });
        response.EnsureSuccessStatusCode();
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("secondStepRequired").GetBoolean());
    }

    private async Task SetModeAsync(TenantId tenant, string role, string mode)
    {
        using var scope = ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ITenantSettings>().SetAsync(SecondStepPolicy.SettingKey,
            JsonSerializer.Serialize(new Dictionary<string, string> { [role] = mode }), default);
    }

    private async Task WaitForMailAsync(Guid userId)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            using var scope = fixture.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().MailMessages
                    .AnyAsync(row => row.UserId == userId && row.Kind == MailKind.SignInMethodAdded))
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException("The sign-in method mail did not reach its delivery log.");
    }

    private static async Task<WebApplication> StartHostWithoutSecondStepAdaptersAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Platform:ConnectionString"] = PlatformHostFixture.ConnectionString;
        builder.Configuration["Platform:MigrateOnStartup"] = "false";
        builder.Configuration["Platform:AutoBuildMessageStorage"] = "false";
        builder.Configuration["Platform:StartScheduler"] = "false";
        builder.Configuration["Platform:ServiceName"] = "yojana-second-step-unavailable-tests";
        builder.Configuration["Platform:SignIn:AuditHmacKey"] = "second-step-unavailable-tests-only-key";
        builder.AddYojanaPlatform();
        var app = builder.Build();
        app.UseYojanaPlatform();
        app.MapYojanaSecurityEndpoints();
        app.MapYojanaSecondStepEndpoints();
        await app.StartAsync();
        return app;
    }

    private async Task<TenantId> AddTenantAsync(string prefix)
    {
        var id = new TenantId(Guid.CreateVersion7());
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Tenants.Add(new Tenant { Id = id, Code = (prefix + Guid.NewGuid().ToString("N"))[..20],
            Name = $"Fictional {prefix} company", CreatedAt = fixture.Clock.UtcNow });
        await db.SaveChangesAsync(); return id;
    }

    private async Task AddMembershipAsync(Guid userId, TenantId tenant, string role)
    {
        using var scope = fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Memberships.Add(new UserTenantMembership
            { UserId = userId, TenantId = tenant, Roles = [role] });
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SaveChangesAsync();
    }

    private IServiceScope ScopeFor(TenantId tenant)
    {
        var scope = fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        return scope;
    }

    private static byte[] Base32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>(); var buffer = 0; var bits = 0;
        foreach (var character in value)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character);
            bits += 5;
            if (bits < 8) continue;
            output.Add((byte)(buffer >> (bits - 8))); bits -= 8;
            buffer &= (1 << bits) - 1;
        }
        return output.ToArray();
    }

    private sealed record Enrolment(byte[] Secret, string ManualKey, IReadOnlyList<string> RecoveryCodes);
}
