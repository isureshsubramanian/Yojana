namespace Yojana.Platform.Contracts.Presence;

public static class PresenceMode
{
    public const string Viewing = "viewing";
    public const string Editing = "editing";

    public static bool IsKnown(string value) => value is Viewing or Editing;
}

public sealed record PresenceRequest(string DocumentType, string DocumentId, string Mode);

public sealed record LeavePresenceRequest(string DocumentType, string DocumentId);

public sealed record PresenceEntry(Guid UserId, string Mode, DateTime Since);

public sealed record PresenceResponse(IReadOnlyList<PresenceEntry> Users);

/// <summary>Short-lived, advisory document presence. It is not a document lock or a business record.</summary>
public interface IPresenceService
{
    Task<IReadOnlyList<PresenceEntry>> HeartbeatAsync(Guid userId, string documentType, string documentId,
        string mode, CancellationToken cancellationToken);

    Task LeaveAsync(Guid userId, string documentType, string documentId, CancellationToken cancellationToken);
}
