using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Yojana.Integration.Connectors.CX01;

public sealed record Cx01AttachmentContent(byte[] Bytes, string ContentType);

/// <summary>Content storage seam. The development implementation reads only inside the configured attachment root.</summary>
public interface ICx01AttachmentContent
{
    /// <summary>Start a relay/correction attempt. Subsequent reads share the validated bytes until the next attempt.</summary>
    void BeginAttempt() { }
    Task<Cx01AttachmentContent?> ReadAsync(string contentRef, long maximumBytes, CancellationToken ct);
}

internal sealed class Cx01LocalAttachmentContent(IConfiguration configuration, Yojana.Serialisation.Contracts.IRenderedOutboundDocumentPort? documents = null) : ICx01AttachmentContent
{
    private readonly Dictionary<string, Cx01AttachmentContent> attemptContent = new(StringComparer.Ordinal);
    public void BeginAttempt() => attemptContent.Clear();

    public async Task<Cx01AttachmentContent?> ReadAsync(string contentRef, long maximumBytes, CancellationToken ct)
    {
        if (!attemptContent.TryGetValue(contentRef, out var file))
        {
            file = await ReadCoreAsync(contentRef, maximumBytes, ct);
            if (file is null) return null;
            attemptContent.Add(contentRef, file);
        }
        if (file.Bytes.LongLength > maximumBytes) throw new Cx01DocumentRuleException("The attachment exceeds the connection's size cap.");
        return file;
    }

    private async Task<Cx01AttachmentContent?> ReadCoreAsync(string contentRef, long maximumBytes, CancellationToken ct)
    {
        if (Yojana.Serialisation.Contracts.OutboundDocumentContentReference.TryDocumentId(contentRef, out var id))
        {
            if (documents is null) return null;
            var rendered = await documents.ReadAsync(id, ct);
            if (rendered is not null && rendered.Bytes.LongLength > maximumBytes)
                throw new Cx01DocumentRuleException("The attachment exceeds the connection's size cap.");
            return rendered is null ? null : new(rendered.Bytes, rendered.ContentType);
        }
        var root = configuration["Integration:AttachmentRoot"];
        if (string.IsNullOrWhiteSpace(root)) return null;
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(root, contentRef));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) return null;
        // A resolved link could leave the configured root; the local store deliberately accepts regular files only.
        for (var entry = new FileInfo(path) as FileSystemInfo; entry is not null; entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
            {
            if (entry.LinkTarget is not null) return null;
            if (entry.FullName == root) break;
        }
        var contentType = Cx01AttachmentMapper.ContentType(Path.GetExtension(path));
        if (contentType is null) return null;
        await using var stream = File.OpenRead(path);
        if (stream.Length > maximumBytes) throw new Cx01DocumentRuleException("The attachment exceeds the connection's size cap.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, ct);
        return new(bytes, contentType);
    }
}

internal sealed record Cx01MultipartUpload(string FileName, byte[] Bytes, string ContentType)
{
    public HttpContent CreateContent()
    {
        // The remote rejects the framework defaults with "Bad Content Disposition" (seen live on FP 2608): the part's
        // Content-Disposition must come BEFORE its Content-Type, and the extra filename* parameter is not accepted.
        // Headers are written in insertion order, so the disposition is added first. File names are generated ASCII.
        var boundary = "yojana-" + Guid.NewGuid().ToString("N");
        var multipart = new MultipartFormDataContent(boundary);
        multipart.Headers.Remove("Content-Type");
        multipart.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);
        var file = new ByteArrayContent(Bytes);
        file.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"files\"; filename=\"{FileName}\"");
        file.Headers.ContentType = new(ContentType);
        multipart.Add(file);
        return multipart;
    }
}

/// <summary>Attachments2 uploads multipart files and returns AbsoluteEntry; a target's AttachmentEntry links it.
/// A deterministic FileName carries the reference, since the attachment entity has no header reference field.</summary>
internal static class Cx01AttachmentMapper
{
    public static string? ContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".txt" => "text/plain",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", _ => null,
    };

    public static string FileName(string reference, string displayName, Cx01DocumentSettings settings)
    {
        var extension = Path.GetExtension(displayName).TrimStart('.').ToLowerInvariant();
        if (extension.Length == 0) throw new Cx01UnmappableException("missing attachment file extension in the module event");
        if (!settings.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new Cx01DocumentRuleException("The attachment file type is not allowed on this connection.");
        return "yj-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference))).ToLowerInvariant() + "." + extension;
    }

    public static (string Collection, string Key) Target(string reference)
    {
        var parts = reference.Split(':', 2);
        if (parts.Length != 2 || !LinkCollections.Contains(parts[0], StringComparer.Ordinal))
            throw new Cx01UnmappableException("The attachment target needs a mapped document or equipment-card reference.");
        return (parts[0], Cx01DocumentBody.Entry(parts[1]).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static IReadOnlyList<string> LinkCollections { get; } = ["DeliveryNotes", "CreditNotes", "Returns", "CustomerEquipmentCards"];
}
