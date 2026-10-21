using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Staging;

public abstract class StagingRow : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid BatchId { get; set; }
    public required string SourceSystem { get; set; }
    public required string SourceTable { get; set; }
    public required string SourcePk { get; set; }
    public DateTime ExtractedAt { get; set; }
    public required byte[] RowHash { get; set; }
    public Guid FirstBatchId { get; set; }
    public Guid? AbsentSinceBatchId { get; set; }
}

public sealed class SerialMasterStagingRow : StagingRow
{
    public string? ItemCode { get; set; }
    public int? SystemNumber { get; set; }
    public string? SerialNumber { get; set; }
    public string? ManufacturerSerial { get; set; }
    public string? LotNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public DateTime? ManufactureDate { get; set; }
    public DateTime? InboundDate { get; set; }
    public DateTime? WarrantyStart { get; set; }
    public DateTime? WarrantyEnd { get; set; }
    public int? Status { get; set; }
    public string? Notes { get; set; }
    public string? DataSource { get; set; }
}

public sealed class SerialQuantityStagingRow : StagingRow
{
    public string? ItemCode { get; set; }
    public int? SystemNumber { get; set; }
    public string? WarehouseCode { get; set; }
    public decimal? Quantity { get; set; }
}

public sealed class SerialBinStagingRow : StagingRow
{
    public string? ItemCode { get; set; }
    public int? SystemNumber { get; set; }
    public string? WarehouseCode { get; set; }
    public int? BinEntry { get; set; }
    public decimal? Quantity { get; set; }
}

public sealed class TransactionLogHeaderStagingRow : StagingRow
{
    public int? LogEntry { get; set; }
    public string? ItemCode { get; set; }
    public int? SystemNumber { get; set; }
    public int? DocumentType { get; set; }
    public int? DocumentEntry { get; set; }
    public int? DocumentLine { get; set; }
    public DateTime? DocumentDate { get; set; }
    public DateTime? CreatedDate { get; set; }
    public int? CreatedTime { get; set; }
    public int? StockEffect { get; set; }
    public string? PartyCode { get; set; }
}

public sealed class TransactionLogLineStagingRow : StagingRow
{
    public int? LogEntry { get; set; }
    public int? LineNumber { get; set; }
    public string? ItemCode { get; set; }
    public int? SystemNumber { get; set; }
    public decimal? Quantity { get; set; }
    public string? WarehouseCode { get; set; }
}

public sealed class EquipmentCardStagingRow : StagingRow
{
    public int? CardId { get; set; }
    public string? ItemCode { get; set; }
    public string? InternalSerial { get; set; }
    public string? ManufacturerSerial { get; set; }
    public string? CustomerCode { get; set; }
    public string? DirectCustomerCode { get; set; }
    public int? ReplacesCardId { get; set; }
    public int? ReplacedByCardId { get; set; }
    public string? PersonalDataStatus { get; set; }
    public DateTime? WarrantyStart { get; set; }
    public DateTime? WarrantyEnd { get; set; }
    public string? Status { get; set; }
}

public sealed class CardHistoryStagingRow : StagingRow
{
    public int? CardId { get; set; }
    public int? HistorySequence { get; set; }
    public string? CustomerCode { get; set; }
    public DateTime? WarrantyStart { get; set; }
    public DateTime? WarrantyEnd { get; set; }
    public string? Status { get; set; }
    public DateTime? ChangedAt { get; set; }
}

public sealed class CardPartnerStagingRow : StagingRow
{
    public int? CardId { get; set; }
    public int? LineNumber { get; set; }
    public string? PartnerCode { get; set; }
    public string? PartnerRole { get; set; }
}

public sealed class ServiceContractStagingRow : StagingRow
{
    public int? ContractId { get; set; }
    public string? CustomerCode { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string? Status { get; set; }
    public int? TemplateId { get; set; }
}

public sealed class ContractLineStagingRow : StagingRow
{
    public int? ContractId { get; set; }
    public int? LineNumber { get; set; }
    public string? ItemCode { get; set; }
    public string? InternalSerial { get; set; }
    public string? ManufacturerSerial { get; set; }
    public string? ServiceType { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
}

public sealed class ContractTemplateStagingRow : StagingRow
{
    public int? TemplateId { get; set; }
    public string? Description { get; set; }
    public int? Duration { get; set; }
    public string? DurationUnit { get; set; }
    public string? PartsCoverage { get; set; }
    public string? LabourCoverage { get; set; }
    public string? TravelCoverage { get; set; }
    public int? ResponseTime { get; set; }
    public string? CoverageHours { get; set; }
}

public sealed class ServiceCallStagingRow : StagingRow
{
    public int? CallId { get; set; }
    public string? CustomerCode { get; set; }
    public string? ItemCode { get; set; }
    public string? InternalSerial { get; set; }
    public string? ManufacturerSerial { get; set; }
    public string? Status { get; set; }
    public DateTime? OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}

public sealed class ReturnStagingRow : StagingRow
{
    public int? DocumentEntry { get; set; }
    public int? DocumentNumber { get; set; }
    public string? CustomerCode { get; set; }
    public DateTime? DocumentDate { get; set; }
    public string? Status { get; set; }
    public string? Cancelled { get; set; }
}

public sealed class MarketingDocumentHeaderStagingRow : StagingRow
{
    public string? SourceDocumentTable { get; set; }
    public int? DocumentType { get; set; }
    public int? DocumentEntry { get; set; }
    public int? DocumentNumber { get; set; }
    public string? PartyCode { get; set; }
    public DateTime? DocumentDate { get; set; }
    public string? Cancelled { get; set; }
}

public sealed class AttachmentStagingRow : StagingRow
{
    public int? AttachmentEntry { get; set; }
    public int? LineNumber { get; set; }
    public string? Path { get; set; }
    public string? FileName { get; set; }
    public string? FileExtension { get; set; }
    public long? FileSize { get; set; }
}

public sealed class ItemSerialSettingStagingRow : StagingRow
{
    public string? ItemCode { get; set; }
    public string? ManagesSerialNumbers { get; set; }
    public string? ManagementMethod { get; set; }
    public int? WarrantyTemplateId { get; set; }
}
