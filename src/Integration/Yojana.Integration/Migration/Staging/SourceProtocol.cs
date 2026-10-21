using System.Data;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Data;

namespace Yojana.Integration.LegacyMigration.Staging;

internal enum RawColumnKind { Text, Int32, Int64, Decimal, DateTime }

internal sealed record RawColumn(
    string? SourceName,
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

internal sealed record SourceRead(
    string SourceTable,
    IReadOnlyDictionary<string, string>? SourceColumnOverrides = null,
    IReadOnlyDictionary<string, string>? TextConstants = null,
    IReadOnlySet<string>? NullProperties = null);

internal sealed record SourceTableProtocol(
    string SourceTable,
    string StagingTable,
    Type RowType,
    IReadOnlyList<RawColumn> Columns,
    IReadOnlyList<SourceRead>? SourceReads = null)
{
    public IReadOnlyList<RawColumn> Keys => Columns.Where(x => x.Key).ToList();
    public IReadOnlyList<SourceRead> Reads => SourceReads ?? [new(SourceTable)];
}

/// <summary>
/// The incumbent ERP's physical identifiers are confined to this boundary. Composite source keys use the declared
/// key-column order below and join the invariant raw values with <c>|</c>. Changing an order changes persisted identity.
/// </summary>
internal static class SourceProtocol
{
    private static RawColumn T(string? source, string property, bool key = false, int length = 500) =>
        new(source, property, RawColumnKind.Text, key, length);
    private static RawColumn I(string source, string property, bool key = false) =>
        new(source, property, RawColumnKind.Int32, key);
    private static RawColumn N(string source, string property) => new(source, property, RawColumnKind.Decimal);
    private static RawColumn D(string source, string property) => new(source, property, RawColumnKind.DateTime);

    // Readable names and stable keys for screens and reports, so nothing outside this boundary has to know the
    // source's physical table names to label them. Keyed by staging row type: one entry per protocol table.
    private static readonly Dictionary<Type, (string Role, string Label)> Descriptions =
        new Dictionary<Type, (string Role, string Label)>
        {
            [typeof(SerialMasterStagingRow)] = ("serial_master", "Serial master"),
            [typeof(SerialQuantityStagingRow)] = ("serial_quantity", "Serial quantity by warehouse"),
            [typeof(SerialBinStagingRow)] = ("serial_bins", "Serial bins"),
            [typeof(TransactionLogHeaderStagingRow)] = ("transaction_log_headers", "Transaction log headers"),
            [typeof(TransactionLogLineStagingRow)] = ("transaction_log_lines", "Transaction log lines"),
            [typeof(EquipmentCardStagingRow)] = ("equipment_cards", "Equipment cards"),
            [typeof(CardHistoryStagingRow)] = ("equipment_card_history", "Equipment card history"),
            [typeof(CardPartnerStagingRow)] = ("equipment_card_partners", "Equipment card customers"),
            [typeof(ServiceContractStagingRow)] = ("service_contracts", "Service contracts"),
            [typeof(ContractLineStagingRow)] = ("service_contract_lines", "Service contract lines"),
            [typeof(ContractTemplateStagingRow)] = ("service_contract_templates", "Service contract templates"),
            [typeof(ServiceCallStagingRow)] = ("service_calls", "Service calls"),
            [typeof(ReturnStagingRow)] = ("returns", "Returns"),
            [typeof(MarketingDocumentHeaderStagingRow)] = ("document_headers",
                "Document headers: deliveries, invoices, returns, receipts, production"),
            [typeof(AttachmentStagingRow)] = ("attachments", "Attachments: file names and paths only"),
            [typeof(ItemSerialSettingStagingRow)] = ("item_serial_settings", "Item serial settings"),
        };

    /// <summary>The stable key and readable name of a protocol table, or the raw name when it is not ours.</summary>
    public static (string Role, string Label) Describe(string sourceTable)
    {
        var table = Tables.FirstOrDefault(row => string.Equals(row.SourceTable, sourceTable, StringComparison.Ordinal));
        return table is not null && Descriptions.TryGetValue(table.RowType, out var description)
            ? description : ("unknown", sourceTable);
    }

    public static IReadOnlyList<SourceTableProtocol> Tables { get; } =
    [
        new("OSRN", "stg_src_osrn", typeof(SerialMasterStagingRow),
        [T("ItemCode", nameof(SerialMasterStagingRow.ItemCode), true, 100), I("SysNumber", nameof(SerialMasterStagingRow.SystemNumber), true),
         I("AbsEntry", nameof(SerialMasterStagingRow.SerialEntry)), T("DistNumber", nameof(SerialMasterStagingRow.SerialNumber), length: 500),
         T("MnfSerial", nameof(SerialMasterStagingRow.ManufacturerSerial), length: 500), T("LotNumber", nameof(SerialMasterStagingRow.LotNumber), length: 500),
         D("ExpDate", nameof(SerialMasterStagingRow.ExpiryDate)), D("MnfDate", nameof(SerialMasterStagingRow.ManufactureDate)),
         D("InDate", nameof(SerialMasterStagingRow.InboundDate)), D("GrntStart", nameof(SerialMasterStagingRow.WarrantyStart)),
         D("GrntExp", nameof(SerialMasterStagingRow.WarrantyEnd)), T("Status", nameof(SerialMasterStagingRow.Status), length: 10),
         T("Notes", nameof(SerialMasterStagingRow.Notes), length: 4000), T("DataSource", nameof(SerialMasterStagingRow.DataSource), length: 20)]),
        new("OSRQ", "stg_src_osrq", typeof(SerialQuantityStagingRow),
        [T("ItemCode", nameof(SerialQuantityStagingRow.ItemCode), true, 100), I("SysNumber", nameof(SerialQuantityStagingRow.SystemNumber), true),
         T("WhsCode", nameof(SerialQuantityStagingRow.WarehouseCode), true, 100), N("Quantity", nameof(SerialQuantityStagingRow.Quantity))]),
        new("OSBQ", "stg_src_osbq", typeof(SerialBinStagingRow),
        [I("AbsEntry", nameof(SerialBinStagingRow.Entry), true), T("ItemCode", nameof(SerialBinStagingRow.ItemCode), length: 100),
         I("SnBMDAbs", nameof(SerialBinStagingRow.SerialEntry)), T("WhsCode", nameof(SerialBinStagingRow.WarehouseCode), length: 100),
         I("BinAbs", nameof(SerialBinStagingRow.BinEntry)), N("OnHandQty", nameof(SerialBinStagingRow.Quantity))]),
        new("OITL", "stg_src_oitl", typeof(TransactionLogHeaderStagingRow),
        [I("LogEntry", nameof(TransactionLogHeaderStagingRow.LogEntry), true), T("ItemCode", nameof(TransactionLogHeaderStagingRow.ItemCode), length: 100),
         I("DocType", nameof(TransactionLogHeaderStagingRow.DocumentType)), I("DocEntry", nameof(TransactionLogHeaderStagingRow.DocumentEntry)),
         I("DocLine", nameof(TransactionLogHeaderStagingRow.DocumentLine)), I("DocNum", nameof(TransactionLogHeaderStagingRow.DocumentNumber)),
         N("DocQty", nameof(TransactionLogHeaderStagingRow.DocumentQuantity)), I("BaseType", nameof(TransactionLogHeaderStagingRow.BaseType)),
         I("BaseEntry", nameof(TransactionLogHeaderStagingRow.BaseEntry)), I("BaseLine", nameof(TransactionLogHeaderStagingRow.BaseLine)),
         I("ApplyType", nameof(TransactionLogHeaderStagingRow.ApplyType)), I("ApplyEntry", nameof(TransactionLogHeaderStagingRow.ApplyEntry)),
         I("ApplyLine", nameof(TransactionLogHeaderStagingRow.ApplyLine)), I("AppDocNum", nameof(TransactionLogHeaderStagingRow.AppliedDocumentNumber)),
         D("DocDate", nameof(TransactionLogHeaderStagingRow.DocumentDate)), D("CreateDate", nameof(TransactionLogHeaderStagingRow.CreatedDate)),
         I("CreateTime", nameof(TransactionLogHeaderStagingRow.CreatedTime)), I("StockEff", nameof(TransactionLogHeaderStagingRow.StockEffect)),
         I("LocType", nameof(TransactionLogHeaderStagingRow.LocationType)), T("LocCode", nameof(TransactionLogHeaderStagingRow.WarehouseCode), length: 100),
         T("CardCode", nameof(TransactionLogHeaderStagingRow.PartyCode), length: 100)]),
        new("ITL1", "stg_src_itl1", typeof(TransactionLogLineStagingRow),
        [I("LogEntry", nameof(TransactionLogLineStagingRow.LogEntry), true), T("ItemCode", nameof(TransactionLogLineStagingRow.ItemCode), true, 100),
         I("SysNumber", nameof(TransactionLogLineStagingRow.SystemNumber), true), N("Quantity", nameof(TransactionLogLineStagingRow.Quantity)),
         I("MdAbsEntry", nameof(TransactionLogLineStagingRow.SerialEntry))]),
        new("OINS", "stg_src_oins", typeof(EquipmentCardStagingRow),
        [I("insID", nameof(EquipmentCardStagingRow.CardId), true), T("itemCode", nameof(EquipmentCardStagingRow.ItemCode), length: 100),
         T("internalSN", nameof(EquipmentCardStagingRow.InternalSerial), length: 500), T("manufSN", nameof(EquipmentCardStagingRow.ManufacturerSerial), length: 500),
         T("customer", nameof(EquipmentCardStagingRow.CustomerCode), length: 100), T("directCsmr", nameof(EquipmentCardStagingRow.DirectCustomerCode), length: 100),
         I("replcIns", nameof(EquipmentCardStagingRow.ReplacesCardId)), I("repByIns", nameof(EquipmentCardStagingRow.ReplacedByCardId)),
         T("DPPStatus", nameof(EquipmentCardStagingRow.PersonalDataStatus), length: 20), D("wrrntyStrt", nameof(EquipmentCardStagingRow.WarrantyStart)),
         D("wrrntyEnd", nameof(EquipmentCardStagingRow.WarrantyEnd)), T("status", nameof(EquipmentCardStagingRow.Status), length: 40)]),
        new("AINS", "stg_src_ains", typeof(CardHistoryStagingRow),
        [I("insID", nameof(CardHistoryStagingRow.CardId), true), I("logInstanc", nameof(CardHistoryStagingRow.HistorySequence), true),
         T("customer", nameof(CardHistoryStagingRow.CustomerCode), length: 100), D("wrrntyStrt", nameof(CardHistoryStagingRow.WarrantyStart)),
         D("wrrntyEnd", nameof(CardHistoryStagingRow.WarrantyEnd)), T("status", nameof(CardHistoryStagingRow.Status), length: 40),
         D("updateDate", nameof(CardHistoryStagingRow.ChangedAt))]),
        // The demo has 245 rows, LogInstanc is NULL on every row, and (InsID, BpCode) is unique. That stable pair is
        // therefore the source identity; extraction deliberately fails on a future source that repeats it.
        new("INS1", "stg_src_ins1", typeof(CardPartnerStagingRow),
        [I("InsID", nameof(CardPartnerStagingRow.CardId), true), T("BpCode", nameof(CardPartnerStagingRow.PartnerCode), true, 100),
         I("LogInstanc", nameof(CardPartnerStagingRow.HistorySequence))]),
        new("OCTR", "stg_src_octr", typeof(ServiceContractStagingRow),
        [I("ContractID", nameof(ServiceContractStagingRow.ContractId), true), T("CstmrCode", nameof(ServiceContractStagingRow.CustomerCode), length: 100),
         D("StartDate", nameof(ServiceContractStagingRow.StartDate)), D("EndDate", nameof(ServiceContractStagingRow.EndDate)),
         D("TermDate", nameof(ServiceContractStagingRow.TerminationDate)), T("Status", nameof(ServiceContractStagingRow.Status), length: 40),
         T("CntrcTmplt", nameof(ServiceContractStagingRow.TemplateName), length: 500), T("SrvcType", nameof(ServiceContractStagingRow.ServiceType), length: 20),
         T("CntrcType", nameof(ServiceContractStagingRow.ContractType), length: 20), I("Duration", nameof(ServiceContractStagingRow.Duration)),
         I("ResponsVal", nameof(ServiceContractStagingRow.ResponseValue)), T("ResponsUnt", nameof(ServiceContractStagingRow.ResponseUnit), length: 20),
         T("InclParts", nameof(ServiceContractStagingRow.IncludesParts), length: 10), T("InclWork", nameof(ServiceContractStagingRow.IncludesLabour), length: 10),
         T("InclTravel", nameof(ServiceContractStagingRow.IncludesTravel), length: 10), I("DocNum", nameof(ServiceContractStagingRow.DocumentNumber))]),
        new("CTR1", "stg_src_ctr1", typeof(ContractLineStagingRow),
        [I("ContractID", nameof(ContractLineStagingRow.ContractId), true), I("Line", nameof(ContractLineStagingRow.LineNumber), true),
         T("ItemCode", nameof(ContractLineStagingRow.ItemCode), length: 100), T("InternalSN", nameof(ContractLineStagingRow.InternalSerial), length: 500),
         T("ManufSN", nameof(ContractLineStagingRow.ManufacturerSerial), length: 500), I("InsID", nameof(ContractLineStagingRow.EquipmentCardId)),
         D("StartDate", nameof(ContractLineStagingRow.StartDate)), D("EndDate", nameof(ContractLineStagingRow.EndDate)),
         D("TermDate", nameof(ContractLineStagingRow.TerminationDate))]),
        new("OCTT", "stg_src_octt", typeof(ContractTemplateStagingRow),
        [T("TmpltName", nameof(ContractTemplateStagingRow.TemplateName), true, 500), T("Descriptio", nameof(ContractTemplateStagingRow.Description), length: 500),
         // The source has no duration-unit column. Per the ADR inference and the demo's value 24, downstream mapping treats Duration as months.
         I("Duration", nameof(ContractTemplateStagingRow.Duration)), I("ResponsVal", nameof(ContractTemplateStagingRow.ResponseValue)),
         T("ResponsUnt", nameof(ContractTemplateStagingRow.ResponseUnit), length: 20), T("InclParts", nameof(ContractTemplateStagingRow.IncludesParts), length: 10),
         T("InclWork", nameof(ContractTemplateStagingRow.IncludesLabour), length: 10), T("InclTravel", nameof(ContractTemplateStagingRow.IncludesTravel), length: 10),
         T("CntrctType", nameof(ContractTemplateStagingRow.ContractType), length: 20), T("Renewal", nameof(ContractTemplateStagingRow.Renewal), length: 10),
         T("Deleted", nameof(ContractTemplateStagingRow.Deleted), length: 10), T("Remark", nameof(ContractTemplateStagingRow.Remark), length: 4000),
         T("MonEnabled", nameof(ContractTemplateStagingRow.MondayEnabled), length: 10), I("MonStart", nameof(ContractTemplateStagingRow.MondayStart)), I("MonEnd", nameof(ContractTemplateStagingRow.MondayEnd)),
         T("TueEnabled", nameof(ContractTemplateStagingRow.TuesdayEnabled), length: 10), I("TueStart", nameof(ContractTemplateStagingRow.TuesdayStart)), I("TueEnd", nameof(ContractTemplateStagingRow.TuesdayEnd)),
         T("WedEnabled", nameof(ContractTemplateStagingRow.WednesdayEnabled), length: 10), I("WedStart", nameof(ContractTemplateStagingRow.WednesdayStart)), I("WedEnd", nameof(ContractTemplateStagingRow.WednesdayEnd)),
         T("ThuEnabled", nameof(ContractTemplateStagingRow.ThursdayEnabled), length: 10), I("ThuStart", nameof(ContractTemplateStagingRow.ThursdayStart)), I("ThuEnd", nameof(ContractTemplateStagingRow.ThursdayEnd)),
         T("FriEnabled", nameof(ContractTemplateStagingRow.FridayEnabled), length: 10), I("FriStart", nameof(ContractTemplateStagingRow.FridayStart)), I("FriEnd", nameof(ContractTemplateStagingRow.FridayEnd)),
         T("SatEnabled", nameof(ContractTemplateStagingRow.SaturdayEnabled), length: 10), I("SatStart", nameof(ContractTemplateStagingRow.SaturdayStart)), I("SatEnd", nameof(ContractTemplateStagingRow.SaturdayEnd)),
         T("SunEnabled", nameof(ContractTemplateStagingRow.SundayEnabled), length: 10), I("SunStrart", nameof(ContractTemplateStagingRow.SundayStart)), I("SunEnd", nameof(ContractTemplateStagingRow.SundayEnd))]),
        new("OSCL", "stg_src_oscl", typeof(ServiceCallStagingRow),
        [I("callID", nameof(ServiceCallStagingRow.CallId), true), T("customer", nameof(ServiceCallStagingRow.CustomerCode), length: 100),
         T("itemCode", nameof(ServiceCallStagingRow.ItemCode), length: 100), T("internalSN", nameof(ServiceCallStagingRow.InternalSerial), length: 500),
         T("manufSN", nameof(ServiceCallStagingRow.ManufacturerSerial), length: 500), I("status", nameof(ServiceCallStagingRow.Status)),
         D("createDate", nameof(ServiceCallStagingRow.OpenedAt)), D("closeDate", nameof(ServiceCallStagingRow.ClosedAt))]),
        new("ORRR", "stg_src_orrr", typeof(ReturnStagingRow),
        [I("DocEntry", nameof(ReturnStagingRow.DocumentEntry), true), I("DocNum", nameof(ReturnStagingRow.DocumentNumber)),
         T("CardCode", nameof(ReturnStagingRow.CustomerCode), length: 100), D("DocDate", nameof(ReturnStagingRow.DocumentDate)),
         T("DocStatus", nameof(ReturnStagingRow.Status), length: 20), T("CANCELED", nameof(ReturnStagingRow.Cancelled), length: 10)]),
        new("MARKETING_DOCUMENT_HEADERS",
            "stg_src_marketing_document_headers", typeof(MarketingDocumentHeaderStagingRow),
        [T(null, nameof(MarketingDocumentHeaderStagingRow.SourceDocumentTable), true, 100),
         I("DocEntry", nameof(MarketingDocumentHeaderStagingRow.DocumentEntry), true),
         T("ObjType", nameof(MarketingDocumentHeaderStagingRow.ObjectType), length: 40),
         T("DocType", nameof(MarketingDocumentHeaderStagingRow.DocumentType), length: 10),
         I("DocNum", nameof(MarketingDocumentHeaderStagingRow.DocumentNumber)),
         T("CardCode", nameof(MarketingDocumentHeaderStagingRow.PartyCode), length: 100),
         D("DocDate", nameof(MarketingDocumentHeaderStagingRow.DocumentDate)),
         T("CANCELED", nameof(MarketingDocumentHeaderStagingRow.Cancelled), length: 10)],
        [Marketing("ODLN"), Marketing("OINV"), Marketing("ORDN"),
         Marketing("ORIN"), Marketing("OPDN"), Marketing("ORPD"),
         Marketing("OIGN"), Marketing("OIGE"), Marketing("OWTR"),
         Marketing("OWOR", documentType: false, documentDate: "PostDate", cancelled: false)]),
        new("ATC1", "stg_src_oatc", typeof(AttachmentStagingRow),
        [I("AbsEntry", nameof(AttachmentStagingRow.AttachmentEntry), true), I("Line", nameof(AttachmentStagingRow.LineNumber), true),
         T("trgtPath", nameof(AttachmentStagingRow.TargetPath), length: 2000), T("srcPath", nameof(AttachmentStagingRow.SourcePath), length: 2000),
         T("subPath", nameof(AttachmentStagingRow.SubPath), length: 500), T("FileName", nameof(AttachmentStagingRow.FileName), length: 500),
         T("FileExt", nameof(AttachmentStagingRow.FileExtension), length: 40), I("FileSize", nameof(AttachmentStagingRow.FileSize)),
         D("Date", nameof(AttachmentStagingRow.AttachedAt))]),
        new("OITM", "stg_src_oitm", typeof(ItemSerialSettingStagingRow),
        [T("ItemCode", nameof(ItemSerialSettingStagingRow.ItemCode), true, 100), T("ManSerNum", nameof(ItemSerialSettingStagingRow.ManagesSerialNumbers), length: 10),
         T("MngMethod", nameof(ItemSerialSettingStagingRow.ManagementMethod), length: 10), T("WarrntTmpl", nameof(ItemSerialSettingStagingRow.WarrantyTemplateName), length: 500)]),
    ];

    private static SourceRead Marketing(string table, bool documentType = true,
        string documentDate = "DocDate", bool cancelled = true)
    {
        var overrides = documentDate == "DocDate"
            ? null : new Dictionary<string, string> { [nameof(MarketingDocumentHeaderStagingRow.DocumentDate)] = documentDate };
        var missing = new HashSet<string>();
        if (!documentType) missing.Add(nameof(MarketingDocumentHeaderStagingRow.DocumentType));
        if (!cancelled) missing.Add(nameof(MarketingDocumentHeaderStagingRow.Cancelled));
        return new(table, overrides,
            new Dictionary<string, string> { [nameof(MarketingDocumentHeaderStagingRow.SourceDocumentTable)] = table },
            missing);
    }

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
            if (table.RowType == typeof(TransactionLogHeaderStagingRow))
                builder.HasIndex(nameof(StagingRow.TenantId), nameof(StagingRow.BatchId),
                    nameof(StagingRow.SourceSystem), nameof(TransactionLogHeaderStagingRow.DocumentType),
                    nameof(TransactionLogHeaderStagingRow.LogEntry));
            if (table.RowType == typeof(TransactionLogLineStagingRow))
                builder.HasIndex(nameof(StagingRow.TenantId), nameof(StagingRow.BatchId),
                    nameof(StagingRow.SourceSystem), nameof(TransactionLogLineStagingRow.ItemCode),
                    nameof(TransactionLogLineStagingRow.SystemNumber), nameof(TransactionLogLineStagingRow.LogEntry));
            foreach (var column in table.Columns.Where(x => x.Kind == RawColumnKind.Text))
                builder.Property(column.PropertyName).HasMaxLength(column.MaxLength!.Value);
            foreach (var column in table.Columns.Where(x => x.Kind == RawColumnKind.Decimal))
                builder.Property(column.PropertyName).HasPrecision(28, 8);
        }
    }
}
