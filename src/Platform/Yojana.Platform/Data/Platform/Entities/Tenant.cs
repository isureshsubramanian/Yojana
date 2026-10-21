using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Platform.Data.Platform.Entities;

/// <summary>Tenant registry. Not tenant-scoped: the host reads it to resolve a tenant before any tenant context exists.</summary>
public sealed class Tenant
{
    public TenantId Id { get; set; }

    /// <summary>Short stable code used in URLs, job keys and logs (for example DEV).</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>Tenant-level configuration as key/value rows; system-versioned so a setting's history is queryable.</summary>
public sealed class TenantSetting : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public required string Key { get; set; }

    public required string Value { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>A sign-in identity. Users can belong to several tenants through <see cref="UserTenantMembership"/>.</summary>
public sealed class AppUser
{
    public Guid Id { get; set; }

    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>PBKDF2 hash in the <see cref="Security.PasswordHasher"/> format; null when sign-in is delegated to an identity provider.</summary>
    public string? PasswordHash { get; set; }

    public bool IsActive { get; set; } = true;

    public Guid SecurityStamp { get; set; } = Guid.NewGuid();

    public bool MustChangePassword { get; set; }

    public DateTime? TemporaryPasswordExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<UserTenantMembership> Memberships { get; } = [];
}

/// <summary>
/// Global failed-attempt state. It deliberately has no tenant id: an identity can belong to several tenants and the
/// state is consulted before tenant selection. The key is a one-way digest, never an e-mail address or client address.
/// </summary>
public sealed class SignInThrottle
{
    public required string KeyKind { get; set; }
    public required byte[] KeyHash { get; set; }
    public DateTime WindowStartedAt { get; set; }
    public int FailureCount { get; set; }
    public int LockoutCount { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Global security audit. TenantId is nullable because sign-in occurs before tenant selection; tenant visibility is
/// enforced by SecurityEventQueries using the selected tenant and memberships rather than by tenant RLS.
/// </summary>
public sealed class SecurityEvent
{
    public Guid Id { get; set; }
    public DateTime OccurredAt { get; set; }
    public required string Kind { get; set; }
    public Guid? UserId { get; set; }
    public TenantId? TenantId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ClientAddress { get; set; }
    public string? UserAgent { get; set; }
    public required string Reason { get; set; }
    public string? UnknownUserHash { get; set; }
}

/// <summary>Which roles a user holds in a tenant. Read before the tenant is selected, so not under row-level security.</summary>
public sealed class UserTenantMembership
{
    public Guid UserId { get; set; }

    public TenantId TenantId { get; set; }

    public Guid? CustomerPartyRef { get; set; }

    public List<string> Roles { get; set; } = [];

    public DateTime? DeactivatedAt { get; set; }

    public Guid? DeactivatedByUserId { get; set; }

    public byte[] Version { get; set; } = [];

    public AppUser? User { get; set; }

    public Tenant? Tenant { get; set; }
}

/// <summary>P0 smoke evidence: one row per handled smoke message or executed smoke job, per tenant.</summary>
public sealed class SmokeLog : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid Id { get; set; }

    /// <summary>request, handler or job.</summary>
    public required string Source { get; set; }

    public required string Note { get; set; }

    public DateTime RecordedAt { get; set; }
}

/// <summary>Ephemeral advisory state; never copied to a document, ledger or outbox.</summary>
public sealed class DocumentPresence : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid UserId { get; set; }

    public required string DocumentType { get; set; }

    public required string DocumentId { get; set; }

    public required string Mode { get; set; }

    public DateTime Since { get; set; }

    public DateTime LastHeartbeat { get; set; }
}

/// <summary>
/// Delivery log of one queued e-mail (P9-T23): who it went to, what kind, and how delivery went. It deliberately has no
/// row-level security and a nullable tenant: account mail about a global identity is sent before any tenant is chosen,
/// like <see cref="SecurityEvent"/>. Readers filter by tenant themselves (MailEndpoints). The bodies are never stored
/// here: they travel only in the delivery message, which Wolverine deletes once the attempt is handled, because account
/// mail carries single-use links.
/// </summary>
public sealed class MailMessage
{
    public Guid Id { get; set; }
    public DateTime QueuedAt { get; set; }
    public required string Kind { get; set; }
    public TenantId? TenantId { get; set; }
    public Guid? UserId { get; set; }
    public required string Recipient { get; set; }
    public required string Subject { get; set; }
    public required string Status { get; set; }
    public required string Transport { get; set; }
    public int Attempts { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime? SentAt { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? LastError { get; set; }
}

/// <summary>The stored values are the published ones, so <see cref="MailDeliveryRecorded"/> carries them unchanged.</summary>
public static class MailStatus
{
    public const string Retrying = MailDeliveryStatus.Retrying;
    public const string Sent = MailDeliveryStatus.Sent;
    public const string Failed = MailDeliveryStatus.Failed;
}

/// <summary>
/// Global password-reset state. It deliberately has no tenant id because a user is a global identity and no tenant
/// exists before sign-in. TokenHash is the SHA-256 digest of the single-use token; the token itself is never stored.
/// </summary>
public sealed class PasswordResetToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required byte[] TokenHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime? SupersededAt { get; set; }
}

/// <summary>
/// A single-use tenant invitation. This table is deliberately global, like <see cref="SecurityEvent"/>, because
/// inspection and acceptance happen before a tenant is known. Every tenant-side reader must filter TenantId itself;
/// there is no RLS safety net for this table.
/// </summary>
public sealed class UserInvitation
{
    public Guid Id { get; set; }
    public TenantId TenantId { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public List<string> Roles { get; set; } = [];
    public required byte[] TokenHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public Guid? AcceptedUserId { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public Tenant? Tenant { get; set; }
}

/// <summary>
/// A global user's authenticator enrolment. This deliberately has no tenant id: sign-in and enrolment happen before
/// tenant selection and one identity may span tenants. Tenant policy is consulted through active memberships.
/// </summary>
public sealed class UserAuthenticator
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required byte[] SealedSecret { get; set; }
    public required string Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public long? LastUsedStep { get; set; }
    public DateTime? ReplacedAt { get; set; }
}

public static class UserAuthenticatorStatus
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Replaced = "replaced";
}

/// <summary>
/// One global, single-use recovery code. Only a salted PBKDF2 digest is stored; the friendly eight-character display
/// format has 40 bits, so a deliberately slow hash protects it from cheap offline enumeration.
/// </summary>
public sealed class UserRecoveryCode
{
    public Guid UserId { get; set; }
    public required byte[] CodeHash { get; set; }
    public DateTime? UsedAt { get; set; }
    public Guid BatchId { get; set; }
}
