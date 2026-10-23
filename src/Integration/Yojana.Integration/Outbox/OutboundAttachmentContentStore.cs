using Microsoft.Extensions.Configuration;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Outbox;

public sealed record OutboundAttachmentContent(byte[] Bytes, string ContentType);

/// <summary>Shared attachment-content boundary. Connectors own remote upload rules, not storage access.</summary>
public sealed class OutboundAttachmentContentStore(
    IConfiguration configuration,
    IRenderedOutboundDocumentPort? documents = null)
{
    public async Task<OutboundAttachmentContent?> ReadAsync(string contentRef, long maximumBytes,
        Func<string, string?> contentTypeForExtension, CancellationToken cancellationToken)
    {
        if (OutboundDocumentContentReference.TryDocumentId(contentRef, out var id))
        {
            if (documents is null) return null;
            var rendered = await documents.ReadAsync(id, cancellationToken);
            if (rendered is not null && rendered.Bytes.LongLength > maximumBytes)
                throw new OutboundAttachmentTooLargeException();
            return rendered is null ? null : new(rendered.Bytes, rendered.ContentType);
        }

        var root = configuration["Integration:AttachmentRoot"];
        if (string.IsNullOrWhiteSpace(root)) return null;
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(root, contentRef));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            return null;
        // A resolved link could leave the configured root; the local store deliberately accepts regular files only.
        for (var entry = new FileInfo(path) as FileSystemInfo; entry is not null;
             entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
        {
            if (entry.LinkTarget is not null) return null;
            if (entry.FullName == root) break;
        }
        var contentType = contentTypeForExtension(Path.GetExtension(path));
        if (contentType is null) return null;
        await using var stream = File.OpenRead(path);
        if (stream.Length > maximumBytes) throw new OutboundAttachmentTooLargeException();
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return new(bytes, contentType);
    }
}

public sealed class OutboundAttachmentTooLargeException : Exception
{
    public OutboundAttachmentTooLargeException() : base("The attachment exceeds the connection's size cap.") { }
}
