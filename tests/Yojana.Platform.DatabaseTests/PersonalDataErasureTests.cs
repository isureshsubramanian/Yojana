using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts;
using Yojana.Platform.Contracts.Privacy;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Privacy;
using Yojana.Platform.Security;
using Yojana.Platform.Tenancy;
using Wolverine;
using Xunit;

namespace Yojana.Platform.DatabaseTests;

[Collection("platform host")]
public sealed class PersonalDataErasureTests(PlatformHostFixture fixture)
{
    [DatabaseFact]
    public async Task Party_natural_person_and_typed_confirmation_guards_are_coded()
    {
        var tenant = await NewTenantAsync("erase-confirm");
        using var scope = ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<PersonalDataErasureService>();
        var naturalPerson = await Assert.ThrowsAsync<CommandRefusedException>(() => service.PreviewAsync(
            new("Party", Guid.CreateVersion7(), "DSR-2026-1001", "verified request", false), default));
        Assert.Equal("erasure_not_natural_person", naturalPerson.Rule);
        var mismatch = await Assert.ThrowsAsync<CommandRefusedException>(() => service.ConfirmAsync(
            new("Party", Guid.CreateVersion7(), "DSR-2026-1002", "DSR-2026-1003", "verified request", true, []),
            Guid.CreateVersion7(), scope.ServiceProvider.GetRequiredService<IMessageBus>(), default));
        Assert.Equal("erasure_confirmation_mismatch", mismatch.Rule);
    }

    [DatabaseFact]
    public async Task Active_user_and_last_administrator_refusals_are_coded()
    {
        var tenant = await NewTenantAsync("erase-guards");
        var administrator = await AddUserAsync(tenant, "Fictional Privacy Administrator",
            [YojanaRole.TenantAdministrator]);
        var staff = await AddUserAsync(tenant, "Fictional Privacy Technician", [YojanaRole.Technician]);

        using var scope = ScopeFor(tenant);
        var holder = scope.ServiceProvider.GetRequiredService<PlatformPersonalDataHolder>();
        var last = await Assert.ThrowsAsync<CommandRefusedException>(() => holder.PreviewAsync(
            Subject(administrator.Id), default));
        Assert.Equal("erasure_last_administrator", last.Rule);
        var active = await Assert.ThrowsAsync<CommandRefusedException>(() => holder.PreviewAsync(
            Subject(staff.Id), default));
        Assert.Equal("erasure_user_active", active.Rule);
    }

    [DatabaseFact]
    public async Task Deactivated_user_erasure_removes_secrets_and_keeps_name_resolution_as_the_token()
    {
        var tenant = await NewTenantAsync("erase-user");
        await AddUserAsync(tenant, "Fictional Continuing Administrator", [YojanaRole.TenantAdministrator]);
        var user = await AddUserAsync(tenant, "Fictional Former Technician", [YojanaRole.Technician], true);
        var oldEmail = user.Email;
        using (var seed = ScopeFor(tenant))
        {
            var db = seed.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.PasswordResetTokens.Add(new PasswordResetToken
            {
                Id = Guid.CreateVersion7(), UserId = user.Id, TokenHash = [1, 2, 3],
                CreatedAt = fixture.Clock.UtcNow, ExpiresAt = fixture.Clock.UtcNow.AddHours(1),
            });
            db.UserInvitations.Add(new UserInvitation
            {
                Id = Guid.CreateVersion7(), TenantId = tenant, Email = oldEmail,
                DisplayName = user.DisplayName, Roles = [YojanaRole.Technician], TokenHash = [4, 5, 6],
                CreatedAt = fixture.Clock.UtcNow, CreatedByUserId = user.Id,
                ExpiresAt = fixture.Clock.UtcNow.AddDays(1), AcceptedUserId = user.Id,
            });
            db.SecurityEvents.Add(new SecurityEvent
            {
                Id = Guid.CreateVersion7(), TenantId = tenant, UserId = user.Id,
                Kind = SecurityEventKind.SignInSucceeded, OccurredAt = fixture.Clock.UtcNow,
                ClientAddress = "198.51.100.42", UserAgent = "erasure-test-agent", Reason = "test",
            });
            db.MailMessages.Add(new MailMessage
            {
                Id = Guid.CreateVersion7(), TenantId = tenant, UserId = user.Id,
                QueuedAt = fixture.Clock.UtcNow, Kind = "test", Recipient = oldEmail,
                Subject = $"Account notice for {user.DisplayName}", Status = MailStatus.Sent, Transport = "test",
            });
            await db.SaveChangesAsync();
        }

        var subject = Subject(user.Id, user.DisplayName, oldEmail);
        using (var erase = ScopeFor(tenant))
        {
            var holder = erase.ServiceProvider.GetRequiredService<PlatformPersonalDataHolder>();
            await holder.EraseAsync(subject, Guid.CreateVersion7(), default);
        }

        using var verify = ScopeFor(tenant);
        var dbVerify = verify.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var stored = await dbVerify.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.Equal(subject.Token, stored.DisplayName);
        Assert.Equal(subject.Email, stored.Email);
        Assert.Null(stored.PasswordHash);
        Assert.False(stored.IsActive);
        Assert.False(await dbVerify.PasswordResetTokens.AnyAsync(x => x.UserId == user.Id));
        Assert.False(await dbVerify.UserInvitations.AnyAsync(x => x.AcceptedUserId == user.Id));
        var security = await dbVerify.SecurityEvents.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
        Assert.Null(security.ClientAddress);
        Assert.Null(security.UserAgent);
        var mail = await dbVerify.MailMessages.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
        Assert.Equal(subject.Email, mail.Recipient);
        Assert.DoesNotContain("Fictional Former Technician", mail.Subject, StringComparison.Ordinal);
        var names = await verify.ServiceProvider.GetRequiredService<IUserDirectory>()
            .DisplayNamesAsync([user.Id.ToString("D")], default);
        Assert.Equal(subject.Token, names[user.Id.ToString("D")]);
    }

    private async Task<TenantId> NewTenantAsync(string prefix)
    {
        var tenant = new TenantId(Guid.CreateVersion7());
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Tenants.Add(new Tenant
        {
            Id = tenant, Code = prefix + Guid.NewGuid().ToString("N")[..6],
            Name = "Fictional Privacy Company", CreatedAt = fixture.Clock.UtcNow,
        });
        await db.SaveChangesAsync();
        return tenant;
    }

    private async Task<AppUser> AddUserAsync(TenantId tenant, string name, IReadOnlyList<string> roles,
        bool deactivated = false)
    {
        var user = new AppUser
        {
            Id = Guid.CreateVersion7(), Email = $"{Guid.NewGuid():N}@example.invalid", DisplayName = name,
            PasswordHash = "test-hash", CreatedAt = fixture.Clock.UtcNow,
        };
        user.Memberships.Add(new UserTenantMembership
        {
            UserId = user.Id, TenantId = tenant, Roles = roles.ToList(), CreatedAt = fixture.Clock.UtcNow,
            DeactivatedAt = deactivated ? fixture.Clock.UtcNow : null,
        });
        using var scope = ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private IServiceScope ScopeFor(TenantId tenant)
    {
        var scope = fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        return scope;
    }

    private static ErasureSubject Subject(Guid userId, params string[] known) =>
        new(ErasureSubjectKind.User, userId, known, []);
}
