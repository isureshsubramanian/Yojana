namespace Yojana.Platform.Contracts.Security;

/// <summary>
/// Display names for the people behind actor references. Records keep the reference they were written with (a user id
/// stamped from the claims, or the e-mail address a client sent as actorRef) because names change; screens and reports
/// turn references into names at read time through this contract.
/// </summary>
public interface IUserDirectory
{
    /// <summary>
    /// The most references one call resolves; a larger request is refused, not truncated. The web client splits its
    /// batches at the same number (MAX_REFERENCES in web/src/people/peopleStore.ts): change both together, or a screen
    /// with more names than the limit gets a 400 and shows bare references.
    /// </summary>
    public const int MaxReferences = 500;

    /// <summary>The prefix of an automatic actor reference (a job or rule, never a person).</summary>
    public const string AutomaticActorPrefix = "system:";

    /// <summary>
    /// Resolves each reference — a user id ("D" format) or an e-mail address, matched case-insensitively — to the user's
    /// display name. Only users who are members of the current tenant resolve, deactivated ones included (they still
    /// appear in history). An automatic actor — a reference starting with "system:", stamped by a job or rule rather
    /// than a person — resolves to a readable label such as "Certificates (automatic)". A reference that does not
    /// resolve is absent from the result; callers show it as given.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> DisplayNamesAsync(IReadOnlyCollection<string> references, CancellationToken cancellationToken);
}
