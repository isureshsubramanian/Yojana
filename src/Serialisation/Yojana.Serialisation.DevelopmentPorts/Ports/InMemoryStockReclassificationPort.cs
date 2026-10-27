using System.Collections.Concurrent;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Ports;

// TIGHT COUPLING (declared, ADR-0001 "Integration boundaries and coupling", coupling 1)
// WHAT:  a production IStockReclassificationPort implementation posts the Inventory reclassification in the SAME local
//        database transaction as CorrectItem's assignment and ledger fact.
// WHY:   on-hand quantity for each serial-tracked item/location/disposition bucket must equal its in-stock unit count at
//        every commit; an asynchronous hand-off exposes one item or the other with the wrong count.
// WHAT BREAKS IF SEPARATED: a crash or retry can leave the stock quantity under the previous item and the unit under the
//        corrected item. This default deliberately only records requests; P12 supplies the in-house posting and P8 the
//        connector implementation. Change this comment with the port and CorrectItem caller.

/// <summary>Development/test default: records idempotent requests and performs no Inventory posting.</summary>
[DevelopmentOnly]
public sealed class InMemoryStockReclassificationPort : IStockReclassificationPort
{
    private readonly ConcurrentDictionary<Guid, StockReclassificationRequest> requests = new();

    public IReadOnlyCollection<StockReclassificationRequest> Requests => requests.Values.ToList();

    public Task<StockReclassificationResult> ReclassifyAsync(StockReclassificationRequest request, CancellationToken cancellationToken)
    {
        var recorded = requests.GetOrAdd(request.CorrectionId, request);
        if (recorded != request)
        {
            throw new CommandRefusedException("stock_reclassification_conflict", $"Correction {request.CorrectionId} already names another stock reclassification.");
        }

        return Task.FromResult(new StockReclassificationResult("in-memory:" + request.CorrectionId.ToString("N")));
    }
}
