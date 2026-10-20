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
