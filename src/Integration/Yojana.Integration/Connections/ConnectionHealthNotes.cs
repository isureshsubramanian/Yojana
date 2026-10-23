namespace Yojana.Integration.Connections;

/// <summary>Optional connector-owned note shown with health without changing the connection's derived health state.</summary>
public sealed record ConnectionHealthNote(string Code, string Message);

public interface IConnectionHealthNoteSource
{
    Task<ConnectionHealthNote?> ReadAsync(Guid connectionId, CancellationToken cancellationToken);
}
