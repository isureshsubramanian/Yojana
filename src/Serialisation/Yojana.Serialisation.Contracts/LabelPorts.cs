namespace Yojana.Serialisation.Contracts;

public sealed record LabelPrinterRequest(
    Guid JobId,
    string PrinterRef,
    string LayoutKind,
    string Payload,
    string PayloadHash,
    decimal? WidthMm = null,
    decimal? HeightMm = null,
    LabelPrintLayout? PrintLayout = null,
    LabelSheetProfile? SheetProfile = null);

public sealed record LabelPrinterBatchRequest(IReadOnlyList<LabelPrinterRequest> Labels);

public sealed record LabelPrinterReceipt(
    bool Confirmed,
    string? Reference = null,
    string? RejectionReason = null,
    LabelDownload? Download = null);

/// <summary>Printer integration boundary. Implementations must de-duplicate by <see cref="LabelPrinterRequest.JobId"/>.</summary>
public interface ILabelPrinterPort
{
    Task<LabelPrinterReceipt> SendAsync(LabelPrinterRequest request, CancellationToken cancellationToken);
    Task<LabelPrinterReceipt> SendBatchAsync(LabelPrinterBatchRequest request, CancellationToken cancellationToken);
}

public sealed record BarcodeEncoding(
    string Symbology,
    string MediaType,
    string DataUrl,
    int Width,
    int Height,
    int QuietZone,
    string Content);

/// <summary>Barcode generation boundary. Implementations return self-contained preview data and never own label layout.</summary>
public interface IBarcodeEncoder
{
    Task<BarcodeEncoding> EncodeAsync(string symbology, string value, CancellationToken cancellationToken);
}

/// <summary>Deployment-owned route from a serial value to the lookup entry point encoded by QR labels.</summary>
public interface ILabelLookupUrlBuilder
{
    string ForSerial(string serial);
}
