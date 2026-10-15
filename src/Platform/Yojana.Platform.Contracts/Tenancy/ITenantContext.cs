namespace Yojana.Platform.Contracts.Tenancy;

/// <summary>
/// The tenant the current unit of work acts for. Set once per HTTP request (from the signed-in
/// user's selected tenant), once per message handler (from the envelope) and once per scheduled job
/// (from the job data). Every module reads it; only the platform adapters set it.
/// </summary>
public interface ITenantContext
{
    /// <summary>True when a tenant has been established for this scope.</summary>
    bool HasTenant { get; }

    /// <summary>The current tenant. Throws <see cref="NoTenantException"/> when none is set.</summary>
    TenantId TenantId { get; }
}

public sealed class NoTenantException : InvalidOperationException
{
    public NoTenantException()
        : base("No tenant is set for the current scope. Tenant-scoped work must run inside a request, message handler or job that carries a tenant.")
    {
    }

    public NoTenantException(string message) : base(message) { }

    public NoTenantException(string message, Exception innerException) : base(message, innerException) { }
}
