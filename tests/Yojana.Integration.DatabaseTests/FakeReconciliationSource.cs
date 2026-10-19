using Yojana.Integration.Connectors;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Integration.Reconciliation;

namespace Yojana.Integration.DatabaseTests;

public sealed class FakeReconciliationSource : IConnectorAdapter
{
    private readonly object sync = new();
    private readonly Dictionary<(Guid ConnectionId, string Entity, DateOnly Day), List<InboundAcceptance>> messages = [];
    private readonly Dictionary<Guid, IReadOnlyList<ConnectorOnHandBalance>> onHand = [];
    private readonly HashSet<(Guid ConnectionId, string Entity)> ranged = [];
    private readonly Dictionary<Guid, TaskCompletionSource> readGates = [];
    private readonly Dictionary<Guid, int> readCalls = [];

    public string ConnectorCode => "CX01";
    public List<ConnectorPollRequest> Requests { get; } = [];
    public ConnectorPollResult NextResult { get; set; } = new([], null);
    public ConnectorOnHandRequest? LastOnHandRequest { get; private set; }
    public Guid? FailConnection { get; set; }
    public int? FailOnReadCall { get; set; }

    public int ReadCallCount(Guid connectionId)
    {
        lock (sync) return readCalls.GetValueOrDefault(connectionId);
    }

    public void Reset(Guid connectionId)
    {
        lock (sync)
        {
            foreach (var key in messages.Keys.Where(key => key.ConnectionId == connectionId).ToList())
                messages.Remove(key);
            onHand.Remove(connectionId);
            foreach (var key in ranged.Where(key => key.ConnectionId == connectionId).ToList()) ranged.Remove(key);
            readGates.Remove(connectionId);
            readCalls.Remove(connectionId);
        }
    }

    public void Seed(Guid connectionId, string entity, DateOnly day,
        params InboundAcceptance[] records)
    {
        lock (sync)
        {
            messages[(connectionId, entity, day)] = records.ToList();
            ranged.Add((connectionId, entity));
        }
    }

    public void SetOnHand(Guid connectionId, params ConnectorOnHandBalance[] balances)
    {
        lock (sync) onHand[connectionId] = balances;
    }

    public TaskCompletionSource BlockReads(Guid connectionId)
    {
        lock (sync)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            readGates[connectionId] = gate;
            return gate;
        }
    }

    public Task<IReadOnlyList<ConnectorEntityDayDigest>> EntityDayDigestsAsync(Guid connectionId,
        DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            var result = messages.Where(row => row.Key.ConnectionId == connectionId
                    && row.Key.Day >= rangeStart && row.Key.Day <= rangeEnd)
                .Select(row => Digest(row.Key.Entity, row.Key.Day, row.Value)).ToList();
            return Task.FromResult<IReadOnlyList<ConnectorEntityDayDigest>>(result);
        }
    }

    public Task<IReadOnlyList<ConnectorOnHandBalance>> OnHandAsync(ConnectorOnHandRequest request,
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            LastOnHandRequest = request;
            return Task.FromResult(onHand.GetValueOrDefault(request.ConnectionId)
                ?? (IReadOnlyList<ConnectorOnHandBalance>)[]);
        }
    }

    public Task<ConnectorPollResult> PollAsync(ConnectorPollRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(NextResult);
    }

    public Task<bool> SupportsRangedReadsAsync(Guid connectionId, string entity,
        CancellationToken cancellationToken)
    {
        lock (sync) return Task.FromResult(ranged.Contains((connectionId, entity)));
    }

    public async Task<ConnectorRangePage> ReadRangePageAsync(Guid connectionId, string entity,
        DateOnly rangeStart, DateOnly rangeEnd, string? cursor, int pageSize,
        CancellationToken cancellationToken)
    {
        Task? gate;
        int call;
        List<InboundAcceptance> records;
        lock (sync)
        {
            call = readCalls.GetValueOrDefault(connectionId) + 1;
            readCalls[connectionId] = call;
            gate = readGates.GetValueOrDefault(connectionId)?.Task;
            records = messages.Where(row => row.Key.ConnectionId == connectionId
                    && row.Key.Entity == entity && row.Key.Day >= rangeStart && row.Key.Day <= rangeEnd)
                .OrderBy(row => row.Key.Day).SelectMany(row => row.Value)
                .OrderBy(row => row.Envelope.ErpExternalId, StringComparer.Ordinal).ToList();
        }
        if (gate is not null) await gate.WaitAsync(cancellationToken);
        if (FailConnection == connectionId && FailOnReadCall == call)
        {
            FailOnReadCall = null;
            throw new InvalidOperationException("planted connector interruption");
        }
        var offset = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
        var page = records.Skip(offset).Take(pageSize).ToList();
        var next = offset + page.Count < records.Count
            ? (offset + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return new(page, next, records.Count);
    }

    private static ConnectorEntityDayDigest Digest(string entity, DateOnly day,
        List<InboundAcceptance> records)
    {
        var metrics = ReconciliationHash.Metrics(records.Select(row => row.CanonicalMessage));
        return new(entity, day, records.Count, metrics.Quantity, metrics.DistinctSerials,
            ReconciliationHash.KeyFields(records.Select(row =>
                (row.Envelope.ErpExternalId, row.CanonicalMessage))));
    }
}
