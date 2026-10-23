using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Settings;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Settings;
using Yojana.Platform.Tenancy;
using Xunit;

namespace Yojana.Platform.DatabaseTests;

[Collection("platform host")]
public sealed class UsersSettingsSummaryContributorTests(PlatformHostFixture fixture)
{
    [DatabaseFact]
    public async Task Counts_active_staff_distinct_roles_and_open_invitations_for_only_the_selected_tenant()
    {
        var tenant = new TenantId(Guid.CreateVersion7());
        var other = new TenantId(Guid.CreateVersion7());
        var now = fixture.Clock.UtcNow;
        var administrator = User(tenant, "Summary Administrator", [YojanaRole.TenantAdministrator], now);
        var technician = User(tenant, "Summary Technician", [YojanaRole.Technician, YojanaRole.ServiceDesk], now);
        var inactiveIdentity = User(tenant, "Inactive Identity", [YojanaRole.ServiceManager], now);
        inactiveIdentity.IsActive = false;
        var deactivated = User(tenant, "Deactivated Staff", [YojanaRole.ServiceManager], now);
        deactivated.Memberships.Single().DeactivatedAt = now;
        var portal = User(tenant, "Portal Contact", ["customer_portal"], now);
        portal.Memberships.Single().CustomerPartyRef = Guid.CreateVersion7();
        var elsewhere = User(other, "Other Tenant Staff", [YojanaRole.ServiceManager], now);

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Tenants.AddRange(
                new Tenant { Id = tenant, Code = $"SUM{Guid.NewGuid():N}"[..20], Name = "Fictional Summary Systems", CreatedAt = now },
                new Tenant { Id = other, Code = $"OTH{Guid.NewGuid():N}"[..20], Name = "Fictional Other Systems", CreatedAt = now });
            db.Users.AddRange(administrator, technician, inactiveIdentity, deactivated, portal, elsewhere);
            db.UserInvitations.AddRange(
                Invitation(tenant, administrator.Id, now.AddDays(1)),
                Invitation(tenant, administrator.Id, now.AddDays(-1)),
                Invitation(tenant, administrator.Id, now.AddDays(1), accepted: now),
                Invitation(tenant, administrator.Id, now.AddDays(1), revoked: now),
                Invitation(other, elsewhere.Id, now.AddDays(1)));
            await db.SaveChangesAsync();
        }

        using var readScope = ScopeFor(tenant);
        var contributor = readScope.ServiceProvider.GetServices<ISettingsSummaryContributor>()
            .Single(candidate => candidate.Keys.Any(key => key.Key == UsersSettingsSummaryContributor.Key));
        var line = Assert.Single(await contributor.ReadAsync(
            new HashSet<string>([UsersSettingsSummaryContributor.Key]), default));

        Assert.Equal(2, line.Counts["staff"]);
        Assert.Equal(3, line.Counts["roles"]);
        Assert.Equal(1, line.Counts["invitationsOpen"]);
    }

    private static AppUser User(TenantId tenant, string name, List<string> roles, DateTime at)
    {
        var user = new AppUser
        {
            Id = Guid.CreateVersion7(), Email = $"{Guid.NewGuid():N}@example.invalid", DisplayName = name,
            CreatedAt = at,
        };
        user.Memberships.Add(new UserTenantMembership
        {
            UserId = user.Id, TenantId = tenant, Roles = roles, CreatedAt = at,
        });
        return user;
    }

    private static UserInvitation Invitation(TenantId tenant, Guid creator, DateTime expires,
        DateTime? accepted = null, DateTime? revoked = null) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenant, Email = $"{Guid.NewGuid():N}@example.invalid",
        DisplayName = "Fictional Invited Staff", Roles = [YojanaRole.Technician],
        TokenHash = RandomNumberGenerator.GetBytes(32), CreatedAt = expires.AddDays(-2), CreatedByUserId = creator,
        ExpiresAt = expires, AcceptedAt = accepted, AcceptedUserId = accepted is null ? null : creator,
        RevokedAt = revoked, RevokedByUserId = revoked is null ? null : creator,
    };

    private IServiceScope ScopeFor(TenantId tenant)
    {
        var scope = fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        return scope;
    }
}
