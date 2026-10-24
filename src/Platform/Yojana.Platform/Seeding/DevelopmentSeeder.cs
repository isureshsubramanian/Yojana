using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Security;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Seeding;

/// <summary>
/// Seeds the development tenant DEV with one user per ADR role plus an administrator holding every role.
/// Idempotent; re-seeding resets these accounts to the given password. Never run against a customer database: the password is shared and printed in the README.
/// </summary>
public static class DevelopmentSeeder
{
    public static readonly TenantId DevTenantId = TenantId.Parse("11111111-1111-1111-1111-111111111111");
    public const string DevTenantCode = "DEV";
    public const string EmailDomain = "dev.yojana.local";

    public static string AdministratorEmail(string emailDomain) => $"admin@{emailDomain}";

    public static async Task SeedAsync(IServiceProvider services, string password, CancellationToken cancellationToken)
    {
        await SeedTenantUsersAsync(services, new TenantUserSeed(DevTenantId, DevTenantCode, "Development tenant",
            EmailDomain, "Development administrator"), password, cancellationToken);
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(DevTenantId);
        await scope.ServiceProvider.GetRequiredService<ITenantSettings>().SetAsync(
            IActingLegalEntity.DefaultSettingKey, "00000000-0000-0000-0000-000000000001", cancellationToken);
    }

    /// <summary>
    /// Creates one tenant's platform identity boundary and its role accounts. Feature tools reuse this seam so the
    /// role catalogue, password hashing and membership rules cannot drift from development sign-in.
    /// </summary>
    public static async Task SeedTenantUsersAsync(IServiceProvider services, TenantUserSeed seed, string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("A seed password is required.", nameof(password));
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == seed.TenantId, cancellationToken);
        if (tenant is null)
        {
            tenant = new Tenant { Id = seed.TenantId, Code = seed.TenantCode, Name = seed.TenantName, CreatedAt = clock.UtcNow };
            db.Tenants.Add(tenant);
        }

        var hash = PasswordHasher.Hash(password);
        var resetUserIds = new List<Guid>();
        var users = YojanaRole.All.Select(r => (Email: $"{r}@{seed.EmailDomain}", Name: r.Replace('_', ' '), Roles: new List<string> { r }))
            .Append((Email: AdministratorEmail(seed.EmailDomain), Name: seed.AdministratorName, Roles: YojanaRole.All.ToList()));
        foreach (var (email, name, roles) in users)
        {
            var user = await db.Users.Include(u => u.Memberships).FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
            if (user is null)
            {
                user = new AppUser { Id = Guid.CreateVersion7(), Email = email, DisplayName = name, PasswordHash = hash, CreatedAt = clock.UtcNow };
                db.Users.Add(user);
            }
            else if (!PasswordHasher.Verify(password, user.PasswordHash))
            {
                // Seeded accounts follow the seed password, so changing it and re-seeding takes effect.
                user.PasswordHash = hash;
                resetUserIds.Add(user.Id);
            }

            var membership = user.Memberships.FirstOrDefault(m => m.TenantId == seed.TenantId);
            if (membership is null)
            {
                user.Memberships.Add(new UserTenantMembership { UserId = user.Id, TenantId = seed.TenantId, Roles = roles });
            }
            else
            {
                membership.Roles = roles;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (resetUserIds.Count > 0)
        {
            var stamps = scope.ServiceProvider.GetRequiredService<ISecurityStampService>();
            foreach (var userId in resetUserIds)
            {
                await stamps.RotateSecurityStampAsync(userId, "development_seed_password_reset", cancellationToken);
            }
        }
        // This is the platform's current tenant-creation seam. Schedule after the tenant commits so Quartz never
        // points at a tenant that rolled back; the startup reconciliation remains the recovery path after a crash.
        await services.GetRequiredService<RecurringTenantJobProvisioner>()
            .EnsureScheduledAsync(seed.TenantId, cancellationToken);
    }
}

public sealed record TenantUserSeed(
    TenantId TenantId,
    string TenantCode,
    string TenantName,
    string EmailDomain,
    string AdministratorName);
