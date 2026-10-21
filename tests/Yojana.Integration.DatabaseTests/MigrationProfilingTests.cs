using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Staging;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class MigrationProfilingTests(IntegrationHostFixture fixture)
{
    private const string SourceDatabase = "yojana_src_p9t04";
    private const string Actor = "fictional-migration-lead";

    [DatabaseFact]
    public async Task Fixed_pack_profiles_staging_blocks_changes_overrides_and_honours_rls()
    {
        await MigrationExtractionTests.SourceFixture.RecreateEmptyAsync(SourceDatabase);
        try
        {
            await SeedAsync();
            var tenant = await fixture.NewTenantAsync("profile");
            var otherTenant = await fixture.NewTenantAsync("profoth");
            using var scope = fixture.ScopeFor(tenant);
            var sources = scope.ServiceProvider.GetRequiredService<MigrationSourceService>();
            var source = await sources.CreateAsync(new("Fictional incumbent copy", "profile-source",
                MigrationSourceRuntime.SqlServer, MigrationSourceRoute.Separate, SourceDatabase,
                ConnectionString: MigrationExtractionTests.SourceFixture.ConnectionString(SourceDatabase)),
                Actor, default);
            await sources.SetLicenceAsync(source.SourceId,
                new(true, "FICTIONAL-CONTRACT-PROFILE", "Read-only extraction confirmed."), Actor, default);
            var batches = scope.ServiceProvider.GetRequiredService<MigrationBatchService>();
            var profiles = scope.ServiceProvider.GetRequiredService<MigrationProfileService>();

            var firstBatch = await ExtractAsync(batches, source.SourceId);
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            Assert.Equal(MigrationProfileRunStatus.Queued, await db.MigrationProfileRuns
                .Where(row => row.BatchId == firstBatch).Select(row => row.Status).SingleAsync());
            Assert.Equal(1, await profiles.RunPendingAsync(default));
            var first = await profiles.GetAsync(firstBatch, default);
            Assert.NotNull(first);
            Assert.Equal(MigrationProfileRunStatus.Blocked, first.Status);
            Assert.True(await profiles.IsLoadBlocked(firstBatch, default));
            var actualCounts = first.Classes.ToDictionary(row => row.Code, row => row.Count);
            Assert.Equal(ExpectedCounts.Count, actualCounts.Count);
            foreach (var expected in ExpectedCounts) Assert.Equal(expected.Value, actualCounts[expected.Key]);
            Assert.Contains(first.Classes, row => row.Code == MigrationProfileClasses.AmbiguousDates.Code &&
                row.Count == 0 && row.Note!.Contains("No spreadsheet source", StringComparison.Ordinal));
            Assert.Contains(first.Classes, row => row.Code == MigrationProfileClasses.SerialsInUserFields.Code &&
                row.Count is null && row.Note!.StartsWith("Not configured", StringComparison.Ordinal));
            var samples = await profiles.SamplesAsync(firstBatch,
                MigrationProfileClasses.SerialInMultipleWarehouses.Code, default);
            Assert.NotNull(samples);
            Assert.Single(samples.Samples);
            Assert.True(samples.Samples.Count <= 20);

            await MigrationExtractionTests.SourceFixture.DeleteAsync<SerialQuantityStagingRow>(SourceDatabase,
                new Dictionary<string, object?>
                {
                    [nameof(SerialQuantityStagingRow.ItemCode)] = "NETWORK-SWITCH-NEG",
                    [nameof(SerialQuantityStagingRow.SystemNumber)] = 3,
                    [nameof(SerialQuantityStagingRow.WarehouseCode)] = "RETURNS",
                });
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
            var secondBatch = await ExtractAsync(batches, source.SourceId);
            Assert.Equal(1, await profiles.RunPendingAsync(default));
            var second = await profiles.GetAsync(secondBatch, default);
            Assert.NotNull(second);
            var negative = Assert.Single(second.Classes,
                row => row.Code == MigrationProfileClasses.NegativeQuantity.Code);
            Assert.Equal(0, negative.Count);
            Assert.Equal(-1, negative.Change);
            Assert.Equal(secondBatch, await db.Set<SerialQuantityStagingRow>().Where(row =>
                    row.ItemCode == "NETWORK-SWITCH-NEG" && row.SystemNumber == 3)
                .Select(row => row.AbsentSinceBatchId).SingleAsync());

            var outcomeSettings = scope.ServiceProvider.GetRequiredService<MigrationProfileOutcomeSettings>();
            var defaults = await outcomeSettings.GetAsync(default);
            var choices = defaults.Classes.Select(row => row.ClassCode ==
                    MigrationProfileClasses.DuplicateSerialSameItem.Code
                ? row with { Outcome = MigrationProfileOutcome.LoadedWithNote } : row).ToList();
            var saved = await outcomeSettings.SetAsync(new(choices, defaults.Version), default);
            Assert.Contains(saved.Classes, row => row.ClassCode ==
                MigrationProfileClasses.DuplicateSerialSameItem.Code &&
                row.Outcome == MigrationProfileOutcome.LoadedWithNote);
            var lowered = saved.Classes.Select(row => row.ClassCode == MigrationProfileClasses.NegativeQuantity.Code
                ? row with { Outcome = MigrationProfileOutcome.NeedsPerson } : row).ToList();
            var hardStop = await Assert.ThrowsAsync<MigrationRuleException>(() =>
                outcomeSettings.SetAsync(new(lowered, saved.Version), default));
            Assert.Equal("migration_profile_hard_stop_required", hardStop.Rule);

            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
            var rerun = await profiles.EnqueueAsync(secondBatch, default);
            Assert.Equal(MigrationProfileRunStatus.Queued, rerun.Status);
            Assert.Equal(1, await profiles.RunPendingAsync(default));
            var overridden = await profiles.GetAsync(secondBatch, default);
            Assert.NotNull(overridden);
            Assert.Contains(overridden.Classes, row => row.Code ==
                MigrationProfileClasses.DuplicateSerialSameItem.Code &&
                row.Outcome == MigrationProfileOutcome.LoadedWithNote);

            using var otherScope = fixture.ScopeFor(otherTenant);
            var otherDb = otherScope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            Assert.Equal(0, await otherDb.MigrationProfileRuns.CountAsync());
            Assert.Equal(0, await otherDb.MigrationProfileResults.CountAsync());
        }
        finally
        {
            await MigrationExtractionTests.SourceFixture.DropAsync(SourceDatabase);
        }
    }

    [DatabaseFact]
    public async Task Refused_and_failed_extractions_do_not_enqueue_profiles()
    {
        var tenant = await fixture.NewTenantAsync("proffail");
        using var scope = fixture.ScopeFor(tenant);
        var sources = scope.ServiceProvider.GetRequiredService<MigrationSourceService>();
        var batches = scope.ServiceProvider.GetRequiredService<MigrationBatchService>();
        var refusedSource = await sources.CreateAsync(new("Fictional unapproved copy", "profile-refused",
            MigrationSourceRuntime.SqlServer, MigrationSourceRoute.Separate, "fictional_refused",
            ConnectionString: MigrationExtractionTests.SourceFixture.ConnectionString("fictional_refused")),
            Actor, default);
        var refused = await batches.StartAsync(new(refusedSource.SourceId), Actor, default);
        Assert.Equal(MigrationBatchStatus.Refused, refused.Status);

        var failedSource = await sources.CreateAsync(new("Fictional unavailable copy", "profile-failed",
            MigrationSourceRuntime.SqlServer, MigrationSourceRoute.Separate, "fictional_unavailable",
            ConnectionString: MigrationExtractionTests.SourceFixture.ConnectionString("fictional_unavailable")),
            Actor, default);
        await sources.SetLicenceAsync(failedSource.SourceId,
            new(true, "FICTIONAL-CONTRACT-FAILED", "Read-only extraction confirmed."), Actor, default);
        var failed = await batches.StartAsync(new(failedSource.SourceId), Actor, default);
        Assert.Equal(MigrationBatchStatus.Queued, failed.Status);
        Assert.Equal(0, await batches.RunPendingAsync(default));
        var failedDetail = await batches.DetailAsync(failed.BatchId, default);
        Assert.NotNull(failedDetail);
        Assert.Equal(MigrationBatchStatus.Failed, failedDetail.Status);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.False(await db.MigrationProfileRuns.AnyAsync(row =>
            row.BatchId == refused.BatchId || row.BatchId == failed.BatchId));
    }

    private static Dictionary<string, long?> ExpectedCounts { get; } =
        new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            [MigrationProfileClasses.SerialInMultipleWarehouses.Code] = 1,
            [MigrationProfileClasses.NegativeQuantity.Code] = 1,
            [MigrationProfileClasses.DuplicateSerialSameItem.Code] = 1,
            [MigrationProfileClasses.DuplicateSerialDifferentItems.Code] = 1,
            [MigrationProfileClasses.SerialWhitespaceOrCase.Code] = 1,
            [MigrationProfileClasses.SerialFormatVariants.Code] = 1,
            [MigrationProfileClasses.PlaceholderSerial.Code] = 1,
            [MigrationProfileClasses.EquipmentCardWithoutSerial.Code] = 1,
            [MigrationProfileClasses.ShippedSerialWithoutCard.Code] = 1,
            [MigrationProfileClasses.CardCustomerDeliveryMismatch.Code] = 1,
            [MigrationProfileClasses.ActiveContractPastEnd.Code] = 1,
            [MigrationProfileClasses.AmbiguousDates.Code] = 0,
            [MigrationProfileClasses.SerialsInUserFields.Code] = null,
            [MigrationProfileClasses.EmptyStatus.Code] = 1,
        };

    private static async Task<Guid> ExtractAsync(MigrationBatchService batches, Guid sourceId)
    {
        var started = await batches.StartAsync(new(sourceId), Actor, default);
        Assert.Equal(MigrationBatchStatus.Queued, started.Status);
        Assert.Equal(1, await batches.RunPendingAsync(default));
        return started.BatchId;
    }

    private static async Task SeedAsync()
    {
        static Dictionary<string, object?> Serial(string item, int number, string? serial, string? status = "0")
        {
            var row = MigrationExtractionTests.SourceFixture.Serial(item, number, serial ?? "", "IT hardware");
            row[nameof(SerialMasterStagingRow.SerialNumber)] = serial;
            row[nameof(SerialMasterStagingRow.Status)] = status;
            return row;
        }

        foreach (var row in new[]
        {
            Serial("SERVER-RACK-A", 1, "MW-1"),
            Serial("SERVER-RACK-A", 2, "MW-NEAR"),
            Serial("PC-DESKTOP-A", 10, " DUP-1 "),
            Serial("PC-DESKTOP-A", 11, "DUP-1"),
            Serial("NETWORK-SWITCH-A", 12, "CROSS-1"),
            Serial("STORAGE-ARRAY-A", 13, "CROSS-1"),
            Serial("SERVER-RACK-B", 20, "000-ABC"),
            Serial("SERVER-STORAGE-B", 21, "ABC"),
            Serial("NETWORK-SWITCH-B", 22, "000-ZZZ"),
            Serial("STORAGE-ARRAY-B", 23, "ZZZ"),
            Serial("PC-SYSTEM", 30, null),
            Serial("PC-MANUAL", 31, null),
            Serial("SHIP-SERVER", 40, "SHIP-1"),
            Serial("SHIP-SERVER", 41, "SHIP-2"),
            Serial("MATCH-SERVER", 50, "MATCH-1"),
            Serial("LATEST-SERVER", 51, "LATEST-1"),
            Serial("STATUS-NETWORK", 60, "STATUS-1", null),
            Serial("CANCEL-SERVER", 70, "CANCEL-1"),
        }) await MigrationExtractionTests.SourceFixture.InsertAsync<SerialMasterStagingRow>(SourceDatabase, row);

        foreach (var row in new[]
        {
            MigrationExtractionTests.SourceFixture.Quantity("SERVER-RACK-A", 1, "PUNE", 1m),
            MigrationExtractionTests.SourceFixture.Quantity("SERVER-RACK-A", 1, "CHENNAI", 1m),
            MigrationExtractionTests.SourceFixture.Quantity("SERVER-RACK-A", 2, "PUNE", .5m),
            MigrationExtractionTests.SourceFixture.Quantity("SERVER-RACK-A", 2, "CHENNAI", .5m),
            MigrationExtractionTests.SourceFixture.Quantity("NETWORK-SWITCH-NEG", 3, "RETURNS", -1m),
            MigrationExtractionTests.SourceFixture.Quantity("NETWORK-SWITCH-CLEAN", 4, "MAIN", 0m),
        }) await MigrationExtractionTests.SourceFixture.InsertAsync<SerialQuantityStagingRow>(SourceDatabase, row);

        await MigrationExtractionTests.SourceFixture.InsertAsync<ItemSerialSettingStagingRow>(SourceDatabase, new()
        {
            [nameof(ItemSerialSettingStagingRow.ItemCode)] = "PC-SYSTEM",
            [nameof(ItemSerialSettingStagingRow.ManagementMethod)] = "R",
        });
        await MigrationExtractionTests.SourceFixture.InsertAsync<ItemSerialSettingStagingRow>(SourceDatabase, new()
        {
            [nameof(ItemSerialSettingStagingRow.ItemCode)] = "PC-MANUAL",
            [nameof(ItemSerialSettingStagingRow.ManagementMethod)] = "A",
        });

        foreach (var row in new[]
        {
            Card(1, "ORPHAN-PC", "CARD-404", "CUSTOMER-A"),
            Card(2, "SERVER-RACK-A", "MW-1", "CUSTOMER-A"),
            Card(3, "SHIP-SERVER", "SHIP-2", "CUSTOMER-B"),
            Card(4, "MATCH-SERVER", "MATCH-1", "CUSTOMER-OLD"),
            Card(5, "LATEST-SERVER", "LATEST-1", "CUSTOMER-LATEST"),
        }) await MigrationExtractionTests.SourceFixture.InsertAsync<EquipmentCardStagingRow>(SourceDatabase, row);

        foreach (var row in new[]
        {
            Header(100, 15, 1000, "CUSTOMER-A"),
            Header(101, 13, 1001, "CUSTOMER-B"),
            Header(102, 15, 1002, "CUSTOMER-NEW"),
            Header(103, 15, 1003, "CUSTOMER-OLDER", new DateTime(2026, 8, 1)),
            Header(104, 15, 1004, "CUSTOMER-LATEST", new DateTime(2026, 9, 1)),
            // Cancelled deliveries: the cancellation is a second row of the same type, opposite sign, based on
            // the original. Neither serial is shipped, and neither delivery is the latest one.
            Header(105, 15, 1005, "CUSTOMER-X", new DateTime(2026, 9, 10), documentEntry: 1005),
            Header(106, 15, 1006, "CUSTOMER-X", new DateTime(2026, 9, 10), documentEntry: 1006,
                baseType: 15, baseEntry: 1005),
            Header(107, 15, 1007, "CUSTOMER-WRONG", new DateTime(2026, 9, 15), documentEntry: 1007),
            Header(108, 15, 1008, "CUSTOMER-WRONG", new DateTime(2026, 9, 15), documentEntry: 1008,
                baseType: 15, baseEntry: 1007),
        }) await MigrationExtractionTests.SourceFixture.InsertAsync<TransactionLogHeaderStagingRow>(SourceDatabase, row);
        foreach (var row in new[]
        {
            Line(100, "SHIP-SERVER", 40),
            Line(101, "SHIP-SERVER", 41),
            Line(102, "MATCH-SERVER", 50),
            Line(103, "LATEST-SERVER", 51),
            Line(104, "LATEST-SERVER", 51),
            Line(105, "CANCEL-SERVER", 70),
            Line(106, "CANCEL-SERVER", 70, 1m),
            Line(107, "LATEST-SERVER", 51),
            Line(108, "LATEST-SERVER", 51, 1m),
        }) await MigrationExtractionTests.SourceFixture.InsertAsync<TransactionLogLineStagingRow>(SourceDatabase, row);

        await MigrationExtractionTests.SourceFixture.InsertAsync<ServiceContractStagingRow>(SourceDatabase,
            Contract(1, "A", new DateTime(2026, 8, 1), null));
        await MigrationExtractionTests.SourceFixture.InsertAsync<ServiceContractStagingRow>(SourceDatabase,
            Contract(2, "A", new DateTime(2027, 8, 1), null));
        await MigrationExtractionTests.SourceFixture.InsertAsync<ServiceContractStagingRow>(SourceDatabase,
            Contract(3, "A", new DateTime(2026, 7, 1), new DateTime(2026, 6, 1)));
    }

    private static Dictionary<string, object?> Card(int id, string item, string serial, string customer) => new()
    {
        [nameof(EquipmentCardStagingRow.CardId)] = id,
        [nameof(EquipmentCardStagingRow.ItemCode)] = item,
        [nameof(EquipmentCardStagingRow.InternalSerial)] = serial,
        [nameof(EquipmentCardStagingRow.CustomerCode)] = customer,
    };

    private static Dictionary<string, object?> Header(int logEntry, int documentType, int documentNumber,
        string party, DateTime? documentDate = null, int? documentEntry = null, int? baseType = null,
        int? baseEntry = null) => new()
    {
        [nameof(TransactionLogHeaderStagingRow.LogEntry)] = logEntry,
        [nameof(TransactionLogHeaderStagingRow.DocumentType)] = documentType,
        [nameof(TransactionLogHeaderStagingRow.DocumentEntry)] = documentEntry,
        [nameof(TransactionLogHeaderStagingRow.BaseType)] = baseType,
        [nameof(TransactionLogHeaderStagingRow.BaseEntry)] = baseEntry,
        [nameof(TransactionLogHeaderStagingRow.DocumentNumber)] = documentNumber,
        [nameof(TransactionLogHeaderStagingRow.DocumentDate)] = documentDate ?? new DateTime(2026, 9, 1),
        [nameof(TransactionLogHeaderStagingRow.CreatedDate)] = documentDate ?? new DateTime(2026, 9, 1),
        [nameof(TransactionLogHeaderStagingRow.CreatedTime)] = 900,
        [nameof(TransactionLogHeaderStagingRow.PartyCode)] = party,
    };

    private static Dictionary<string, object?> Line(int logEntry, string item, int systemNumber,
        decimal quantity = -1m) => new()
    {
        [nameof(TransactionLogLineStagingRow.LogEntry)] = logEntry,
        [nameof(TransactionLogLineStagingRow.ItemCode)] = item,
        [nameof(TransactionLogLineStagingRow.SystemNumber)] = systemNumber,
        [nameof(TransactionLogLineStagingRow.Quantity)] = quantity,
    };

    private static Dictionary<string, object?> Contract(int id, string status, DateTime end,
        DateTime? terminated) => new()
    {
        [nameof(ServiceContractStagingRow.ContractId)] = id,
        [nameof(ServiceContractStagingRow.CustomerCode)] = "CUSTOMER-A",
        [nameof(ServiceContractStagingRow.StartDate)] = new DateTime(2025, 8, 1),
        [nameof(ServiceContractStagingRow.EndDate)] = end,
        [nameof(ServiceContractStagingRow.TerminationDate)] = terminated,
        [nameof(ServiceContractStagingRow.Status)] = status,
        [nameof(ServiceContractStagingRow.DocumentNumber)] = 2000 + id,
    };
}
