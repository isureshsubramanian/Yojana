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
    public int? SerialEntry { get; set; }
    public string? SerialNumber { get; set; }
    public string? NormalisedSerialNumber { get; set; }
    public string? FamilyNormalisedSerialNumber { get; set; }
    public string? ManufacturerSerial { get; set; }
    public string? NormalisedManufacturerSerial { get; set; }
    public string? LotNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public DateTime? ManufactureDate { get; set; }
    public DateTime? InboundDate { get; set; }
    public DateTime? WarrantyStart { get; set; }
    public DateTime? WarrantyEnd { get; set; }
    public string? Status { get; set; }
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
    public int? Entry { get; set; }
    public string? ItemCode { get; set; }
    public int? SerialEntry { get; set; }
    public string? WarehouseCode { get; set; }
    public int? BinEntry { get; set; }
    public decimal? Quantity { get; set; }
}

public sealed class TransactionLogHeaderStagingRow : StagingRow
{
    public int? LogEntry { get; set; }
    public string? ItemCode { get; set; }
    public int? DocumentType { get; set; }
    public int? DocumentEntry { get; set; }
    public int? DocumentLine { get; set; }
    public int? DocumentNumber { get; set; }
    public decimal? DocumentQuantity { get; set; }
    public int? BaseType { get; set; }
    public int? BaseEntry { get; set; }
    public int? BaseLine { get; set; }
    public int? ApplyType { get; set; }
    public int? ApplyEntry { get; set; }
    public int? ApplyLine { get; set; }
    public int? AppliedDocumentNumber { get; set; }
    public DateTime? DocumentDate { get; set; }
    public DateTime? CreatedDate { get; set; }
    public int? CreatedTime { get; set; }
    public int? StockEffect { get; set; }
    public int? LocationType { get; set; }
    public string? WarehouseCode { get; set; }
    public string? PartyCode { get; set; }
}

public sealed class TransactionLogLineStagingRow : StagingRow
{
    public int? LogEntry { get; set; }
    public string? ItemCode { get; set; }
    public int? SystemNumber { get; set; }
    public decimal? Quantity { get; set; }
    public int? SerialEntry { get; set; }
}

public sealed class EquipmentCardStagingRow : StagingRow
{
    public int? CardId { get; set; }
    public string? ItemCode { get; set; }
    public string? InternalSerial { get; set; }
    public string? NormalisedInternalSerial { get; set; }
    public string? FamilyNormalisedInternalSerial { get; set; }
    public string? ManufacturerSerial { get; set; }
    public string? NormalisedManufacturerSerial { get; set; }
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
    public string? PartnerCode { get; set; }
    public int? HistorySequence { get; set; }
}

public sealed class ServiceContractStagingRow : StagingRow
{
    public int? ContractId { get; set; }
    public string? CustomerCode { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string? Status { get; set; }
    public string? TemplateName { get; set; }
    public string? ServiceType { get; set; }
    public string? ContractType { get; set; }
    public int? Duration { get; set; }
    public int? ResponseValue { get; set; }
    public string? ResponseUnit { get; set; }
    public string? IncludesParts { get; set; }
    public string? IncludesLabour { get; set; }
    public string? IncludesTravel { get; set; }
    public int? DocumentNumber { get; set; }
}

public sealed class ContractLineStagingRow : StagingRow
{
    public int? ContractId { get; set; }
    public int? LineNumber { get; set; }
    public string? ItemCode { get; set; }
    public string? InternalSerial { get; set; }
    public string? NormalisedInternalSerial { get; set; }
    public string? FamilyNormalisedInternalSerial { get; set; }
    public string? ManufacturerSerial { get; set; }
    public int? EquipmentCardId { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? TerminationDate { get; set; }
}

public sealed class ContractTemplateStagingRow : StagingRow
{
    public string? TemplateName { get; set; }
    public string? Description { get; set; }
    public int? Duration { get; set; }
    public int? ResponseValue { get; set; }
    public string? ResponseUnit { get; set; }
    public string? IncludesParts { get; set; }
    public string? IncludesLabour { get; set; }
    public string? IncludesTravel { get; set; }
    public string? ContractType { get; set; }
    public string? Renewal { get; set; }
    public string? Deleted { get; set; }
    public string? Remark { get; set; }
    public string? MondayEnabled { get; set; }
    public int? MondayStart { get; set; }
    public int? MondayEnd { get; set; }
    public string? TuesdayEnabled { get; set; }
    public int? TuesdayStart { get; set; }
    public int? TuesdayEnd { get; set; }
    public string? WednesdayEnabled { get; set; }
    public int? WednesdayStart { get; set; }
    public int? WednesdayEnd { get; set; }
    public string? ThursdayEnabled { get; set; }
    public int? ThursdayStart { get; set; }
    public int? ThursdayEnd { get; set; }
    public string? FridayEnabled { get; set; }
    public int? FridayStart { get; set; }
    public int? FridayEnd { get; set; }
    public string? SaturdayEnabled { get; set; }
    public int? SaturdayStart { get; set; }
    public int? SaturdayEnd { get; set; }
    public string? SundayEnabled { get; set; }
    public int? SundayStart { get; set; }
    public int? SundayEnd { get; set; }
}

public sealed class ServiceCallStagingRow : StagingRow
{
    public int? CallId { get; set; }
    public string? CustomerCode { get; set; }
    public string? ItemCode { get; set; }
    public string? InternalSerial { get; set; }
    public string? ManufacturerSerial { get; set; }
    public int? Status { get; set; }
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
    public int? DocumentEntry { get; set; }
    public string? ObjectType { get; set; }
    public string? DocumentType { get; set; }
    public int? DocumentNumber { get; set; }
    public string? PartyCode { get; set; }
    public DateTime? DocumentDate { get; set; }
    public string? Cancelled { get; set; }
}

public sealed class AttachmentStagingRow : StagingRow
{
    public int? AttachmentEntry { get; set; }
    public int? LineNumber { get; set; }
    public string? TargetPath { get; set; }
    public string? SourcePath { get; set; }
    public string? SubPath { get; set; }
    public string? FileName { get; set; }
    public string? FileExtension { get; set; }
    public int? FileSize { get; set; }
    public DateTime? AttachedAt { get; set; }
}

public sealed class ItemSerialSettingStagingRow : StagingRow
{
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public string? ManagesSerialNumbers { get; set; }
    public string? ManagementMethod { get; set; }
    public string? WarrantyTemplateName { get; set; }
}
