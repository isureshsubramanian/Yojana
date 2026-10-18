namespace Yojana.Serialisation.Contracts;

/// <summary>
/// Implemented by edit commands that carry the row version read by their caller. API callers must provide it;
/// internal and event-driven callers may omit it when there is no user-held edit snapshot.
/// </summary>
public interface IExpectedDocumentVersion
{
    string? ExpectedVersion { get; }
}

public sealed class DocumentChangedException(string changedBy, DateTime changedAt)
    : CommandRefusedException(DocumentVersionRules.Changed, "This document changed after it was opened.", DocumentVersionRules.Reload)
{
    public string ChangedBy { get; } = changedBy;

    public DateTime ChangedAt { get; } = changedAt;
}

public static class DocumentVersionRules
{
    public const string Changed = "document_changed";
    public const string Reload = "Reload to see their change";
}
