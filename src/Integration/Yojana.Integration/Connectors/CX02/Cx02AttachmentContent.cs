using Yojana.Integration.Outbox;

namespace Yojana.Integration.Connectors.CX02;

public interface ICx02AttachmentContent
{
    void BeginAttempt() { }
    Task<OutboundAttachmentContent?> ReadAsync(string contentRef, long maximumBytes,
        CancellationToken cancellationToken);
}

internal sealed class Cx02AttachmentContent(OutboundAttachmentContentStore store) : ICx02AttachmentContent
{
    private readonly Dictionary<string, OutboundAttachmentContent> attemptContent = new(StringComparer.Ordinal);

    public void BeginAttempt() => attemptContent.Clear();

    public async Task<OutboundAttachmentContent?> ReadAsync(string contentRef, long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!attemptContent.TryGetValue(contentRef, out var content))
        {
            content = await store.ReadAsync(contentRef, maximumBytes, Cx02AttachmentMapper.ContentType,
                cancellationToken);
            if (content is null) return null;
            attemptContent.Add(contentRef, content);
        }
        if (content.Bytes.LongLength > maximumBytes) throw new OutboundAttachmentTooLargeException();
        return content;
    }
}
