namespace Yojana.Platform.Contracts.Tenancy;

/// <summary>Resolves the public tenant code embedded in a QR URL without exposing the tenant registry.</summary>
public interface IPortalTenantResolver
{
    Task<TenantId?> ResolveCodeAsync(string code, CancellationToken cancellationToken);
}
