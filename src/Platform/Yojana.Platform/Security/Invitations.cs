using System.Data;
using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
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

public sealed record InvitationCreated(Guid InvitationId, DateTime ExpiresAt, Guid MailId);
public sealed record InvitationInspection(string TenantName, IReadOnlyList<string> Roles, string Email,
    bool AccountExists);

public sealed class InvitationService(
    PlatformDbContext db,
    ITenantContext tenant,
    ITenantSettings settings,
    IClock clock,
    IMailOutbox mail,
    MailOptions mailOptions,
    IPasswordPolicy passwordPolicy,
    SecurityEventWriter events,
    SignInService signIn,
    CompanyMailIdentity company,
    SecondStepPolicy secondStepPolicy)
{
    public const string ValidDaysSetting = "security.invitation.valid_days";
    public const int DefaultValidDays = 7;
    public const int MinimumValidDays = 1;
    public const int MaximumValidDays = 30;

    public async Task<InvitationCreated> CreateAsync(string? email, string? displayName, IReadOnlyList<string>? roles,
        Guid actorUserId, SecurityRequestContext request, CancellationToken cancellationToken)
    {
        var input = Validate(email, displayName, roles);
        var tenantId = tenant.TenantId;
        var existingUser = await db.Users.FirstOrDefaultAsync(u => u.Email == input.Email, cancellationToken);
        if (existingUser is not null && await db.Memberships.AnyAsync(m => m.TenantId == tenantId &&
                m.UserId == existingUser.Id && m.DeactivatedAt == null, cancellationToken))
            throw new InvitationConflictException("already_member");
        // user_invitation is global. This explicit tenant predicate is the tenant isolation boundary.
        if (await db.UserInvitations.AnyAsync(i => i.TenantId == tenantId && i.Email == input.Email &&
                i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > clock.UtcNow, cancellationToken))
            throw new InvitationConflictException("already_invited");

        var days = await ValidDaysAsync(cancellationToken);
        var (token, hash) = NewToken();
        var invitation = new UserInvitation
        {
            Id = TimeOrderedId.New(clock.UtcNow), TenantId = tenantId, Email = input.Email,
            DisplayName = input.DisplayName, Roles = input.Roles, TokenHash = hash, CreatedAt = clock.UtcNow,
            CreatedByUserId = actorUserId, ExpiresAt = clock.UtcNow.AddDays(days),
        };
        db.UserInvitations.Add(invitation);
        events.Add(SecurityEventKind.UserInvited, existingUser?.Id, tenantId, actorUserId, request, "created");
        await db.SaveChangesAsync(cancellationToken);
        var mailId = await QueueMailAsync(invitation, token, actorUserId, cancellationToken);
        return new InvitationCreated(invitation.Id, invitation.ExpiresAt, mailId);
    }

    public async Task<InvitationCreated> ResendAsync(Guid invitationId, Guid actorUserId,
        SecurityRequestContext request, CancellationToken cancellationToken)
    {
        // user_invitation has no RLS; never remove this tenant predicate.
        var invitation = await db.UserInvitations.FirstOrDefaultAsync(i => i.Id == invitationId &&
            i.TenantId == tenant.TenantId && i.AcceptedAt == null && i.RevokedAt == null, cancellationToken);
        if (invitation is null) throw new InvitationNotFoundException();
        var (token, hash) = NewToken();
        invitation.TokenHash = hash;
        invitation.ExpiresAt = clock.UtcNow.AddDays(await ValidDaysAsync(cancellationToken));
        invitation.CreatedAt = clock.UtcNow;
        invitation.CreatedByUserId = actorUserId;
        var userId = await db.Users.Where(u => u.Email == invitation.Email).Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
        events.Add(SecurityEventKind.UserInvited, userId, invitation.TenantId, actorUserId, request, "resent");
        await db.SaveChangesAsync(cancellationToken);
        var mailId = await QueueMailAsync(invitation, token, actorUserId, cancellationToken);
        return new InvitationCreated(invitation.Id, invitation.ExpiresAt, mailId);
    }

    public async Task RevokeAsync(Guid invitationId, Guid actorUserId, CancellationToken cancellationToken)
    {
        // user_invitation has no RLS; never remove this tenant predicate.
        var invitation = await db.UserInvitations.FirstOrDefaultAsync(i => i.Id == invitationId &&
            i.TenantId == tenant.TenantId && i.AcceptedAt == null && i.RevokedAt == null, cancellationToken);
        if (invitation is null) throw new InvitationNotFoundException();
        invitation.RevokedAt = clock.UtcNow;
        invitation.RevokedByUserId = actorUserId;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<InvitationInspection?> InspectAsync(string? token, CancellationToken cancellationToken)
    {
        var hash = TokenHash(token);
        if (hash is null) return null;
        var invitation = await db.UserInvitations.AsNoTracking().Where(i => i.TokenHash == hash &&
                i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > clock.UtcNow)
            .Select(i => new { i.Email, i.Roles, TenantName = i.Tenant!.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (invitation is null) return null;
        var exists = await db.Users.AnyAsync(u => u.Email == invitation.Email, cancellationToken);
        return new InvitationInspection(invitation.TenantName, invitation.Roles, Mask(invitation.Email), exists);
    }

    public async Task<(AppUser User, TenantChoice Choice, IReadOnlyList<TenantChoice> Tenants)> AcceptNewAsync(
        string? token, string? password, SecurityRequestContext request, CancellationToken cancellationToken)
    {
        var hash = TokenHash(token) ?? throw new InvitationInvalidException();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var invitation = await ValidInvitationAsync(hash, cancellationToken);
        if (await db.Users.AnyAsync(u => u.Email == invitation.Email, cancellationToken))
            throw new InvitationAccountExistsException();
        var policy = passwordPolicy.Validate(password ?? string.Empty, invitation.Email, invitation.DisplayName);
        if (!policy.IsValid) throw new InvitationPasswordException(policy.Reasons);

        var user = new AppUser
        {
            Id = TimeOrderedId.New(clock.UtcNow), Email = invitation.Email, DisplayName = invitation.DisplayName,
            PasswordHash = PasswordHasher.Hash(password!), CreatedAt = clock.UtcNow,
        };
        db.Users.Add(user);
        db.Memberships.Add(new UserTenantMembership
        {
            UserId = user.Id, TenantId = invitation.TenantId, Roles = invitation.Roles.ToList(),
            CreatedAt = clock.UtcNow,
        });
        invitation.AcceptedAt = clock.UtcNow;
        invitation.AcceptedUserId = user.Id;
        events.Add(SecurityEventKind.UserInvited, user.Id, invitation.TenantId, null, request, "accepted");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var tenants = await signIn.TenantsForAsync(user.Id, cancellationToken);
        return (user, tenants.Single(t => t.TenantId == invitation.TenantId), tenants);
    }

    public async Task<(AppUser User, TenantChoice? Choice, IReadOnlyList<TenantChoice> Tenants)> AcceptExistingAsync(
        string? token, Guid signedInUserId, string? selectedTenantCode, SecurityRequestContext request,
        CancellationToken cancellationToken)
    {
        var hash = TokenHash(token) ?? throw new InvitationInvalidException();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var invitation = await ValidInvitationAsync(hash, cancellationToken);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == signedInUserId && u.IsActive, cancellationToken)
            ?? throw new InvitationOtherAccountException();
        if (!string.Equals(user.Email, invitation.Email, StringComparison.OrdinalIgnoreCase))
            throw new InvitationOtherAccountException();
        var membership = await db.Memberships.FirstOrDefaultAsync(m => m.UserId == user.Id &&
            m.TenantId == invitation.TenantId, cancellationToken);
        if (membership is null)
        {
            db.Memberships.Add(new UserTenantMembership
                { UserId = user.Id, TenantId = invitation.TenantId, Roles = invitation.Roles.ToList(),
                    CreatedAt = clock.UtcNow });
        }
        else
        {
            membership.DeactivatedAt = null;
            membership.DeactivatedByUserId = null;
            membership.CustomerPartyRef = null;
            membership.Roles = invitation.Roles.ToList();
            membership.CreatedAt = clock.UtcNow;
        }
        invitation.AcceptedAt = clock.UtcNow;
        invitation.AcceptedUserId = user.Id;
        events.Add(SecurityEventKind.UserInvited, user.Id, invitation.TenantId, user.Id, request, "accepted");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var tenants = await signIn.TenantsForAsync(user.Id, cancellationToken);
        var selected = selectedTenantCode is null ? null : tenants.FirstOrDefault(t => t.Code == selectedTenantCode);
        return (user, selected, tenants);
    }

    private async Task<UserInvitation> ValidInvitationAsync(byte[] hash, CancellationToken cancellationToken) =>
        await db.UserInvitations.FirstOrDefaultAsync(i => i.TokenHash == hash && i.AcceptedAt == null &&
            i.RevokedAt == null && i.ExpiresAt > clock.UtcNow, cancellationToken) ??
        throw new InvitationInvalidException();

    private async Task<int> ValidDaysAsync(CancellationToken cancellationToken)
    {
        var value = await settings.GetAsync(ValidDaysSetting, cancellationToken);
        if (value is null) return DefaultValidDays;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days is < MinimumValidDays or > MaximumValidDays)
            throw new InvalidOperationException($"Tenant setting {ValidDaysSetting} must be an integer from 1 to 30.");
        return days;
    }

    private async Task<Guid> QueueMailAsync(UserInvitation invitation, string token, Guid actorUserId,
        CancellationToken cancellationToken)
    {
        var facts = await db.Tenants.AsNoTracking().Where(t => t.Id == invitation.TenantId)
            .Select(t => new { TenantName = t.Name }).SingleAsync(cancellationToken);
        var inviter = await db.Users.AsNoTracking().Where(u => u.Id == actorUserId).Select(u => u.DisplayName)
            .SingleAsync(cancellationToken);
        var expiry = invitation.ExpiresAt.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        var modes = (await secondStepPolicy.GetCurrentAsync(cancellationToken)).Roles
            .ToDictionary(row => row.Role, row => row.Mode, StringComparer.Ordinal);
        var secondStep = SecondStepMode.Strictest(invitation.Roles.Select(role =>
            modes.GetValueOrDefault(role, SecondStepPolicy.DefaultFor(role))));
        var content = new AccountMailContent(
            facts.TenantName,
            "Invitation",
            $"You are invited to {facts.TenantName}",
            [$"{inviter} invited you to work in Yojana, where {facts.TenantName} tracks its serial numbers, warranty cover, claims and returns."],
            [("Your role", string.Join(", ", invitation.Roles.Select(RoleLabel))), ("Sign in with", invitation.Email),
                ("Second step", secondStep == SecondStepMode.Required ? "Required for your role" : "Optional")],
            ("Set up my account", mailOptions.Link("/accept-invitation", "t=" + token)),
            [secondStep == SecondStepMode.Required
                ? "Setting up takes about two minutes: choose a password, then add an authenticator app."
                : "Setting up takes about two minutes: choose a password.",
                $"The invitation works once and expires on {expiry}. Not expecting it? Ignore this e-mail; no account is created until you accept."],
            $"Sent by Yojana for {facts.TenantName} at the request of {inviter} (tenant administrator). Replies reach the {facts.TenantName} service desk.",
            await company.CompanyLineAsync(invitation.TenantId, facts.TenantName, cancellationToken));
        var rendered = AccountMailLayout.Render(content);
        return await mail.QueueAsync(new OutgoingMail(MailKind.Invitation, invitation.TenantId, null,
            new MailRecipient(invitation.Email, invitation.DisplayName),
            $"{inviter} invited you to {facts.TenantName} on Yojana", rendered.Html, rendered.Text), cancellationToken);
    }

    private static InvitationInput Validate(string? email, string? displayName, IReadOnlyList<string>? roles)
    {
        var normalised = (email ?? string.Empty).Trim().ToLowerInvariant();
        var name = (displayName ?? string.Empty).Trim();
        var selected = (roles ?? []).Distinct(StringComparer.Ordinal).OrderBy(value => value).ToList();
        if (normalised.Length > 320 || !MailAddress.TryCreate(normalised, out var address) ||
            !string.Equals(address.Address, normalised, StringComparison.OrdinalIgnoreCase))
            throw new InvitationInputException("email_invalid", "Enter one valid e-mail address.");
        if (name.Length is < 1 or > 200 || name.Any(char.IsControl))
            throw new InvitationInputException("display_name_invalid", "Enter a display name up to 200 characters.");
        if (selected.Count == 0 || selected.Any(role => !YojanaRole.IsKnown(role)))
            throw new InvitationInputException("roles_invalid", "Choose at least one known staff role.");
        return new InvitationInput(normalised, name, selected);
    }

    private static (string Token, byte[] Hash) NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return (WebEncoders.Base64UrlEncode(bytes), SHA256.HashData(bytes));
    }

    private static byte[]? TokenHash(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(token.Trim());
            return bytes.Length == 32 ? SHA256.HashData(bytes) : null;
        }
        catch (FormatException) { return null; }
    }

    private static string Mask(string email)
    {
        var at = email.IndexOf('@');
        var local = email[..at];
        var shown = local.Length <= 3 ? local[..1] + "…" : local[..3] + "…";
        return shown + email[at..];
    }

    internal static string RoleLabel(string role) => string.Join(' ', role.Split('_')) switch
    {
        var value when value.Length == 0 => value,
        var value => char.ToUpperInvariant(value[0]) + value[1..],
    };

    private sealed record InvitationInput(string Email, string DisplayName, List<string> Roles);
}

public sealed class InvitationInputException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
public sealed class InvitationConflictException(string code) : Exception(code) { public string Code { get; } = code; }
public sealed class InvitationNotFoundException : Exception;
public sealed class InvitationInvalidException : Exception;
public sealed class InvitationOtherAccountException : Exception;
public sealed class InvitationAccountExistsException : Exception;
public sealed class InvitationPasswordException(IReadOnlyList<PasswordPolicyReason> reasons) : Exception
{
    public IReadOnlyList<PasswordPolicyReason> Reasons { get; } = reasons;
}
