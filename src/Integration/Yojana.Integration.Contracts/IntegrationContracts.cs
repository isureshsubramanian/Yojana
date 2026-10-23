using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yojana.Integration.Contracts;

public static class IntegrationContracts
{
    public const string ModuleName = "Integration";
    public const string CurrentVersion = "1.0";
    public const int CurrentMajor = 1;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        WriteIndented = true,
    };

    public static bool Accepts(string version)
    {
        if (!Version.TryParse(version, out var parsed)) return false;
        return parsed.Major is CurrentMajor or CurrentMajor - 1;
    }
}

/// <summary>Envelope carried by every canonical event and command.</summary>
public sealed record CanonicalEnvelope(
    string ContractVersion,
    Guid MessageId,
    Guid TenantId,
    Guid ConnectionId,
    string ConnectorCode,
    string ErpEntity,
    string ErpExternalId,
    ChangeMarker ErpChangeMarker,
    DateTimeOffset OccurredAt,
    DateTimeOffset ObservedAt,
    OpenEnum CaptureMode,
    OpenEnum AttributionCompleteness,
    string PayloadRef,
    Guid? CorrelationId = null);

public sealed record ChangeMarker(string Value, OpenEnum MarkerKind);

/// <summary>
/// A forward-compatible contract value. Unknown wire values become <c>other</c>, while RawValue keeps the exact value
/// for mapping exceptions and later replay. The converter also reads the explicit { value, raw_value } form.
/// </summary>
[JsonConverter(typeof(OpenEnumJsonConverter))]
public sealed record OpenEnum
{
    private OpenEnum(string value, string? rawValue)
    {
        Value = value;
        RawValue = rawValue;
    }

    public const string Other = "other";
    public string Value { get; }
    public string? RawValue { get; }

    public static OpenEnum Known(string value) => new(value, null);

    public static OpenEnum Read(string value, IReadOnlySet<string> known) =>
        known.Contains(value) ? Known(value) : new(Other, value);
}

public sealed class OpenEnumJsonConverter : JsonConverter<OpenEnum>
{
    private static readonly HashSet<string> KnownValues = new(StringComparer.Ordinal)
    {
        "observed", "timestamp", "sequence", "etag", "row_version",
        "webhook", "poll", "file", "edi", "backfill", "replay", "manual",
        "complete", "partial", "unknown",
        "none", "serial", "lot", "serial_and_lot",
        "customer", "vendor", "both",
        "in", "out", "count", "status_change",
        "cancelled", "reversed", "deleted", "voided",
        "zero", "difference", "full",
        "created", "already_existed", "rejected", "unsupported", "manual_task_raised",
        OpenEnum.Other,
    };

    public override OpenEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return OpenEnum.Read(reader.GetString() ?? string.Empty, KnownValues);
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var value = root.GetProperty("value").GetString() ?? OpenEnum.Other;
        var raw = root.TryGetProperty("raw_value", out var rawValue) ? rawValue.GetString() : null;
        return value == OpenEnum.Other && raw is not null
            ? new OpenEnumJsonValue(raw).AsOther()
            : OpenEnum.Read(value, KnownValues);
    }

    public override void Write(Utf8JsonWriter writer, OpenEnum value, JsonSerializerOptions options)
    {
        if (value.RawValue is null)
        {
            writer.WriteStringValue(value.Value);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("value", OpenEnum.Other);
        writer.WriteString("raw_value", value.RawValue);
        writer.WriteEndObject();
    }

    private sealed record OpenEnumJsonValue(string Raw)
    {
        public OpenEnum AsOther() => OpenEnum.Read(Raw, new HashSet<string>(StringComparer.Ordinal));
    }
}

public sealed record CanonicalMessage<TPayload>(CanonicalEnvelope Envelope, string MessageType, TPayload Payload);

public sealed record ExternalDocumentRef(string Type, string Number, DateOnly Date, string? ExternalId = null, int? Line = null);
public sealed record CanonicalSerial(string Value, string? Issuer = null, string? ErpSerialId = null);
public sealed record CanonicalLot(string Value, DateOnly? ExpiryDate = null);
public sealed record CanonicalLine(int LineNo, string ErpLineId, string ItemExternalId, decimal Quantity, string Uom,
    IReadOnlyList<CanonicalSerial> Serials, IReadOnlyList<CanonicalLot> Lots, string? LocationExternalId = null,
    ExternalDocumentRef? BaseDocumentRef = null, string? PartyExternalId = null, decimal? Price = null,
    string? Currency = null);
public sealed record Money(decimal Amount, string Currency);
public sealed record UnitReference(Guid UnitId, string IdentifierValue);

public sealed record ItemUpserted(string ItemExternalId, string Description, OpenEnum TrackingMode, string Uom,
    bool IsWarrantyProduct, string? WarrantyTemplateRef = null, bool Deleted = false);
public sealed record PartyUpserted(string PartyExternalId, OpenEnum PartyKind, string LegalName, string? TaxId,
    string Country, string? MergedFromExternalId = null, bool Deleted = false);
public sealed record GoodsReceiptPosted(ExternalDocumentRef DocumentRef, string VendorPartyExternalId,
    IReadOnlyList<CanonicalLine> Lines, ExternalDocumentRef? BaseOrderRef = null, bool IsAsn = false);
public sealed record ProductionCompleted(ExternalDocumentRef DocumentRef, ExternalDocumentRef ProductionOrderRef,
    string ProducedItemExternalId, IReadOnlyList<CanonicalLine> Lines, IReadOnlyList<CanonicalLine> Components);
public sealed record DeliveryPosted(ExternalDocumentRef DocumentRef, string CustomerPartyExternalId, string ShipToRef,
    IReadOnlyList<CanonicalLine> Lines, ExternalDocumentRef? BaseOrderRef = null, bool IsDropShip = false);
public sealed record InvoicePosted(ExternalDocumentRef DocumentRef, string CustomerPartyExternalId,
    IReadOnlyList<ExternalDocumentRef> BaseDeliveryRefs, IReadOnlyList<CanonicalLine> Lines, Money Totals);
public sealed record ReturnReceived(ExternalDocumentRef DocumentRef, string CustomerPartyExternalId,
    IReadOnlyList<CanonicalLine> Lines, ExternalDocumentRef? BaseDeliveryRef = null, string? ReturnReasonCode = null,
    string? RmaExternalRef = null);
public sealed record TransferPosted(ExternalDocumentRef DocumentRef, string FromLocationExternalId,
    string ToLocationExternalId, IReadOnlyList<CanonicalLine> Lines);
public sealed record InventoryAdjusted(ExternalDocumentRef DocumentRef, OpenEnum AdjustmentKind,
    IReadOnlyList<CanonicalLine> Lines, string? ReasonCode = null);
public sealed record WarrantyTerm(int Months);
public sealed record WarrantyLineSold(ExternalDocumentRef DocumentRef, string CustomerPartyExternalId,
    IReadOnlyList<string> CoveredUnitSerials, ExternalDocumentRef? CoveredDeliveryRef,
    string WarrantyProductExternalId, WarrantyTerm Term, Money Price, CanonicalLine Line);
public sealed record DocumentCancelled(ExternalDocumentRef DocumentRef, OpenEnum CancellationKind,
    ExternalDocumentRef? CancelledByDocumentRef = null);

public interface IOutboundCommand
{
    Guid IdempotencyKey { get; }
    string ModuleDocumentRef { get; }
    string ErpReferenceText { get; }
}

public sealed record CreateReplacementDelivery(Guid IdempotencyKey, string ModuleDocumentRef, string ErpReferenceText,
    string CustomerRef, string ShipToRef, UnitReference ReplacementUnit, OpenEnum PricePolicy,
    string OriginalInvoiceRef, string ReasonCodeText, string? BaseReturnRef = null, int? BaseReturnLine = null,
    Money? Price = null) : IOutboundCommand;
public sealed record CreditMemoLine(Guid? UnitId, string Description, decimal Quantity, Money Amount,
    string? ItemExternalId = null, IReadOnlyList<CanonicalSerial>? Serials = null, int? BaseLine = null);
public sealed record CreateCreditMemo(Guid IdempotencyKey, string ModuleDocumentRef, string ErpReferenceText,
    string CustomerRef, IReadOnlyList<CreditMemoLine> Lines, Money? RestockingFee, string? BaseInvoiceRef) : IOutboundCommand;
public sealed record JournalLine(string AccountRef, decimal Debit, decimal Credit, string Currency);
public sealed record PostWarrantyExpense(Guid IdempotencyKey, string ModuleDocumentRef, string ErpReferenceText,
    IReadOnlyList<JournalLine> JournalLines, string? CohortTag, bool AccrualSuppressed, bool ReserveDrawdown = false) : IOutboundCommand;
public sealed record ChangeStockStatusOrLocation(Guid IdempotencyKey, string ModuleDocumentRef, string ErpReferenceText,
    UnitReference Unit, string? FromLocationOrStatus, string ToLocationOrStatus, string Reason) : IOutboundCommand;
public sealed record AttachDocument(Guid IdempotencyKey, string ModuleDocumentRef, string ErpReferenceText,
    string TargetRecordRef, string DisplayName, string? ContentRef, string? Url) : IOutboundCommand;
public sealed record CreateServiceOrder(Guid IdempotencyKey, string ModuleDocumentRef, string ErpReferenceText,
    string CustomerRef, UnitReference Unit, string CoverageDecisionText, string ProblemDescription) : IOutboundCommand;

/// <summary>Connector-neutral installed-base card statuses derived from module-owned unit state.</summary>
public static class NativeWarrantyCardStatus
{
    public const string Active = "active";
    public const string InRepair = "in_repair";
    public const string Returned = "returned";
    public const string Loaned = "loaned";
    public const string Terminated = "terminated";
}

/// <summary>
/// Replaces the values on one already-mapped remote installed-base record. The command carries module vocabulary;
/// each connector owns its remote field names and value translation. WarrantyEndDate is deliberately absent when
/// the tenant still maintains warranty contracts in the remote system.
/// </summary>
public sealed record PushNativeWarrantyObject(Guid IdempotencyKey, string ModuleDocumentRef, string ErpReferenceText,
    UnitReference Unit, string ModuleStatus, DateOnly? WarrantyEndDate,
    bool IncludesWarrantyContractFields, string? CardStatus = null) : IOutboundCommand;

public sealed record OutboundCommandReply(Guid IdempotencyKey, string? ErpExternalId, ChangeMarker? ErpChangeMarker,
    OpenEnum Outcome, string? Reason = null);
