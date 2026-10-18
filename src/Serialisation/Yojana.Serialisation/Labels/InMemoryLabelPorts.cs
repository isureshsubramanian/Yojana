using System.Collections.Concurrent;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Labels;

/// <summary>Development/test printer. Job id is its idempotency key, matching the production port contract.</summary>
public sealed class InMemoryLabelPrinterPort : ILabelPrinterPort
{
    private readonly ConcurrentDictionary<Guid, LabelPrinterRequest> sent = new();

    public IReadOnlyCollection<LabelPrinterRequest> Sent => sent.Values.ToList();

    public Task<LabelPrinterReceipt> SendAsync(LabelPrinterRequest request, CancellationToken cancellationToken)
    {
        sent.TryAdd(request.JobId, request);
        return Task.FromResult(new LabelPrinterReceipt(true, $"memory:{request.JobId:N}"));
    }
}

/// <summary>P6-T11 is licence-blocked; layouts may still contain native ZPL barcode commands as ordinary text.</summary>
public sealed class StubBarcodeEncoder : IBarcodeEncoder
{
    public Task<BarcodeEncoding?> EncodeAsync(string symbology, string value, CancellationToken cancellationToken) =>
        Task.FromResult<BarcodeEncoding?>(null);
}
