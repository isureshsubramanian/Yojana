namespace Yojana.Platform.Contracts;

/// <summary>The canonical coded refusal shape used by every module and HTTP problem response.</summary>
public class CommandRefusedException(string rule, string message, string? unblockingStep = null)
    : InvalidOperationException(message)
{
    public string Rule { get; } = rule;

    public string? UnblockingStep { get; } = unblockingStep;
}
