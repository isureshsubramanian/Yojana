using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts;
using Yojana.Platform.Contracts.Privacy;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;

namespace Yojana.Platform.Privacy;

public sealed class PlatformPersonalDataHolder(PlatformDbContext db, ITenantContext tenant) : IPersonalDataHolder
{
    private const int BatchSize = 500;
    public string Store => "platform";

    public async Task<PersonalDataPreview> PreviewAsync(ErasureSubject subject,
        CancellationToken cancellationToken)
    {
        if (subject.Kind != ErasureSubjectKind.User) return Empty();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == subject.SubjectId,
            cancellationToken);
        if (user is null) return Empty();

        var membership = await db.Memberships.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.UserId == subject.SubjectId, cancellationToken);
        if (membership is null) return Empty();
        if (membership?.Roles.Contains(YojanaRole.TenantAdministrator) == true)
        {
            var anotherAdministrator = await db.Memberships.AsNoTracking().AnyAsync(x =>
                x.TenantId == tenant.TenantId && x.UserId != subject.SubjectId && x.DeactivatedAt == null
                && x.User!.IsActive && x.Roles.Contains(YojanaRole.TenantAdministrator), cancellationToken);
            if (!anotherAdministrator)
                throw new CommandRefusedException("erasure_last_administrator",
                    "The tenant's last administrator cannot be erased.", "appoint another tenant administrator first");
        }
        if (await db.Memberships.AsNoTracking().AnyAsync(x => x.UserId == subject.SubjectId
                && x.DeactivatedAt == null, cancellationToken))
            throw new CommandRefusedException("erasure_user_active",
                "The user must be deactivated before personal data can be erased.",
                "deactivate the user in every company first");

        var secrets = await db.UserAuthenticators.CountAsync(x => x.UserId == subject.SubjectId, cancellationToken)
            + await db.UserRecoveryCodes.CountAsync(x => x.UserId == subject.SubjectId, cancellationToken)
            + await db.UserPasskeys.CountAsync(x => x.UserId == subject.SubjectId, cancellationToken)
            + await db.PasskeyCeremonies.CountAsync(x => x.UserId == subject.SubjectId, cancellationToken)
            + await db.PasswordResetTokens.CountAsync(x => x.UserId == subject.SubjectId, cancellationToken);
        var invitations = await db.UserInvitations.CountAsync(x => x.TenantId == tenant.TenantId
            && (x.AcceptedUserId == subject.SubjectId || x.Email == user.Email), cancellationToken);
        var security = await db.SecurityEvents.CountAsync(x => x.TenantId == tenant.TenantId
            && (x.UserId == subject.SubjectId || x.ActorUserId == subject.SubjectId), cancellationToken);
        var mail = await db.MailMessages.CountAsync(x => x.TenantId == tenant.TenantId
            && x.UserId == subject.SubjectId, cancellationToken);
        return new PersonalDataPreview([
            new("user", 1, "name and e-mail replaced"),
            new("secrets", secrets, "deleted"),
            new("invitations", invitations, "deleted"),
            new("security_events", security, "client address and user agent cleared"),
            new("staff_mail", mail, "recipient replaced"),
        ], [], [user.DisplayName, user.Email]);
    }

    public async Task<PersonalDataEraseResult> EraseAsync(ErasureSubject subject, Guid erasureId,
        CancellationToken cancellationToken)
    {
        if (subject.Kind != ErasureSubjectKind.User) return new([]);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == subject.SubjectId, cancellationToken);
        if (user is null) return new([]);
        var oldEmail = user.Email;
        var userChanged = user.DisplayName != subject.Token || user.Email != subject.Email
            || user.PasswordHash is not null || user.IsActive || user.MustChangePassword
            || user.TemporaryPasswordExpiresAt is not null;
        user.DisplayName = subject.Token;
        user.Email = subject.Email;
        user.PasswordHash = null;
        user.IsActive = false;
        user.MustChangePassword = false;
        user.TemporaryPasswordExpiresAt = null;
        user.SecurityStamp = Guid.NewGuid();

        var deletedSecrets = await db.UserAuthenticators.Where(x => x.UserId == subject.SubjectId)
                .ExecuteDeleteAsync(cancellationToken)
            + await db.UserRecoveryCodes.Where(x => x.UserId == subject.SubjectId).ExecuteDeleteAsync(cancellationToken)
            + await db.UserPasskeys.Where(x => x.UserId == subject.SubjectId).ExecuteDeleteAsync(cancellationToken)
            + await db.PasskeyCeremonies.Where(x => x.UserId == subject.SubjectId).ExecuteDeleteAsync(cancellationToken)
            + await db.PasswordResetTokens.Where(x => x.UserId == subject.SubjectId).ExecuteDeleteAsync(cancellationToken);
        var invitations = await db.UserInvitations.Where(x => x.TenantId == tenant.TenantId
                && (x.AcceptedUserId == subject.SubjectId || x.Email == oldEmail))
            .ExecuteDeleteAsync(cancellationToken);
        var security = await db.SecurityEvents.Where(x => x.TenantId == tenant.TenantId
                && (x.UserId == subject.SubjectId || x.ActorUserId == subject.SubjectId))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.ClientAddress, (string?)null)
                .SetProperty(x => x.UserAgent, (string?)null), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var messageCount = 0;
        var cursor = Guid.Empty;
        while (true)
        {
            var messages = await db.MailMessages.Where(x => x.TenantId == tenant.TenantId
                    && x.UserId == subject.SubjectId && x.Id.CompareTo(cursor) > 0)
                .OrderBy(x => x.Id).Take(BatchSize).ToListAsync(cancellationToken);
            if (messages.Count == 0) break;
            foreach (var message in messages)
            {
                message.Recipient = subject.Email;
                foreach (var known in subject.KnownValues.Where(x => !string.IsNullOrWhiteSpace(x))
                             .OrderByDescending(x => x.Length))
                    message.Subject = message.Subject.Replace(known, subject.Token, StringComparison.Ordinal);
            }
            cursor = messages[^1].Id;
            messageCount += messages.Count;
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
        return new([
            new("user", userChanged ? 1 : 0, "name and e-mail replaced"),
            new("secrets", deletedSecrets, "deleted"),
            new("invitations", invitations, "deleted"),
            new("security_events", security, "client address and user agent cleared"),
            new("staff_mail", messageCount, "recipient replaced"),
        ]);
    }

    private static PersonalDataPreview Empty() => new([], [], []);
}

public sealed class PersonalDataErasureReadPort(PlatformDbContext db, ITenantContext tenant)
    : IPersonalDataErasureReadPort
{
    public async Task<ErasedParty?> ErasedPartyAsync(Guid partyRef, CancellationToken cancellationToken)
    {
        var exists = await db.ErasureRequests.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId
            && x.SubjectKind == ErasureSubjectKind.Party && x.SubjectId == partyRef, cancellationToken);
        if (!exists) return null;
        var subject = new ErasureSubject(ErasureSubjectKind.Party, partyRef, [], []);
        return new(partyRef, subject.Token);
    }
}
