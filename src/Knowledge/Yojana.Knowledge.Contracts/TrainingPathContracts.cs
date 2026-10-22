namespace Yojana.Knowledge.Contracts;

public sealed record SaveTrainingPath(string Role, string Introduction, IReadOnlyList<Guid> EntryIds,
    int ExpectedVersion, QuestionActor Actor);

public sealed record MarkTrainingEntryRead(string Role, Guid EntryId, QuestionActor Actor);

public sealed record DismissTrainingWelcome(Guid PathId, QuestionActor Actor);

public sealed record TrainingPathChangeResult(Guid PathId, string Role, int Version);

public sealed record TrainingReadResult(Guid PathId, Guid EntryId, int EntryVersion, DateTime ReadAt);
