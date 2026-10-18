namespace Yojana.Serialisation.Contracts;

public sealed record LabelPrinterRequest(Guid JobId, string PrinterRef, string LayoutKind, string Payload, string PayloadHash);

public sealed record LabelPrinterReceipt(bool Confirmed, string? Reference = null, string? RejectionReason = null);

/// <summary>Printer integration boundary. Implementations must de-duplicate by <see cref="LabelPrinterRequest.JobId"/>.</summary>
public interface ILabelPrinterPort
{
    Task<LabelPrinterReceipt> SendAsync(LabelPrinterRequest request, CancellationToken cancellationToken);
}

public sealed record BarcodeEncoding(string MediaType, string Data);

/// <summary>Deferred P6-T11 barcode/QR implementation boundary; the current stub returns no image.</summary>
public interface IBarcodeEncoder
{
    Task<BarcodeEncoding?> EncodeAsync(string symbology, string value, CancellationToken cancellationToken);
}
