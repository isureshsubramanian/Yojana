namespace Yojana.Platform.Contracts.Security;

/// <summary>Checks question addressees without exposing platform membership tables to another module.</summary>
public interface ITenantStaffDirectory
{
    Task<bool> ActiveStaffMemberExistsAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> ActiveStaffRoleExistsAsync(string role, CancellationToken cancellationToken);
}
