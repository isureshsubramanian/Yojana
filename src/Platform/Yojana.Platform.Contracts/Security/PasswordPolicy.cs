using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Platform.Contracts.Security;

public sealed record PasswordPolicyReason(string Code, string Message);

public sealed record PasswordPolicyResult(bool IsValid, IReadOnlyList<PasswordPolicyReason> Reasons);

/// <summary>
/// A company's password rules (ADR-0008). A person in several companies follows the strictest: the longest minimum
/// and every character class any of them requires.
/// </summary>
public sealed record PasswordRules(int MinimumLength, bool RequireUppercase, bool RequireLowercase, bool RequireDigit,
    bool RequireSymbol)
{
    public const int ShortestMinimum = 8;
    public const int LongestMinimum = 64;

    /// <summary>Applies until a company changes its rules.</summary>
    public static readonly PasswordRules Default = new(12, true, true, true, true);

    public PasswordRules StricterOf(PasswordRules other) => new(Math.Max(MinimumLength, other.MinimumLength),
        RequireUppercase || other.RequireUppercase, RequireLowercase || other.RequireLowercase,
        RequireDigit || other.RequireDigit, RequireSymbol || other.RequireSymbol);
}

/// <summary>
/// A password someone is about to set. <see cref="UserId"/> names an existing person, whose company memberships
/// decide the rules; <see cref="InvitedTo"/> names the company of an invitation for someone with no account yet.
/// </summary>
public sealed record PasswordCandidate(string Password, string Email, string DisplayName, Guid? UserId,
    TenantId? InvitedTo);

/// <summary>Password rules for newly set passwords. Existing hashes are not re-evaluated.</summary>
public interface IPasswordPolicy
{
    /// <summary>Every unmet rule is returned together, so a screen can show them all at once.</summary>
    Task<PasswordPolicyResult> ValidateAsync(PasswordCandidate candidate, CancellationToken cancellationToken);

    Task<PasswordRules> RulesForAsync(Guid? userId, TenantId? invitedTo, CancellationToken cancellationToken);
}

/// <summary>Revokes a user's existing sessions and records why the revocation occurred.</summary>
public interface ISecurityStampService
{
    Task RotateSecurityStampAsync(Guid userId, string reason, CancellationToken cancellationToken);
}

public static class SecurityEventKind
{
    public const string SignInSucceeded = "sign_in_succeeded";
    public const string SignInFailed = "sign_in_failed";
    public const string LockedOut = "locked_out";
    public const string SignedOut = "signed_out";
    public const string SecurityStampRotated = "security_stamp_rotated";
    public const string PasswordChanged = "password_changed";
    public const string PasswordResetRequested = "password_reset_requested";
    public const string PasswordResetCompleted = "password_reset_completed";
    public const string AdminPasswordReset = "admin_password_reset";
    public const string SecondStepEnrolled = "second_step_enrolled";
    public const string SecondStepRemoved = "second_step_removed";
    public const string RecoveryCodesRegenerated = "recovery_codes_regenerated";
    public const string RecoveryCodeUsed = "recovery_code_used";
    public const string PasskeyAdded = "passkey_added";
    public const string PasskeyRemoved = "passkey_removed";
    public const string UserInvited = "user_invited";
    public const string UserDeactivated = "user_deactivated";
    public const string UserReactivated = "user_reactivated";
    public const string RolesChanged = "roles_changed";
    public const string ApiTokenIssued = "api_token_issued";
    public const string ApiTokenRefused = "api_token_refused";
    public const string ApiClientCreated = "api_client_created";
    public const string ApiClientSecretRotated = "api_client_secret_rotated";
    public const string ApiClientRevoked = "api_client_revoked";
    public const string ApiClientChanged = "api_client_changed";
    /// <summary>A tenant administrator changed a security or company setting; the reason lists the changed keys.</summary>
    public const string SettingsChanged = "settings_changed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        SignInSucceeded, SignInFailed, LockedOut, SignedOut, SecurityStampRotated, PasswordChanged,
        PasswordResetRequested, PasswordResetCompleted, AdminPasswordReset, SecondStepEnrolled,
        SecondStepRemoved, RecoveryCodesRegenerated, RecoveryCodeUsed, PasskeyAdded, PasskeyRemoved,
        UserInvited, UserDeactivated,
        UserReactivated, RolesChanged, SettingsChanged, ApiTokenIssued, ApiTokenRefused, ApiClientCreated,
        ApiClientSecretRotated, ApiClientRevoked, ApiClientChanged,
    };
}

public sealed record SecurityEventView(
    DateTime OccurredAt,
    string Kind,
    string? UserName,
    string? ActorName,
    string? ClientAddress,
    string? UserAgent,
    string Reason);

public sealed record SecurityEventPage(IReadOnlyList<SecurityEventView> Items, string? NextCursor);

/// <summary>Counts behind the Security events page cards (ADR-0007); numbers only, no personal data.</summary>
public sealed record SecurityEventSummary(
    long SignIns,
    long FailedSignIns,
    long Lockouts,
    long PasswordResets,
    long MethodChanges);

/// <summary>The one definition of which event kinds each summary card counts (ADR-0007 §3).</summary>
public static class SecurityEventSummaryKinds
{
    /// <summary>Counted from the start of the tenant's day.</summary>
    public static readonly IReadOnlySet<string> SignIns = Set(SecurityEventKind.SignInSucceeded);
    public static readonly IReadOnlySet<string> FailedSignIns = Set(SecurityEventKind.SignInFailed);
    public static readonly IReadOnlySet<string> Lockouts = Set(SecurityEventKind.LockedOut);

    /// <summary>Counted over the last seven days. A reset request is not a reset, so it is left out.</summary>
    public static readonly IReadOnlySet<string> PasswordResets =
        Set(SecurityEventKind.PasswordResetCompleted, SecurityEventKind.AdminPasswordReset);
    public static readonly IReadOnlySet<string> MethodChanges = Set(SecurityEventKind.SecondStepEnrolled,
        SecurityEventKind.SecondStepRemoved, SecurityEventKind.PasskeyAdded, SecurityEventKind.PasskeyRemoved,
        SecurityEventKind.RecoveryCodesRegenerated);

    public static readonly IReadOnlySet<string> Today = Set([.. SignIns, .. FailedSignIns, .. Lockouts]);
    public static readonly IReadOnlySet<string> Week = Set([.. PasswordResets, .. MethodChanges]);

    private static HashSet<string> Set(params string[] kinds) => new(kinds, StringComparer.Ordinal);
}
