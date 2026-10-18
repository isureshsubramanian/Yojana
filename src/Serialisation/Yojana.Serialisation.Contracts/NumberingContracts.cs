namespace Yojana.Serialisation.Contracts;

public static class NumberingMode
{
    public const string Sequence = "sequence";
    public const string GapFree = "gap_free";
    public static readonly IReadOnlyList<string> All = [Sequence, GapFree];
}

public sealed record NumberingSeriesSetting(string Series, string Label, string Mode, string Example, string GapBehaviour);

public sealed record NumberingSettingsView(IReadOnlyList<NumberingSeriesSetting> Series);

public sealed record SetNumberingModeRequest(Guid CommandId, string Mode);

public sealed record SetNumberingMode(Guid CommandId, string Series, string Mode, string ActorRef = "");

public sealed record SetNumberingModeResult(NumberingSeriesSetting Setting, bool Replayed);
