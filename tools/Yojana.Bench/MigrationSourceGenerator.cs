using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Yojana.Integration.LegacyMigration.Staging;

namespace Yojana.Bench;

public static class MigrationSourceProfile
{
    public const int ItemCodes = 40;
    public const int SkuFamilies = 8;
    public const int ContractTemplates = 50;
    public const int DeliveredBasisPoints = 6_500;
    public const int ReturnedBasisPoints = 500;
    public const int TransferBasisPoints = 500;
    public const int CancelledDeliveryBasisPoints = 100;
    public const int CreditMemoBasisPoints = 200;
    public const int GoodsIssueBasisPoints = 200;
    public const int EquipmentCardBasisPoints = 8_500;
    public const int ContractBasisPoints = 3_500;
    public const int ReplacementBasisPoints = 100;
    public const int MultiPartnerBasisPoints = 200;
    public const int CustomerMismatchBasisPoints = 100;
    public const int NormalisedDuplicateBasisPoints = 40;
    public const int WhitespaceCaseBasisPoints = 100;
    public const int PlaceholderBasisPoints = 30;
    public const int OrphanCardBasisPoints = 20;
    public const int DeltaNewBasisPoints = 50;
    public const int DeltaCoverBasisPoints = 100;
    public const int DeltaOwnerBasisPoints = 20;
    public const int DeltaGoneBasisPoints = 10;
}

public sealed record MigrationSourceGenerationResult(
    int RequestedSerials,
    long RowsWritten,
    string ContentHash,
    IReadOnlyDictionary<string, long> RowsByRole);

public sealed record MigrationDeltaMutationResult(int NewSerials, int CoverChanges, int OwnerChanges, int RemovedSerials);

/// <summary>Builds deterministic, source-shaped data through the extraction protocol rather than a second schema.</summary>
public sealed class MigrationSourceGenerator(string connectionString, int serials, int seed)
{
    private const int BulkBatchSize = 10_000;
    private readonly Dictionary<string, long> counts = new(StringComparer.Ordinal);
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private static readonly DateTime Epoch = new(2023, 1, 2, 9, 0, 0, DateTimeKind.Unspecified);
    private static readonly string[] Families =
        ["SERVER", "STORAGE", "NETWORK", "DESKTOP", "LAPTOP", "WORKSTATION", "FIREWALL", "BACKUP"];
    private static readonly string[] Customers =
        ["ASTER-COMPUTE", "BLUE-CEDAR", "COPPER-VALE", "DELTA-WORKS", "EMBER-SYSTEMS", "FARADAY-LABS"];
    private static readonly string[] Warehouses = ["MAIN", "NORTH", "SOUTH", "SERVICE"];

    public async Task<MigrationSourceGenerationResult> GenerateAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await DropProtocolTablesAsync(connection, cancellationToken);
        await SourceProtocolDatabase.CreateSchemaAsync(connection, cancellationToken);

        foreach (var table in SourceProtocol.Tables)
        foreach (var read in table.Reads)
            await WriteAsync(connection, table, read, Rows(table, read), cancellationToken);

        return new(serials, counts.Values.Sum(), Convert.ToHexString(hash.GetHashAndReset()),
            new Dictionary<string, long>(counts, StringComparer.Ordinal));
    }

    public async Task<MigrationDeltaMutationResult> MutateForDeltaAsync(CancellationToken cancellationToken = default)
    {
        var added = Math.Max(1, serials * MigrationSourceProfile.DeltaNewBasisPoints / 10_000);
        var cover = Math.Max(1, serials * MigrationSourceProfile.DeltaCoverBasisPoints / 10_000);
        var owners = Math.Max(1, serials * MigrationSourceProfile.DeltaOwnerBasisPoints / 10_000);
        var removed = Math.Max(1, serials * MigrationSourceProfile.DeltaGoneBasisPoints / 10_000);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var serialTable = Table<SerialMasterStagingRow>();
        var serialRead = serialTable.Reads[0];
        await WriteAsync(connection, serialTable, serialRead,
            Enumerable.Range(serials + 1, added).Select(SerialRow), cancellationToken, track: false);

        var itemTable = Table<ItemSerialSettingStagingRow>();
        _ = itemTable;
        var cardTable = Table<EquipmentCardStagingRow>();
        cover = await UpdateExistingCardsAsync(connection, cardTable, nameof(EquipmentCardStagingRow.WarrantyEnd),
            Epoch.AddYears(5), 0, cover, cancellationToken);
        owners = await UpdateExistingCardsAsync(connection, cardTable, nameof(EquipmentCardStagingRow.CustomerCode),
            "HELIOS-DATA", cover, owners, cancellationToken);

        await DeleteSerialRangeAsync(connection, serialTable, serials - removed + 1, serials, cancellationToken);
        return new(added, cover, owners, removed);
    }

    private IEnumerable<IReadOnlyDictionary<string, object?>> Rows(SourceTableProtocol table, SourceRead read)
    {
        if (table.RowType == typeof(SerialMasterStagingRow))
            return Enumerable.Range(1, serials).Select(SerialRow);
        if (table.RowType == typeof(SerialQuantityStagingRow))
            return Enumerable.Range(1, serials).Where(InStock).Select(QuantityRow);
        if (table.RowType == typeof(SerialBinStagingRow))
            return Enumerable.Range(1, serials).Where(index => InStock(index) && Bucket(index, 9) < 3_000)
                .Select(BinRow);
        if (table.RowType == typeof(TransactionLogHeaderStagingRow)) return MovementHeaders();
        if (table.RowType == typeof(TransactionLogLineStagingRow)) return MovementLines();
        if (table.RowType == typeof(EquipmentCardStagingRow)) return EquipmentCards();
        if (table.RowType == typeof(CardHistoryStagingRow)) return CardHistory();
        if (table.RowType == typeof(CardPartnerStagingRow)) return CardPartners();
        if (table.RowType == typeof(ServiceContractStagingRow)) return Contracts();
        if (table.RowType == typeof(ContractLineStagingRow)) return ContractLines();
        if (table.RowType == typeof(ContractTemplateStagingRow))
            return Enumerable.Range(1, MigrationSourceProfile.ContractTemplates).Select(TemplateRow);
        if (table.RowType == typeof(ServiceCallStagingRow))
            return Enumerable.Range(1, serials).Where(index => HasCard(index) && Bucket(index, 16) < 200)
                .Select(ServiceCallRow);
        if (table.RowType == typeof(ReturnStagingRow))
            return Enumerable.Range(1, serials).Where(IsReturned).Select(ReturnRow);
        if (table.RowType == typeof(MarketingDocumentHeaderStagingRow)) return MarketingRows(read);
        if (table.RowType == typeof(AttachmentStagingRow))
            return Enumerable.Range(1, Math.Max(1, serials / 500)).Select(AttachmentRow);
        if (table.RowType == typeof(ItemSerialSettingStagingRow))
            return Enumerable.Range(0, MigrationSourceProfile.ItemCodes).Select(ItemRow);
        return [];
    }

    private IReadOnlyDictionary<string, object?> SerialRow(int index)
    {
        var serial = Serial(index);
        return Row(
            (nameof(SerialMasterStagingRow.ItemCode), Item(index)),
            (nameof(SerialMasterStagingRow.SystemNumber), index),
            (nameof(SerialMasterStagingRow.SerialEntry), index),
            (nameof(SerialMasterStagingRow.SerialNumber), serial),
            (nameof(SerialMasterStagingRow.ManufacturerSerial), $"MFG-{index:D9}"),
            (nameof(SerialMasterStagingRow.LotNumber), $"LOT-{1 + index / 500:D5}"),
            (nameof(SerialMasterStagingRow.ExpiryDate), Epoch.AddYears(8)),
            (nameof(SerialMasterStagingRow.ManufactureDate), Epoch.AddDays(-(index % 365))),
            (nameof(SerialMasterStagingRow.InboundDate), Day(index, 0)),
            (nameof(SerialMasterStagingRow.WarrantyStart), Day(index, 0)),
            (nameof(SerialMasterStagingRow.WarrantyEnd), Day(index, 0).AddYears(3)),
            (nameof(SerialMasterStagingRow.Status), InStock(index) ? "0" : "1"),
            (nameof(SerialMasterStagingRow.Notes), AddOn(index)),
            (nameof(SerialMasterStagingRow.DataSource), "N"));
    }

    private IReadOnlyDictionary<string, object?> QuantityRow(int index) => Row(
        (nameof(SerialQuantityStagingRow.ItemCode), Item(index)),
        (nameof(SerialQuantityStagingRow.SystemNumber), index),
        (nameof(SerialQuantityStagingRow.WarehouseCode), Warehouse(index)),
        (nameof(SerialQuantityStagingRow.Quantity), 1m));

    private IReadOnlyDictionary<string, object?> BinRow(int index) => Row(
        (nameof(SerialBinStagingRow.Entry), index),
        (nameof(SerialBinStagingRow.ItemCode), Item(index)),
        (nameof(SerialBinStagingRow.SerialEntry), index),
        (nameof(SerialBinStagingRow.WarehouseCode), Warehouse(index)),
        (nameof(SerialBinStagingRow.BinEntry), 100 + index % 20),
        (nameof(SerialBinStagingRow.Quantity), 1m));

    private IEnumerable<IReadOnlyDictionary<string, object?>> MovementHeaders()
    {
        foreach (var index in Enumerable.Range(1, serials))
        foreach (var movement in Movements(index))
            yield return Row(
                (nameof(TransactionLogHeaderStagingRow.LogEntry), movement.Log),
                (nameof(TransactionLogHeaderStagingRow.ItemCode), Item(index)),
                (nameof(TransactionLogHeaderStagingRow.DocumentType), movement.Type),
                (nameof(TransactionLogHeaderStagingRow.DocumentEntry), movement.Entry),
                (nameof(TransactionLogHeaderStagingRow.DocumentLine), 0),
                (nameof(TransactionLogHeaderStagingRow.DocumentNumber), 100_000 + movement.Entry),
                (nameof(TransactionLogHeaderStagingRow.DocumentQuantity), movement.Quantity),
                (nameof(TransactionLogHeaderStagingRow.BaseType), movement.BaseType),
                (nameof(TransactionLogHeaderStagingRow.BaseEntry), movement.BaseEntry),
                (nameof(TransactionLogHeaderStagingRow.BaseLine), 0),
                (nameof(TransactionLogHeaderStagingRow.ApplyType), null),
                (nameof(TransactionLogHeaderStagingRow.ApplyEntry), null),
                (nameof(TransactionLogHeaderStagingRow.ApplyLine), null),
                (nameof(TransactionLogHeaderStagingRow.AppliedDocumentNumber), null),
                (nameof(TransactionLogHeaderStagingRow.DocumentDate), Day(index, movement.Day)),
                (nameof(TransactionLogHeaderStagingRow.CreatedDate), Day(index, movement.Day)),
                (nameof(TransactionLogHeaderStagingRow.CreatedTime), 900 + movement.Day),
                (nameof(TransactionLogHeaderStagingRow.StockEffect), 1),
                (nameof(TransactionLogHeaderStagingRow.LocationType), 64),
                (nameof(TransactionLogHeaderStagingRow.WarehouseCode), movement.Warehouse),
                (nameof(TransactionLogHeaderStagingRow.PartyCode), movement.Party));
    }

    private IEnumerable<IReadOnlyDictionary<string, object?>> MovementLines()
    {
        foreach (var index in Enumerable.Range(1, serials))
        foreach (var movement in Movements(index))
            yield return Row(
                (nameof(TransactionLogLineStagingRow.LogEntry), movement.Log),
                (nameof(TransactionLogLineStagingRow.ItemCode), Item(index)),
                (nameof(TransactionLogLineStagingRow.SystemNumber), index),
                (nameof(TransactionLogLineStagingRow.Quantity), movement.Quantity),
                (nameof(TransactionLogLineStagingRow.SerialEntry), index));
    }

    private IEnumerable<IReadOnlyDictionary<string, object?>> EquipmentCards()
    {
        foreach (var index in Enumerable.Range(1, serials).Where(HasCard)) yield return CardRow(index, false);
        var orphans = Math.Max(1, serials * MigrationSourceProfile.OrphanCardBasisPoints / 10_000);
        foreach (var index in Enumerable.Range(serials + 10_000, orphans)) yield return CardRow(index, true);
    }

    private IReadOnlyDictionary<string, object?> CardRow(int index, bool orphan)
    {
        var replacement = Bucket(index, 15) < MigrationSourceProfile.ReplacementBasisPoints;
        var customer = Customer(index);
        if (!orphan && Bucket(index, 14) < MigrationSourceProfile.CustomerMismatchBasisPoints)
            customer = Customers[(Array.IndexOf(Customers, customer) + 1) % Customers.Length];
        return Row(
            (nameof(EquipmentCardStagingRow.CardId), index),
            (nameof(EquipmentCardStagingRow.ItemCode), orphan ? Item(index) : Item(index)),
            (nameof(EquipmentCardStagingRow.InternalSerial), orphan ? $"ORPHAN-{index:D9}" : Serial(index)),
            (nameof(EquipmentCardStagingRow.ManufacturerSerial), orphan ? $"ORPHAN-MFG-{index:D9}" : $"MFG-{index:D9}"),
            (nameof(EquipmentCardStagingRow.CustomerCode), customer),
            (nameof(EquipmentCardStagingRow.DirectCustomerCode), customer),
            (nameof(EquipmentCardStagingRow.ReplacesCardId), replacement && index > 1 ? index - 1 : null),
            (nameof(EquipmentCardStagingRow.ReplacedByCardId), replacement && index < serials ? index + 1 : null),
            (nameof(EquipmentCardStagingRow.PersonalDataStatus), "N"),
            (nameof(EquipmentCardStagingRow.WarrantyStart), Day(index, 1)),
            (nameof(EquipmentCardStagingRow.WarrantyEnd), Day(index, 1).AddYears(3)),
            (nameof(EquipmentCardStagingRow.Status), "A"));
    }

    private IEnumerable<IReadOnlyDictionary<string, object?>> CardHistory()
    {
        foreach (var index in Enumerable.Range(1, serials).Where(HasCard))
        {
            yield return Row(
                (nameof(CardHistoryStagingRow.CardId), index),
                (nameof(CardHistoryStagingRow.HistorySequence), 1),
                (nameof(CardHistoryStagingRow.CustomerCode), Customer(index)),
                (nameof(CardHistoryStagingRow.WarrantyStart), Day(index, 1)),
                (nameof(CardHistoryStagingRow.WarrantyEnd), Day(index, 1).AddYears(3)),
                (nameof(CardHistoryStagingRow.Status), "A"),
                (nameof(CardHistoryStagingRow.ChangedAt), Day(index, 2)));
        }
    }

    private IEnumerable<IReadOnlyDictionary<string, object?>> CardPartners()
    {
        foreach (var index in Enumerable.Range(1, serials).Where(HasCard))
        {
            var customer = Customer(index);
            yield return Row((nameof(CardPartnerStagingRow.CardId), index),
                (nameof(CardPartnerStagingRow.PartnerCode), customer),
                (nameof(CardPartnerStagingRow.HistorySequence), null));
            if (Bucket(index, 17) < MigrationSourceProfile.MultiPartnerBasisPoints)
                yield return Row((nameof(CardPartnerStagingRow.CardId), index),
                    (nameof(CardPartnerStagingRow.PartnerCode),
                        Customers[(Array.IndexOf(Customers, customer) + 1) % Customers.Length]),
                    (nameof(CardPartnerStagingRow.HistorySequence), null));
        }
    }

    private IEnumerable<IReadOnlyDictionary<string, object?>> Contracts()
    {
        foreach (var index in Enumerable.Range(1, serials).Where(HasContract))
        {
            var terminated = Bucket(index, 19) < 500;
            yield return Row(
                (nameof(ServiceContractStagingRow.ContractId), index),
                (nameof(ServiceContractStagingRow.CustomerCode), Customer(index)),
                (nameof(ServiceContractStagingRow.StartDate), Day(index, 1)),
                (nameof(ServiceContractStagingRow.EndDate), Day(index, 1).AddYears(3)),
                (nameof(ServiceContractStagingRow.TerminationDate), terminated ? Day(index, 200) : null),
                (nameof(ServiceContractStagingRow.Status), terminated ? "T" : "A"),
                (nameof(ServiceContractStagingRow.TemplateName), Template(index)),
                (nameof(ServiceContractStagingRow.ServiceType), "R"),
                (nameof(ServiceContractStagingRow.ContractType), "C"),
                (nameof(ServiceContractStagingRow.Duration), 36),
                (nameof(ServiceContractStagingRow.ResponseValue), 8),
                (nameof(ServiceContractStagingRow.ResponseUnit), "H"),
                (nameof(ServiceContractStagingRow.IncludesParts), "Y"),
                (nameof(ServiceContractStagingRow.IncludesLabour), "Y"),
                (nameof(ServiceContractStagingRow.IncludesTravel), "N"),
                (nameof(ServiceContractStagingRow.DocumentNumber), 400_000 + index));
        }
    }

    private IEnumerable<IReadOnlyDictionary<string, object?>> ContractLines() =>
        Enumerable.Range(1, serials).Where(HasContract).Select(index => Row(
            (nameof(ContractLineStagingRow.ContractId), index),
            (nameof(ContractLineStagingRow.LineNumber), 0),
            (nameof(ContractLineStagingRow.ItemCode), Item(index)),
            (nameof(ContractLineStagingRow.InternalSerial), Serial(index)),
            (nameof(ContractLineStagingRow.ManufacturerSerial), $"MFG-{index:D9}"),
            (nameof(ContractLineStagingRow.EquipmentCardId), index),
            (nameof(ContractLineStagingRow.StartDate), Day(index, 1)),
            (nameof(ContractLineStagingRow.EndDate), Day(index, 1).AddYears(3)),
            (nameof(ContractLineStagingRow.TerminationDate), null)));

    private IReadOnlyDictionary<string, object?> TemplateRow(int index)
    {
        var addOns = index % 8;
        var words = new List<string> { "Base hardware cover" };
        if ((addOns & 1) != 0) words.Add("accidental damage");
        if ((addOns & 2) != 0) words.Add("media retention");
        if ((addOns & 4) != 0) words.Add("next-business-day service");
        return Row(
            (nameof(ContractTemplateStagingRow.TemplateName), Template(index)),
            (nameof(ContractTemplateStagingRow.Description), string.Join(" + ", words)),
            (nameof(ContractTemplateStagingRow.Duration), 36),
            (nameof(ContractTemplateStagingRow.ResponseValue), (addOns & 4) != 0 ? 8 : 24),
            (nameof(ContractTemplateStagingRow.ResponseUnit), "H"),
            (nameof(ContractTemplateStagingRow.IncludesParts), "Y"),
            (nameof(ContractTemplateStagingRow.IncludesLabour), "Y"),
            (nameof(ContractTemplateStagingRow.IncludesTravel), "N"),
            (nameof(ContractTemplateStagingRow.ContractType), "C"),
            (nameof(ContractTemplateStagingRow.Renewal), "N"),
            (nameof(ContractTemplateStagingRow.Deleted), "N"),
            (nameof(ContractTemplateStagingRow.Remark), string.Join("; ", words)),
            (nameof(ContractTemplateStagingRow.MondayEnabled), "Y"),
            (nameof(ContractTemplateStagingRow.MondayStart), 800),
            (nameof(ContractTemplateStagingRow.MondayEnd), 1800),
            (nameof(ContractTemplateStagingRow.TuesdayEnabled), "Y"),
            (nameof(ContractTemplateStagingRow.TuesdayStart), 800),
            (nameof(ContractTemplateStagingRow.TuesdayEnd), 1800),
            (nameof(ContractTemplateStagingRow.WednesdayEnabled), "Y"),
            (nameof(ContractTemplateStagingRow.WednesdayStart), 800),
            (nameof(ContractTemplateStagingRow.WednesdayEnd), 1800),
            (nameof(ContractTemplateStagingRow.ThursdayEnabled), "Y"),
            (nameof(ContractTemplateStagingRow.ThursdayStart), 800),
            (nameof(ContractTemplateStagingRow.ThursdayEnd), 1800),
            (nameof(ContractTemplateStagingRow.FridayEnabled), "Y"),
            (nameof(ContractTemplateStagingRow.FridayStart), 800),
            (nameof(ContractTemplateStagingRow.FridayEnd), 1800),
            (nameof(ContractTemplateStagingRow.SaturdayEnabled), "N"),
            (nameof(ContractTemplateStagingRow.SaturdayStart), 0),
            (nameof(ContractTemplateStagingRow.SaturdayEnd), 0),
            (nameof(ContractTemplateStagingRow.SundayEnabled), "N"),
            (nameof(ContractTemplateStagingRow.SundayStart), 0),
            (nameof(ContractTemplateStagingRow.SundayEnd), 0));
    }

    private IReadOnlyDictionary<string, object?> ServiceCallRow(int index) => Row(
        (nameof(ServiceCallStagingRow.CallId), index),
        (nameof(ServiceCallStagingRow.CustomerCode), Customer(index)),
        (nameof(ServiceCallStagingRow.ItemCode), Item(index)),
        (nameof(ServiceCallStagingRow.InternalSerial), Serial(index)),
        (nameof(ServiceCallStagingRow.ManufacturerSerial), $"MFG-{index:D9}"),
        (nameof(ServiceCallStagingRow.Status), index % 4 == 0 ? -1 : 1),
        (nameof(ServiceCallStagingRow.OpenedAt), Day(index, 30)),
        (nameof(ServiceCallStagingRow.ClosedAt), index % 4 == 0 ? Day(index, 35) : null));

    private IReadOnlyDictionary<string, object?> ReturnRow(int index) => Row(
        (nameof(ReturnStagingRow.DocumentEntry), Entry(index, 4)),
        (nameof(ReturnStagingRow.DocumentNumber), 100_000 + Entry(index, 4)),
        (nameof(ReturnStagingRow.CustomerCode), Customer(index)),
        (nameof(ReturnStagingRow.DocumentDate), Day(index, 40)),
        (nameof(ReturnStagingRow.Status), "O"),
        (nameof(ReturnStagingRow.Cancelled), "N"));

    private IEnumerable<IReadOnlyDictionary<string, object?>> MarketingRows(SourceRead read)
    {
        var sourceName = read.TextConstants?.GetValueOrDefault(
            nameof(MarketingDocumentHeaderStagingRow.SourceDocumentTable)) ?? read.SourceTable;
        var code = Math.Abs(StableHash(sourceName)) % 1_000;
        yield return Row(
            (nameof(MarketingDocumentHeaderStagingRow.SourceDocumentTable), sourceName),
            (nameof(MarketingDocumentHeaderStagingRow.DocumentEntry), 900_000 + code),
            (nameof(MarketingDocumentHeaderStagingRow.ObjectType), code.ToString(CultureInfo.InvariantCulture)),
            (nameof(MarketingDocumentHeaderStagingRow.DocumentType), "I"),
            (nameof(MarketingDocumentHeaderStagingRow.DocumentNumber), 900_000 + code),
            (nameof(MarketingDocumentHeaderStagingRow.PartyCode), Customers[code % Customers.Length]),
            (nameof(MarketingDocumentHeaderStagingRow.DocumentDate), Epoch),
            (nameof(MarketingDocumentHeaderStagingRow.Cancelled), "N"));
    }

    private IReadOnlyDictionary<string, object?> AttachmentRow(int index) => Row(
        (nameof(AttachmentStagingRow.AttachmentEntry), index),
        (nameof(AttachmentStagingRow.LineNumber), 0),
        (nameof(AttachmentStagingRow.TargetPath), @"C:\Fictional\Warranty"),
        (nameof(AttachmentStagingRow.SourcePath), @"C:\Fictional\Warranty"),
        (nameof(AttachmentStagingRow.SubPath), "certificates"),
        (nameof(AttachmentStagingRow.FileName), $"hardware-cover-{index:D6}"),
        (nameof(AttachmentStagingRow.FileExtension), "pdf"),
        (nameof(AttachmentStagingRow.FileSize), 2048 + index),
        (nameof(AttachmentStagingRow.AttachedAt), Epoch.AddDays(index % 365)));

    private IReadOnlyDictionary<string, object?> ItemRow(int zeroBased)
    {
        var index = zeroBased + 1;
        var family = Families[zeroBased % Families.Length];
        return Row(
            (nameof(ItemSerialSettingStagingRow.ItemCode), $"{family}-{index:D2}"),
            (nameof(ItemSerialSettingStagingRow.ItemName), $"{family.ToLowerInvariant()} hardware model {index:D2}"),
            (nameof(ItemSerialSettingStagingRow.ManagesSerialNumbers), "Y"),
            (nameof(ItemSerialSettingStagingRow.ManagementMethod), "R"),
            (nameof(ItemSerialSettingStagingRow.WarrantyTemplateName), Template(index)));
    }

    private IEnumerable<Movement> Movements(int index)
    {
        yield return new(index * 10 + 1, 20, Entry(index, 1), 1m, 0, -1, null, Warehouse(index), "NOVA-SUPPLY");
        if (IsCancelledDelivery(index))
        {
            var entry = Entry(index, 2);
            yield return new(index * 10 + 2, 15, entry, -1m, 10, -1, null, Warehouse(index), Customer(index));
            yield return new(index * 10 + 3, 15, Entry(index, 3), 1m, 11, 15, entry, Warehouse(index), Customer(index));
        }
        else if (IsCreditMemo(index))
        {
            var delivery = Entry(index, 2);
            yield return new(index * 10 + 2, 15, delivery, -1m, 10, -1, null, Warehouse(index), Customer(index));
            yield return new(index * 10 + 3, 14, Entry(index, 3), 1m, 40, 13, delivery, Warehouse(index), Customer(index));
        }
        else if (IsReturned(index))
        {
            var delivery = Entry(index, 2);
            yield return new(index * 10 + 2, 15, delivery, -1m, 10, -1, null, Warehouse(index), Customer(index));
            yield return new(index * 10 + 3, 16, Entry(index, 4), 1m, 40, 15, delivery, Warehouse(index), Customer(index));
        }
        else if (IsGoodsIssue(index))
            yield return new(index * 10 + 2, 60, Entry(index, 5), -1m, 20, -1, null, Warehouse(index), null);
        else if (IsTransfer(index))
        {
            var next = Warehouses[(Array.IndexOf(Warehouses, Warehouse(index)) + 1) % Warehouses.Length];
            yield return new(index * 10 + 2, 67, Entry(index, 6), -1m, 20, -1, null, Warehouse(index), null);
            yield return new(index * 10 + 3, 67, Entry(index, 6), 1m, 20, -1, null, next, null);
        }
        else if (IsDelivered(index))
            yield return new(index * 10 + 2, 15, Entry(index, 2), -1m, 10, -1, null, Warehouse(index), Customer(index));
    }

    private bool InStock(int index) => Movements(index).Sum(row => row.Quantity) > 0m;
    private bool IsDelivered(int index) => Bucket(index, 1) < MigrationSourceProfile.DeliveredBasisPoints;
    private bool IsReturned(int index) => Bucket(index, 2) < MigrationSourceProfile.ReturnedBasisPoints;
    private bool IsTransfer(int index) => Bucket(index, 3) < MigrationSourceProfile.TransferBasisPoints;
    private bool IsCancelledDelivery(int index) => Bucket(index, 4) < MigrationSourceProfile.CancelledDeliveryBasisPoints;
    private bool IsCreditMemo(int index) => Bucket(index, 5) < MigrationSourceProfile.CreditMemoBasisPoints;
    private bool IsGoodsIssue(int index) => Bucket(index, 6) < MigrationSourceProfile.GoodsIssueBasisPoints;
    private bool HasCard(int index) => IsDelivered(index) && Bucket(index, 7) < MigrationSourceProfile.EquipmentCardBasisPoints;
    private bool HasContract(int index) => HasCard(index) && Bucket(index, 8) < MigrationSourceProfile.ContractBasisPoints;

    private string Serial(int index)
    {
        if (Bucket(index, 12) < MigrationSourceProfile.PlaceholderBasisPoints) return "";
        var basis = Bucket(index, 10) < MigrationSourceProfile.NormalisedDuplicateBasisPoints && index > 40
            ? index - 40 : index;
        var serial = $"{Families[(basis - 1) % Families.Length][..3]}-{basis:D9}";
        return Bucket(index, 11) < MigrationSourceProfile.WhitespaceCaseBasisPoints
            ? $"  {serial.ToLowerInvariant()}  " : serial;
    }

    private static string AddOn(int index) => (index % 4) switch
    {
        0 => "Base hardware cover",
        1 => "Accidental damage add-on",
        2 => "Media retention service",
        _ => "Next-business-day service",
    };

    private string Item(int index)
    {
        var item = (index - 1) % MigrationSourceProfile.ItemCodes + 1;
        return $"{Families[(item - 1) % Families.Length]}-{item:D2}";
    }

    private string Customer(int index) => Customers[Bucket(index, 20) % Customers.Length];
    private string Warehouse(int index) => Warehouses[Bucket(index, 21) % Warehouses.Length];
    private static string Template(int index) => $"Hardware cover {((index - 1) % MigrationSourceProfile.ContractTemplates) + 1:D2}";
    private DateTime Day(int index, int offset) => Epoch.AddDays((index + seed) % 730 + offset);
    private static int Entry(int index, int kind) => index * 10 + kind;

    private int Bucket(int index, int salt)
    {
        unchecked
        {
            uint value = (uint)(index * 747_796_405 + (seed + salt * 101) * 2_891_336_453L);
            value = (value ^ (value >> 16)) * 2_246_822_519u;
            value ^= value >> 13;
            return (int)(value % 10_000);
        }
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            var result = 17;
            foreach (var character in value) result = result * 31 + character;
            return result;
        }
    }

    private static IReadOnlyDictionary<string, object?> Row(params (string Name, object? Value)[] values) =>
        values.ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);

    private async Task WriteAsync(SqlConnection connection, SourceTableProtocol table, SourceRead read,
        IEnumerable<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken,
        bool track = true)
    {
        var projected = table.Columns.Select(column => (Column: column,
                Name: SourceProtocolDatabase.SourceColumn(read, column)))
            .Where(item => item.Name is not null).ToList();
        var buffer = new DataTable { Locale = CultureInfo.InvariantCulture };
        foreach (var item in projected) buffer.Columns.Add(item.Name!, item.Column.ClrType);
        long written = 0;
        foreach (var row in rows)
        {
            var values = projected.Select(item => row.GetValueOrDefault(item.Column.PropertyName) ?? DBNull.Value)
                .ToArray();
            buffer.Rows.Add(values);
            if (track) Track(table, read, projected.Select((item, index) => (item.Column.PropertyName, values[index])));
            if (buffer.Rows.Count < BulkBatchSize) continue;
            await BulkAsync(connection, read.SourceTable, buffer, cancellationToken);
            written += buffer.Rows.Count;
            buffer.Clear();
        }
        if (buffer.Rows.Count != 0)
        {
            await BulkAsync(connection, read.SourceTable, buffer, cancellationToken);
            written += buffer.Rows.Count;
        }
        var role = SourceProtocol.Describe(table.SourceTable).Role;
        counts[role] = counts.GetValueOrDefault(role) + written;
    }

    private static async Task BulkAsync(SqlConnection connection, string destination, DataTable buffer,
        CancellationToken cancellationToken)
    {
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, null)
        {
            DestinationTableName = $"[dbo].[{SourceProtocolDatabase.Escape(destination)}]",
            BatchSize = BulkBatchSize,
            BulkCopyTimeout = 600,
        };
        foreach (DataColumn column in buffer.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await bulk.WriteToServerAsync(buffer, cancellationToken);
    }

    private void Track(SourceTableProtocol table, SourceRead read, IEnumerable<(string Name, object Value)> values)
    {
        var line = new StringBuilder(table.SourceTable).Append('|').Append(read.SourceTable);
        foreach (var (name, value) in values)
            line.Append('|').Append(name).Append('=').Append(value is DBNull ? "NULL" : Convert.ToString(value,
                CultureInfo.InvariantCulture));
        line.Append('\n');
        hash.AppendData(Encoding.UTF8.GetBytes(line.ToString()));
    }

    private async Task EnsureDatabaseAsync(CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
            throw new ArgumentException("The source connection must name a database.", nameof(connectionString));
        var database = builder.InitialCatalog;
        builder.InitialCatalog = "master";
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT DB_ID(@database);";
        probe.Parameters.AddWithValue("@database", database);
        if (await probe.ExecuteScalarAsync(cancellationToken) is not DBNull) return;
        await using var create = connection.CreateCommand();
        create.CommandText = $"CREATE DATABASE [{SourceProtocolDatabase.Escape(database)}];";
        await create.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DropProtocolTablesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        foreach (var sourceTable in SourceProtocol.Tables.SelectMany(table => table.Reads)
                     .Select(read => read.SourceTable).Distinct(StringComparer.Ordinal).Reverse())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{SourceProtocolDatabase.Escape(sourceTable)}];";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static SourceTableProtocol Table<TRow>() where TRow : StagingRow =>
        SourceProtocol.Tables.Single(table => table.RowType == typeof(TRow));

    private static async Task<int> UpdateExistingCardsAsync(SqlConnection connection, SourceTableProtocol table,
        string property, object value, int offset, int count, CancellationToken cancellationToken)
    {
        var read = table.Reads[0];
        var column = SourceProtocolDatabase.SourceColumn(read,
            table.Columns.Single(item => item.PropertyName == property))!;
        var key = SourceProtocolDatabase.SourceColumn(read,
            table.Columns.Single(item => item.PropertyName == nameof(EquipmentCardStagingRow.CardId)))!;
        var contractTable = Table<ContractLineStagingRow>();
        var contractRead = contractTable.Reads[0];
        var contractCardKey = SourceProtocolDatabase.SourceColumn(contractRead,
            contractTable.Columns.Single(item => item.PropertyName == nameof(ContractLineStagingRow.EquipmentCardId)))!;
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH candidates AS
            (
                SELECT card.[{SourceProtocolDatabase.Escape(key)}],
                       ROW_NUMBER() OVER (ORDER BY card.[{SourceProtocolDatabase.Escape(key)}]) AS row_number
                  FROM [dbo].[{SourceProtocolDatabase.Escape(read.SourceTable)}] AS card
                 WHERE NOT EXISTS
                 (
                     SELECT 1
                       FROM [dbo].[{SourceProtocolDatabase.Escape(contractRead.SourceTable)}] AS contract_line
                      WHERE contract_line.[{SourceProtocolDatabase.Escape(contractCardKey)}] = card.[{SourceProtocolDatabase.Escape(key)}]
                 )
            )
            UPDATE card
               SET [{SourceProtocolDatabase.Escape(column)}] = @value
              FROM [dbo].[{SourceProtocolDatabase.Escape(read.SourceTable)}] AS card
              JOIN candidates ON candidates.[{SourceProtocolDatabase.Escape(key)}] = card.[{SourceProtocolDatabase.Escape(key)}]
             WHERE candidates.row_number > @offset
               AND candidates.row_number <= @offset + @count;
            """;
        command.Parameters.AddWithValue("@value", value);
        command.Parameters.AddWithValue("@offset", offset);
        command.Parameters.AddWithValue("@count", count);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteSerialRangeAsync(SqlConnection connection, SourceTableProtocol table, int start,
        int finish, CancellationToken cancellationToken)
    {
        var read = table.Reads[0];
        var key = SourceProtocolDatabase.SourceColumn(read, table.Columns.Single(item =>
            item.PropertyName == nameof(SerialMasterStagingRow.SystemNumber)))!;
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE [dbo].[{SourceProtocolDatabase.Escape(read.SourceTable)}] WHERE " +
            $"[{SourceProtocolDatabase.Escape(key)}] BETWEEN @start AND @finish;";
        command.Parameters.AddWithValue("@start", start);
        command.Parameters.AddWithValue("@finish", finish);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record Movement(int Log, int Type, int Entry, decimal Quantity, int Day, int BaseType,
        int? BaseEntry, string Warehouse, string? Party);
}
