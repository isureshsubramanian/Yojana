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

    public DateTime CreatedAt { get; set; }

    public List<UserTenantMembership> Memberships { get; } = [];
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
