using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Mail;

namespace Yojana.Platform.Security;

public sealed record PasswordResetCompletion(AppUser User, IReadOnlyList<TenantChoice> Tenants);
public sealed record PasswordResetOptions(bool CanSendLink, bool CanSetTemporary,
    string? TemporaryUnavailableReason);
public sealed record AdminPasswordResetResult(string Mode, Guid? MailId, string? TemporaryPassword);

public sealed class PasswordResetService(
    PlatformDbContext db,
    PasswordSecuritySettings settings,
    SignInThrottleService throttles,
    SecurityEventWriter events,
    ISecurityStampService stamps,
    IPasswordPolicy passwordPolicy,
    IMailOutbox mail,
    MailOptions mailOptions,
    SignInService signIn,
    CompanyMailIdentity company,
    MembershipAdministration administration,
    IClock clock)
{
    // Temporary passwords are a short-lived administrative recovery bridge, not a second durable credential.
    public static readonly TimeSpan TemporaryPasswordLifetime = TimeSpan.FromHours(24);

    public async Task RequestAsync(string? email, SecurityRequestContext request,
        CancellationToken cancellationToken)
    {
        var normalised = NormaliseEmail(email);
        var user = normalised.Length <= 320
            ? await db.Users.AsNoTracking().FirstOrDefaultAsync(row => row.Email == normalised, cancellationToken)
            : null;
        if (user is not { IsActive: true, PasswordHash: not null })
        {
            events.Add(SecurityEventKind.PasswordResetRequested, user?.Id, null, null, request,
                "identity_not_eligible", normalised);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!await throttles.TryConsumePasswordResetMailAsync(normalised, cancellationToken))
        {
            events.Add(SecurityEventKind.PasswordResetRequested, user.Id, null, null, request,
                "mail_throttled");
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var memberships = await ActiveMembershipsAsync(user.Id, cancellationToken);
        // A global identity can belong to tenants with different policies. Before tenant selection the strictest
        // (shortest) active-membership value wins; using any longer value would weaken one tenant's chosen policy.
        var minutes = memberships.Count == 0 ? PasswordSecuritySettings.DefaultResetLinkMinutes
            : (await Task.WhenAll(memberships.Select(row => settings.ForTenantAsync(row.TenantId,
                cancellationToken)))).Min();
        var tenantName = memberships.Count == 1 ? memberships[0].Name : "Yojana";
        await IssueLinkAsync(user, null, tenantName, memberships.Count == 1 ? memberships[0].TenantId : null, minutes,
            request, null, cancellationToken);
    }

    public async Task<(PasswordResetCompletion? Completion, PasswordPolicyResult? Policy)> CompleteAsync(
        string? token, string newPassword, SecurityRequestContext request, CancellationToken cancellationToken)
    {
        if (!PasswordResetTokens.TryHash(token, out var tokenHash)) return (null, null);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var row = await db.PasswordResetTokens.FirstOrDefaultAsync(candidate => candidate.TokenHash == tokenHash,
            cancellationToken);
        if (row is null || row.UsedAt is not null || row.SupersededAt is not null || row.ExpiresAt <= clock.UtcNow)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (null, null);
        }

        var user = await db.Users.FirstOrDefaultAsync(candidate => candidate.Id == row.UserId && candidate.IsActive,
            cancellationToken);
        if (user is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (null, null);
        }

        var policy = await passwordPolicy.ValidateAsync(
            new PasswordCandidate(newPassword, user.Email, user.DisplayName, user.Id, null), cancellationToken);
        if (!policy.IsValid)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (null, policy);
        }

        row.UsedAt = clock.UtcNow;
        user.PasswordHash = PasswordHasher.Hash(newPassword);
        user.MustChangePassword = false;
        user.TemporaryPasswordExpiresAt = null;
        await stamps.RotateSecurityStampAsync(user.Id, "password_reset_completed", cancellationToken);
        await throttles.ResetAccountAsync(user.Email, cancellationToken);
        events.Add(SecurityEventKind.PasswordResetCompleted, user.Id, null, user.Id, request,
            "reset_link_completed");
        await db.SaveChangesAsync(cancellationToken);
        var identity = await IdentityCompanyAsync(user.Id, cancellationToken);
        await QueueChangedMailAsync(user, null, identity.Name, identity.TenantId,
            "With a reset link sent to this address", request, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (new PasswordResetCompletion(user, await signIn.TenantsForAsync(user.Id, cancellationToken)), null);
    }

    /// <summary>The person a live reset link belongs to, for the password rules read (ADR-0008 §7); null otherwise.</summary>
    public async Task<Guid?> UserForLinkAsync(string? token, CancellationToken cancellationToken)
    {
        if (!PasswordResetTokens.TryHash(token, out var tokenHash)) return null;
        return await db.PasswordResetTokens.AsNoTracking()
            .Where(row => row.TokenHash == tokenHash && row.UsedAt == null && row.SupersededAt == null &&
                row.ExpiresAt > clock.UtcNow)
            .Select(row => (Guid?)row.UserId).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<(AppUser? User, PasswordPolicyResult? Policy, bool CurrentPasswordWrong)> ChangeAsync(
        Guid userId, string currentPassword, string newPassword, TenantId? tenantId, string? tenantName,
        SecurityRequestContext request, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstAsync(row => row.Id == userId && row.IsActive, cancellationToken);
        var currentValid = PasswordHasher.Verify(currentPassword, user.PasswordHash)
            && (!user.MustChangePassword || user.TemporaryPasswordExpiresAt > clock.UtcNow);
        if (!currentValid)
        {
            await throttles.RegisterAccountFailureAsync(user.Email, cancellationToken);
            return (null, null, true);
        }

        var policy = await passwordPolicy.ValidateAsync(
            new PasswordCandidate(newPassword, user.Email, user.DisplayName, user.Id, null), cancellationToken);
        if (!policy.IsValid) return (null, policy, false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        user.PasswordHash = PasswordHasher.Hash(newPassword);
        user.MustChangePassword = false;
        user.TemporaryPasswordExpiresAt = null;
        await stamps.RotateSecurityStampAsync(user.Id, "password_changed", cancellationToken);
        await throttles.ResetAccountAsync(user.Email, cancellationToken);
        events.Add(SecurityEventKind.PasswordChanged, user.Id, tenantId, user.Id, request, "changed_by_user");
        await db.SaveChangesAsync(cancellationToken);
        var identity = tenantId is { } current && tenantName is not null
            ? (TenantId: (TenantId?)current, Name: tenantName)
            : await IdentityCompanyAsync(user.Id, cancellationToken);
        await QueueChangedMailAsync(user, tenantId, identity.Name, identity.TenantId, "Changed in Yojana", request,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (user, null, false);
    }

    public async Task<PasswordResetOptions?> AdminOptionsAsync(Guid actorId, Guid targetId, TenantId tenantId,
        CancellationToken cancellationToken)
    {
        if (actorId == targetId) return new PasswordResetOptions(false, false, "use_change_password");
        var target = await db.Users.AsNoTracking().FirstOrDefaultAsync(row => row.Id == targetId, cancellationToken);
        if (target is null || !await db.Memberships.AsNoTracking()
                .AnyAsync(row => row.UserId == targetId && row.TenantId == tenantId, cancellationToken))
            return null;
        var reason = await TemporaryUnavailableReasonAsync(actorId, target, cancellationToken);
        return new PasswordResetOptions(true, reason is null, reason);
    }

    public async Task<AdminPasswordResetResult?> AdminResetAsync(Guid actorId, Guid targetId, TenantId tenantId,
        string tenantName, string mode, SecurityRequestContext request, CancellationToken cancellationToken)
    {
        var target = await db.Users.FirstOrDefaultAsync(row => row.Id == targetId, cancellationToken);
        if (target is null || !await db.Memberships.AsNoTracking()
                .AnyAsync(row => row.UserId == targetId && row.TenantId == tenantId, cancellationToken))
            return null;
        if (mode == "link")
        {
            var id = await IssueLinkAsync(target, tenantId, tenantName, tenantId,
                await settings.ForTenantAsync(tenantId, cancellationToken), request, actorId, cancellationToken);
            return new AdminPasswordResetResult("link", id, null);
        }

        if (mode != "temporary") throw new ArgumentException("Mode must be link or temporary.", nameof(mode));
        var reason = await TemporaryUnavailableReasonAsync(actorId, target, cancellationToken);
        if (reason is not null) throw new TemporaryPasswordNotAllowedException(reason);
        var rules = await passwordPolicy.RulesForAsync(target.Id, tenantId, cancellationToken);
        var temporaryPassword = TemporaryPasswordGenerator.Create(rules.MinimumLength);
        var policy = await passwordPolicy.ValidateAsync(
            new PasswordCandidate(temporaryPassword, target.Email, target.DisplayName, target.Id, tenantId),
            cancellationToken);
        if (!policy.IsValid) throw new InvalidOperationException("The generated temporary password failed policy.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        target.PasswordHash = PasswordHasher.Hash(temporaryPassword);
        target.MustChangePassword = true;
        target.TemporaryPasswordExpiresAt = clock.UtcNow + TemporaryPasswordLifetime;
        await stamps.RotateSecurityStampAsync(target.Id, "admin_password_reset", cancellationToken);
        events.Add(SecurityEventKind.AdminPasswordReset, target.Id, tenantId, actorId, request,
            "temporary_password_set");
        await db.SaveChangesAsync(cancellationToken);
        var actorName = await db.Users.AsNoTracking().Where(row => row.Id == actorId)
            .Select(row => row.DisplayName).SingleAsync(cancellationToken);
        await QueueChangedMailAsync(target, tenantId, tenantName, tenantId, $"Set by an administrator, {actorName}",
            request, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdminPasswordResetResult("temporary", null, temporaryPassword);
    }

    private async Task<Guid> IssueLinkAsync(AppUser user, TenantId? tenantId, string tenantName, TenantId? companyId,
        int minutes,
        SecurityRequestContext request, Guid? actorId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var now = clock.UtcNow;
        await db.PasswordResetTokens.Where(row => row.UserId == user.Id && row.UsedAt == null
                && row.SupersededAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.SupersededAt, now), cancellationToken);
        var token = PasswordResetTokens.Create();
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            Id = TimeOrderedId.New(now), UserId = user.Id, TokenHash = token.Hash,
            CreatedAt = now, ExpiresAt = now.AddMinutes(minutes),
        });
        events.Add(SecurityEventKind.PasswordResetRequested, user.Id, tenantId, actorId, request,
            actorId is null ? "reset_mail_queued" : "administrator_sent_link");
        await db.SaveChangesAsync(cancellationToken);
        var link = mailOptions.Link("/reset-password", "t=" + token.Token);
        var rendered = AccountMailLayout.Render(new AccountMailContent(tenantName, "Security",
            "Reset your password",
            [$"Someone asked to reset the password for your Yojana account at {tenantName}. If it was you, choose a new password:"],
            [("Requested", FormatUtc(now)), ("From", From(request))],
            ("Choose a new password", link),
            [$"The link works once and stops working at {FormatUtc(now.AddMinutes(minutes))} ({minutes} minutes).",
                "If you did not ask, ignore this e-mail. Your password stays as it is and nobody can use this link without access to your mailbox."],
            Footer(tenantName), await CompanyLineAsync(companyId, tenantName, cancellationToken)));
        var mailId = await mail.QueueAsync(new OutgoingMail(MailKind.PasswordReset, tenantId, user.Id,
            new MailRecipient(user.Email, user.DisplayName), "Reset your Yojana password", rendered.Html,
            rendered.Text), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return mailId;
    }

    private async Task QueueChangedMailAsync(AppUser user, TenantId? tenantId, string tenantName, TenantId? companyId,
        string how,
        SecurityRequestContext request, CancellationToken cancellationToken)
    {
        var rendered = AccountMailLayout.Render(new AccountMailContent(tenantName, "Security notice",
            "Your password was changed",
            [$"The password for your Yojana account at {tenantName} was changed. You were signed out on every other device."],
            [("Changed", FormatUtc(clock.UtcNow)), ("How", how), ("From", From(request))],
            ("Reset my password", mailOptions.Link("/forgot-password")),
            ["Was this you? Then there is nothing to do.",
                "If not, reset your password now and tell your administrator. Someone may have access to your mailbox."],
            Footer(tenantName), await CompanyLineAsync(companyId, tenantName, cancellationToken)));
        await mail.QueueAsync(new OutgoingMail(MailKind.PasswordChanged, tenantId, user.Id,
            new MailRecipient(user.Email, user.DisplayName), "Your Yojana password was changed", rendered.Html,
            rendered.Text), cancellationToken);
    }

    private async Task<string?> TemporaryUnavailableReasonAsync(Guid actorId, AppUser target,
        CancellationToken cancellationToken)
    {
        if (target.PasswordHash is null) return "no_password_sign_in";
        if (!await settings.TemporaryAllowedForCurrentAsync(cancellationToken)) return "tenant_setting_off";
        return await administration.AdministersEveryMembershipAsync(actorId, target.Id, cancellationToken)
            ? null : "other_memberships";
    }

    private async Task<List<(TenantId TenantId, string Name)>> ActiveMembershipsAsync(Guid userId,
        CancellationToken cancellationToken)
    {
        var rows = await db.Memberships.AsNoTracking()
            .Where(row => row.UserId == userId && row.DeactivatedAt == null && row.Tenant!.IsActive)
            .Select(row => new { row.TenantId, row.Tenant!.Name })
            .ToListAsync(cancellationToken);
        return rows.Select(row => (row.TenantId, row.Name)).ToList();
    }

    private async Task<(TenantId? TenantId, string Name)> IdentityCompanyAsync(Guid userId,
        CancellationToken cancellationToken)
    {
        var memberships = await ActiveMembershipsAsync(userId, cancellationToken);
        return memberships.Count == 1 ? (memberships[0].TenantId, memberships[0].Name) : (null, "Yojana");
    }

    private async Task<string?> CompanyLineAsync(TenantId? companyId, string companyName,
        CancellationToken cancellationToken) => companyId is { } id
        ? await company.CompanyLineAsync(id, companyName, cancellationToken)
        : null;

    private static string NormaliseEmail(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
    private static string FormatUtc(DateTime value) => value.ToUniversalTime()
        .ToString("dd MMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture);
    private static string From(SecurityRequestContext request) =>
        $"{(string.IsNullOrWhiteSpace(request.ClientAddress) ? "Unknown address" : request.ClientAddress)} · {BrowserSummary.FromUserAgent(request.UserAgent)}";
    private static string Footer(string tenantName) =>
        $"Sent by Yojana for {tenantName}. You get this e-mail because it is about the security of your account, so it cannot be switched off.";
}

public sealed class TemporaryPasswordNotAllowedException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
