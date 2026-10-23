using Yojana.Integration.Connectors;

namespace Yojana.Integration.Connections;

public sealed record ConnectionExtensionStatusView(
    Guid ConnectionId,
    string ConnectionName,
    ConnectionFeatures Features,
    string InstalledVersion,
    string ShippedVersion,
    bool IsNewerThanOnePointZero,
    string ContractVersion,
    DateTime? LastCheckedAt,
    string? LastCheckedBy,
    int ReachablePages,
    int UsedPages,
    ConnectionExtensionAccessView Access,
    ConnectionExtensionNotificationsView Notifications,
    IReadOnlyList<ConnectionExtensionPageView> Pages,
    string InstallationGuidePath);

public sealed record ConnectionExtensionAccessView(
    bool ApplicationRegistrationPerTenant,
    bool PermissionSetWithoutFullAccessRole,
    bool? FullAccessRoleAssigned,
    DateTime? CredentialExpiresAt,
    DateTime? CredentialReminderAt,
    DateTime? CredentialReplaceBy);

public sealed record ConnectionExtensionNotificationsView(int LiveCount, DateTime? NextRenewalAt);

public sealed record ConnectionExtensionPageView(
    string Code,
    string Label,
    string ContractVersion,
    string Status,
    string? Reason,
    long? RowsReadToday,
    DateTime? LastChangeSeen,
    string NotificationStatus,
    DateTime? NotificationExpiresAt,
    int? FallbackPollIntervalSeconds);

internal sealed record ConnectionExtensionCheckAudit(
    string InstalledVersion,
    string ContractVersion,
    IReadOnlyList<ConnectionExtensionPageProbe> Pages);
