using System.Collections.Concurrent;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Security.Workers;

public sealed record ConnectionDailyBudgetState(
    ConnectionWorkerKey WorkerKey,
    DateOnly BudgetDateUtc,
    int UsedCalls,
    bool ApproachAlertRaised,
    bool CapAlertRaised,
    long Revision);

public interface IConnectionDailyBudgetStore
{
    Task<ConnectionDailyBudgetState?> LoadAsync(ConnectionWorkerKey workerKey, DateOnly budgetDateUtc,
        CancellationToken cancellationToken);

    Task<bool> TrySaveAsync(ConnectionDailyBudgetState state, long expectedRevision,
        CancellationToken cancellationToken);
}

public sealed class InMemoryConnectionDailyBudgetStore : IConnectionDailyBudgetStore
{
    private readonly ConcurrentDictionary<(ConnectionWorkerKey WorkerKey, DateOnly Date),
        ConnectionDailyBudgetState> states = new();

    public Task<ConnectionDailyBudgetState?> LoadAsync(ConnectionWorkerKey workerKey, DateOnly budgetDateUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(states.GetValueOrDefault((workerKey, budgetDateUtc)));
    }

    public Task<bool> TrySaveAsync(ConnectionDailyBudgetState state, long expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = (state.WorkerKey, state.BudgetDateUtc);
        var saved = expectedRevision == 0
            ? states.TryAdd(key, state)
            : states.TryGetValue(key, out var current) && current.Revision == expectedRevision
                && states.TryUpdate(key, state, current);
        return Task.FromResult(saved);
    }
}

public enum ConnectionCallBudgetAlertKind
{
    ApproachingDailyCap,
    DailyCapReached,
}

/// <summary>Narrow input for the P8-T13 connection-health projection.</summary>
public sealed record ConnectionCallBudgetAlert(
    ConnectionWorkerKey WorkerKey,
    DateOnly BudgetDateUtc,
    ConnectionCallBudgetAlertKind Kind,
    int UsedCalls,
    int DailyCap,
    int ThresholdPercent,
    DateTime OccurredAtUtc);

public interface IConnectionCallBudgetAlertPublisher
{
    Task PublishAsync(ConnectionCallBudgetAlert alert, CancellationToken cancellationToken);
}

public sealed class NullConnectionCallBudgetAlertPublisher : IConnectionCallBudgetAlertPublisher
{
    public Task PublishAsync(ConnectionCallBudgetAlert alert, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public sealed class ConnectionDailyCapExceededException(ConnectionWorkerKey workerKey, int dailyCap)
    : InvalidOperationException($"Connection {workerKey} has reached its daily call cap of {dailyCap}.")
{
    public ConnectionWorkerKey WorkerKey { get; } = workerKey;
    public int DailyCap { get; } = dailyCap;
}

internal sealed class ConnectionDailyBudget(
    IConnectionDailyBudgetStore store,
    IConnectionCallBudgetAlertPublisher alerts,
    IClock clock)
{
    public async Task ReserveAsync(ConnectionWorkerKey workerKey, ConnectionThrottleProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile.RequestsPerDay is not { } cap) return;
        var date = DateOnly.FromDateTime(clock.UtcNow);
        while (true)
        {
            var current = await store.LoadAsync(workerKey, date, cancellationToken)
                ?? new ConnectionDailyBudgetState(workerKey, date, 0, false, false, 0);
            if (current.UsedCalls >= cap) throw new ConnectionDailyCapExceededException(workerKey, cap);

            var used = current.UsedCalls + 1;
            var threshold = (int)Math.Ceiling(cap * profile.AlertThresholdPercent / 100m);
            var approach = !current.ApproachAlertRaised && used >= threshold;
            var reached = !current.CapAlertRaised && used >= cap;
            var updated = current with
            {
                UsedCalls = used,
                ApproachAlertRaised = current.ApproachAlertRaised || approach,
                CapAlertRaised = current.CapAlertRaised || reached,
                Revision = current.Revision + 1,
            };
            if (!await store.TrySaveAsync(updated, current.Revision, cancellationToken)) continue;

            if (approach)
                await alerts.PublishAsync(new(workerKey, date,
                    ConnectionCallBudgetAlertKind.ApproachingDailyCap, used, cap,
                    profile.AlertThresholdPercent, clock.UtcNow), cancellationToken);
            if (reached)
                await alerts.PublishAsync(new(workerKey, date,
                    ConnectionCallBudgetAlertKind.DailyCapReached, used, cap,
                    profile.AlertThresholdPercent, clock.UtcNow), cancellationToken);
            return;
        }
    }
}
