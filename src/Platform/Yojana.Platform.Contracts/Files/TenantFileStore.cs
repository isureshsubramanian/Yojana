using Yojana.Platform.Contracts.Records;

namespace Yojana.Platform.Contracts.Files;

/// <summary>The record whose existing read permissions govern both upload and download.</summary>
public sealed record TenantFileOwner(string RecordKind, Guid RecordId);

/// <summary>Claims-derived caller. Portal state is explicit so a role claim can never make a portal caller staff.</summary>
public sealed record TenantFileCaller(Guid UserId, IReadOnlyList<string> Roles, bool IsPortalUser)
{
    public RecordReferenceViewer Viewer => new(UserId, Roles);
}

public sealed record TenantFileUpload(string FileName, string ContentType, long ContentLength, Stream Content);

public sealed record TenantFileMetadata(Guid FileId, TenantFileOwner Owner, string FileName, string ContentType,
    long ContentLength, string Sha256, Guid UploadedBy, DateTime UploadedAt);

public sealed record OpenTenantFile(TenantFileMetadata Metadata, Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// Tenant-scoped immutable file storage. The implementation validates the caller against the owning record on every
/// put and read; a stored file has no replace operation.
/// </summary>
public interface ITenantFileStore
{
    Task<TenantFileMetadata> PutAsync(TenantFileOwner owner, TenantFileUpload upload, TenantFileCaller caller,
        CancellationToken cancellationToken);

    Task<TenantFileMetadata?> GetMetadataAsync(Guid fileId, TenantFileCaller caller,
        CancellationToken cancellationToken);

    Task<OpenTenantFile?> OpenReadAsync(Guid fileId, TenantFileCaller caller,
        CancellationToken cancellationToken);
}

/// <summary>
/// Narrow lifecycle seam for an owning module's recorded purge or redaction. Interactive code must use
/// <see cref="ITenantFileStore"/>; this port deliberately has no replace operation.
/// </summary>
public interface ITenantFileLifecycle
{
    Task<int> PurgeAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken);
}

public sealed class TenantFileRuleException(string rule, string message) : InvalidOperationException(message)
{
    public string Rule { get; } = rule;
}
