using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Mail;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Security;

public sealed record SecondStepChallenge(bool Required, bool EnrolmentRequired,
    IReadOnlyList<string> Methods, string EffectiveMode);
public sealed record SecondStepEnrolmentStart(Guid EnrolmentId, string OtpAuthUri, string ManualKey,
    string QrSvg);
public sealed record SecondStepConfirmation(IReadOnlyList<string> RecoveryCodes, AppUser User,
    bool Replaced);
public sealed record SecondStepAccountView(string Mode, bool Enrolled, bool Required, DateTime? AddedAt,
    int RecoveryCodesLeft);
public sealed record SecondStepVerification(AppUser? User, bool Wrong, bool Locked, bool RecoveryCodesLow);

public sealed class SecondStepUnavailableException : Exception;
public sealed class SecondStepCodeWrongException : Exception;
public sealed class SecondStepEnrolmentNotFoundException : Exception;
public sealed class SecondStepRemovalNotAllowedException : Exception;
public sealed class SecondStepPasswordWrongException : Exception;

public sealed class SecondStepService(
    PlatformDbContext db,
    SecondStepPolicy policy,
    SignInThrottleService throttles,
    SecurityEventWriter events,
    ISecurityStampService stamps,
    IMailOutbox mail,
    MailOptions mailOptions,
    CompanyMailIdentity company,
    IServiceProvider services,
    IServiceScopeFactory scopes,
    IClock clock)
{
    public const string AuthenticatorMethod = "authenticator";
    public const string RecoveryCodeMethod = "recovery_code";
    private const string SecretPurpose = "user-authenticator-totp";

    public async Task<SecondStepChallenge> ChallengeAsync(Guid userId, CancellationToken cancellationToken)
    {
        var mode = await policy.EffectiveForUserAsync(userId, cancellationToken);
        var enrolled = await db.UserAuthenticators.AsNoTracking().AnyAsync(row => row.UserId == userId &&
            row.Status == UserAuthenticatorStatus.Active, cancellationToken);
        if (mode == SecondStepMode.Off) return new SecondStepChallenge(false, false, [], mode);
        if (!enrolled) return new SecondStepChallenge(mode == SecondStepMode.Required,
            mode == SecondStepMode.Required,
            mode == SecondStepMode.Required ? [AuthenticatorMethod] : [], mode);
        return new SecondStepChallenge(true, false, [AuthenticatorMethod, RecoveryCodeMethod], mode);
    }

    public async Task<SecondStepVerification> VerifyAsync(Guid userId, string? method, string? code,
        SecurityRequestContext request, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstAsync(row => row.Id == userId && row.IsActive, cancellationToken);
        var clientAddress = string.IsNullOrWhiteSpace(request.ClientAddress) ? "unknown" : request.ClientAddress.Trim();
        var lockState = await throttles.LockStateAsync(user.Email, clientAddress, cancellationToken);
        if (lockState.AccountLocked || lockState.AddressRetryAfter is not null)
            return new SecondStepVerification(null, false, true, false);

        var accepted = false;
        var low = false;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        if (method == AuthenticatorMethod)
        {
            var authenticator = await db.UserAuthenticators.SingleOrDefaultAsync(row => row.UserId == userId &&
                row.Status == UserAuthenticatorStatus.Active, cancellationToken);
            if (authenticator is not null)
            {
                var secret = await Protector().UnprotectAsync(authenticator.SealedSecret, SecretPurpose,
                    cancellationToken);
                try
                {
                    var step = AuthenticatorCodes.Verify(secret, code, clock.UtcNow, authenticator.LastUsedStep);
                    if (step is not null)
                    {
                        authenticator.LastUsedStep = step;
                        accepted = true;
                    }
                }
                finally { CryptographicOperations.ZeroMemory(secret); }
            }
        }
        else if (method == RecoveryCodeMethod && RecoveryCodes.Normalise(code) is { } normalised)
        {
            var candidates = await db.UserRecoveryCodes.Where(row => row.UserId == userId && row.UsedAt == null)
                .ToListAsync(cancellationToken);
            var recovery = candidates.FirstOrDefault(row => RecoveryCodes.Verify(normalised, row.CodeHash));
            if (recovery is not null)
            {
                recovery.UsedAt = clock.UtcNow;
                accepted = true;
                low = candidates.Count - 1 <= 2;
                events.Add(SecurityEventKind.RecoveryCodeUsed, userId, null, userId, request,
                    "recovery_code_used");
            }
        }

        if (!accepted)
        {
            await transaction.RollbackAsync(cancellationToken);
            var failure = await throttles.RegisterFailureAsync(user.Email, clientAddress, true, cancellationToken);
            events.Add(SecurityEventKind.SignInFailed, userId, null, null, request, "second_step_code_wrong");
            if (failure.AccountLockedNow || failure.AddressRetryAfter is not null)
                events.Add(SecurityEventKind.LockedOut, userId, null, null, request, "account_lockout");
            await db.SaveChangesAsync(cancellationToken);
            return new SecondStepVerification(null, true, failure.AccountLockedNow ||
                failure.AddressRetryAfter is not null, false);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var completion = await throttles.CompleteSuccessAsync(user.Email, clientAddress, cancellationToken);
        return completion.AccountLocked || completion.AddressRetryAfter is not null
            ? new SecondStepVerification(null, false, true, false)
            : new SecondStepVerification(user, false, false, low);
    }

    public async Task<bool> RegisterWrongSignInCodeAsync(Guid userId, SecurityRequestContext request,
        CancellationToken cancellationToken)
    {
        var email = await db.Users.AsNoTracking().Where(row => row.Id == userId)
            .Select(row => row.Email).SingleAsync(cancellationToken);
        var clientAddress = string.IsNullOrWhiteSpace(request.ClientAddress) ? "unknown" : request.ClientAddress.Trim();
        var failure = await throttles.RegisterFailureAsync(email, clientAddress, true, cancellationToken);
        events.Add(SecurityEventKind.SignInFailed, userId, null, null, request, "second_step_code_wrong");
        if (failure.AccountLockedNow || failure.AddressRetryAfter is not null)
            events.Add(SecurityEventKind.LockedOut, userId, null, null, request, "account_lockout");
        await db.SaveChangesAsync(cancellationToken);
        return failure.AccountLockedNow || failure.AddressRetryAfter is not null;
    }

    public async Task<SecondStepEnrolmentStart> StartAsync(Guid userId, CancellationToken cancellationToken)
    {
        var protector = Protector();
        var renderer = services.GetService<IQrCodeRenderer>() ?? throw new SecondStepUnavailableException();
        var user = await db.Users.AsNoTracking().SingleAsync(row => row.Id == userId && row.IsActive,
            cancellationToken);
        var secret = AuthenticatorCodes.CreateSecret();
        try
        {
            var base32 = AuthenticatorCodes.Base32Encode(secret);
            var uri = AuthenticatorCodes.OtpAuthUri(user.Email, base32);
            var sealedSecret = await protector.ProtectAsync(secret, SecretPurpose, cancellationToken);
            var row = new UserAuthenticator
            {
                Id = Guid.CreateVersion7(), UserId = userId, SealedSecret = sealedSecret,
                Status = UserAuthenticatorStatus.Pending, CreatedAt = clock.UtcNow,
                ExpiresAt = clock.UtcNow.AddMinutes(10),
            };
            db.UserAuthenticators.Add(row);
            await db.SaveChangesAsync(cancellationToken);
            return new SecondStepEnrolmentStart(row.Id, uri, AuthenticatorCodes.GroupManualKey(base32),
                await renderer.RenderSvgAsync(uri, cancellationToken));
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    /// <param name="currentPassword">Required to replace a working authenticator from a signed-in session, so a session
    /// alone (a stolen cookie) cannot swap the second step; not asked at sign-in, where the password was just given.</param>
    public async Task<SecondStepConfirmation> ConfirmAsync(Guid userId, Guid enrolmentId, string? code,
        string? currentPassword, bool passwordToReplace, TenantId? tenantId, SecurityRequestContext request,
        CancellationToken cancellationToken)
    {
        if (passwordToReplace && await db.UserAuthenticators.AnyAsync(row => row.UserId == userId &&
                row.Status == UserAuthenticatorStatus.Active, cancellationToken))
            await RequirePasswordAsync(userId, currentPassword, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var pending = await db.UserAuthenticators.SingleOrDefaultAsync(row => row.Id == enrolmentId &&
            row.UserId == userId && row.Status == UserAuthenticatorStatus.Pending, cancellationToken);
        if (pending is null || pending.ExpiresAt <= clock.UtcNow) throw new SecondStepEnrolmentNotFoundException();
        var secret = await Protector().UnprotectAsync(pending.SealedSecret, SecretPurpose, cancellationToken);
        try
        {
            var step = AuthenticatorCodes.Verify(secret, code, clock.UtcNow, null);
            if (step is null) throw new SecondStepCodeWrongException();
            pending.LastUsedStep = step;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }

        var current = await db.UserAuthenticators.Where(row => row.UserId == userId &&
            row.Status == UserAuthenticatorStatus.Active).ToListAsync(cancellationToken);
        var replaced = current.Count > 0;
        foreach (var old in current)
        {
            old.Status = UserAuthenticatorStatus.Replaced;
            old.ReplacedAt = clock.UtcNow;
        }
        pending.Status = UserAuthenticatorStatus.Active;
        pending.ConfirmedAt = clock.UtcNow;
        pending.ExpiresAt = null;
        var recoveryCodes = await ReplaceRecoveryCodesAsync(userId, cancellationToken);
        events.Add(SecurityEventKind.SecondStepEnrolled, userId, tenantId, userId, request,
            replaced ? "authenticator_replaced" : "authenticator_added");
        await db.SaveChangesAsync(cancellationToken);
        if (replaced) await stamps.RotateSecurityStampAsync(userId, "authenticator_replaced", cancellationToken);
        var user = await db.Users.AsNoTracking().SingleAsync(row => row.Id == userId, cancellationToken);
        await QueueMethodMailAsync(user, tenantId,
            replaced ? "Authenticator app (replaced)" : "Authenticator app", false, request,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SecondStepConfirmation(recoveryCodes, user, replaced);
    }

    public async Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(Guid userId, string? code,
        TenantId? tenantId, SecurityRequestContext request, CancellationToken cancellationToken)
    {
        var authenticator = await db.UserAuthenticators.SingleOrDefaultAsync(row =>
            row.UserId == userId && row.Status == UserAuthenticatorStatus.Active, cancellationToken);
        if (authenticator is null) throw new SecondStepCodeWrongException();
        var secret = await Protector().UnprotectAsync(authenticator.SealedSecret, SecretPurpose, cancellationToken);
        try
        {
            var step = AuthenticatorCodes.Verify(secret, code, clock.UtcNow, authenticator.LastUsedStep);
            if (step is null)
                throw new SecondStepCodeWrongException();
            authenticator.LastUsedStep = step;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
        var values = await ReplaceRecoveryCodesAsync(userId, cancellationToken);
        events.Add(SecurityEventKind.RecoveryCodesRegenerated, userId, tenantId, userId, request,
            "recovery_codes_replaced");
        await db.SaveChangesAsync(cancellationToken);
        return values;
    }

    public async Task<SecondStepAccountView> AccountAsync(Guid userId, CancellationToken cancellationToken)
    {
        var mode = await policy.EffectiveForUserAsync(userId, cancellationToken);
        var authenticator = await db.UserAuthenticators.AsNoTracking().SingleOrDefaultAsync(row =>
            row.UserId == userId && row.Status == UserAuthenticatorStatus.Active, cancellationToken);
        var remaining = await db.UserRecoveryCodes.CountAsync(row => row.UserId == userId && row.UsedAt == null,
            cancellationToken);
        return new SecondStepAccountView(mode, authenticator is not null, mode == SecondStepMode.Required,
            authenticator?.ConfirmedAt, remaining);
    }

    public async Task<AppUser> RemoveAsync(Guid userId, string? password, TenantId? tenantId,
        SecurityRequestContext request, CancellationToken cancellationToken)
    {
        if (await policy.EffectiveForUserAsync(userId, cancellationToken) != SecondStepMode.Optional)
            throw new SecondStepRemovalNotAllowedException();
        var user = await RequirePasswordAsync(userId, password, cancellationToken);
        await RemoveDataAsync(userId, cancellationToken);
        events.Add(SecurityEventKind.SecondStepRemoved, userId, tenantId, userId, request,
            "removed_by_user");
        await db.SaveChangesAsync(cancellationToken);
        await stamps.RotateSecurityStampAsync(userId, "second_step_removed", cancellationToken);
        await QueueMethodMailAsync(user, tenantId, "Authenticator app", true, request, cancellationToken);
        return user;
    }

    public async Task RemoveByAdministratorAsync(Guid userId, Guid actorId, TenantId tenantId, string reason,
        string actorName, SecurityRequestContext request, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleAsync(row => row.Id == userId, cancellationToken);
        await RemoveDataAsync(userId, cancellationToken);
        events.Add(SecurityEventKind.SecondStepRemoved, userId, tenantId, actorId, request,
            reason[..Math.Min(reason.Length, 100)]);
        await db.SaveChangesAsync(cancellationToken);
        await stamps.RotateSecurityStampAsync(userId, "admin_second_step_reset", cancellationToken);
        await QueueMethodMailAsync(user, tenantId, $"Authenticator app · Removed by an administrator, {actorName}",
            true, request, cancellationToken);
    }

    /// <summary>A wrong current password counts toward the account lockout, as on change-password, so a signed-in
    /// session cannot be used to guess the password. Runs outside any transaction so the failure is kept.</summary>
    private async Task<AppUser> RequirePasswordAsync(Guid userId, string? password, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleAsync(row => row.Id == userId && row.IsActive, cancellationToken);
        if (PasswordHasher.Verify(password ?? string.Empty, user.PasswordHash)) return user;
        await throttles.RegisterAccountFailureAsync(user.Email, cancellationToken);
        throw new SecondStepPasswordWrongException();
    }

    private async Task RemoveDataAsync(Guid userId, CancellationToken cancellationToken)
    {
        await db.UserRecoveryCodes.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.UserAuthenticators.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(Guid userId,
        CancellationToken cancellationToken)
    {
        await db.UserRecoveryCodes.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        var values = RecoveryCodes.Create();
        var batchId = Guid.CreateVersion7();
        db.UserRecoveryCodes.AddRange(values.Select(value => new UserRecoveryCode
        {
            UserId = userId, CodeHash = RecoveryCodes.Hash(RecoveryCodes.Normalise(value)!), BatchId = batchId,
        }));
        return values;
    }

    private IUserSecretProtector Protector() => services.GetService<IUserSecretProtector>()
        ?? throw new SecondStepUnavailableException();

    private async Task QueueMethodMailAsync(AppUser user, TenantId? tenantId, string method, bool removed,
        SecurityRequestContext request, CancellationToken cancellationToken)
    {
        var tenantName = tenantId is { } selected
            ? await db.Tenants.AsNoTracking().Where(row => row.Id == selected).Select(row => row.Name)
                .SingleAsync(cancellationToken) : "Yojana";
        var line = tenantId is { } companyId
            ? await company.CompanyLineAsync(companyId, tenantName, cancellationToken) : null;
        var rendered = AccountMailLayout.Render(new AccountMailContent(tenantName, "Security notice",
            removed ? "A sign-in method was removed" : "A new sign-in method was added",
            [removed ? "A way to complete sign-in was removed from your Yojana account." :
                "A new way to complete sign-in was added to your Yojana account."],
            [("Method", method), (removed ? "Removed" : "Added", clock.UtcNow.ToString("d MMM yyyy, HH:mm 'UTC'")),
                ("From", string.IsNullOrWhiteSpace(request.ClientAddress) ? "Unknown address" : request.ClientAddress!),
                ("Your sign-in methods", removed ? "Password" : "Password, authenticator app")],
            ("Review my sign-in methods", mailOptions.Link("/account/security")),
            ["Was this you? Then there is nothing to do.",
                "If not, change your password and tell your administrator."],
            $"Security notice for {tenantName}.", line));
        var outgoing = new OutgoingMail(removed ? MailKind.SignInMethodRemoved : MailKind.SignInMethodAdded,
            tenantId, user.Id, new MailRecipient(user.Email, user.DisplayName),
            removed ? "A Yojana sign-in method was removed" : "A Yojana sign-in method was added",
            rendered.Html, rendered.Text);
        if (tenantId is not { } selectedTenant)
        {
            await mail.QueueAsync(outgoing, cancellationToken);
            return;
        }

        // MailOutbox deliberately enforces tenant scope so sender identity cannot leak between companies. A
        // pre-tenant second-step flow must establish that scope explicitly before it queues the notification.
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(selectedTenant);
        await scope.ServiceProvider.GetRequiredService<IMailOutbox>().QueueAsync(outgoing, cancellationToken);
    }
}
