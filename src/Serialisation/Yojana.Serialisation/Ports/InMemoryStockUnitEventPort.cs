using System.Collections.Concurrent;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Ports;

// TIGHT COUPLING (declared, ADR-0001 "Integration boundaries and coupling", coupling 1)
// WHAT:  Warranty replacement calls IStockUnitEventPort.IssueWarrantyReplacementAsync(...), while document and
//        replacement-link corrections call IStockUnitEventPort.RecordCorrectionForPosting(...); Inventory must handle each inside the SAME database
//        transaction that writes the corresponding stock ledger line and valuation layer.
// WHY:   The invariant "on-hand quantity of a serial-tracked item at a location equals the count of
//        units in stock at that location" (ADR-0001, edge case 34) must hold at every commit. An outbox
//        or event hand-off would allow a committed quantity with no units (or units with no quantity)
//        between the two writes, which the nightly check would only detect, not prevent.
// WHAT BREAKS IF SEPARATED: serial counts and stock quantities can diverge on any crash or retry
//        between the two writes; the WarrantyReplacement and return receipts could post stock without
//        serial events; the reconciliation job would emit mismatch_quantity exceptions that no one can
//        resolve from history. Change the port signature and this comment together, on both sides.
// NOTE:  Serialisation.CorrectDocumentSerials (ADR-0001 cases 32 and 141) and CorrectReplacementLink (case 144) call RecordCorrectionForPosting(...)
//        on this same port with a quantity delta of ZERO: it replaces which units a posted document
//        refers to, or removes units recorded as shipped when none physically left, while the posted document's
//        quantity remains unchanged. The port asserts the zero delta; a quantity correction is a document reversal.
// Engine: SQL Server / Azure SQL; the pairing relies on a single local transaction, never on a distributed one.

/// <summary>Development/test boundary until Inventory supplies the same-transaction implementation.</summary>
public sealed class InMemoryStockUnitEventPort : IStockUnitEventPort
{
    public ConcurrentQueue<StockPostingSerialCorrection> Corrections { get; } = new();

    // P5 RMA return-receipt extension — keep this block separate so sibling port additions merge mechanically.
    public ConcurrentQueue<StockReturnPosting> Returns { get; } = new();
    // End P5 RMA return-receipt extension.
    public ConcurrentQueue<WarrantyReplacementStockIssue> ReplacementIssues { get; } = new();

    public Task<WarrantyReplacementStockIssueResult> IssueWarrantyReplacementAsync(WarrantyReplacementStockIssue issue, CancellationToken cancellationToken)
    {
        ReplacementIssues.Enqueue(issue);
        return Task.FromResult(new WarrantyReplacementStockIssueResult($"WRP-STOCK-{issue.WarrantyReplacementId:N}"));
    }

    public Task RecordCorrectionForPosting(StockPostingSerialCorrection correction, CancellationToken cancellationToken)
    {
        if (correction.QuantityDelta != 0)
        {
            throw new CommandRefusedException("document_correction_quantity_delta", "A document-serial correction must have a zero quantity delta.", "reverse the posted document and make a new posting when quantity changes");
        }

        Corrections.Enqueue(correction);
        return Task.CompletedTask;
    }

    // P5 RMA return-receipt extension — keep this block separate so sibling port additions merge mechanically.
    public Task RecordReturnForPosting(StockReturnPosting request, CancellationToken cancellationToken)
    {
        Returns.Enqueue(request);
        return Task.CompletedTask;
    }
    // End P5 RMA return-receipt extension.
}
