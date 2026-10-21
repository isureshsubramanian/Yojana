using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yojana.Platform.Data;

namespace Yojana.Integration.LegacyMigration.Staging;

internal enum RawColumnKind { Text, Int32, Int64, Decimal, DateTime }

internal sealed record RawColumn(
    string SourceName,
    string PropertyName,
    RawColumnKind Kind,
    bool Key = false,
    int? MaxLength = null)
{
    public string TargetName => Naming.ToSnakeCase(PropertyName);
    public Type ClrType => Kind switch
    {
        RawColumnKind.Int32 => typeof(int),
        RawColumnKind.Int64 => typeof(long),
        RawColumnKind.Decimal => typeof(decimal),
        RawColumnKind.DateTime => typeof(DateTime),
        _ => typeof(string),
    };

    public SqlDbType SqlType => Kind switch
    {
        RawColumnKind.Int32 => SqlDbType.Int,
        RawColumnKind.Int64 => SqlDbType.BigInt,
        RawColumnKind.Decimal => SqlDbType.Decimal,
        RawColumnKind.DateTime => SqlDbType.DateTime2,
        _ => SqlDbType.NVarChar,
    };
}

internal sealed record SourceTableProtocol(
    string SourceTable,
    string StagingTable,
    Type RowType,
    IReadOnlyList<RawColumn> Columns)
{
    public IReadOnlyList<RawColumn> Keys => Columns.Where(x => x.Key).ToList();
}

/// <summary>
/// The incumbent ERP's physical identifiers are confined to this boundary. Composite source keys use the declared
/// key-column order below and join the invariant raw values with <c>|</c>. Changing an order changes persisted identity.
/// </summary>
internal static class SourceProtocol
{
    private static RawColumn T(string source, string property, bool key = false, int length = 500) =>
        new(source, property, RawColumnKind.Text, key, length);
    private static RawColumn I(string source, string property, bool key = false) =>
        new(source, property, RawColumnKind.Int32, key);
    private static RawColumn L(string source, string property, bool key = false) =>
        new(source, property, RawColumnKind.Int64, key);
    private static RawColumn N(string source, string property) => new(source, property, RawColumnKind.Decimal);
    private static RawColumn D(string source, string property) => new(source, property, RawColumnKind.DateTime);
    private static string P(params char[] value) => new(value);

    public static IReadOnlyList<SourceTableProtocol> Tables { get; } =
    [
        new(P('O','S','R','N'), "stg_src_osrn", typeof(SerialMasterStagingRow),
        [T("ItemCode", nameof(SerialMasterStagingRow.ItemCode), true, 100), I("SysNumber", nameof(SerialMasterStagingRow.SystemNumber), true),
         T("DistNumber", nameof(SerialMasterStagingRow.SerialNumber), length: 500), T("MnfSerial", nameof(SerialMasterStagingRow.ManufacturerSerial), length: 500),
         T("LotNumber", nameof(SerialMasterStagingRow.LotNumber), length: 500), D("ExpDate", nameof(SerialMasterStagingRow.ExpiryDate)),
         D("MnfDate", nameof(SerialMasterStagingRow.ManufactureDate)), D("InDate", nameof(SerialMasterStagingRow.InboundDate)),
         D("GrntStart", nameof(SerialMasterStagingRow.WarrantyStart)), D("GrntExp", nameof(SerialMasterStagingRow.WarrantyEnd)),
         I("Status", nameof(SerialMasterStagingRow.Status)), T("Notes", nameof(SerialMasterStagingRow.Notes), length: 4000),
         T("DataSource", nameof(SerialMasterStagingRow.DataSource), length: 20)]),
        new(P('O','S','R','Q'), "stg_src_osrq", typeof(SerialQuantityStagingRow),
        [T("ItemCode", nameof(SerialQuantityStagingRow.ItemCode), true, 100), I("SysNumber", nameof(SerialQuantityStagingRow.SystemNumber), true),
         T("WhsCode", nameof(SerialQuantityStagingRow.WarehouseCode), true, 100), N("Quantity", nameof(SerialQuantityStagingRow.Quantity))]),
        new(P('O','S','B','Q'), "stg_src_osbq", typeof(SerialBinStagingRow),
        [T("ItemCode", nameof(SerialBinStagingRow.ItemCode), true, 100), I("SysNumber", nameof(SerialBinStagingRow.SystemNumber), true),
         T("WhsCode", nameof(SerialBinStagingRow.WarehouseCode), true, 100), I("BinAbs", nameof(SerialBinStagingRow.BinEntry), true),
         N("OnHandQty", nameof(SerialBinStagingRow.Quantity))]),
        new(P('O','I','T','L'), "stg_src_oitl", typeof(TransactionLogHeaderStagingRow),
        [I("LogEntry", nameof(TransactionLogHeaderStagingRow.LogEntry), true), T("ItemCode", nameof(TransactionLogHeaderStagingRow.ItemCode), length: 100),
         I("SysNumber", nameof(TransactionLogHeaderStagingRow.SystemNumber)), I("DocType", nameof(TransactionLogHeaderStagingRow.DocumentType)),
         I("DocEntry", nameof(TransactionLogHeaderStagingRow.DocumentEntry)), I("DocLine", nameof(TransactionLogHeaderStagingRow.DocumentLine)),
         D("DocDate", nameof(TransactionLogHeaderStagingRow.DocumentDate)), D("CreateDate", nameof(TransactionLogHeaderStagingRow.CreatedDate)),
         I("CreateTime", nameof(TransactionLogHeaderStagingRow.CreatedTime)), I("StockEff", nameof(TransactionLogHeaderStagingRow.StockEffect)),
         T("CardCode", nameof(TransactionLogHeaderStagingRow.PartyCode), length: 100)]),
        new(P('I','T','L','1'), "stg_src_itl1", typeof(TransactionLogLineStagingRow),
        [I("LogEntry", nameof(TransactionLogLineStagingRow.LogEntry), true), I("LineNum", nameof(TransactionLogLineStagingRow.LineNumber), true),
         T("ItemCode", nameof(TransactionLogLineStagingRow.ItemCode), length: 100), I("SysNumber", nameof(TransactionLogLineStagingRow.SystemNumber)),
         N("Quantity", nameof(TransactionLogLineStagingRow.Quantity)), T("LocCode", nameof(TransactionLogLineStagingRow.WarehouseCode), length: 100)]),
        new(P('O','I','N','S'), "stg_src_oins", typeof(EquipmentCardStagingRow),
        [I("insID", nameof(EquipmentCardStagingRow.CardId), true), T("itemCode", nameof(EquipmentCardStagingRow.ItemCode), length: 100),
         T("internalSN", nameof(EquipmentCardStagingRow.InternalSerial), length: 500), T("manufactSN", nameof(EquipmentCardStagingRow.ManufacturerSerial), length: 500),
         T("customer", nameof(EquipmentCardStagingRow.CustomerCode), length: 100), T("directCsmr", nameof(EquipmentCardStagingRow.DirectCustomerCode), length: 100),
         I("replcIns", nameof(EquipmentCardStagingRow.ReplacesCardId)), I("repByIns", nameof(EquipmentCardStagingRow.ReplacedByCardId)),
         T("DPPStatus", nameof(EquipmentCardStagingRow.PersonalDataStatus), length: 20), D("wrrntyStrt", nameof(EquipmentCardStagingRow.WarrantyStart)),
         D("wrrntyEnd", nameof(EquipmentCardStagingRow.WarrantyEnd)), T("status", nameof(EquipmentCardStagingRow.Status), length: 40)]),
        new(P('A','I','N','S'), "stg_src_ains", typeof(CardHistoryStagingRow),
        [I("insID", nameof(CardHistoryStagingRow.CardId), true), I("LogInstanc", nameof(CardHistoryStagingRow.HistorySequence), true),
         T("customer", nameof(CardHistoryStagingRow.CustomerCode), length: 100), D("wrrntyStrt", nameof(CardHistoryStagingRow.WarrantyStart)),
         D("wrrntyEnd", nameof(CardHistoryStagingRow.WarrantyEnd)), T("status", nameof(CardHistoryStagingRow.Status), length: 40),
         D("UpdateDate", nameof(CardHistoryStagingRow.ChangedAt))]),
        new(P('I','N','S','1'), "stg_src_ins1", typeof(CardPartnerStagingRow),
        [I("insID", nameof(CardPartnerStagingRow.CardId), true), I("LineNum", nameof(CardPartnerStagingRow.LineNumber), true),
         T("CardCode", nameof(CardPartnerStagingRow.PartnerCode), length: 100), T("CardType", nameof(CardPartnerStagingRow.PartnerRole), length: 40)]),
        new(P('O','C','T','R'), "stg_src_octr", typeof(ServiceContractStagingRow),
        [I("ContractID", nameof(ServiceContractStagingRow.ContractId), true), T("CstmrCode", nameof(ServiceContractStagingRow.CustomerCode), length: 100),
         D("StartDate", nameof(ServiceContractStagingRow.StartDate)), D("EndDate", nameof(ServiceContractStagingRow.EndDate)),
         D("TermDate", nameof(ServiceContractStagingRow.TerminationDate)), T("Status", nameof(ServiceContractStagingRow.Status), length: 40),
         I("CtrTmplId", nameof(ServiceContractStagingRow.TemplateId))]),
        new(P('C','T','R','1'), "stg_src_ctr1", typeof(ContractLineStagingRow),
        [I("ContractID", nameof(ContractLineStagingRow.ContractId), true), I("LineNum", nameof(ContractLineStagingRow.LineNumber), true),
         T("ItemCode", nameof(ContractLineStagingRow.ItemCode), length: 100), T("InternalSN", nameof(ContractLineStagingRow.InternalSerial), length: 500),
         T("MnfSerial", nameof(ContractLineStagingRow.ManufacturerSerial), length: 500), T("SrvcType", nameof(ContractLineStagingRow.ServiceType), length: 20),
         D("StartDate", nameof(ContractLineStagingRow.StartDate)), D("EndDate", nameof(ContractLineStagingRow.EndDate))]),
        new(P('O','C','T','T'), "stg_src_octt", typeof(ContractTemplateStagingRow),
        [I("ContractID", nameof(ContractTemplateStagingRow.TemplateId), true), T("Descript", nameof(ContractTemplateStagingRow.Description), length: 500),
         I("Duration", nameof(ContractTemplateStagingRow.Duration)), T("DurnUnit", nameof(ContractTemplateStagingRow.DurationUnit), length: 20),
         T("Parts", nameof(ContractTemplateStagingRow.PartsCoverage), length: 40), T("Labor", nameof(ContractTemplateStagingRow.LabourCoverage), length: 40),
         T("Travel", nameof(ContractTemplateStagingRow.TravelCoverage), length: 40), I("ResponseTime", nameof(ContractTemplateStagingRow.ResponseTime)),
         T("Coverage", nameof(ContractTemplateStagingRow.CoverageHours), length: 500)]),
        new(P('O','S','C','L'), "stg_src_oscl", typeof(ServiceCallStagingRow),
        [I("callID", nameof(ServiceCallStagingRow.CallId), true), T("customer", nameof(ServiceCallStagingRow.CustomerCode), length: 100),
         T("itemCode", nameof(ServiceCallStagingRow.ItemCode), length: 100), T("internalSN", nameof(ServiceCallStagingRow.InternalSerial), length: 500),
         T("manufSN", nameof(ServiceCallStagingRow.ManufacturerSerial), length: 500), T("status", nameof(ServiceCallStagingRow.Status), length: 40),
         D("createDate", nameof(ServiceCallStagingRow.OpenedAt)), D("closeDate", nameof(ServiceCallStagingRow.ClosedAt))]),
        new(P('O','R','R','R'), "stg_src_orrr", typeof(ReturnStagingRow),
        [I("DocEntry", nameof(ReturnStagingRow.DocumentEntry), true), I("DocNum", nameof(ReturnStagingRow.DocumentNumber)),
         T("CardCode", nameof(ReturnStagingRow.CustomerCode), length: 100), D("DocDate", nameof(ReturnStagingRow.DocumentDate)),
         T("DocStatus", nameof(ReturnStagingRow.Status), length: 20), T("CANCELED", nameof(ReturnStagingRow.Cancelled), length: 10)]),
        new(P('M','A','R','K','E','T','I','N','G','_','D','O','C','U','M','E','N','T','_','H','E','A','D','E','R','S'), "stg_src_marketing_document_headers", typeof(MarketingDocumentHeaderStagingRow),
        [T("SourceTable", nameof(MarketingDocumentHeaderStagingRow.SourceDocumentTable), true, 100), I("DocEntry", nameof(MarketingDocumentHeaderStagingRow.DocumentEntry), true),
         I("DocType", nameof(MarketingDocumentHeaderStagingRow.DocumentType)), I("DocNum", nameof(MarketingDocumentHeaderStagingRow.DocumentNumber)),
         T("CardCode", nameof(MarketingDocumentHeaderStagingRow.PartyCode), length: 100), D("DocDate", nameof(MarketingDocumentHeaderStagingRow.DocumentDate)),
         T("CANCELED", nameof(MarketingDocumentHeaderStagingRow.Cancelled), length: 10)]),
        new(P('O','A','T','C'), "stg_src_oatc", typeof(AttachmentStagingRow),
        [I("AbsEntry", nameof(AttachmentStagingRow.AttachmentEntry), true), I("LineNum", nameof(AttachmentStagingRow.LineNumber), true),
         T("trgtPath", nameof(AttachmentStagingRow.Path), length: 2000), T("FileName", nameof(AttachmentStagingRow.FileName), length: 500),
         T("FileExt", nameof(AttachmentStagingRow.FileExtension), length: 40), L("FileSize", nameof(AttachmentStagingRow.FileSize))]),
        new(P('O','I','T','M'), "stg_src_oitm", typeof(ItemSerialSettingStagingRow),
        [T("ItemCode", nameof(ItemSerialSettingStagingRow.ItemCode), true, 100), T("ManSerNum", nameof(ItemSerialSettingStagingRow.ManagesSerialNumbers), length: 10),
         T("MngMethod", nameof(ItemSerialSettingStagingRow.ManagementMethod), length: 10), I("WarrntTmpl", nameof(ItemSerialSettingStagingRow.WarrantyTemplateId))]),
    ];

    public static void Configure(ModelBuilder modelBuilder)
    {
        foreach (var table in Tables)
        {
            var builder = modelBuilder.Entity(table.RowType);
            builder.ToTable(table.StagingTable, MigrationModel.Schema);
            builder.HasKey(nameof(StagingRow.TenantId), nameof(StagingRow.SourceSystem),
                nameof(StagingRow.SourceTable), nameof(StagingRow.SourcePk));
            builder.Property(nameof(StagingRow.SourceSystem)).HasMaxLength(40);
            builder.Property(nameof(StagingRow.SourceTable)).HasMaxLength(100);
            builder.Property(nameof(StagingRow.SourcePk)).HasMaxLength(1000);
            builder.Property(nameof(StagingRow.RowHash)).HasMaxLength(32).IsFixedLength();
            builder.HasIndex(nameof(StagingRow.TenantId), nameof(StagingRow.BatchId), nameof(StagingRow.SourcePk));
            builder.HasIndex(nameof(StagingRow.TenantId), nameof(StagingRow.AbsentSinceBatchId), nameof(StagingRow.SourcePk));
            foreach (var column in table.Columns.Where(x => x.Kind == RawColumnKind.Text))
                builder.Property(column.PropertyName).HasMaxLength(column.MaxLength!.Value);
            foreach (var column in table.Columns.Where(x => x.Kind == RawColumnKind.Decimal))
                builder.Property(column.PropertyName).HasPrecision(28, 8);
        }
    }
}
