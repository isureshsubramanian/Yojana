using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Reconstruction;
using Yojana.Integration.LegacyMigration.Staging;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class MigrationReconstructionTests(IntegrationHostFixture fixture)
{
    private const string SourceDatabase = "yojana_src_p9t06";
    private const string Actor = "fictional-history-migration-lead";

    [DatabaseFact]
    public async Task Reconstruction_orders_maps_cancels_opens_episodes_and_reuses_the_review_queue()
    {
        await MigrationExtractionTests.SourceFixture.RecreateEmptyAsync(SourceDatabase);
        try
        {
            await SeedAsync();
            var tenant = await fixture.NewTenantAsync("recon");
            var otherTenant = await fixture.NewTenantAsync("reconoth");
            using var scope = fixture.ScopeFor(tenant);
            var batchId = await CanonicaliseAsync(scope);
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var skipped = await db.MigrationUnitCandidates.SingleAsync(row => row.ItemCode == "PC-SKIPPED");
            skipped.Disposition = CandidateDisposition.Review;
            await db.SaveChangesAsync();

            var reconstruction = scope.ServiceProvider.GetRequiredService<ReconstructionService>();
            Assert.Equal(1, await reconstruction.EnqueueEligibleAsync(default));
            var stopwatch = Stopwatch.StartNew();
            Assert.Equal(1, await reconstruction.RunPendingAsync(default));
            stopwatch.Stop();
            var status = await reconstruction.GetAsync(batchId, default);
            Assert.NotNull(status);
            Assert.Equal(ReconstructionRunStatus.Completed, status.Status);
            Assert.Equal(11, status.CandidatesReconstructed);
            Assert.Equal(1, status.CandidatesSkippedByDisposition);
            Assert.Equal(1, status.CancellationPairsRemoved);
            Assert.Contains(status.EventsByMovementKind, row => row.Key == HistoryMovementKind.Received);
            Assert.Contains(status.EventsByMovementKind, row => row.Key == HistoryMovementKind.Produced);
            Assert.Contains(status.EventsByMovementKind, row => row.Key == HistoryMovementKind.Consumed);
            Assert.Contains(status.EventsByMovementKind, row => row.Key == HistoryMovementKind.Shipped);
            Assert.Contains(status.EventsByMovementKind, row => row.Key == HistoryMovementKind.CustomerReturned);
            Assert.Contains(status.EventsByMovementKind, row => row.Key == HistoryMovementKind.VendorReturned);
            Assert.Contains(status.EventsByMovementKind, row => row.Key == HistoryMovementKind.Transferred);
            Assert.Contains(status.EventsByMovementKind, row => row.Key == HistoryMovementKind.IssuedOut);
            Assert.Contains(status.EventsByMovementKind, row => row.Key == HistoryMovementKind.Unknown);
            Assert.Contains(status.EventsByMovementKind, row => row.Key == HistoryMovementKind.OpeningBalance);
            Assert.Contains(status.HistoryDepth, row => row.Key == HistoryDepth.Full);
            Assert.Contains(status.HistoryDepth, row => row.Key == HistoryDepth.Partial);
            Assert.Contains(status.HistoryDepth, row => row.Key == HistoryDepth.OpeningBalanceOnly);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
                $"Tiny reconstruction took {stopwatch.Elapsed}.");

            var cancelled = await CandidateAsync(db, "SERVER-CANCELLED");
            var cancelledHistory = await reconstruction.HistoryAsync(cancelled.CandidateId, null, 100, default);
            Assert.NotNull(cancelledHistory);
            Assert.DoesNotContain(cancelledHistory.Events, row => row.MovementKind == HistoryMovementKind.Shipped);
            Assert.Equal(2, cancelledHistory.LogEntries.Count(row =>
                row.ReasonCode == ReconstructionLogReason.CancelledInSource));

            var credit = await CandidateAsync(db, "STORAGE-CREDIT");
            var creditHistory = await reconstruction.HistoryAsync(credit.CandidateId, null, 100, default);
            Assert.Equal(1, creditHistory!.Events.Count(row =>
                row.MovementKind == HistoryMovementKind.CustomerReturned));
            Assert.Contains(creditHistory.LogEntries, row =>
                row.ReasonCode == ReconstructionLogReason.DuplicateCustomerReturn);

            var resale = await CandidateAsync(db, "PC-RESALE");
            var resaleHistory = await reconstruction.HistoryAsync(resale.CandidateId, null, 100, default);
            Assert.Equal(2, resaleHistory!.OwnershipEpisodes.Count);
            Assert.DoesNotContain((await ReviewAsync(scope, batchId)).Items, row =>
                row.SerialAsStored == "RESALE-1");

            var doubled = await CandidateAsync(db, "PC-DOUBLE-SHIP");
            var doubleHistory = await reconstruction.HistoryAsync(doubled.CandidateId, null, 100, default);
            Assert.Equal(2, doubleHistory!.OwnershipEpisodes.Count);
            var queue = await ReviewAsync(scope, batchId);
            var doubleReview = Assert.Single(queue.Items, row => row.SerialAsStored == "DOUBLE-1");
            Assert.Equal("History needs review", doubleReview.KindLabel);
            Assert.Contains("Shipped twice with no return between", doubleReview.CandidateSummary,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(queue.Items, row => row.SerialAsStored == "ISSUE-1" &&
                row.CandidateSummary.Contains("issued out", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(queue.Items, row => row.SerialAsStored == "UNKNOWN-1" &&
                row.CandidateSummary.Contains("unknown", StringComparison.OrdinalIgnoreCase));

            var opening = await CandidateAsync(db, "SERVER-OPENING");
            var openingHistory = await reconstruction.HistoryAsync(opening.CandidateId, null, 100, default);
            Assert.Equal(HistoryDepth.OpeningBalanceOnly, openingHistory!.HistoryDepth);
            Assert.Equal(new DateTime(2026, 8, 1), Assert.Single(openingHistory.Events).EffectiveDate);
            Assert.Equal(HistoryProvenance.Reconstructed, openingHistory.Events[0].Provenance);

            var ordering = await CandidateAsync(db, "NETWORK-ORDERING");
            var orderingHistory = await reconstruction.HistoryAsync(ordering.CandidateId, null, 1, default);
            Assert.Equal(8101, Assert.Single(orderingHistory!.Events).DocumentNumber);
            Assert.NotNull(orderingHistory.NextCursor);
            var orderingSecond = await reconstruction.HistoryAsync(ordering.CandidateId,
                orderingHistory.NextCursor, 1, default);
            Assert.Equal(8102, Assert.Single(orderingSecond!.Events).DocumentNumber);

            var canonical = scope.ServiceProvider.GetRequiredService<CanonicalisationService>();
            await canonical.DecideAsync(doubleReview.ItemId,
                new(MigrationReviewDecisionKind.AcceptAsRecorded, null, "The source documents were verified."),
                Actor, default);
            var second = await reconstruction.EnqueueAsync(batchId, default);
            Assert.Equal(ReconstructionRunStatus.Queued, second.Status);
            Assert.Equal(1, await reconstruction.RunPendingAsync(default));
            var carriedQueue = await ReviewAsync(scope, batchId);
            Assert.Contains(carriedQueue.Items, row => row.SerialAsStored == "DOUBLE-1" &&
                row.Status == MigrationReviewStatus.CarriedOver);

            using var otherScope = fixture.ScopeFor(otherTenant);
            var otherDb = otherScope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            Assert.Equal(0, await otherDb.MigrationReconstructionRuns.CountAsync());
            Assert.Equal(0, await otherDb.MigrationHistoryEvents.CountAsync());
            Assert.Equal(0, await otherDb.MigrationOwnershipEpisodes.CountAsync());
            Assert.Equal(0, await otherDb.MigrationReconstructionLogs.CountAsync());
        }
        finally
        {
            await MigrationExtractionTests.SourceFixture.DropAsync(SourceDatabase);
        }
    }

    [DatabaseFact]
    public async Task Reconstruction_refuses_missing_incomplete_and_profile_blocked_canonicalisation()
    {
        await MigrationExtractionTests.SourceFixture.RecreateEmptyAsync(SourceDatabase);
        try
        {
            await MigrationExtractionTests.SourceFixture.InsertAsync<SerialMasterStagingRow>(SourceDatabase,
                MigrationExtractionTests.SourceFixture.Serial("SERVER-REFUSAL", 1, "REFUSE-1", "Server"));
            await ItemAsync("SERVER-REFUSAL", "Rack server");
            var tenant = await fixture.NewTenantAsync("recref");
            using var scope = fixture.ScopeFor(tenant);
            var sourceId = await SourceAsync(scope);
            var batches = scope.ServiceProvider.GetRequiredService<MigrationBatchService>();
            var profiles = scope.ServiceProvider.GetRequiredService<MigrationProfileService>();
            var reconstruction = scope.ServiceProvider.GetRequiredService<ReconstructionService>();

            var missingBatch = await batches.StartAsync(new(sourceId), Actor, default);
            Assert.Equal(1, await batches.RunPendingAsync(default));
            Assert.Equal(1, await profiles.RunPendingAsync(default));
            var missing = await Assert.ThrowsAsync<MigrationRuleException>(() =>
                reconstruction.EnqueueAsync(missingBatch.BatchId, default));
            Assert.Equal("migration_reconstruction_canonicalisation_missing", missing.Rule);

            var canonical = scope.ServiceProvider.GetRequiredService<CanonicalisationService>();
            await canonical.EnqueueAsync(missingBatch.BatchId, default);
            var incomplete = await Assert.ThrowsAsync<MigrationRuleException>(() =>
                reconstruction.EnqueueAsync(missingBatch.BatchId, default));
            Assert.Equal("migration_reconstruction_canonicalisation_not_completed", incomplete.Rule);
            Assert.Equal(1, await canonical.RunPendingAsync(default));

            await MigrationExtractionTests.SourceFixture.InsertAsync<SerialQuantityStagingRow>(SourceDatabase,
                MigrationExtractionTests.SourceFixture.Quantity("SERVER-REFUSAL", 1, "QUARANTINE", -1m));
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
            var blockedBatch = await batches.StartAsync(new(sourceId), Actor, default);
            Assert.Equal(1, await batches.RunPendingAsync(default));
            Assert.Equal(1, await profiles.RunPendingAsync(default));
            var blockedProfile = await profiles.GetAsync(blockedBatch.BatchId, default);
            Assert.Equal(MigrationProfileRunStatus.Blocked, blockedProfile!.Status);
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            db.MigrationCanonRuns.Add(new MigrationCanonRun
            {
                TenantId = tenant,
                RunId = Guid.CreateVersion7(fixture.Clock.UtcNow),
                BatchId = blockedBatch.BatchId,
                ProfileRunId = blockedProfile.RunId,
                RequestedAt = fixture.Clock.UtcNow,
                StartedAt = fixture.Clock.UtcNow,
                FinishedAt = fixture.Clock.UtcNow,
                Status = CanonicalisationRunStatus.Completed,
            });
            await db.SaveChangesAsync();
            var blocked = await Assert.ThrowsAsync<MigrationRuleException>(() =>
                reconstruction.EnqueueAsync(blockedBatch.BatchId, default));
            Assert.Equal("migration_reconstruction_profile_blocked", blocked.Rule);
        }
        finally
        {
            await MigrationExtractionTests.SourceFixture.DropAsync(SourceDatabase);
        }
    }

    private static async Task<MigrationUnitCandidate> CandidateAsync(IntegrationDbContext db, string item) =>
        await db.MigrationUnitCandidates.AsNoTracking().SingleAsync(row => row.ItemCode == item &&
            row.Disposition == CandidateDisposition.Active);

    private static async Task<MigrationReviewPage> ReviewAsync(IServiceScope scope, Guid batchId) =>
        await scope.ServiceProvider.GetRequiredService<CanonicalisationService>().ListAsync(batchId,
            MigrationReviewKind.HistoryNeedsReview, null, null, null, 200, default);

    private async Task<Guid> CanonicaliseAsync(IServiceScope scope)
    {
        var sourceId = await SourceAsync(scope);
        var batches = scope.ServiceProvider.GetRequiredService<MigrationBatchService>();
        var batch = await batches.StartAsync(new(sourceId), Actor, default);
        Assert.Equal(1, await batches.RunPendingAsync(default));
        var profiles = scope.ServiceProvider.GetRequiredService<MigrationProfileService>();
        Assert.Equal(1, await profiles.RunPendingAsync(default));
        Assert.False(await profiles.IsLoadBlocked(batch.BatchId, default));
        var canonical = scope.ServiceProvider.GetRequiredService<CanonicalisationService>();
        Assert.Equal(1, await canonical.EnqueueEligibleAsync(default));
        Assert.Equal(1, await canonical.RunPendingAsync(default));
        return batch.BatchId;
    }

    private async Task<Guid> SourceAsync(IServiceScope scope)
    {
        var sources = scope.ServiceProvider.GetRequiredService<MigrationSourceService>();
        var source = await sources.CreateAsync(new("Fictional history source", "history-source",
            MigrationSourceRuntime.SqlServer, MigrationSourceRoute.Separate, SourceDatabase,
            ConnectionString: MigrationExtractionTests.SourceFixture.ConnectionString(SourceDatabase)),
            Actor, default);
        await sources.SetLicenceAsync(source.SourceId,
            new(true, "FICTIONAL-HISTORY-CONTRACT", "Read-only extraction confirmed."), Actor, default);
        return source.SourceId;
    }

    private static async Task SeedAsync()
    {
        var serials = new[]
        {
            ("PC-RESALE", 1, "RESALE-1"),
            ("PC-DOUBLE-SHIP", 2, "DOUBLE-1"),
            ("SERVER-CANCELLED", 3, "CANCEL-1"),
            ("STORAGE-CREDIT", 4, "CREDIT-1"),
            ("SERVER-ISSUE", 5, "ISSUE-1"),
            ("NETWORK-UNKNOWN", 6, "UNKNOWN-1"),
            ("SERVER-OPENING", 7, "OPEN-1"),
            ("SERVER-PRODUCTION", 8, "PRODUCTION-1"),
            ("STORAGE-TRANSFER", 9, "TRANSFER-1"),
            ("SERVER-VENDOR-RETURN", 10, "VRETURN-1"),
            ("NETWORK-ORDERING", 11, "ORDER-1"),
            ("PC-SKIPPED", 12, "SKIP-1"),
        };
        foreach (var (item, number, serial) in serials)
        {
            var row = MigrationExtractionTests.SourceFixture.Serial(item, number, serial,
                "IT hardware history fixture");
            row[nameof(SerialMasterStagingRow.InboundDate)] = item == "SERVER-OPENING"
                ? new DateTime(2026, 8, 1) : new DateTime(2026, 7, 1);
            await MigrationExtractionTests.SourceFixture.InsertAsync<SerialMasterStagingRow>(SourceDatabase, row);
            await ItemAsync(item, item.Replace('-', ' '));
        }

        var log = new LogCounter(100);
        await MovementAsync(log, "PC-RESALE", 1, 20, 1, 7001);
        await MovementAsync(log, "PC-RESALE", 1, 15, -1, 7002, party: "FIC-CUSTOMER-A");
        await MovementAsync(log, "PC-RESALE", 1, 16, 1, 7003, party: "FIC-CUSTOMER-A");
        await MovementAsync(log, "PC-RESALE", 1, 15, -1, 7004, party: "FIC-CUSTOMER-B");

        await MovementAsync(log, "PC-DOUBLE-SHIP", 2, 20, 1, 7101);
        await MovementAsync(log, "PC-DOUBLE-SHIP", 2, 15, -1, 7102, party: "FIC-CUSTOMER-A");
        await MovementAsync(log, "PC-DOUBLE-SHIP", 2, 15, -1, 7103, party: "FIC-CUSTOMER-B");

        await MovementAsync(log, "SERVER-CANCELLED", 3, 20, 1, 7201);
        var deliveryLog = log.Value;
        await MovementAsync(log, "SERVER-CANCELLED", 3, 15, -1, 7202,
            party: "FIC-CUSTOMER-A", documentEntry: 17202, createdTime: 1000);
        await MovementAsync(log, "SERVER-CANCELLED", 3, 15, 1, 7203,
            party: "FIC-CUSTOMER-A", documentEntry: 17203, baseType: 15, baseEntry: 17202,
            createdTime: 1000);
        Assert.True(log.Value > deliveryLog);

        await MovementAsync(log, "STORAGE-CREDIT", 4, 20, 1, 7301);
        await MovementAsync(log, "STORAGE-CREDIT", 4, 15, -1, 7302, party: "FIC-CUSTOMER-A");
        await MovementAsync(log, "STORAGE-CREDIT", 4, 16, 1, 7303, party: "FIC-CUSTOMER-A",
            documentEntry: 17303);
        await MovementAsync(log, "STORAGE-CREDIT", 4, 14, 1, 7304, party: "FIC-CUSTOMER-A",
            baseType: 16, baseEntry: 17303);

        await MovementAsync(log, "SERVER-ISSUE", 5, 20, 1, 7401);
        await MovementAsync(log, "SERVER-ISSUE", 5, 60, -1, 7402);
        await MovementAsync(log, "NETWORK-UNKNOWN", 6, 999, 1, 7501);
        await MovementAsync(log, "SERVER-PRODUCTION", 8, 202, 1, 7601);
        await MovementAsync(log, "SERVER-PRODUCTION", 8, 202, -1, 7602);
        await MovementAsync(log, "STORAGE-TRANSFER", 9, 20, 1, 7701, warehouse: "MAIN");
        await MovementAsync(log, "STORAGE-TRANSFER", 9, 67, -1, 7702,
            documentEntry: 17702, warehouse: "MAIN");
        await MovementAsync(log, "STORAGE-TRANSFER", 9, 67, 1, 7702,
            documentEntry: 17702, warehouse: "FIELD");
        await MovementAsync(log, "SERVER-VENDOR-RETURN", 10, 20, 1, 7801);
        await MovementAsync(log, "SERVER-VENDOR-RETURN", 10, 21, -1, 7802);
        var sameDate = new DateTime(2026, 9, 1);
        await MovementAsync(log, "NETWORK-ORDERING", 11, 999, 1, 8101,
            date: sameDate, createdTime: 930);
        await MovementAsync(log, "NETWORK-ORDERING", 11, 998, -1, 8102,
            date: sameDate, createdTime: 930);
        await MovementAsync(log, "PC-SKIPPED", 12, 20, 1, 8201);
    }

    private static async Task ItemAsync(string item, string name) =>
        await MigrationExtractionTests.SourceFixture.InsertAsync<ItemSerialSettingStagingRow>(SourceDatabase,
            new()
            {
                [nameof(ItemSerialSettingStagingRow.ItemCode)] = item,
                [nameof(ItemSerialSettingStagingRow.ItemName)] = name,
                [nameof(ItemSerialSettingStagingRow.ManagesSerialNumbers)] = "Y",
                [nameof(ItemSerialSettingStagingRow.ManagementMethod)] = "A",
            });

    private static async Task MovementAsync(LogCounter logEntry, string item, int systemNumber, int documentType,
        decimal quantity, int documentNumber, string? party = null, int? documentEntry = null,
        int? baseType = -1, int? baseEntry = 0, string? warehouse = "MAIN", DateTime? date = null,
        int createdTime = 900)
    {
        var current = logEntry.Value++;
        var effective = date ?? new DateTime(2026, 9, 1).AddDays(current - 100);
        await MigrationExtractionTests.SourceFixture.InsertAsync<TransactionLogHeaderStagingRow>(SourceDatabase,
            new()
            {
                [nameof(TransactionLogHeaderStagingRow.LogEntry)] = current,
                [nameof(TransactionLogHeaderStagingRow.ItemCode)] = item,
                [nameof(TransactionLogHeaderStagingRow.DocumentType)] = documentType,
                [nameof(TransactionLogHeaderStagingRow.DocumentEntry)] = documentEntry ?? 10000 + current,
                [nameof(TransactionLogHeaderStagingRow.DocumentLine)] = 0,
                [nameof(TransactionLogHeaderStagingRow.DocumentNumber)] = documentNumber,
                [nameof(TransactionLogHeaderStagingRow.BaseType)] = baseType,
                [nameof(TransactionLogHeaderStagingRow.BaseEntry)] = baseEntry,
                [nameof(TransactionLogHeaderStagingRow.DocumentDate)] = effective,
                [nameof(TransactionLogHeaderStagingRow.CreatedDate)] = effective,
                [nameof(TransactionLogHeaderStagingRow.CreatedTime)] = createdTime,
                [nameof(TransactionLogHeaderStagingRow.StockEffect)] = 1,
                [nameof(TransactionLogHeaderStagingRow.WarehouseCode)] = warehouse,
                [nameof(TransactionLogHeaderStagingRow.PartyCode)] = party,
            });
        await MigrationExtractionTests.SourceFixture.InsertAsync<TransactionLogLineStagingRow>(SourceDatabase,
            new()
            {
                [nameof(TransactionLogLineStagingRow.LogEntry)] = current,
                [nameof(TransactionLogLineStagingRow.ItemCode)] = item,
                [nameof(TransactionLogLineStagingRow.SystemNumber)] = systemNumber,
                [nameof(TransactionLogLineStagingRow.Quantity)] = quantity,
                [nameof(TransactionLogLineStagingRow.SerialEntry)] = systemNumber,
            });
    }

    private sealed class LogCounter(int value)
    {
        public int Value { get; set; } = value;
    }
}
