using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Staging;

internal sealed record StagingCardCustomerMismatch(
    string SourcePk,
    int? CardId,
    string? ItemCode,
    string? Serial,
    string CardCustomer,
    string DeliveredCustomer,
    int? DocumentNumber);

internal sealed record StagingCardCustomerMismatchAnswer(
    long Count,
    IReadOnlyList<StagingCardCustomerMismatch> Rows);

/// <summary>
/// Keeps source-shaped physical staging names behind a narrow read contract. Profiling owns the business question;
/// staging owns the SQL needed to answer it without leaking source identifiers into the rest of the pipeline.
/// </summary>
internal sealed class StagingMismatchProfileReader(IntegrationDbContext db)
{
    public async Task<StagingCardCustomerMismatchAnswer> ReadCardCustomerDeliveryMismatchesAsync(
        TenantId tenantId, Guid batchId, string sourceSystem, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != System.Data.ConnectionState.Open;
        if (closeConnection) await db.Database.OpenConnectionAsync(cancellationToken);
        long count;
        var rows = new List<StagingCardCustomerMismatch>();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = db.Database.GetCommandTimeout() ?? 30;
            command.CommandText = CardCustomerDeliveryMismatchSql;
            command.Parameters.Add(new SqlParameter("@tenant", tenantId.Value));
            command.Parameters.Add(new SqlParameter("@batch", batchId));
            command.Parameters.Add(new SqlParameter("@source", sourceSystem));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            count = reader.GetInt64(0);
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4), reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6)));
        }
        finally
        {
            if (closeConnection) await db.Database.CloseConnectionAsync();
        }
        return new(count, rows);
    }

    private const string CardCustomerDeliveryMismatchSql = """
        SET NOCOUNT ON;
        CREATE TABLE #mismatch
        (
            source_pk nvarchar(1000) NOT NULL,
            card_id int NULL,
            item_code nvarchar(100) NULL,
            serial nvarchar(500) NULL,
            card_customer nvarchar(100) NOT NULL,
            delivered_customer nvarchar(100) NOT NULL,
            document_number int NULL
        );

        WITH logged AS
        (
            SELECT line.tenant_id, line.batch_id, line.source_system, line.item_code, line.system_number,
                   line.quantity, header.document_type, header.document_entry, header.base_type,
                   header.base_entry, header.party_code, header.document_number, header.document_date,
                   header.created_date, header.created_time, header.log_entry
              FROM [migration].[stg_src_itl1] AS line
              JOIN [migration].[stg_src_oitl] AS header
                ON header.tenant_id = line.tenant_id AND header.batch_id = line.batch_id
               AND header.source_system = line.source_system AND header.log_entry = line.log_entry
             WHERE line.tenant_id = @tenant AND line.batch_id = @batch AND line.source_system = @source
               AND line.absent_since_batch_id IS NULL AND header.absent_since_batch_id IS NULL
        ), standing AS
        (
            SELECT delivery.*
              FROM logged AS delivery
             WHERE delivery.document_type IN (13, 15) AND COALESCE(delivery.quantity, 0) < 0
               AND delivery.party_code IS NOT NULL
               AND NOT EXISTS
               (
                   SELECT 1 FROM logged AS cancellation
                    WHERE cancellation.tenant_id = delivery.tenant_id
                      AND cancellation.batch_id = delivery.batch_id
                      AND cancellation.source_system = delivery.source_system
                      AND cancellation.document_type = delivery.document_type
                      AND cancellation.base_type = delivery.document_type
                      AND cancellation.base_entry = delivery.document_entry
                      AND cancellation.item_code = delivery.item_code
                      AND cancellation.system_number = delivery.system_number
                      AND COALESCE(cancellation.quantity, 0) > 0
               )
        ), latest AS
        (
            SELECT standing.*,
                   ROW_NUMBER() OVER
                   (
                       PARTITION BY tenant_id, batch_id, source_system, item_code, system_number
                       ORDER BY COALESCE(document_date, CONVERT(datetime2, '0001-01-01')) DESC,
                                COALESCE(created_date, CONVERT(datetime2, '0001-01-01')) DESC,
                                COALESCE(created_time, -1) DESC, COALESCE(log_entry, -1) DESC
                   ) AS latest_row
              FROM standing
        ), matches AS
        (
            SELECT card.tenant_id, card.batch_id, card.source_system, card.source_pk AS card_source_pk,
                   card.card_id, card.item_code, COALESCE(card.internal_serial, card.manufacturer_serial) AS serial,
                   card.customer_code AS card_customer, master.system_number
              FROM [migration].[stg_src_oins] AS card
              JOIN [migration].[stg_src_osrn] AS master
                ON master.tenant_id = card.tenant_id AND master.batch_id = card.batch_id
               AND master.source_system = card.source_system AND master.item_code = card.item_code
               AND master.normalised_serial_number = card.normalised_internal_serial
             WHERE card.tenant_id = @tenant AND card.batch_id = @batch AND card.source_system = @source
               AND card.absent_since_batch_id IS NULL AND master.absent_since_batch_id IS NULL
               AND card.normalised_internal_serial IS NOT NULL
            UNION
            SELECT card.tenant_id, card.batch_id, card.source_system, card.source_pk AS card_source_pk,
                   card.card_id, card.item_code, COALESCE(card.internal_serial, card.manufacturer_serial) AS serial,
                   card.customer_code AS card_customer, master.system_number
              FROM [migration].[stg_src_oins] AS card
              JOIN [migration].[stg_src_osrn] AS master
                ON master.tenant_id = card.tenant_id AND master.batch_id = card.batch_id
               AND master.source_system = card.source_system AND master.item_code = card.item_code
               AND master.normalised_manufacturer_serial = card.normalised_manufacturer_serial
             WHERE card.tenant_id = @tenant AND card.batch_id = @batch AND card.source_system = @source
               AND card.absent_since_batch_id IS NULL AND master.absent_since_batch_id IS NULL
               AND card.normalised_manufacturer_serial IS NOT NULL
        )
        INSERT #mismatch
            (source_pk, card_id, item_code, serial, card_customer, delivered_customer, document_number)
        SELECT match.card_source_pk, match.card_id, match.item_code, match.serial, match.card_customer,
               delivery.party_code, delivery.document_number
          FROM matches AS match
          JOIN latest AS delivery
            ON delivery.tenant_id = match.tenant_id AND delivery.batch_id = match.batch_id
           AND delivery.source_system = match.source_system AND delivery.item_code = match.item_code
           AND delivery.system_number = match.system_number AND delivery.latest_row = 1
         WHERE match.card_customer IS NOT NULL AND match.card_customer <> delivery.party_code;

        SELECT COUNT_BIG(1) FROM #mismatch;
        SELECT TOP (20) source_pk, card_id, item_code, serial, card_customer, delivered_customer, document_number
          FROM #mismatch ORDER BY source_pk;
        DROP TABLE #mismatch;
        """;
}
