namespace Yojana.Knowledge.Questions;

public sealed class QuestionRuleException(string rule, string message, string? unblockingStep = null)
    : InvalidOperationException(message)
{
    public string Rule { get; } = rule;
    public string? UnblockingStep { get; } = unblockingStep;
}
