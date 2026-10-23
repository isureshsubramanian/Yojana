using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.DatabaseTests;

[Collection("platform host")]
public sealed class UserDirectoryTests(PlatformHostFixture fixture)
{
    private async Task<(TenantId Tenant, AppUser Member, AppUser Outsider)> ArrangeAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenant = new TenantId(Guid.NewGuid());
        var other = new TenantId(Guid.NewGuid());
        foreach (var id in new[] { tenant, other })
        {
            db.Tenants.Add(new Tenant { Id = id, Code = ("ud" + Guid.NewGuid().ToString("N"))[..20], Name = "directory test", CreatedAt = DateTime.UtcNow });
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var member = new AppUser { Id = Guid.CreateVersion7(), Email = $"member-{suffix}@example.test", DisplayName = "Meena Member", CreatedAt = DateTime.UtcNow, IsActive = false };
        var outsider = new AppUser { Id = Guid.CreateVersion7(), Email = $"outsider-{suffix}@example.test", DisplayName = "Oscar Outsider", CreatedAt = DateTime.UtcNow };
        db.Users.AddRange(member, outsider);
        db.Memberships.Add(new UserTenantMembership { UserId = member.Id, TenantId = tenant, Roles = ["service_desk"] });
        db.Memberships.Add(new UserTenantMembership { UserId = outsider.Id, TenantId = other, Roles = ["service_desk"] });
        await db.SaveChangesAsync();
        return (tenant, member, outsider);
    }

    private IUserDirectory DirectoryFor(IServiceScope scope, TenantId tenant)
    {
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        return scope.ServiceProvider.GetRequiredService<IUserDirectory>();
    }

    [DatabaseFact]
    public async Task Resolves_ids_and_emails_of_tenant_members_and_leaves_everything_else_out()
    {
        var (tenant, member, outsider) = await ArrangeAsync();
        using var scope = fixture.Services.CreateScope();
        var directory = DirectoryFor(scope, tenant);
        var byId = member.Id.ToString("D");
        var byEmail = member.Email.ToUpperInvariant();

        var names = await directory.DisplayNamesAsync([byId, byEmail, outsider.Id.ToString("D"), outsider.Email, "not-a-user", Guid.NewGuid().ToString("D"), ""], CancellationToken.None);

        Assert.Equal(2, names.Count);
        Assert.Equal("Meena Member", names[byId]);
        Assert.Equal("Meena Member", names[byEmail]);
    }

    [DatabaseFact]
    public async Task Labels_automatic_actors_without_a_lookup()
    {
        var (tenant, member, _) = await ArrangeAsync();
        using var scope = fixture.Services.CreateScope();
        var directory = DirectoryFor(scope, tenant);
        var byId = member.Id.ToString("D");

        var names = await directory.DisplayNamesAsync(["system:certificates", "system:replacement-overdue",
            "system:entitlement-requirement", "system:", byId], CancellationToken.None);

        Assert.Equal(4, names.Count);
        Assert.Equal("Certificates (automatic)", names["system:certificates"]);
        Assert.Equal("Replacement overdue (automatic)", names["system:replacement-overdue"]);
        Assert.Equal("Entitlement requirement (automatic)", names["system:entitlement-requirement"]);
        Assert.Equal("Meena Member", names[byId]);
    }

    [DatabaseFact]
    public async Task Refuses_more_references_than_one_call_resolves()
    {
        var (tenant, _, _) = await ArrangeAsync();
        using var scope = fixture.Services.CreateScope();
        var directory = DirectoryFor(scope, tenant);
        var references = Enumerable.Range(0, IUserDirectory.MaxReferences + 1).Select(i => $"user{i}@example.test").ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => directory.DisplayNamesAsync(references, CancellationToken.None));
    }
}
