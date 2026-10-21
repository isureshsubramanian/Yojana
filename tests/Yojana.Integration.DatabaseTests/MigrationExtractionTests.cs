using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class MigrationExtractionTests(IntegrationHostFixture fixture)
{
    private const string SourceDatabase = "yojana_src_p9t02";
    private const string Actor = "fictional-migration-admin";
    private const string SecretMarker = "P9T02-secret-marker";

    [DatabaseFact]
    public async Task Full_extraction_is_raw_idempotent_marks_absence_and_supports_cross_database()
    {
        await SourceFixture.RecreateAsync(SourceDatabase);
        try
        {
            var tenant = await fixture.NewTenantAsync("mig");
            var otherTenant = await fixture.NewTenantAsync("migoth");
            using var scope = fixture.ScopeFor(tenant);
            var sources = scope.ServiceProvider.GetRequiredService<MigrationSourceService>();
            var batches = scope.ServiceProvider.GetRequiredService<MigrationBatchService>();
            var connectionString = SourceFixture.ConnectionString(SourceDatabase, SecretMarker);
            var source = await sources.CreateAsync(new("Incumbent production copy", "cx01",
                MigrationSourceRuntime.SqlServer, MigrationSourceRoute.Separate, SourceDatabase,
                ConnectionString: connectionString), Actor, default);
            Assert.True(source.HasConnectionString);
            Assert.Equal(MigrationLicencePosition.Unconfirmed, source.LicencePosition);
            Assert.DoesNotContain(SecretMarker, JsonSerializer.Serialize(source));
            source = await sources.UpdateAsync(new(source.SourceId, "Incumbent production copy (read-only)",
                "cx01", MigrationSourceRuntime.SqlServer, MigrationSourceRoute.Separate, SourceDatabase),
                Actor, default);
            Assert.True(source.HasConnectionString);
            var sourcePage = await sources.ListAsync(null, 1, default);
            Assert.Equal(source.SourceId, Assert.Single(sourcePage.Items).SourceId);
            Assert.DoesNotContain(SecretMarker, JsonSerializer.Serialize(sourcePage));

            var refused = await batches.StartAsync(new(source.SourceId), Actor, default);
            Assert.Equal(MigrationBatchStatus.Refused, refused.Status);
            Assert.Equal("migration_extraction_licence_unconfirmed", refused.Failure);
            source = await sources.SetLicenceAsync(source.SourceId,
                new(true, "FICTIONAL-CONTRACT-2026-09", "Customer counsel confirmed read-only extraction."),
                Actor, default);
            Assert.Equal(Actor, source.LicenceConfirmedByName);

            var tested = await sources.TestAsync(source.SourceId, default);
            Assert.All(tested.Tables, table => Assert.Null(table.Error));
            Assert.DoesNotContain(SecretMarker, JsonSerializer.Serialize(tested));

            var first = await RunAsync(batches, source.SourceId);
            Assert.Equal(20, first.Tables!.Sum(x => x.Inserted));
            Assert.Equal(0, first.Tables!.Sum(x => x.Updated + x.Unchanged + x.Absent));

            var second = await RunAsync(batches, source.SourceId);
            Assert.Equal(20, second.Tables!.Sum(x => x.Unchanged));
            Assert.Equal(0, second.Tables!.Sum(x => x.Inserted + x.Updated + x.Absent));

            await SourceFixture.UpdateAsync<SerialMasterStagingRow>(SourceDatabase,
                new Dictionary<string, object?> { [nameof(SerialMasterStagingRow.Notes)] = "Accidental damage add-on" },
                new Dictionary<string, object?>
                {
                    [nameof(SerialMasterStagingRow.ItemCode)] = "SERVER-RACK-01",
                    [nameof(SerialMasterStagingRow.SystemNumber)] = 1,
                });
            var changed = await RunAsync(batches, source.SourceId);
            Assert.Equal(1, changed.Tables!.Sum(x => x.Updated));
            Assert.Equal(19, changed.Tables!.Sum(x => x.Unchanged));

            await SourceFixture.DeleteAsync<SerialMasterStagingRow>(SourceDatabase, new Dictionary<string, object?>
            {
                [nameof(SerialMasterStagingRow.ItemCode)] = "SERVER-RACK-01",
                [nameof(SerialMasterStagingRow.SystemNumber)] = 1,
            });
            var deleted = await RunAsync(batches, source.SourceId);
            Assert.Equal(1, deleted.Tables!.Sum(x => x.Absent));
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var absent = await db.Set<SerialMasterStagingRow>().SingleAsync(x =>
                x.SourceSystem == "cx01" && x.ItemCode == "SERVER-RACK-01" && x.SystemNumber == 1);
            Assert.Equal(deleted.BatchId, absent.AbsentSinceBatchId);

            await SourceFixture.InsertAsync<SerialMasterStagingRow>(SourceDatabase,
                SourceFixture.Serial("SERVER-RACK-01", 1, "  srv-Mixed-001  ", "Accidental damage add-on"));
            var returned = await RunAsync(batches, source.SourceId);
            Assert.Equal(1, returned.Tables!.Sum(x => x.Updated));
            var restored = await db.Set<SerialMasterStagingRow>().AsNoTracking().SingleAsync(x =>
                x.SourceSystem == "cx01" && x.ItemCode == "SERVER-RACK-01" && x.SystemNumber == 1);
            Assert.Null(restored.AbsentSinceBatchId);
            Assert.Equal("  srv-Mixed-001  ", restored.SerialNumber);
            Assert.Equal("SERVER-RACK-01|1|MAIN", await db.Set<SerialQuantityStagingRow>().Where(x =>
                x.SourceSystem == "cx01" && x.ItemCode == "SERVER-RACK-01").Select(x => x.SourcePk).SingleAsync());

            var cross = await sources.CreateAsync(new("Same-server production copy", "cx01-cross",
                MigrationSourceRuntime.SqlServer, MigrationSourceRoute.CrossDatabase, SourceDatabase), Actor, default);
            cross = await sources.SetLicenceAsync(cross.SourceId,
                new(true, "FICTIONAL-CONTRACT-2026-09", "Same-server read-only extraction confirmed."), Actor, default);
            var crossBatch = await RunAsync(batches, cross.SourceId);
            Assert.Equal(20, crossBatch.Tables!.Sum(x => x.Inserted));
            await sources.SetLicenceAsync(cross.SourceId,
                new(false, null, "Contract review reopened by customer counsel."), Actor, default);
            var withdrawn = await batches.StartAsync(new(cross.SourceId), Actor, default);
            Assert.Equal(MigrationBatchStatus.Refused, withdrawn.Status);
            Assert.Contains(await db.ConnectionEvents.AsNoTracking().Where(x =>
                    x.ConnectionId == cross.SourceId).Select(x => x.EventType).ToListAsync(),
                eventType => eventType == "MigrationLicenceConfirmed");
            Assert.Contains(await db.ConnectionEvents.AsNoTracking().Where(x =>
                    x.ConnectionId == cross.SourceId).Select(x => x.EventType).ToListAsync(),
                eventType => eventType == "MigrationLicenceWithdrawn");

            using var otherScope = fixture.ScopeFor(otherTenant);
            var otherDb = otherScope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            Assert.Equal(0, await otherDb.Set<SerialMasterStagingRow>().CountAsync());
            Assert.Equal(0, await otherDb.MigrationBatches.CountAsync());
        }
        finally
        {
            await SourceFixture.DropAsync(SourceDatabase);
        }
    }

    [DatabaseFact]
    public async Task Unsupported_runtime_is_recorded_as_a_refused_batch_and_secrets_are_not_response_fields()
    {
        var tenant = await fixture.NewTenantAsync("migrun");
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<ITenantSettings>();
        await settings.SetAsync(MigrationLicenceSettings.SettingKey, MigrationLicencePosition.Confirmed, default);
        var sources = scope.ServiceProvider.GetRequiredService<MigrationSourceService>();
        var source = await sources.CreateAsync(new("Incumbent archive", "archive-source",
            MigrationSourceRuntime.UnsupportedColumnar, MigrationSourceRoute.Separate, "fictional_archive",
            ConnectionString: $"Server=fictitious;Password={SecretMarker}",
            LicenceContractReference: "FICTIONAL-CONTRACT-RUNTIME",
            LicenceNote: "Customer confirmation recorded for the archive."), Actor, default);
        var response = await scope.ServiceProvider.GetRequiredService<MigrationBatchService>()
            .StartAsync(new(source.SourceId), Actor, default);
        Assert.Equal(MigrationBatchStatus.Refused, response.Status);
        Assert.Equal("migration_source_runtime_unsupported", response.Failure);
        Assert.DoesNotContain(SecretMarker, JsonSerializer.Serialize(source));
        Assert.DoesNotContain(typeof(MigrationSourceView).GetProperties(),
            property => property.Name == nameof(CreateMigrationSourceRequest.ConnectionString));
    }

    [DatabaseFact]
    public async Task Database_prevents_two_running_batches_for_one_tenant_source()
    {
        var tenant = await fixture.NewTenantAsync("miglock");
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<ITenantSettings>();
        await settings.SetAsync(MigrationLicenceSettings.SettingKey, MigrationLicencePosition.Confirmed, default);
        var source = await scope.ServiceProvider.GetRequiredService<MigrationSourceService>().CreateAsync(
            new("Fictional same-server source", "lock-source", MigrationSourceRuntime.SqlServer,
                MigrationSourceRoute.CrossDatabase, "fictional_source",
                LicenceContractReference: "FICTIONAL-CONTRACT-LOCK",
                LicenceNote: "Read-only extraction confirmed."), Actor, default);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        foreach (var offset in new[] { 0, 1 })
            db.MigrationBatches.Add(new()
            {
                TenantId = tenant,
                BatchId = Guid.CreateVersion7(fixture.Clock.UtcNow.AddMilliseconds(offset)),
                SourceSystem = source.SourceSystem,
                ConnectionId = source.SourceId,
                Kind = "full",
                RequestedBy = Actor,
                RequestedAt = fixture.Clock.UtcNow.AddMilliseconds(offset),
                StartedAt = fixture.Clock.UtcNow.AddMilliseconds(offset),
                Status = MigrationBatchStatus.Running,
            });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static async Task<MigrationBatchView> RunAsync(MigrationBatchService batches, Guid sourceId)
    {
        var started = await batches.StartAsync(new(sourceId), Actor, default);
        Assert.Equal(MigrationBatchStatus.Queued, started.Status);
        var ran = await batches.RunPendingAsync(default);
        var afterRun = await batches.DetailAsync(started.BatchId, default);
        Assert.True(ran == 1, $"Expected one completed batch; worker returned {ran} and batch status was {afterRun?.Status} ({afterRun?.Failure}).");
        var detail = await batches.DetailAsync(started.BatchId, default);
        Assert.NotNull(detail);
        Assert.Equal(MigrationBatchStatus.Succeeded, detail.Status);
        Assert.Equal(SourceProtocol.Tables.Count, detail.Tables!.Count);
        return detail;
    }

    private static class SourceFixture
    {
        public static async Task RecreateAsync(string database)
        {
            await DropAsync(database);
            var master = MasterConnectionString();
            await using (var connection = new SqlConnection(master))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand($"CREATE DATABASE [{database}];", connection);
                await command.ExecuteNonQueryAsync();
            }
            await using var source = new SqlConnection(ConnectionString(database));
            await source.OpenAsync();
            foreach (var table in SourceProtocol.Tables)
            {
                var columns = string.Join(", ", table.Columns.Select(column =>
                    $"[{Escape(column.SourceName)}] {Declaration(column)} NULL"));
                await using var command = new SqlCommand(
                    $"CREATE TABLE [dbo].[{Escape(table.SourceTable)}] ({columns});", source);
                await command.ExecuteNonQueryAsync();
            }
            foreach (var table in SourceProtocol.Tables)
                await InsertAsync(source, table, DefaultRow(table));
            await InsertAsync<SerialMasterStagingRow>(database,
                Serial("STORAGE-ARRAY-02", 2, "Storage-Mixed-002", "Media retention service"));
            await InsertAsync<SerialMasterStagingRow>(database,
                Serial("NETWORK-SWITCH-03", 3, " switch-003 ", "Next-business-day service"));
            await InsertAsync<SerialQuantityStagingRow>(database, Quantity("STORAGE-ARRAY-02", 2));
            await InsertAsync<SerialQuantityStagingRow>(database, Quantity("NETWORK-SWITCH-03", 3));
        }

        public static Dictionary<string, object?> Serial(string item, int number, string serial, string notes) => new()
        {
            [nameof(SerialMasterStagingRow.ItemCode)] = item,
            [nameof(SerialMasterStagingRow.SystemNumber)] = number,
            [nameof(SerialMasterStagingRow.SerialNumber)] = serial,
            [nameof(SerialMasterStagingRow.ManufacturerSerial)] = $"Mnf-{number}",
            [nameof(SerialMasterStagingRow.LotNumber)] = $"LOT-{number}",
            [nameof(SerialMasterStagingRow.InboundDate)] = new DateTime(2026, 9, 1),
            [nameof(SerialMasterStagingRow.WarrantyStart)] = new DateTime(2026, 9, 1),
            [nameof(SerialMasterStagingRow.WarrantyEnd)] = new DateTime(2029, 9, 1),
            [nameof(SerialMasterStagingRow.Status)] = 0,
            [nameof(SerialMasterStagingRow.Notes)] = notes,
            [nameof(SerialMasterStagingRow.DataSource)] = "N",
        };

        private static Dictionary<string, object?> Quantity(string item, int number) => new()
        {
            [nameof(SerialQuantityStagingRow.ItemCode)] = item,
            [nameof(SerialQuantityStagingRow.SystemNumber)] = number,
            [nameof(SerialQuantityStagingRow.WarehouseCode)] = "MAIN",
            [nameof(SerialQuantityStagingRow.Quantity)] = 1m,
        };

        public static async Task InsertAsync<TRow>(string database, Dictionary<string, object?> values)
            where TRow : StagingRow
        {
            var table = SourceProtocol.Tables.Single(x => x.RowType == typeof(TRow));
            await using var connection = new SqlConnection(ConnectionString(database));
            await connection.OpenAsync();
            await InsertAsync(connection, table, values);
        }

        private static async Task InsertAsync(SqlConnection connection, SourceTableProtocol table,
            IReadOnlyDictionary<string, object?> values)
        {
            var names = table.Columns.Select(x => $"[{Escape(x.SourceName)}]").ToList();
            var parameters = table.Columns.Select((_, index) => $"@p{index}").ToList();
            await using var command = new SqlCommand(
                $"INSERT [dbo].[{Escape(table.SourceTable)}] ({string.Join(',', names)}) VALUES ({string.Join(',', parameters)});",
                connection);
            for (var index = 0; index < table.Columns.Count; index++)
            {
                var column = table.Columns[index];
                command.Parameters.AddWithValue(parameters[index], values.GetValueOrDefault(column.PropertyName)
                    ?? DBNull.Value);
            }
            await command.ExecuteNonQueryAsync();
        }

        public static async Task UpdateAsync<TRow>(string database, IReadOnlyDictionary<string, object?> updates,
            IReadOnlyDictionary<string, object?> keys) where TRow : StagingRow
        {
            var table = SourceProtocol.Tables.Single(x => x.RowType == typeof(TRow));
            await using var connection = new SqlConnection(ConnectionString(database));
            await connection.OpenAsync();
            var updateColumns = updates.Keys.Select((property, index) =>
                $"[{Escape(Column(table, property).SourceName)}] = @u{index}").ToList();
            var keyColumns = keys.Keys.Select((property, index) =>
                $"[{Escape(Column(table, property).SourceName)}] = @k{index}").ToList();
            await using var command = new SqlCommand($"UPDATE [dbo].[{Escape(table.SourceTable)}] SET " +
                string.Join(',', updateColumns) + " WHERE " + string.Join(" AND ", keyColumns) + ";", connection);
            var index = 0;
            foreach (var value in updates.Values) command.Parameters.AddWithValue($"@u{index++}", value ?? DBNull.Value);
            index = 0;
            foreach (var value in keys.Values) command.Parameters.AddWithValue($"@k{index++}", value ?? DBNull.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public static async Task DeleteAsync<TRow>(string database, IReadOnlyDictionary<string, object?> keys)
            where TRow : StagingRow
        {
            var table = SourceProtocol.Tables.Single(x => x.RowType == typeof(TRow));
            await using var connection = new SqlConnection(ConnectionString(database));
            await connection.OpenAsync();
            var keyColumns = keys.Keys.Select((property, index) =>
                $"[{Escape(Column(table, property).SourceName)}] = @k{index}").ToList();
            await using var command = new SqlCommand($"DELETE [dbo].[{Escape(table.SourceTable)}] WHERE " +
                string.Join(" AND ", keyColumns) + ";", connection);
            var index = 0;
            foreach (var value in keys.Values) command.Parameters.AddWithValue($"@k{index++}", value ?? DBNull.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public static async Task DropAsync(string database)
        {
            SqlConnection.ClearAllPools();
            await using var connection = new SqlConnection(MasterConnectionString());
            await connection.OpenAsync();
            await using var command = new SqlCommand($"""
                IF DB_ID(N'{database}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{database}];
                END
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        public static string ConnectionString(string database, string? applicationName = null)
        {
            var builder = new SqlConnectionStringBuilder(IntegrationHostFixture.ConnectionString!)
            {
                InitialCatalog = database,
            };
            if (applicationName is not null) builder.ApplicationName = applicationName;
            return builder.ConnectionString;
        }

        private static string MasterConnectionString()
        {
            var builder = new SqlConnectionStringBuilder(IntegrationHostFixture.ConnectionString!)
            {
                InitialCatalog = "master",
                ApplicationIntent = ApplicationIntent.ReadWrite,
            };
            return builder.ConnectionString;
        }

        private static Dictionary<string, object?> DefaultRow(SourceTableProtocol table)
        {
            var result = new Dictionary<string, object?>();
            foreach (var column in table.Columns)
                result[column.PropertyName] = column.Kind switch
                {
                    RawColumnKind.Text => TextValue(column.PropertyName),
                    RawColumnKind.Int32 => column.PropertyName.Contains("Line", StringComparison.Ordinal) ? 0 : 1,
                    RawColumnKind.Int64 => 2048L,
                    RawColumnKind.Decimal => 1m,
                    RawColumnKind.DateTime => new DateTime(2026, 9, 1, 9, 0, 0),
                    _ => null,
                };
            return result;
        }

        private static string TextValue(string property) => property switch
        {
            nameof(SerialMasterStagingRow.ItemCode) or nameof(SerialQuantityStagingRow.ItemCode) => "SERVER-RACK-01",
            nameof(SerialQuantityStagingRow.WarehouseCode) => "MAIN",
            nameof(SerialMasterStagingRow.SerialNumber) => "  srv-Mixed-001  ",
            nameof(SerialMasterStagingRow.ManufacturerSerial) => "Mnf-1",
            nameof(SerialMasterStagingRow.Notes) => "Next-business-day service",
            nameof(EquipmentCardStagingRow.CustomerCode) or nameof(ServiceContractStagingRow.CustomerCode) => "ASTER-COMPUTE",
            nameof(AttachmentStagingRow.Path) => @"C:\Fictional\Attachments",
            nameof(AttachmentStagingRow.FileName) => "server-warranty",
            nameof(AttachmentStagingRow.FileExtension) => "pdf",
            nameof(MarketingDocumentHeaderStagingRow.SourceDocumentTable) => "delivery",
            _ => "N",
        };

        private static RawColumn Column(SourceTableProtocol table, string property) =>
            table.Columns.Single(x => x.PropertyName == property);
        private static string Declaration(RawColumn column) => column.Kind switch
        {
            RawColumnKind.Int32 => "int",
            RawColumnKind.Int64 => "bigint",
            RawColumnKind.Decimal => "decimal(28,8)",
            RawColumnKind.DateTime => "datetime2(3)",
            _ => $"nvarchar({column.MaxLength!.Value.ToString(CultureInfo.InvariantCulture)})",
        };
        private static string Escape(string value) => value.Replace("]", "]]", StringComparison.Ordinal);
    }
}
