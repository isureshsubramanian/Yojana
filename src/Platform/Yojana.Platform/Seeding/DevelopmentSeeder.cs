using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Security;

namespace Yojana.Platform.Seeding;

/// <summary>
/// Seeds the development tenant DEV with one user per ADR role plus an administrator holding every role.
/// Idempotent. Never run against a customer database: the password is shared and printed in the README.
/// </summary>
public static class DevelopmentSeeder
{
    public static readonly TenantId DevTenantId = TenantId.Parse("11111111-1111-1111-1111-111111111111");
    public const string DevTenantCode = "DEV";
    public const string EmailDomain = "dev.yojana.local";

    public static async Task SeedAsync(IServiceProvider services, string password, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == DevTenantId, cancellationToken);
        if (tenant is null)
        {
            tenant = new Tenant { Id = DevTenantId, Code = DevTenantCode, Name = "Development tenant", CreatedAt = clock.UtcNow };
            db.Tenants.Add(tenant);
        }

        var hash = PasswordHasher.Hash(password);
        var users = YojanaRole.All.Select(r => (Email: $"{r}@{EmailDomain}", Name: r.Replace('_', ' '), Roles: new List<string> { r }))
            .Append((Email: $"admin@{EmailDomain}", Name: "Development administrator", Roles: YojanaRole.All.ToList()));
        foreach (var (email, name, roles) in users)
        {
            var user = await db.Users.Include(u => u.Memberships).FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
            if (user is null)
            {
                user = new AppUser { Id = Guid.CreateVersion7(), Email = email, DisplayName = name, PasswordHash = hash, CreatedAt = clock.UtcNow };
                db.Users.Add(user);
            }

            var membership = user.Memberships.FirstOrDefault(m => m.TenantId == DevTenantId);
            if (membership is null)
            {
                user.Memberships.Add(new UserTenantMembership { UserId = user.Id, TenantId = DevTenantId, Roles = roles });
            }
            else
            {
                membership.Roles = roles;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
