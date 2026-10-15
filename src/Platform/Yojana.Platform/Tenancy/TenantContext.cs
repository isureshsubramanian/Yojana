using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Platform.Tenancy;

/// <summary>
/// Platform-internal side of <see cref="ITenantContext"/>: the request, message and job adapters set
/// the tenant once per scope through this interface. Modules never see it.
/// </summary>
public interface ITenantContextSetter
{
    void Set(TenantId tenantId);
}

/// <summary>Scoped holder of the current tenant. One scope serves exactly one tenant.</summary>
public sealed class TenantContext : ITenantContext, ITenantContextSetter
{
    private TenantId? tenantId;

    public bool HasTenant => tenantId is { IsEmpty: false };

    public TenantId TenantId => tenantId is { IsEmpty: false } id ? id : throw new NoTenantException();

    public void Set(TenantId tenantId)
    {
        if (tenantId.IsEmpty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        if (this.tenantId is { } existing && existing != tenantId)
        {
            throw new InvalidOperationException(
                $"The scope already serves tenant {existing}; it cannot be switched to {tenantId}. Open a new scope per tenant.");
        }

        this.tenantId = tenantId;
    }
}

/// <summary>A context with no tenant, for design-time tooling and platform-level operations only.</summary>
public sealed class NoTenantContext : ITenantContext
{
    public static readonly NoTenantContext Instance = new();

    public bool HasTenant => false;

    public TenantId TenantId => throw new NoTenantException();
}
