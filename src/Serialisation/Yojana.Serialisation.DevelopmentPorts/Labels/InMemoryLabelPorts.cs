using System.Collections.Concurrent;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Labels;

/// <summary>Development/test printer. Job id is its idempotency key, matching the production port contract.</summary>
[DevelopmentOnly]
public sealed class InMemoryLabelPrinterPort : ILabelPrinterPort
{
    private readonly ConcurrentDictionary<Guid, LabelPrinterRequest> sent = new();

    public IReadOnlyCollection<LabelPrinterRequest> Sent => sent.Values.ToList();

    public Task<LabelPrinterReceipt> SendAsync(LabelPrinterRequest request, CancellationToken cancellationToken)
    {
        sent.TryAdd(request.JobId, request);
        return Task.FromResult(new LabelPrinterReceipt(true, $"memory:{request.JobId:N}"));
    }

    public Task<LabelPrinterReceipt> SendBatchAsync(LabelPrinterBatchRequest request, CancellationToken cancellationToken)
    {
        foreach (var label in request.Labels) sent.TryAdd(label.JobId, label);
        return Task.FromResult(new LabelPrinterReceipt(true, $"memory:batch:{request.Labels.Count}"));
    }
}
