using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Security;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.DatabaseTests;

[Collection("platform host")]
public sealed class UserManagementTests(PlatformHostFixture fixture)
{
    private static readonly SecurityRequestContext Request = new("198.51.100.25", "user-management-tests");

    [DatabaseFact]
    public async Task List_is_keyset_paged_filterable_and_excludes_portal_members()
    {
        var tenant = await AddTenantAsync("users-list");
        var admin = await AddUserAsync(tenant, "Fictional Admin", [YojanaRole.TenantAdministrator]);
        var alice = await AddUserAsync(tenant, "Alice Storage", [YojanaRole.ServiceDesk]);
        var bob = await AddUserAsync(tenant, "Bob Network", [YojanaRole.Technician], deactivated: true);
        await AddUserAsync(tenant, "Portal Person", ["customer_portal"], customerParty: Guid.CreateVersion7());
        fixture.Clock.UtcNow = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        using var scope = ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.SecurityEvents.Add(new SecurityEvent
        {
            Id = Guid.CreateVersion7(), OccurredAt = fixture.Clock.UtcNow.AddHours(-1),
            Kind = SecurityEventKind.SignInSucceeded, UserId = alice.Id, TenantId = tenant, Reason = "test",
        });
        await db.SaveChangesAsync();
        var throttles = scope.ServiceProvider.GetRequiredService<SignInThrottleService>();
        for (var attempt = 0; attempt < 5; attempt++)
            await throttles.RegisterFailureAsync(alice.Email, $"198.51.100.{attempt + 1}", true, default);

        var users = scope.ServiceProvider.GetRequiredService<UserManagementService>();
        var first = await users.PageAsync("all", null, null, null, 1, default);
        Assert.Single(first.Items);
        Assert.NotNull(first.NextCursor);
        var second = await users.PageAsync("all", null, null, first.NextCursor, 1, default);
        Assert.Single(second.Items);
        var all = await users.PageAsync("all", null, null, null, 100, default);
        Assert.Equal([admin.Id, alice.Id, bob.Id], all.Items.Select(row => row.UserId).Order().ToArray());
        var locked = Assert.Single(all.Items, row => row.UserId == alice.Id);
        Assert.Equal("locked", locked.Status);
        Assert.Equal(fixture.Clock.UtcNow.AddHours(-1), locked.LastSignInAt);
        Assert.Single((await users.PageAsync("active", YojanaRole.ServiceDesk, "STOR", null, 10, default)).Items);
        Assert.Single((await users.PageAsync("deactivated", null, "bob.network", null, 10, default)).Items);
        var summary = await users.SummaryAsync(default);
        Assert.Equal((2, 0, 1), (summary.Active, summary.Invited, summary.Deactivated));
    }

    [DatabaseFact]
    public async Task Role_edits_are_versioned_audited_and_preserve_a_last_administrator()
    {
        var tenant = await AddTenantAsync("roles");
        var admin = await AddUserAsync(tenant, "Fictional Role Admin", [YojanaRole.TenantAdministrator]);
        var user = await AddUserAsync(tenant, "Fictional Role User", [YojanaRole.ServiceDesk]);
        using var scope = ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<UserManagementService>();
        var detail = (await service.DetailAsync(user.Id, admin.Id, default))!;
        var oldStamp = user.SecurityStamp;
        var changed = await service.ChangeRolesAsync(user.Id, [YojanaRole.Technician], detail.User.Version!,
            admin.Id, Request, default);
        Assert.Equal([YojanaRole.Technician], changed.Roles);
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.NotEqual(oldStamp, await db.Users.Where(row => row.Id == user.Id).Select(row => row.SecurityStamp).SingleAsync());
        Assert.Contains(await db.SecurityEvents.Where(row => row.UserId == user.Id).ToListAsync(),
            row => row.Kind == SecurityEventKind.RolesChanged && row.ActorUserId == admin.Id &&
                   row.Reason.Contains(YojanaRole.ServiceDesk) && row.Reason.Contains(YojanaRole.Technician));
        await Assert.ThrowsAsync<UserVersionConflictException>(() => service.ChangeRolesAsync(user.Id,
            [YojanaRole.ServiceManager], detail.User.Version!, admin.Id, Request, default));
        await Assert.ThrowsAsync<UserManagementInputException>(() => service.ChangeRolesAsync(user.Id,
            ["unknown_role"], changed.Version!, admin.Id, Request, default));
        var adminDetail = (await service.DetailAsync(admin.Id, admin.Id, default))!;
        await Assert.ThrowsAsync<LastAdministratorException>(() => service.ChangeRolesAsync(admin.Id,
            [YojanaRole.ServiceDesk], adminDetail.User.Version!, admin.Id, Request, default));

        var other = await AddTenantAsync("roles-other");
        using var otherScope = ScopeFor(other);
        Assert.Null(await otherScope.ServiceProvider.GetRequiredService<UserManagementService>()
            .DetailAsync(user.Id, admin.Id, default));
    }

    [DatabaseFact]
    public async Task Deactivation_is_membership_level_rotates_the_stamp_and_can_be_reactivated()
    {
        var tenant = await AddTenantAsync("deactivate");
        var other = await AddTenantAsync("deactivate-other");
        var admin = await AddUserAsync(tenant, "Fictional Deactivation Admin", [YojanaRole.TenantAdministrator]);
        var user = await AddUserAsync(tenant, "Fictional Shared User", [YojanaRole.ServiceDesk]);
        await AddMembershipAsync(user.Id, other, [YojanaRole.Technician]);
        using var scope = ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<UserManagementService>();
        await Assert.ThrowsAsync<CannotDeactivateSelfException>(() => service.DeactivateAsync(admin.Id, "test", admin.Id, Request, default));
        await Assert.ThrowsAsync<LastAdministratorException>(() => service.DeactivateAsync(admin.Id, "test", user.Id, Request, default));
        var oldStamp = user.SecurityStamp;
        await service.DeactivateAsync(user.Id, "left the service desk", admin.Id, Request, default);
        var choices = await scope.ServiceProvider.GetRequiredService<SignInService>().TenantsForAsync(user.Id, default);
        Assert.Single(choices);
        Assert.Equal(other, choices[0].TenantId);
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.NotEqual(oldStamp, await db.Users.Where(row => row.Id == user.Id).Select(row => row.SecurityStamp).SingleAsync());
        Assert.Contains(await db.SecurityEvents.Where(row => row.UserId == user.Id).ToListAsync(),
            row => row.Kind == SecurityEventKind.UserDeactivated && row.ActorUserId == admin.Id);
        await service.ReactivateAsync(user.Id, admin.Id, Request, default);
        Assert.Equal(2, (await scope.ServiceProvider.GetRequiredService<SignInService>().TenantsForAsync(user.Id, default)).Count);
    }

    [DatabaseFact]
    public async Task Invitation_mail_resend_revoke_and_invalid_states_never_disclose_the_token()
    {
        fixture.Clock.UtcNow = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var tenant = await AddTenantAsync("invite");
        var admin = await AddUserAsync(tenant, "Fictional Invitation Admin", [YojanaRole.TenantAdministrator]);
        using var scope = ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ITenantSettings>()
            .SetAsync(InvitationService.ValidDaysSetting, "3", default);
        var invitations = scope.ServiceProvider.GetRequiredService<InvitationService>();
        var email = $"l.fischer-{Guid.NewGuid():N}@asterra.example";
        var created = await invitations.CreateAsync(email, "Lena Fischer", [YojanaRole.CertificateAdministrator],
            admin.Id, Request, default);
        Assert.Equal(fixture.Clock.UtcNow.AddDays(3), created.ExpiresAt);
        var firstMail = await WaitForMailAsync(email, 1);
        var firstToken = Token(firstMail.Mail.TextBody);
        Assert.Contains("Certificate administrator", firstMail.Mail.TextBody);
        Assert.Contains("at the request of Fictional Invitation Admin (tenant administrator)", firstMail.Mail.TextBody);
        var inspected = await invitations.InspectAsync(firstToken, default);
        Assert.NotNull(inspected);
        Assert.StartsWith("l.f…@", inspected!.Email);
        await Assert.ThrowsAsync<InvitationConflictException>(() => invitations.CreateAsync(email, "Lena Fischer",
            [YojanaRole.Technician], admin.Id, Request, default));

        await invitations.ResendAsync(created.InvitationId, admin.Id, Request, default);
        var secondToken = Token((await WaitForMailAsync(email, 2)).Mail.TextBody);
        Assert.NotEqual(firstToken, secondToken);
        Assert.Null(await invitations.InspectAsync(firstToken, default));
        Assert.NotNull(await invitations.InspectAsync(secondToken, default));
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.DoesNotContain(secondToken, string.Join('|', await db.SecurityEvents.Select(row => row.Reason).ToListAsync()));
        Assert.DoesNotContain(secondToken, string.Join('|', await db.MailMessages.Select(row => row.Subject).ToListAsync()));
        await invitations.RevokeAsync(created.InvitationId, admin.Id, default);
        Assert.Null(await invitations.InspectAsync(secondToken, default));
        Assert.Null(await invitations.InspectAsync("not-a-token", default));

        var member = await AddUserAsync(tenant, "Existing Member", [YojanaRole.ServiceDesk]);
        await Assert.ThrowsAsync<InvitationConflictException>(() => invitations.CreateAsync(member.Email,
            member.DisplayName, [YojanaRole.Technician], admin.Id, Request, default));
    }

    [DatabaseFact]
    public async Task Acceptance_enforces_password_policy_is_single_use_and_existing_accounts_must_match()
    {
        fixture.Clock.UtcNow = new DateTime(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc);
        var tenant = await AddTenantAsync("accept");
        var admin = await AddUserAsync(tenant, "Fictional Acceptance Admin", [YojanaRole.TenantAdministrator]);
        using var scope = ScopeFor(tenant);
        var invitations = scope.ServiceProvider.GetRequiredService<InvitationService>();
        var email = $"new-{Guid.NewGuid():N}@example.invalid";
        await invitations.CreateAsync(email, "Fictional New User", [YojanaRole.ServiceDesk], admin.Id, Request, default);
        var token = Token((await WaitForMailAsync(email, 1)).Mail.TextBody);
        await Assert.ThrowsAsync<InvitationPasswordException>(() => invitations.AcceptNewAsync(token, "short", Request, default));
        var accepted = await invitations.AcceptNewAsync(token, "Fictional-Strong-Password-2026", Request, default);
        Assert.Equal(email, accepted.User.Email);
        Assert.Single(accepted.Tenants);
        Assert.True(PasswordHasher.Verify("Fictional-Strong-Password-2026", accepted.User.PasswordHash));
        await Assert.ThrowsAsync<InvitationInvalidException>(() => invitations.AcceptNewAsync(token,
            "Another-Strong-Password-2026", Request, default));

        var existing = await AddUserAsync(tenant, "Fictional Existing Account", [YojanaRole.ServiceDesk],
            deactivated: true);
        var existingEmail = existing.Email;
        var created = await invitations.CreateAsync(existingEmail, existing.DisplayName, [YojanaRole.Technician],
            admin.Id, Request, default);
        var existingToken = Token((await WaitForMailAsync(existingEmail, 1)).Mail.TextBody);
        await Assert.ThrowsAsync<InvitationAccountExistsException>(() => invitations.AcceptNewAsync(existingToken,
            "Another-Strong-Password-2026", Request, default));
        var wrong = await AddUserAsync(null, "Fictional Wrong Account", []);
        await Assert.ThrowsAsync<InvitationOtherAccountException>(() => invitations.AcceptExistingAsync(existingToken,
            wrong.Id, null, Request, default));
        var joined = await invitations.AcceptExistingAsync(existingToken, existing.Id, null, Request, default);
        Assert.Contains(joined.Tenants, choice => choice.TenantId == tenant);
        Assert.Null((await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Memberships
            .SingleAsync(row => row.UserId == existing.Id && row.TenantId == tenant)).DeactivatedAt);

        var expiredEmail = $"expired-{Guid.NewGuid():N}@example.invalid";
        await invitations.CreateAsync(expiredEmail, "Fictional Expired User", [YojanaRole.Technician], admin.Id, Request, default);
        var expiredToken = Token((await WaitForMailAsync(expiredEmail, 1)).Mail.TextBody);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(8);
        Assert.Null(await invitations.InspectAsync(expiredToken, default));
    }

    private async Task<TenantId> AddTenantAsync(string prefix)
    {
        var id = new TenantId(Guid.CreateVersion7());
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Tenants.Add(new Tenant { Id = id, Code = (prefix + Guid.NewGuid().ToString("N"))[..20],
            Name = $"Fictional {prefix} company", CreatedAt = fixture.Clock.UtcNow });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<AppUser> AddUserAsync(TenantId? tenant, string name, IReadOnlyList<string> roles,
        bool deactivated = false, Guid? customerParty = null)
    {
        var user = new AppUser { Id = Guid.CreateVersion7(), Email = $"{name.Replace(' ', '.').ToLowerInvariant()}-{Guid.NewGuid():N}@example.invalid",
            DisplayName = name, PasswordHash = PasswordHasher.Hash("Fictional-Test-Password-2026"),
            CreatedAt = fixture.Clock.UtcNow };
        if (tenant is { } tenantId) user.Memberships.Add(new UserTenantMembership { UserId = user.Id,
            TenantId = tenantId, Roles = roles.ToList(), CustomerPartyRef = customerParty,
            DeactivatedAt = deactivated ? fixture.Clock.UtcNow : null });
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task AddMembershipAsync(Guid userId, TenantId tenant, IReadOnlyList<string> roles)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Memberships.Add(new UserTenantMembership { UserId = userId, TenantId = tenant, Roles = roles.ToList() });
        await db.SaveChangesAsync();
    }

    private IServiceScope ScopeFor(TenantId tenant)
    {
        var scope = fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        return scope;
    }

    private async Task<Yojana.Platform.Mail.PreparedMail> WaitForMailAsync(string email, int count)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            var attempts = fixture.Mail.AttemptsFor(email);
            if (attempts.Count >= count) return attempts[count - 1];
            await Task.Delay(50);
        }
        throw new TimeoutException("Invitation mail was not delivered to the scripted transport.");
    }

    private static string Token(string text) => Regex.Match(text, @"#t=([A-Za-z0-9_-]+)").Groups[1].Value;
}
