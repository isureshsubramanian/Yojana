namespace Yojana.Integration.Connectors;

/// <summary>Connector seam for an optional tenant-installed extension; connectors without one do not register it.</summary>
public interface IConnectionExtensionAdapter
{
    ConnectionExtensionDeclaration Declaration { get; }
    Task<ConnectionExtensionSnapshot> SnapshotAsync(Guid connectionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConnectionExtensionPageProbe>> ProbeAsync(Guid connectionId, bool outboundEnabled,
        CancellationToken cancellationToken);
    string InstallationGuide();
}

public sealed record ConnectionExtensionDeclaration(
    string ShippedVersion,
    string ContractVersion,
    bool PermissionSetWithoutFullAccessRole,
    IReadOnlyList<ConnectionExtensionPageDefinition> Pages);

public sealed record ConnectionExtensionPageDefinition(
    string Code,
    string Label,
    string? PollEntity,
    string? InboundEntity = null,
    bool OutboundOnly = false);

public sealed record ConnectionExtensionSnapshot(
    string InstalledVersion,
    IReadOnlyList<ConnectionExtensionNotification> Notifications,
    int RenewSubscriptionsBeforeHours);

public sealed record ConnectionExtensionNotification(string PageCode, DateTime ExpiresAt);

public sealed record ConnectionExtensionPageProbe(string PageCode, string Status, string? Reason);

public static class ConnectionExtensionPageStatus
{
    public const string Reachable = "reachable";
    public const string NotReachable = "not_reachable";
    public const string NotUsedYet = "not_used_yet";
}
