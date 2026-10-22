namespace Yojana.Platform.Contracts.Security;

public sealed record TenantStaffChoice(Guid UserId, string Name, IReadOnlyList<string> Roles);

public sealed record TenantStaffChoicePage(IReadOnlyList<TenantStaffChoice> Items, string? NextCursor);

public sealed record TenantRoleChoice(string Role, string Label, int People);

public sealed record TenantStaffNotificationRecipient(Guid UserId, string Name, string Email,
    IReadOnlyList<string> Roles);

public sealed record TenantTrainingMember(Guid UserId, string Name, IReadOnlyList<string> Roles,
    DateTime MembershipCreatedAt);

public sealed record TenantTrainingMemberPage(IReadOnlyList<TenantTrainingMember> Items, string? NextCursor);

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

    /// <summary>
    /// Resolves active staff recipients for an internal notification. Exactly one target is supplied; callers still
    /// decide what the recipient may see through the owning module's read port.
    /// </summary>
    Task<IReadOnlyList<TenantStaffNotificationRecipient>> NotificationRecipientsAsync(Guid? userId, string? role,
        CancellationToken cancellationToken);
}

/// <summary>
/// Tenant-membership facts needed by training paths. Knowledge owns reading progress; Platform keeps membership dates
/// and role membership behind this contract so the two modules never share tables or query ordering.
/// </summary>
public interface ITenantTrainingDirectory
{
    Task<TenantTrainingMember?> ActiveMemberAsync(Guid userId, CancellationToken cancellationToken);

    Task<TenantTrainingMemberPage> ActiveMembersInRoleAsync(string role, string? cursor, int take,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TenantTrainingMember>> AllActiveMembersInRoleAsync(string role,
        CancellationToken cancellationToken);
}
