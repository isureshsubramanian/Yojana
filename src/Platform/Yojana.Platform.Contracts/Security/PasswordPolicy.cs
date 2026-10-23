namespace Yojana.Platform.Contracts.Security;

public sealed record PasswordPolicyReason(string Code, string Message);

public sealed record PasswordPolicyResult(bool IsValid, IReadOnlyList<PasswordPolicyReason> Reasons);

/// <summary>Password rules for newly set passwords. Existing hashes are not re-evaluated.</summary>
public interface IPasswordPolicy
{
    PasswordPolicyResult Validate(string password, string email, string displayName);
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
    /// <summary>A tenant administrator changed a security or company setting; the reason lists the changed keys.</summary>
    public const string SettingsChanged = "settings_changed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        SignInSucceeded, SignInFailed, LockedOut, SignedOut, SecurityStampRotated, PasswordChanged,
        PasswordResetRequested, PasswordResetCompleted, AdminPasswordReset, SecondStepEnrolled,
        SecondStepRemoved, RecoveryCodesRegenerated, RecoveryCodeUsed, PasskeyAdded, PasskeyRemoved,
        UserInvited, UserDeactivated,
        UserReactivated, RolesChanged, SettingsChanged,
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
