namespace Yojana.Platform.Contracts.Security;

public sealed record TenantStaffChoice(Guid UserId, string Name, IReadOnlyList<string> Roles);

public sealed record TenantStaffChoicePage(IReadOnlyList<TenantStaffChoice> Items, string? NextCursor);

public sealed record TenantRoleChoice(string Role, string Label, int People);

/// <summary>Checks question addressees without exposing platform membership tables to another module.</summary>
public interface ITenantStaffDirectory
{
    Task<bool> ActiveStaffMemberExistsAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> ActiveStaffRoleExistsAsync(string role, CancellationToken cancellationToken);

    /// <summary>Names-only picker data for another module; e-mail and membership persistence stay private.</summary>
    Task<TenantStaffChoicePage> ActiveStaffAsync(string? search, string? cursor, int take,
        CancellationToken cancellationToken);

    /// <summary>Only roles held by active staff are offered as question addressees.</summary>
    Task<IReadOnlyList<TenantRoleChoice>> ActiveRolesAsync(CancellationToken cancellationToken);
}
