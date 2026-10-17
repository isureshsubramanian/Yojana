using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Rmas;

/// <summary>The six-stage lifecycle as an explicit table; commands may traverse more than one adjacent edge atomically.</summary>
public static class RmaStateMachine
{
    private static readonly Dictionary<string, string> NextStage = new(StringComparer.Ordinal)
    {
        [RmaStage.Requested] = RmaStage.Authorised,
        [RmaStage.Authorised] = RmaStage.InTransit,
        [RmaStage.InTransit] = RmaStage.Received,
        [RmaStage.Received] = RmaStage.Dispositioned,
        [RmaStage.Dispositioned] = RmaStage.Closed,
    };

    public static string Next(string stage) => NextStage.TryGetValue(stage, out var next)
        ? next
        : throw new CommandRefusedException("rma_transition_not_allowed", $"An RMA at '{stage}' has no next stage.", "closed RMAs cannot be reopened");

    public static void Transition(Data.Entities.Rma rma, string expectedNext)
    {
        var next = Next(rma.Stage);
        if (next != expectedNext)
            throw new CommandRefusedException("rma_transition_not_allowed", $"RMA {rma.RmaId} is '{rma.Stage}'; it cannot move to '{expectedNext}'.", $"the next allowed stage is '{next}'");
        rma.Stage = next;
    }
}
