using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Rmas;

/// <summary>The six-stage lifecycle as an explicit table. Receipt may skip the unrecorded dispatch stage.</summary>
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

    public static void EnsureCanTransition(Data.Entities.Rma rma, string expectedNext)
    {
        var next = Next(rma.Stage);
        if (next != expectedNext)
            throw new CommandRefusedException("rma_transition_not_allowed", $"RMA {rma.RmaId} is '{rma.Stage}'; it cannot move to '{expectedNext}'.", $"the next allowed stage is '{next}'");
    }

    // Coupled deliberately to RmaStageCounter: stage is the source fact and its two rows in the RMA's stable counter
    // slot are the synchronous projection. Both change in Wolverine's transaction; bypassing this method makes the
    // KPI strip drift or moves a document between slots, defeating the writer's clustered-key lock order.
    public static async Task TransitionAsync(Data.Entities.Rma rma, string expectedNext,
        RmaStageCounterWriter counters, CancellationToken cancellationToken)
    {
        var previous = rma.Stage;
        EnsureCanTransition(rma, expectedNext);
        var next = expectedNext;
        await counters.MoveAsync(rma.RmaId, previous, next, rma.HasException, rma.HasException, cancellationToken);
        rma.Stage = next;
    }

    public static async Task ReceiveAsync(Data.Entities.Rma rma, bool hasException,
        RmaStageCounterWriter counters, CancellationToken cancellationToken)
    {
        var previous = rma.Stage;
        if (rma.Stage == RmaStage.Authorised)
        {
            await counters.MoveAsync(rma.RmaId, previous, RmaStage.Received, rma.HasException, hasException, cancellationToken);
            rma.Stage = RmaStage.Received;
            rma.HasException = hasException;
            return;
        }

        if (Next(rma.Stage) != RmaStage.Received)
            throw new CommandRefusedException("rma_transition_not_allowed", $"RMA {rma.RmaId} is '{rma.Stage}'; it cannot move to '{RmaStage.Received}'.", $"the next allowed stage is '{Next(rma.Stage)}'");
        await counters.MoveAsync(rma.RmaId, previous, RmaStage.Received, rma.HasException, hasException, cancellationToken);
        rma.Stage = RmaStage.Received;
        rma.HasException = hasException;
    }

    public static void EnsureCanReceive(Data.Entities.Rma rma)
    {
        if (rma.Stage is RmaStage.Authorised or RmaStage.InTransit) return;
        var next = Next(rma.Stage);
        throw new CommandRefusedException("rma_transition_not_allowed", $"RMA {rma.RmaId} is '{rma.Stage}'; it cannot move to '{RmaStage.Received}'.", $"the next allowed stage is '{next}'");
    }
}
