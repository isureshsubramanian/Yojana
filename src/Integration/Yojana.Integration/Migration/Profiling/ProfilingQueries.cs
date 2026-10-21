using Microsoft.EntityFrameworkCore;
using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Integration.LegacyMigration.Canonicalisation;

// Culture-aware overloads and StringComparison are not translated by the SQL Server EF provider. These expressions
// execute in SQL under the database collation; the binary collation below is explicit where case must be observed.
#pragma warning disable CA1304, CA1311, CA1862

namespace Yojana.Integration.LegacyMigration.Profiling;

internal static class ProfilingQuery
{
    public static IQueryable<TRow> Active<TRow>(this MigrationProfileQueryContext context)
        where TRow : StagingRow => context.Db.Set<TRow>().AsNoTracking().Where(row =>
            row.BatchId == context.BatchId && row.SourceSystem == context.SourceSystem &&
            row.AbsentSinceBatchId == null);

    public static IReadOnlyDictionary<string, string?> Facts(params (string Key, object? Value)[] facts) =>
        facts.ToDictionary(item => item.Key, item => item.Value?.ToString(), StringComparer.Ordinal);
}

internal sealed class SerialInMultipleWarehousesProfileQuery : IMigrationProfileQuery
{
    public MigrationProfileClass Class => MigrationProfileClasses.SerialInMultipleWarehouses;

    public async Task<MigrationProfileQueryAnswer> ExecuteAsync(MigrationProfileQueryContext context,
        CancellationToken cancellationToken)
    {
        var query = context.Active<SerialQuantityStagingRow>()
            .Where(row => row.ItemCode != null && row.SystemNumber != null)
            .GroupBy(row => new { row.ItemCode, row.SystemNumber })
            .Select(group => new
            {
                group.Key.ItemCode,
                group.Key.SystemNumber,
                Total = group.Sum(row => row.Quantity ?? 0m),
                Warehouses = group.Count(row => (row.Quantity ?? 0m) > 0m),
            })
            .Where(row => row.Total > 1m && row.Warehouses > 1);
        var count = await query.LongCountAsync(cancellationToken);
        var rows = await (from anomaly in query
                          join master in context.Active<SerialMasterStagingRow>()
                              on new { anomaly.ItemCode, anomaly.SystemNumber } equals
                              new { master.ItemCode, master.SystemNumber } into masters
                          from master in masters.DefaultIfEmpty()
                          orderby anomaly.ItemCode, anomaly.SystemNumber
                          select new
                          {
                              anomaly.ItemCode,
                              anomaly.SystemNumber,
                              anomaly.Total,
                              anomaly.Warehouses,
                              master.SerialNumber,
                          }).Take(20)
            .ToListAsync(cancellationToken);
        return new(count, null, rows.Select(row => new MigrationProfileSample(
            $"{row.ItemCode}|{row.SystemNumber}", row.SerialNumber, row.ItemCode,
            ProfilingQuery.Facts(("system_number", row.SystemNumber), ("total_quantity", row.Total),
                ("warehouse_count", row.Warehouses)))).ToList());
    }
}

internal sealed class NegativeQuantityProfileQuery : IMigrationProfileQuery
{
    public MigrationProfileClass Class => MigrationProfileClasses.NegativeQuantity;

    public async Task<MigrationProfileQueryAnswer> ExecuteAsync(MigrationProfileQueryContext context,
        CancellationToken cancellationToken)
    {
        var query = context.Active<SerialQuantityStagingRow>().Where(row => (row.Quantity ?? 0m) < 0m);
        var count = await query.LongCountAsync(cancellationToken);
        var rows = await (from quantity in query
                          join master in context.Active<SerialMasterStagingRow>()
                              on new { quantity.ItemCode, quantity.SystemNumber } equals
                              new { master.ItemCode, master.SystemNumber } into masters
                          from master in masters.DefaultIfEmpty()
                          orderby quantity.SourcePk
                          select new
                          {
                              quantity.SourcePk,
                              quantity.ItemCode,
                              quantity.SystemNumber,
                              quantity.WarehouseCode,
                              quantity.Quantity,
                              master.SerialNumber,
                          }).Take(20).ToListAsync(cancellationToken);
        return new(count, null, rows.Select(row => new MigrationProfileSample(row.SourcePk, row.SerialNumber, row.ItemCode,
            ProfilingQuery.Facts(("system_number", row.SystemNumber), ("warehouse", row.WarehouseCode),
                ("quantity", row.Quantity)))).ToList());
    }
}

internal sealed class DuplicateSerialSameItemProfileQuery : IMigrationProfileQuery
{
    public MigrationProfileClass Class => MigrationProfileClasses.DuplicateSerialSameItem;

    public async Task<MigrationProfileQueryAnswer> ExecuteAsync(MigrationProfileQueryContext context,
        CancellationToken cancellationToken)
    {
        var query = context.Active<SerialMasterStagingRow>()
            .Where(row => row.ItemCode != null && row.SerialNumber != null && row.SerialNumber.Trim() != "")
            .GroupBy(row => new { row.ItemCode, Serial = row.SerialNumber!.Trim().ToUpper() })
            .Where(group => group.Count() > 1)
            .Select(group => new
            {
                group.Key.ItemCode,
                group.Key.Serial,
                Rows = group.Count(),
                Stored = group.Min(row => row.SerialNumber),
            });
        var count = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(row => row.ItemCode).ThenBy(row => row.Serial).Take(20)
            .ToListAsync(cancellationToken);
        return new(count, null, rows.Select(row => new MigrationProfileSample(
            $"{row.ItemCode}|{row.Serial}", row.Stored, row.ItemCode,
            ProfilingQuery.Facts(("normalised", row.Serial), ("matching_rows", row.Rows),
                ("match_rule", "trimmed_upper_case")))).ToList());
    }
}

internal sealed class DuplicateSerialDifferentItemsProfileQuery : IMigrationProfileQuery
{
    public MigrationProfileClass Class => MigrationProfileClasses.DuplicateSerialDifferentItems;

    public async Task<MigrationProfileQueryAnswer> ExecuteAsync(MigrationProfileQueryContext context,
        CancellationToken cancellationToken)
    {
        var query = context.Active<SerialMasterStagingRow>()
            .Where(row => row.ItemCode != null && row.SerialNumber != null && row.SerialNumber.Trim() != "")
            .GroupBy(row => row.SerialNumber!.Trim().ToUpper())
            .Where(group => group.Select(row => row.ItemCode).Distinct().Count() > 1)
            .Select(group => new
            {
                Serial = group.Key,
                Items = group.Select(row => row.ItemCode).Distinct().Count(),
                FirstItem = group.Min(row => row.ItemCode),
                Stored = group.Min(row => row.SerialNumber),
            });
        var count = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(row => row.Serial).Take(20).ToListAsync(cancellationToken);
        return new(count, null, rows.Select(row => new MigrationProfileSample(row.Serial, row.Stored, row.FirstItem,
            ProfilingQuery.Facts(("normalised", row.Serial), ("item_count", row.Items),
                ("match_rule", "trimmed_upper_case")))).ToList());
    }
}

internal sealed class SerialWhitespaceOrCaseProfileQuery : IMigrationProfileQuery
{
    public MigrationProfileClass Class => MigrationProfileClasses.SerialWhitespaceOrCase;

    public async Task<MigrationProfileQueryAnswer> ExecuteAsync(MigrationProfileQueryContext context,
        CancellationToken cancellationToken)
    {
        const string binaryCollation = "Latin1_General_100_BIN2";
        var query = context.Active<SerialMasterStagingRow>().Where(row => row.SerialNumber != null &&
            row.SerialNumber != "" && EF.Functions.Collate(row.SerialNumber, binaryCollation) !=
            EF.Functions.Collate(row.SerialNumber.Trim().Replace(" ", "").ToUpper(), binaryCollation));
        var count = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(row => row.SourcePk).Take(20).ToListAsync(cancellationToken);
        return new(count, null, rows.Select(row => new MigrationProfileSample(row.SourcePk, row.SerialNumber,
            row.ItemCode, ProfilingQuery.Facts(("normalised", row.SerialNumber!.Trim().Replace(" ", "")
                .ToUpperInvariant())))).ToList());
    }
}

internal sealed class SerialFormatVariantsProfileQuery : IMigrationProfileQuery
{
    public MigrationProfileClass Class => MigrationProfileClasses.SerialFormatVariants;

    public async Task<MigrationProfileQueryAnswer> ExecuteAsync(MigrationProfileQueryContext context,
        CancellationToken cancellationToken)
    {
        var candidates = context.Active<SerialMasterStagingRow>()
            .Where(row => row.ItemCode != null && row.SerialNumber != null && row.SerialNumber.Trim() != "")
            .Select(row => new
            {
                Family = row.ItemCode!.Contains('-')
                    ? row.ItemCode.Substring(0, row.ItemCode.IndexOf('-')) : row.ItemCode,
                Stored = row.SerialNumber!.Trim().ToUpper(),
                Normalised = CanonicalSerialNormalisation.Apply(row.SerialNumber, true, true, true),
                row.ItemCode,
            })
            .Where(row => row.Normalised != null);
        var query = candidates.GroupBy(row => new { row.Family, row.Normalised })
            .Where(group => group.Select(row => row.Stored).Distinct().Count() > 1)
            .Select(group => new
            {
                group.Key.Family,
                group.Key.Normalised,
                Variants = group.Select(row => row.Stored).Distinct().Count(),
                FirstItem = group.Min(row => row.ItemCode),
                FirstStored = group.Min(row => row.Stored),
            });
        var count = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(row => row.Family).ThenBy(row => row.Normalised).Take(20)
            .ToListAsync(cancellationToken);
        const string note = "The item code prefix before the first hyphen is the temporary family rule until approved family rules are configured.";
        return new(count, note, rows.Select(row => new MigrationProfileSample(
            $"{row.Family}|{row.Normalised}", row.FirstStored, row.FirstItem,
            ProfilingQuery.Facts(("item_family", row.Family), ("variant_count", row.Variants),
                ("normalisation", "remove_hyphens_spaces_and_leading_zeroes")))).ToList());
    }
}

internal sealed class PlaceholderSerialProfileQuery : IMigrationProfileQuery
{
    public MigrationProfileClass Class => MigrationProfileClasses.PlaceholderSerial;

    public async Task<MigrationProfileQueryAnswer> ExecuteAsync(MigrationProfileQueryContext context,
        CancellationToken cancellationToken)
    {
        var settings = context.Active<ItemSerialSettingStagingRow>()
            .Where(row => row.ItemCode != null && row.ManagementMethod == "R");
        var query = from master in context.Active<SerialMasterStagingRow>()
                    join setting in settings on master.ItemCode equals setting.ItemCode
                    where master.SerialNumber == null || master.SerialNumber.Trim() == ""
                    select master;
        var count = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(row => row.SourcePk).Take(20).ToListAsync(cancellationToken);
        return new(count, null, rows.Select(row => new MigrationProfileSample(row.SourcePk, row.SerialNumber,
            row.ItemCode, ProfilingQuery.Facts(("system_number", row.SystemNumber),
                ("management_method", "system_assigned")))).ToList());
    }
}

internal sealed class EquipmentCardWithoutSerialProfileQuery : IMigrationProfileQuery
{
    public MigrationProfileClass Class => MigrationProfileClasses.EquipmentCardWithoutSerial;

    public async Task<MigrationProfileQueryAnswer> ExecuteAsync(MigrationProfileQueryContext context,
        CancellationToken cancellationToken)
    {
        var masters = context.Active<SerialMasterStagingRow>();
        var query = context.Active<EquipmentCardStagingRow>().Where(card => !masters.Any(master =>
            master.ItemCode == card.ItemCode &&
            ((card.InternalSerial != null && master.SerialNumber != null &&
              card.InternalSerial.Trim().ToUpper() == master.SerialNumber.Trim().ToUpper()) ||
             (card.ManufacturerSerial != null && master.ManufacturerSerial != null &&
              card.ManufacturerSerial.Trim().ToUpper() == master.ManufacturerSerial.Trim().ToUpper()))));
        var count = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(row => row.SourcePk).Take(20).ToListAsync(cancellationToken);
        return new(count, null, rows.Select(row => new MigrationProfileSample(row.SourcePk,
            row.InternalSerial ?? row.ManufacturerSerial, row.ItemCode,
            ProfilingQuery.Facts(("card_id", row.CardId), ("customer", row.CustomerCode),
                ("reason", "no_serial_master_match")))).ToList());
    }
}

internal sealed class ShippedSerialWithoutCardProfileQuery : IMigrationProfileQuery
{
    public MigrationProfileClass Class => MigrationProfileClasses.ShippedSerialWithoutCard;

    public async Task<MigrationProfileQueryAnswer> ExecuteAsync(MigrationProfileQueryContext context,
        CancellationToken cancellationToken)
    {
        var headers = context.Active<TransactionLogHeaderStagingRow>()
            .Where(row => row.LogEntry != null && (row.DocumentType == 13 || row.DocumentType == 15));
        var cards = context.Active<EquipmentCardStagingRow>();
        var query = (from line in context.Active<TransactionLogLineStagingRow>()
                     join header in headers on line.LogEntry equals header.LogEntry
                     join master in context.Active<SerialMasterStagingRow>()
                         on new { line.ItemCode, line.SystemNumber } equals
                         new { master.ItemCode, master.SystemNumber }
                     where (line.Quantity ?? 0m) < 0m && !cards.Any(card => card.ItemCode == master.ItemCode &&
                         ((card.InternalSerial != null && master.SerialNumber != null &&
                           card.InternalSerial.Trim().ToUpper() == master.SerialNumber.Trim().ToUpper()) ||
                          (card.ManufacturerSerial != null && master.ManufacturerSerial != null &&
                           card.ManufacturerSerial.Trim().ToUpper() == master.ManufacturerSerial.Trim().ToUpper())))
                     select new
                     {
                         line.ItemCode,
                         line.SystemNumber,
                         master.SerialNumber,
                         header.DocumentType,
                         header.DocumentNumber,
                         header.PartyCode,
                     }).Distinct();
        var count = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(row => row.ItemCode).ThenBy(row => row.SystemNumber).Take(20)
            .ToListAsync(cancellationToken);
        return new(count, null, rows.Select(row => new MigrationProfileSample(
            $"{row.ItemCode}|{row.SystemNumber}", row.SerialNumber, row.ItemCode,
            ProfilingQuery.Facts(("document_type", row.DocumentType), ("document_number", row.DocumentNumber),
                ("party", row.PartyCode)))).ToList());
    }
}

internal sealed class CardCustomerDeliveryMismatchProfileQuery : IMigrationProfileQuery
{
    public MigrationProfileClass Class => MigrationProfileClasses.CardCustomerDeliveryMismatch;

    public async Task<MigrationProfileQueryAnswer> ExecuteAsync(MigrationProfileQueryContext context,
        CancellationToken cancellationToken)
    {
        var headers = context.Active<TransactionLogHeaderStagingRow>()
            .Where(row => row.LogEntry != null && row.PartyCode != null &&
                (row.DocumentType == 13 || row.DocumentType == 15));
        var deliveries = from line in context.Active<TransactionLogLineStagingRow>()
                         join header in headers on line.LogEntry equals header.LogEntry
                         where (line.Quantity ?? 0m) < 0m
                         select new
                         {
                             line.ItemCode,
                             line.SystemNumber,
                             header.PartyCode,
                             header.DocumentType,
                             header.DocumentNumber,
                             header.DocumentDate,
                             header.CreatedDate,
                             header.CreatedTime,
                             header.LogEntry,
                         };
        var query = from card in context.Active<EquipmentCardStagingRow>()
                    from master in context.Active<SerialMasterStagingRow>().Where(master =>
                        master.ItemCode == card.ItemCode &&
                        ((card.InternalSerial != null && master.SerialNumber != null &&
                          card.InternalSerial.Trim().ToUpper() == master.SerialNumber.Trim().ToUpper()) ||
                         (card.ManufacturerSerial != null && master.ManufacturerSerial != null &&
                          card.ManufacturerSerial.Trim().ToUpper() == master.ManufacturerSerial.Trim().ToUpper())))
                    join delivery in deliveries on new { master.ItemCode, master.SystemNumber } equals
                        new { delivery.ItemCode, delivery.SystemNumber }
                    where card.CustomerCode != null && delivery.PartyCode != null &&
                          card.CustomerCode != delivery.PartyCode &&
                          !deliveries.Any(later => later.ItemCode == delivery.ItemCode &&
                              later.SystemNumber == delivery.SystemNumber &&
                              ((later.DocumentDate ?? DateTime.MinValue) >
                                   (delivery.DocumentDate ?? DateTime.MinValue) ||
                               ((later.DocumentDate ?? DateTime.MinValue) ==
                                    (delivery.DocumentDate ?? DateTime.MinValue) &&
                                (later.CreatedDate ?? DateTime.MinValue) >
                                    (delivery.CreatedDate ?? DateTime.MinValue)) ||
                               ((later.DocumentDate ?? DateTime.MinValue) ==
                                    (delivery.DocumentDate ?? DateTime.MinValue) &&
                                (later.CreatedDate ?? DateTime.MinValue) ==
                                    (delivery.CreatedDate ?? DateTime.MinValue) &&
                                (later.CreatedTime ?? -1) > (delivery.CreatedTime ?? -1)) ||
                               ((later.DocumentDate ?? DateTime.MinValue) ==
                                    (delivery.DocumentDate ?? DateTime.MinValue) &&
                                (later.CreatedDate ?? DateTime.MinValue) ==
                                    (delivery.CreatedDate ?? DateTime.MinValue) &&
                                (later.CreatedTime ?? -1) == (delivery.CreatedTime ?? -1) &&
                                (later.LogEntry ?? -1) > (delivery.LogEntry ?? -1))))
                    select new
                    {
                        card.SourcePk,
                        card.CardId,
                        card.ItemCode,
                        Serial = card.InternalSerial ?? card.ManufacturerSerial,
                        CardCustomer = card.CustomerCode,
                        DeliveredCustomer = delivery.PartyCode,
                        delivery.DocumentNumber,
                    };
        var count = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(row => row.SourcePk).Take(20).ToListAsync(cancellationToken);
        return new(count, null, rows.Select(row => new MigrationProfileSample(row.SourcePk, row.Serial, row.ItemCode,
            ProfilingQuery.Facts(("card_id", row.CardId), ("card_customer", row.CardCustomer),
                ("last_delivery_customer", row.DeliveredCustomer),
                ("last_document_number", row.DocumentNumber)))).ToList());
    }
}

internal sealed class ActiveContractPastEndProfileQuery : IMigrationProfileQuery
{
    public MigrationProfileClass Class => MigrationProfileClasses.ActiveContractPastEnd;

    public async Task<MigrationProfileQueryAnswer> ExecuteAsync(MigrationProfileQueryContext context,
        CancellationToken cancellationToken)
    {
        var asOf = context.ExtractionDate;
        var query = context.Active<ServiceContractStagingRow>().Where(row =>
            (row.Status == "A" || row.Status == "Active") && row.EndDate < asOf && row.TerminationDate == null);
        var count = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(row => row.SourcePk).Take(20).ToListAsync(cancellationToken);
        return new(count, null, rows.Select(row => new MigrationProfileSample(row.SourcePk, null, null,
            ProfilingQuery.Facts(("contract_id", row.ContractId), ("document_number", row.DocumentNumber),
                ("customer", row.CustomerCode), ("end_date", row.EndDate), ("status", row.Status),
                ("as_of", asOf)))).ToList());
    }
}

internal sealed class AmbiguousDatesProfileQuery : IMigrationProfileQuery
{
    public MigrationProfileClass Class => MigrationProfileClasses.AmbiguousDates;

    public Task<MigrationProfileQueryAnswer> ExecuteAsync(MigrationProfileQueryContext context,
        CancellationToken cancellationToken) => Task.FromResult(new MigrationProfileQueryAnswer(0,
        "No spreadsheet source in this extraction; staged dates are typed values and cannot reveal an ambiguous text date.", []));
}

internal sealed class SerialsInUserFieldsProfileQuery : IMigrationProfileQuery
{
    public MigrationProfileClass Class => MigrationProfileClasses.SerialsInUserFields;

    public Task<MigrationProfileQueryAnswer> ExecuteAsync(MigrationProfileQueryContext context,
        CancellationToken cancellationToken) => Task.FromResult(new MigrationProfileQueryAnswer(null,
        "Not configured: user fields are not staged and no discovery rule has been approved.", []));
}

internal sealed class EmptyStatusProfileQuery : IMigrationProfileQuery
{
    public MigrationProfileClass Class => MigrationProfileClasses.EmptyStatus;

    public async Task<MigrationProfileQueryAnswer> ExecuteAsync(MigrationProfileQueryContext context,
        CancellationToken cancellationToken)
    {
        var query = context.Active<SerialMasterStagingRow>().Where(row => row.Status == null);
        var count = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(row => row.SourcePk).Take(20).ToListAsync(cancellationToken);
        const string note = "Availability is derived from per-warehouse quantity; serial-master status is ignored.";
        return new(count, note, rows.Select(row => new MigrationProfileSample(row.SourcePk, row.SerialNumber,
            row.ItemCode, ProfilingQuery.Facts(("system_number", row.SystemNumber),
                ("availability_basis", "per_warehouse_quantity")))).ToList());
    }
}
