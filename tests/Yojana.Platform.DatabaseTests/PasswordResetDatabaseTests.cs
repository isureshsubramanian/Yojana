using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Mail;
using Yojana.Platform.Security;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.DatabaseTests;

[Collection("platform host")]
public sealed class PasswordResetDatabaseTests(PlatformHostFixture fixture)
{
    private static readonly Regex TokenPattern = new(@"#t=(?<token>[A-Za-z0-9_-]+)", RegexOptions.Compiled);

    [DatabaseFact]
    public async Task Request_is_identical_for_unknown_inactive_and_active_addresses_and_only_active_gets_mail()
    {
        var tenant = await AddTenantAsync("request");
        var active = await AddUserAsync("active-request", [tenant]);
        var inactive = await AddUserAsync("inactive-request", [tenant], active: false);
        using var client = fixture.Client();

        var responses = new[]
        {
            await client.PostAsJsonAsync("/api/auth/password-reset/request", new { email = UnknownEmail() }),
            await client.PostAsJsonAsync("/api/auth/password-reset/request", new { email = inactive.Email }),
            await client.PostAsJsonAsync("/api/auth/password-reset/request", new { email = active.Email }),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        Assert.All(responses, response => Assert.Equal("{}", response.Content.ReadAsStringAsync().Result));
        await WaitForMailAsync(active.Email, 1);
        Assert.Empty(fixture.Mail.AttemptsFor(inactive.Email));
        using var scope = fixture.Services.CreateScope();
        var inactiveEvent = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SecurityEvents
            .Where(row => row.UserId == inactive.Id && row.Kind == SecurityEventKind.PasswordResetRequested)
            .OrderByDescending(row => row.OccurredAt).FirstAsync();
        Assert.NotNull(inactiveEvent.UnknownUserHash);
        Assert.DoesNotContain(inactive.Email, inactiveEvent.UnknownUserHash!, StringComparison.OrdinalIgnoreCase);
    }

    [DatabaseFact]
    public async Task Fourth_request_in_fifteen_minutes_is_silently_dropped()
    {
        var tenant = await AddTenantAsync("throttle");
        var user = await AddUserAsync("reset-throttle", [tenant]);
        using var client = fixture.Client();
        for (var index = 0; index < 4; index++)
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/auth/password-reset/request",
                new { email = user.Email })).StatusCode);

        await WaitForMailAsync(user.Email, 3);
        await Task.Delay(500);
        Assert.Equal(3, fixture.Mail.AttemptsFor(user.Email).Count);
        using var scope = fixture.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SecurityEvents
            .AnyAsync(row => row.UserId == user.Id && row.Reason == "mail_throttled"));
    }

    [DatabaseFact]
    public async Task Tokens_are_single_use_superseded_and_expired_with_one_public_problem()
    {
        var tenant = await AddTenantAsync("tokens");
        var user = await AddUserAsync("reset-tokens", [tenant]);
        using var client = fixture.Client();
        await RequestAsync(client, user.Email);
        var first = await TokenAsync(user.Email, 1);
        await RequestAsync(client, user.Email);
        var second = await TokenAsync(user.Email, 2);

        Assert.Equal("reset_link_invalid", await ProblemTitleAsync(await client.PostAsJsonAsync(
            "/api/auth/password-reset/complete", new { token = first, newPassword = StrongPassword("First") })));
        (await client.PostAsJsonAsync("/api/auth/password-reset/complete",
            new { token = second, newPassword = StrongPassword("Second") })).EnsureSuccessStatusCode();
        Assert.Equal("reset_link_invalid", await ProblemTitleAsync(await client.PostAsJsonAsync(
            "/api/auth/password-reset/complete", new { token = second, newPassword = StrongPassword("Third") })));

        await RequestAsync(client, user.Email);
        var expired = await TokenAsync(user.Email, 3);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(31);
        Assert.Equal("reset_link_invalid", await ProblemTitleAsync(await client.PostAsJsonAsync(
            "/api/auth/password-reset/complete", new { token = expired, newPassword = StrongPassword("Expired") })));
    }

    [DatabaseFact]
    public async Task Self_service_uses_the_strictest_active_membership_setting()
    {
        var shortTenant = await AddTenantAsync("short");
        var longTenant = await AddTenantAsync("long");
        await SetAsync(shortTenant, PasswordSecuritySettings.ResetLinkMinutesKey, "10");
        await SetAsync(longTenant, PasswordSecuritySettings.ResetLinkMinutesKey, "90");
        var user = await AddUserAsync("strictest", [shortTenant, longTenant]);
        using var client = fixture.Client();

        await RequestAsync(client, user.Email);
        await WaitForMailAsync(user.Email, 1);
        using var scope = fixture.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().PasswordResetTokens
            .SingleAsync(token => token.UserId == user.Id);
        Assert.Equal(TimeSpan.FromMinutes(10), row.ExpiresAt - row.CreatedAt);
        Assert.Contains("Yojana", fixture.Mail.AttemptsFor(user.Email).Single().Mail.TextBody);
    }

    [DatabaseFact]
    public async Task Completion_enforces_policy_signs_in_revokes_another_session_clears_throttle_and_sends_notice()
    {
        var tenant = await AddTenantAsync("complete");
        var user = await AddUserAsync("complete", [tenant]);
        using var oldSession = fixture.Client();
        (await oldSession.PostAsJsonAsync("/api/auth/sign-in", new { email = user.Email, password = InitialPassword })).EnsureSuccessStatusCode();
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SignInThrottleService>()
                .RegisterAccountFailureAsync(user.Email, CancellationToken.None);
        using var client = fixture.Client();
        await RequestAsync(client, user.Email);
        var token = await TokenAsync(user.Email, 1);
        var weak = await client.PostAsJsonAsync("/api/auth/password-reset/complete", new { token, newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("password_too_short", await weak.Content.ReadAsStringAsync());

        var completed = await client.PostAsJsonAsync("/api/auth/password-reset/complete",
            new { token, newPassword = StrongPassword("Completed") });
        completed.EnsureSuccessStatusCode();
        Assert.False((await completed.Content.ReadFromJsonAsync<SessionView>())!.PasswordChangeRequired);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldSession.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        await WaitForMailKindAsync(user.Email, MailKind.PasswordChanged);
        using var check = fixture.Services.CreateScope();
        Assert.False(await AccountThrottleExistsAsync(check, user.Email));
        var db = check.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var storedHash = await db.Users.Where(row => row.Id == user.Id).Select(row => row.PasswordHash).SingleAsync();
        var auditAndDelivery = JsonSerializer.Serialize(new
        {
            Events = await db.SecurityEvents.Where(row => row.UserId == user.Id).ToListAsync(),
            Mail = await db.MailMessages.Where(row => row.UserId == user.Id).ToListAsync(),
        });
        Assert.DoesNotContain(token, auditAndDelivery);
        Assert.DoesNotContain(StrongPassword("Completed"), auditAndDelivery);
        Assert.DoesNotContain(storedHash!, auditAndDelivery);
    }

    [DatabaseFact]
    public async Task Change_password_keeps_this_session_revokes_another_and_wrong_current_counts_for_lockout()
    {
        var tenant = await AddTenantAsync("change");
        var user = await AddUserAsync("change", [tenant]);
        using var first = fixture.Client(); using var second = fixture.Client();
        foreach (var client in new[] { first, second })
            (await client.PostAsJsonAsync("/api/auth/sign-in", new { email = user.Email, password = InitialPassword })).EnsureSuccessStatusCode();

        Assert.Equal("current_password_wrong", await ProblemTitleAsync(await first.PostAsJsonAsync(
            "/api/auth/change-password", new { currentPassword = "wrong", newPassword = StrongPassword("Changed") })));
        using (var scope = fixture.Services.CreateScope())
            Assert.Equal(1, (await AccountRowAsync(scope, user.Email)).FailureCount);

        Assert.Equal(HttpStatusCode.NoContent, (await first.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = InitialPassword, newPassword = StrongPassword("Changed") })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/auth/me")).StatusCode);
        await WaitForMailKindAsync(user.Email, MailKind.PasswordChanged);
    }

    [DatabaseFact]
    public async Task Admin_options_report_each_temporary_password_reason()
    {
        var tenant = await AddTenantAsync("options");
        var other = await AddTenantAsync("options-other");
        var actor = await AddUserAsync("options-admin", [tenant], roles: [YojanaRole.TenantAdministrator]);
        var normal = await AddUserAsync("options-normal", [tenant]);
        var delegated = await AddUserAsync("options-delegated", [tenant], password: null);
        var shared = await AddUserAsync("options-shared", [tenant, other]);
        using var client = fixture.Client(); await SignInAsync(client, actor);

        var off = await client.GetFromJsonAsync<PasswordResetOptions>($"/api/security/users/{normal.Id}/password-reset-options");
        Assert.Equal("tenant_setting_off", off!.TemporaryUnavailableReason);
        var noPassword = await client.GetFromJsonAsync<PasswordResetOptions>($"/api/security/users/{delegated.Id}/password-reset-options");
        Assert.Equal("no_password_sign_in", noPassword!.TemporaryUnavailableReason);
        await SetAsync(tenant, PasswordSecuritySettings.AdminTemporaryPasswordAllowedKey, "true");
        var memberships = await client.GetFromJsonAsync<PasswordResetOptions>($"/api/security/users/{shared.Id}/password-reset-options");
        Assert.Equal("other_memberships", memberships!.TemporaryUnavailableReason);
        using (var scope = fixture.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Memberships.Add(new UserTenantMembership
            {
                UserId = actor.Id, TenantId = other, Roles = [YojanaRole.TenantAdministrator],
            });
            await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SaveChangesAsync();
        }
        var fullyAdministered = await client.GetFromJsonAsync<PasswordResetOptions>($"/api/security/users/{shared.Id}/password-reset-options");
        Assert.True(fullyAdministered!.CanSetTemporary);
        var allowed = await client.GetFromJsonAsync<PasswordResetOptions>($"/api/security/users/{normal.Id}/password-reset-options");
        Assert.True(allowed!.CanSendLink); Assert.True(allowed.CanSetTemporary); Assert.Null(allowed.TemporaryUnavailableReason);
    }

    [DatabaseFact]
    public async Task Admin_link_uses_caller_tenant_sign_off_and_admin_cannot_reset_self()
    {
        var tenant = await AddTenantAsync("admin-link");
        await SetAsync(tenant, TenantMailSettings.SenderNameKey, "Fictional Northstar Service Desk");
        var actor = await AddUserAsync("admin-link-actor", [tenant], roles: [YojanaRole.TenantAdministrator]);
        var target = await AddUserAsync("admin-link-target", [tenant]);
        using var client = fixture.Client(); await SignInAsync(client, actor);
        var response = await client.PostAsJsonAsync($"/api/security/users/{target.Id}/password-reset", new { mode = "link" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var view = (await response.Content.ReadFromJsonAsync<AdminPasswordResetResult>())!;
        Assert.Equal("link", view.Mode); Assert.NotNull(view.MailId); Assert.Null(view.TemporaryPassword);
        await WaitForMailAsync(target.Email, 1);
        Assert.Equal("Fictional Northstar Service Desk", fixture.Mail.AttemptsFor(target.Email).Single().SenderName);
        Assert.Equal("use_change_password", await ProblemTitleAsync(await client.PostAsJsonAsync(
            $"/api/security/users/{actor.Id}/password-reset", new { mode = "link" })));
    }

    [DatabaseFact]
    public async Task Temporary_password_is_shown_once_forces_change_blocks_other_apis_and_expires_after_a_day()
    {
        var tenant = await AddTenantAsync("temporary");
        await SetAsync(tenant, PasswordSecuritySettings.AdminTemporaryPasswordAllowedKey, "true");
        var actor = await AddUserAsync("temporary-actor", [tenant], roles: [YojanaRole.TenantAdministrator]);
        var target = await AddUserAsync("temporary-target", [tenant]);
        using var admin = fixture.Client(); await SignInAsync(admin, actor);
        var reset = await admin.PostAsJsonAsync($"/api/security/users/{target.Id}/password-reset", new { mode = "temporary" });
        reset.EnsureSuccessStatusCode();
        var temporary = (await reset.Content.ReadFromJsonAsync<AdminPasswordResetResult>())!.TemporaryPassword!;
        Assert.Equal(16, temporary.Length);

        using var targetClient = fixture.Client();
        var signIn = await targetClient.PostAsJsonAsync("/api/auth/sign-in", new { email = target.Email, password = temporary });
        signIn.EnsureSuccessStatusCode();
        Assert.True((await signIn.Content.ReadFromJsonAsync<SessionView>())!.PasswordChangeRequired);
        var blocked = await targetClient.GetAsync("/api/security/settings/passwords");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("password_change_required", await ProblemTitleAsync(blocked));
        Assert.Equal(HttpStatusCode.OK, (await targetClient.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await targetClient.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = temporary, newPassword = StrongPassword("Permanent") })).StatusCode);

        var expiring = await AddUserAsync("temporary-expiring", [tenant]);
        var expiringReset = await admin.PostAsJsonAsync($"/api/security/users/{expiring.Id}/password-reset", new { mode = "temporary" });
        var expiringPassword = (await expiringReset.Content.ReadFromJsonAsync<AdminPasswordResetResult>())!.TemporaryPassword!;
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddHours(24).AddMilliseconds(1);
        using var expiredClient = fixture.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await expiredClient.PostAsJsonAsync("/api/auth/sign-in",
            new { email = expiring.Email, password = expiringPassword })).StatusCode);

        using var scope = fixture.Services.CreateScope();
        var serialised = JsonSerializer.Serialize(await scope.ServiceProvider.GetRequiredService<PlatformDbContext>()
            .SecurityEvents.Where(row => row.UserId == target.Id || row.UserId == expiring.Id).ToListAsync());
        Assert.DoesNotContain(temporary, serialised); Assert.DoesNotContain(expiringPassword, serialised);
    }

    [DatabaseFact]
    public async Task Password_settings_are_versioned_validated_and_conflicts_return_409()
    {
        var tenant = await AddTenantAsync("settings");
        var actor = await AddUserAsync("settings-admin", [tenant], roles: [YojanaRole.TenantAdministrator]);
        using var client = fixture.Client(); await SignInAsync(client, actor);
        var initial = (await client.GetFromJsonAsync<PasswordSecuritySettingsView>("/api/security/settings/passwords"))!;
        Assert.Equal(30, initial.ResetLinkMinutes); Assert.False(initial.AdminTemporaryPasswordAllowed);
        var updated = await client.PutAsJsonAsync("/api/security/settings/passwords",
            initial with { ResetLinkMinutes = 45, AdminTemporaryPasswordAllowed = true });
        updated.EnsureSuccessStatusCode();
        Assert.Equal(45, (await updated.Content.ReadFromJsonAsync<PasswordSecuritySettingsView>())!.ResetLinkMinutes);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/security/settings/passwords",
            initial with { ResetLinkMinutes = 60 })).StatusCode);
        var current = (await client.GetFromJsonAsync<PasswordSecuritySettingsView>("/api/security/settings/passwords"))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/security/settings/passwords",
            current with { ResetLinkMinutes = 9 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/security/settings/passwords",
            current with { InvitationValidDays = 31 })).StatusCode);
    }

    [DatabaseFact]
    public async Task Invitation_expiry_is_a_tenant_setting_on_the_sign_in_policy_and_changes_are_audited()
    {
        var tenant = await AddTenantAsync("invite-days");
        var actor = await AddUserAsync("invite-days-admin", [tenant], roles: [YojanaRole.TenantAdministrator]);
        using var client = fixture.Client(); await SignInAsync(client, actor);
        var initial = (await client.GetFromJsonAsync<PasswordSecuritySettingsView>("/api/security/settings/passwords"))!;
        Assert.Equal(InvitationService.DefaultValidDays, initial.InvitationValidDays);

        (await client.PutAsJsonAsync("/api/security/settings/passwords", initial with { InvitationValidDays = 14 }))
            .EnsureSuccessStatusCode();
        var invited = await client.PostAsJsonAsync("/api/security/invitations", new
        {
            email = $"invitee-{Guid.NewGuid():N}@example.invalid", displayName = "Fictional invitee",
            roles = new[] { YojanaRole.ServiceDesk },
        });
        invited.EnsureSuccessStatusCode();
        var created = (await invited.Content.ReadFromJsonAsync<InvitationCreated>())!;
        Assert.Equal(fixture.Clock.UtcNow.AddDays(14), created.ExpiresAt);

        using var scope = fixture.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SecurityEvents
            .Where(row => row.TenantId == tenant && row.Kind == SecurityEventKind.SettingsChanged).ToListAsync();
        Assert.Equal(InvitationService.ValidDaysSetting, Assert.Single(audit).Reason);
        Assert.Equal(actor.Id, audit[0].ActorUserId);
    }

    [DatabaseFact]
    public async Task Company_settings_are_validated_versioned_audited_and_end_every_account_mail()
    {
        var tenant = await AddTenantAsync("company");
        var actor = await AddUserAsync("company-admin", [tenant], roles: [YojanaRole.TenantAdministrator]);
        var member = await AddUserAsync("company-member", [tenant]);
        using var client = fixture.Client(); await SignInAsync(client, actor);
        var initial = (await client.GetFromJsonAsync<CompanyMailSettingsView>("/api/platform/company/mail"))!;
        Assert.Equal("Fictional company company", initial.CompanyName);
        Assert.Equal("", initial.FooterLine);

        Assert.Equal("footer_line_invalid", await ProblemTitleAsync(await client.PutAsJsonAsync(
            "/api/platform/company/mail", initial with { FooterLine = new string('x', 121) })));
        Assert.Equal("reply_to_invalid", await ProblemTitleAsync(await client.PutAsJsonAsync(
            "/api/platform/company/mail", initial with { ReplyTo = "a@example.invalid, b@example.invalid" })));
        var saved = await client.PutAsJsonAsync("/api/platform/company/mail", initial with
        {
            FooterLine = " Service operations · Bengaluru, India ", SenderName = "Fictional Service Desk",
        });
        saved.EnsureSuccessStatusCode();
        Assert.Equal("Service operations · Bengaluru, India",
            (await saved.Content.ReadFromJsonAsync<CompanyMailSettingsView>())!.FooterLine);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/platform/company/mail",
            initial with { FooterLine = "Stale" })).StatusCode);

        using (var scope = fixture.Services.CreateScope())
        {
            var audit = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SecurityEvents
                .Where(row => row.TenantId == tenant && row.Kind == SecurityEventKind.SettingsChanged).ToListAsync();
            Assert.Equal($"{CompanyMailIdentity.FooterLineKey},{TenantMailSettings.SenderNameKey}",
                Assert.Single(audit).Reason);
        }

        // A self-service reset is written before any tenant is selected; the line still comes from the user's company.
        using var anonymous = fixture.Client();
        await RequestAsync(anonymous, member.Email);
        await WaitForMailAsync(member.Email, 1);
        var mail = fixture.Mail.AttemptsFor(member.Email).Single();
        Assert.Contains("Fictional company company · Service operations · Bengaluru, India", mail.Mail.TextBody);
        Assert.Contains("Fictional company company &#183; Service operations &#183; Bengaluru, India<br>Delivered by Yojana",
            mail.Mail.HtmlBody);
        // The mail itself belongs to no tenant until one is selected, so it keeps the service's sender name.
        Assert.Null(mail.SenderName);
    }

    [DatabaseFact]
    public async Task Maintenance_purges_used_or_expired_tokens_only_after_seven_days_in_batches()
    {
        var tenant = await AddTenantAsync("purge-token");
        var user = await AddUserAsync("purge-token", [tenant]);
        var old = fixture.Clock.UtcNow.AddDays(-8);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.PasswordResetTokens.AddRange(
            TokenRow(user.Id, old, old.AddMinutes(30)),
            TokenRow(user.Id, old, fixture.Clock.UtcNow.AddDays(1), old.AddMinutes(1)),
            TokenRow(user.Id, fixture.Clock.UtcNow, fixture.Clock.UtcNow.AddMinutes(-1)),
            TokenRow(user.Id, fixture.Clock.UtcNow, fixture.Clock.UtcNow.AddDays(1)));
        await db.SaveChangesAsync();

        var result = await scope.ServiceProvider.GetRequiredService<SecurityMaintenance>()
            .PurgeAsync(CancellationToken.None);

        Assert.Equal(2, result.TokenRows);
        Assert.Equal(2, await db.PasswordResetTokens.CountAsync(row => row.UserId == user.Id));
    }

    private const string InitialPassword = "Fictional-Initial-Password-2026";
    private static string StrongPassword(string marker) => $"Fictional-{marker}-Password-2026";
    private static string UnknownEmail() => $"unknown-{Guid.NewGuid():N}@example.invalid";

    private static PasswordResetToken TokenRow(Guid userId, DateTime created, DateTime expires,
        DateTime? used = null) => new()
    {
        Id = Guid.CreateVersion7(), UserId = userId, TokenHash = RandomNumberGenerator.GetBytes(32),
        CreatedAt = created, ExpiresAt = expires, UsedAt = used,
    };

    private async Task<TenantId> AddTenantAsync(string prefix)
    {
        var id = new TenantId(Guid.CreateVersion7());
        using var scope = fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Tenants.Add(new Tenant
        {
            Id = id, Code = (prefix + Guid.NewGuid().ToString("N"))[..20],
            Name = $"Fictional {prefix} company", CreatedAt = fixture.Clock.UtcNow,
        });
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SaveChangesAsync();
        return id;
    }

    private async Task<AppUser> AddUserAsync(string prefix, IReadOnlyList<TenantId> tenants, bool active = true,
        string? password = InitialPassword, IReadOnlyList<string>? roles = null)
    {
        var user = new AppUser
        {
            Id = Guid.CreateVersion7(), Email = $"{prefix}-{Guid.NewGuid():N}@example.invalid",
            DisplayName = $"Fictional {prefix} person", PasswordHash = password is null ? null : PasswordHasher.Hash(password),
            IsActive = active, CreatedAt = fixture.Clock.UtcNow,
        };
        foreach (var tenant in tenants) user.Memberships.Add(new UserTenantMembership
        {
            UserId = user.Id, TenantId = tenant, Roles = roles?.ToList() ?? [],
        });
        using var scope = fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Users.Add(user);
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SaveChangesAsync();
        return user;
    }

    private async Task SetAsync(TenantId tenant, string key, string value)
    {
        using var scope = ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ITenantSettings>().SetAsync(key, value, CancellationToken.None);
    }

    private async Task SignInAsync(HttpClient client, AppUser user) =>
        (await client.PostAsJsonAsync("/api/auth/sign-in", new { email = user.Email, password = InitialPassword })).EnsureSuccessStatusCode();

    private static async Task RequestAsync(HttpClient client, string email) =>
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/auth/password-reset/request", new { email })).StatusCode);

    private async Task<string> TokenAsync(string email, int count)
    {
        await WaitForMailAsync(email, count);
        var text = fixture.Mail.AttemptsFor(email).Where(row => row.Mail.Kind == MailKind.PasswordReset).Last().Mail.TextBody;
        return TokenPattern.Match(text).Groups["token"].Value;
    }

    private async Task WaitForMailAsync(string email, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (fixture.Mail.AttemptsFor(email).Count(row => row.Mail.Kind == MailKind.PasswordReset) >= count) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Password reset mail for {email} was not delivered.");
    }

    private async Task WaitForMailKindAsync(string email, string kind)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (fixture.Mail.AttemptsFor(email).Any(row => row.Mail.Kind == kind)) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Mail kind {kind} for {email} was not delivered.");
    }

    private static async Task<string?> ProblemTitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

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

    private static async Task<bool> AccountThrottleExistsAsync(IServiceScope scope, string email)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("account\0" + email));
        return await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SignInThrottles
            .AnyAsync(row => row.KeyKind == "account" && row.KeyHash == hash);
    }
}
