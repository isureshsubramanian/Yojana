using System.Collections.Concurrent;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Ports;

// TIGHT COUPLING (declared, ADR-0001 "Integration boundaries and coupling", coupling 1)
// WHAT:  Inventory.StockPostingService calls Serialisation.IStockUnitEventPort.RecordForPosting(...)
//        inside the SAME database transaction that writes the stock ledger line and valuation layer.
// WHY:   The invariant "on-hand quantity of a serial-tracked item at a location equals the count of
//        units in stock at that location" (ADR-0001, edge case 34) must hold at every commit. An outbox
//        or event hand-off would allow a committed quantity with no units (or units with no quantity)
//        between the two writes, which the nightly check would only detect, not prevent.
// WHAT BREAKS IF SEPARATED: serial counts and stock quantities can diverge on any crash or retry
//        between the two writes; the WarrantyReplacement and return receipts could post stock without
//        serial events; the reconciliation job would emit mismatch_quantity exceptions that no one can
//        resolve from history. Change the port signature and this comment together, on both sides.
// NOTE:  Serialisation.CorrectDocumentSerials (ADR-0001 case 141) calls RecordCorrectionForPosting(...)
//        on this same port with a quantity delta of ZERO: it replaces which units a posted document
//        refers to (same item, same quantity) and touches only the serial side of the pairing. The port
//        asserts the zero delta; a correction that would change quantity is a reversal plus a new posting.
// Engine: SQL Server / Azure SQL; the pairing relies on a single local transaction, never on a distributed one.

/// <summary>Development/test boundary until Inventory supplies the same-transaction implementation.</summary>
public sealed class InMemoryStockUnitEventPort : IStockUnitEventPort
{
    public ConcurrentQueue<StockPostingSerialCorrection> Corrections { get; } = new();

    public Task RecordCorrectionForPosting(StockPostingSerialCorrection correction, CancellationToken cancellationToken)
    {
        if (correction.QuantityDelta != 0)
        {
            throw new CommandRefusedException("document_correction_quantity_delta", "A document-serial correction must have a zero quantity delta.", "reverse the posted document and make a new posting when quantity changes");
        }

        Corrections.Enqueue(correction);
        return Task.CompletedTask;
    }
}
