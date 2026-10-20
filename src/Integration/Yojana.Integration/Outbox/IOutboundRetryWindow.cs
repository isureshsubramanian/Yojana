namespace Yojana.Integration.Outbox;

/// <summary>An explicit operator retry starts a fresh bounded window without changing the command's creation time.</summary>
public interface IOutboundRetryWindow
{
    Task<DateTime?> StartedAtAsync(Guid outboundCommandId, CancellationToken cancellationToken);
}
