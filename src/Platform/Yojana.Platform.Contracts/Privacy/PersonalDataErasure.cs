namespace Yojana.Platform.Contracts.Privacy;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PersonalDataAttribute : Attribute;

public enum ErasureSubjectKind
{
    Party,
    User,
}

public enum PersonalDataReviewAction
{
    Redact,
    Keep,
}

public sealed record PersonalDataReviewDecision(string ItemKey, PersonalDataReviewAction Action);

public sealed record ErasureSubject(
    ErasureSubjectKind Kind,
    Guid SubjectId,
    IReadOnlyList<string> KnownValues,
    IReadOnlyList<PersonalDataReviewDecision> ReviewDecisions)
{
    public string Token => Kind == ErasureSubjectKind.Party
        ? $"Erased customer · {Suffix}"
        : $"Former user · {Suffix}";

    public string Email => $"erased-{SubjectId:D}@invalid";

    public string Code => $"ERASED-{Suffix}";

    private string Suffix => SubjectId.ToString("N")[..4].ToUpperInvariant();
}

public sealed record PersonalDataStoreCount(string Kind, int Count, string Action);

public sealed record PersonalDataReviewItem(
    string ItemKey,
    string Store,
    string RecordReference,
    string Field,
    string? Excerpt);

public sealed record PersonalDataPreview(
    IReadOnlyList<PersonalDataStoreCount> Counts,
    IReadOnlyList<PersonalDataReviewItem> ReviewItems,
    IReadOnlyList<string> KnownValues);

public sealed record PersonalDataEraseResult(IReadOnlyList<PersonalDataStoreCount> Counts);

/// <summary>A module-owned erasure boundary. Implementations must be safe to repeat for the same erasure id.</summary>
public interface IPersonalDataHolder
{
    string Store { get; }

    Task<PersonalDataPreview> PreviewAsync(ErasureSubject subject, CancellationToken cancellationToken);

    Task<PersonalDataEraseResult> EraseAsync(ErasureSubject subject, Guid erasureId,
        CancellationToken cancellationToken);
}

public sealed record ErasedParty(Guid PartyRef, string Token);

/// <summary>The integration gateway's narrow tombstone read; it never reads Platform persistence.</summary>
public interface IPersonalDataErasureReadPort
{
    Task<ErasedParty?> ErasedPartyAsync(Guid partyRef, CancellationToken cancellationToken);
}
