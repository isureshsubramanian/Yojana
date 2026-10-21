using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Staging;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class MigrationCanonicalisationTests(IntegrationHostFixture fixture)
{
    private const string SourceDatabase = "yojana_src_p9t13gaps";
    private const string Actor = "fictional-migration-lead";

    [DatabaseFact]
    public async Task Canonicalisation_applies_rules_builds_review_queue_and_rebinds_automatic_merge()
    {
        await MigrationExtractionTests.SourceFixture.RecreateEmptyAsync(SourceDatabase);
        try
        {
            await SeedAsync();
            var tenant = await fixture.NewTenantAsync("canon");
            var otherTenant = await fixture.NewTenantAsync("canonoth");
            using var scope = fixture.ScopeFor(tenant);
            var sourceId = await SourceAsync(scope);
            var batchId = await ProfileAsync(scope, sourceId);
            var canonical = scope.ServiceProvider.GetRequiredService<CanonicalisationService>();
            Assert.Equal(1, await canonical.EnqueueEligibleAsync(default));

            var started = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(1, await canonical.RunPendingAsync(default));
            started.Stop();
            var status = await canonical.GetAsync(batchId, default);
            Assert.NotNull(status);
            var page = await canonical.ListAsync(batchId, null, null, null, null, 25, default);
            Assert.Equal(CanonicalisationRunStatus.Completed, status.Status);
            Assert.Equal(10, status.CandidateRowsTotal);
            Assert.True(status.CandidateRowsExact >= 5);
            Assert.Equal(2, status.CandidateRowsTrimmedUpper);
            Assert.Equal(2, status.CandidateRowsFamilyNormalised);
            Assert.True(status.WaitingReviewItems == 4,
                $"Unexpected review rows: {string.Join(", ", page.Items.Select(row => $"{row.Kind}/{row.RuleTried}/{row.SerialAsStored}"))}");
            Assert.Equal(1, status.CandidateRowsMerged);
            Assert.Equal(2, status.WaitingSameItemReviewItems);
            Assert.Equal(1, status.WaitingAcrossItemsReviewItems);
            Assert.Equal(1, status.WaitingReviewItemsByKind.Single(row =>
                row.Key == MigrationReviewKind.OrphanEquipmentCard).Count);
            Assert.Equal(0, status.WaitingReviewItemsByKind.Single(row =>
                row.Key == MigrationReviewKind.HistoryNeedsReview).Count);
            Assert.Equal(0, status.DecidedReviewItems);
            Assert.Equal(0, status.UnverifiedDates);
            Assert.Contains(status.Gaps, gap => gap.Kind == MigrationReviewKind.SerialInUserField &&
                gap.Reason.StartsWith("Not configured", StringComparison.Ordinal));
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(30),
                $"Tiny canonicalisation took {started.Elapsed}.");

            Assert.Equal(4, page.Items.Count);
            var trimmed = Assert.Single(page.Items, row => row.RuleTried == CanonicalMatchRule.TrimmedUpper);
            Assert.Equal(MigrationReviewDecisionKind.MergeInto, trimmed.Suggestion);
            Assert.Equal(trimmed.Suggestion is null ? null : "A", trimmed.SuggestedKeepLabel);
            Assert.NotNull(trimmed.SuggestedKeepSourceKey);
            Assert.NotNull(trimmed.SuggestionReason);
            var family = Assert.Single(page.Items, row => row.RuleTried == CanonicalMatchRule.FamilyNormalised);
            Assert.Null(family.Suggestion);
            Assert.Contains("tied", family.SuggestionReason!, StringComparison.OrdinalIgnoreCase);
            var cross = Assert.Single(page.Items, row => row.Kind == MigrationReviewKind.SameSerialDifferentItems);
            Assert.Equal(MigrationReviewDecisionKind.KeepApart, cross.Suggestion);
            Assert.Null(cross.SuggestedKeepSourceKey);
            Assert.Null(cross.SuggestedKeepLabel);
            var orphan = Assert.Single(page.Items, row => row.Kind == MigrationReviewKind.OrphanEquipmentCard);
            Assert.Null(orphan.Suggestion);
            Assert.Contains("one edit", orphan.Hint!, StringComparison.Ordinal);

            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var orphanDetail = await canonical.DetailAsync(orphan.ItemId, default);
            Assert.NotNull(orphanDetail);
            var hintedSource = Assert.Single(orphanDetail.Candidates).SourceKey;
            var missingFixNote = await Assert.ThrowsAsync<MigrationRuleException>(() => canonical.DecideAsync(
                orphan.ItemId, new(MigrationReviewDecisionKind.FixInIncumbent, null,
                    "The source owner will correct this card."), Actor, default));
            Assert.Equal("migration_review_note_required", missingFixNote.Rule);
            await canonical.DecideAsync(orphan.ItemId, new(MigrationReviewDecisionKind.FixInIncumbent, null,
                "The source owner will correct this card.", "Correct the card serial before the next rehearsal."),
                Actor, default);
            Assert.Equal(CandidateDisposition.Active,
                (await db.MigrationUnitCandidates.SingleAsync(row => row.RunId == status.RunId &&
                    row.PrimarySourceKey == hintedSource)).Disposition);
            Assert.DoesNotContain(await db.MigrationUnitCandidateSources.Where(row => row.RunId == status.RunId)
                .ToListAsync(), row => row.MatchedSerialSourceKey == hintedSource &&
                    row.SourceKind == CandidateSourceKind.EquipmentCard);
            var exactRows = await db.MigrationUnitCandidates.Where(row => row.RunId == status.RunId &&
                    row.ItemCode == "SERVER-RACK-EXACT")
                .OrderBy(row => row.PrimarySourceKey).ToListAsync();
            Assert.Equal(2, exactRows.Count);
            var kept = Assert.Single(exactRows, row => row.Disposition == CandidateDisposition.Active);
            Assert.Single(exactRows, row => row.Disposition == CandidateDisposition.Merged);
            var exactEvents = await db.MigrationEventCandidates.Where(row => row.RunId == status.RunId &&
                (row.SerialMasterSourceKey == exactRows[0].PrimarySourceKey ||
                 row.SerialMasterSourceKey == exactRows[1].PrimarySourceKey)).ToListAsync();
            Assert.Equal(3, exactEvents.Count);
            Assert.All(exactEvents, row => Assert.Equal(kept.CandidateId, row.CandidateId));
            var sourceLinks = await db.MigrationUnitCandidateSources.Where(row => row.RunId == status.RunId &&
                row.SourceKind == CandidateSourceKind.SerialMaster &&
                (row.SourceKey == exactRows[0].PrimarySourceKey || row.SourceKey == exactRows[1].PrimarySourceKey))
                .ToListAsync();
            Assert.All(sourceLinks, row => Assert.Equal(kept.CandidateId, row.CandidateId));
            Assert.Contains(sourceLinks, row => row.Role == CandidateSourceRole.Merged);
            var placeholder = await db.MigrationUnitCandidates.SingleAsync(row => row.RunId == status.RunId &&
                row.ItemCode == "PC-SYSTEM");
            Assert.Equal(CanonicalMatchRule.Exact, placeholder.MatchRule);
            Assert.Null(placeholder.StoredSerial);
            Assert.StartsWith("SYSTEM-", placeholder.CanonicalSerial, StringComparison.Ordinal);
            Assert.NotNull(placeholder.Note);

            var crossDetail = await canonical.DetailAsync(cross.ItemId, default);
            Assert.NotNull(crossDetail);
            var refused = await Assert.ThrowsAsync<MigrationRuleException>(() => canonical.DecideAsync(cross.ItemId,
                new(MigrationReviewDecisionKind.MergeInto, JsonKeys(crossDetail)[0],
                    "These must not merge."), Actor, default));
            Assert.Equal("migration_cross_item_merge_forbidden", refused.Rule);

            var trimmedDetail = await canonical.DetailAsync(trimmed.ItemId, default);
            Assert.NotNull(trimmedDetail);
            Assert.Equal(2, trimmedDetail.Candidates.Count);
            var keepSource = trimmedDetail.SuggestedKeepSourceKey!;
            var suggestedCandidate = Assert.Single(trimmedDetail.Candidates,
                candidate => candidate.SourceKey == keepSource);
            Assert.Equal("51320", suggestedCandidate.CardNumber);
            Assert.Equal("82001", suggestedCandidate.ContractNumber);
            Assert.Equal(7110, suggestedCandidate.ReceiptDocumentNumber);
            Assert.Equal(0, suggestedCandidate.ReceiptDocumentLine);
            Assert.Contains(trimmedDetail.Candidates, candidate => candidate.CardNumber is null &&
                candidate.ContractNumber is null && candidate.ReceiptDocumentNumber == 7111);
            await canonical.DecideAsync(trimmed.ItemId,
                new(MigrationReviewDecisionKind.MergeInto, keepSource, "The longer source history is authoritative."),
                Actor, default);
            await canonical.DecideAsync(trimmed.ItemId,
                new(MigrationReviewDecisionKind.KeepApart, null, "A later source check proved these are separate units."),
                Actor, default);
            Assert.Equal(2, await db.MigrationReviewDecisions.CountAsync(row => row.ReviewItemId == trimmed.ItemId));
            Assert.Equal(2, await db.MigrationUnitCandidates.CountAsync(row => row.RunId == status.RunId &&
                row.ItemCode == "SERVER-TRIM" && row.Disposition == CandidateDisposition.Active));

            var afterDecisions = await canonical.GetAsync(batchId, default);
            Assert.Equal(2, afterDecisions!.DecidedReviewItems);
            Assert.Equal(1, afterDecisions.DecidedReviewItemsByDecision.Single(row =>
                row.Key == MigrationReviewDecisionKind.KeepApart).Count);
            Assert.Equal(1, afterDecisions.DecidedReviewItemsByDecision.Single(row =>
                row.Key == MigrationReviewDecisionKind.FixInIncumbent).Count);
            Assert.Equal(0, afterDecisions.DecidedReviewItemsByDecision.Single(row =>
                row.Key == MigrationReviewDecisionKind.MergeInto).Count);

            Assert.Single((await canonical.ListAsync(batchId, null, null, null,
                "trim-1", null, 25, default)).Items);
            Assert.Single((await canonical.ListAsync(batchId, null, null, null,
                "SERVER-TRIM", null, 25, default)).Items);
            Assert.Empty((await canonical.ListAsync(batchId, null, null, null,
                "TRIM-9", null, 25, default)).Items);
            var exported = await ExportAsync(canonical, batchId, "trim-1");
            Assert.Single(exported);
            Assert.Equal("SERVER-TRIM", exported[0][1]);
            Assert.Empty(await ExportAsync(canonical, batchId, "TRIM-9"));
            Assert.False(await canonical.ExportTooLargeAsync(batchId, null, null, null, null, default));

            var catalogue = await canonical.FamilyCatalogueAsync(default);
            Assert.Contains(catalogue.Families, family => family.Code == "SERVER" && family.Label == "Server");
            Assert.Contains(catalogue.Families, family => family.Code == "STORAGE");

            using var otherScope = fixture.ScopeFor(otherTenant);
            var otherCanonical = otherScope.ServiceProvider.GetRequiredService<CanonicalisationService>();
            Assert.Empty((await otherCanonical.FamilyCatalogueAsync(default)).Families);
            Assert.Empty((await otherCanonical.ListAsync(batchId, null, null, null,
                "trim-1", null, 25, default)).Items);
            Assert.Empty(await ExportAsync(otherCanonical, batchId, "trim-1"));
            var otherDb = otherScope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            Assert.Equal(0, await otherDb.MigrationCanonRuns.CountAsync());
            Assert.Equal(0, await otherDb.MigrationReviewItems.CountAsync());
        }
        finally
        {
            await MigrationExtractionTests.SourceFixture.DropAsync(SourceDatabase);
        }
    }

    [DatabaseFact]
    public async Task Decisions_carry_to_unchanged_batches_and_changed_fingerprints_return_to_queue()
    {
        await MigrationExtractionTests.SourceFixture.RecreateEmptyAsync(SourceDatabase);
        try
        {
            await SeedAsync();
            var tenant = await fixture.NewTenantAsync("carry");
            using var scope = fixture.ScopeFor(tenant);
            var sourceId = await SourceAsync(scope);
            var canonical = scope.ServiceProvider.GetRequiredService<CanonicalisationService>();
            var firstBatch = await ProfileAsync(scope, sourceId);
            await canonical.EnqueueEligibleAsync(default);
            await canonical.RunPendingAsync(default);
            var firstPage = await canonical.ListAsync(firstBatch, MigrationReviewKind.SameSerialOneItem,
                null, MigrationReviewStatus.Waiting, null, 25, default);
            var first = Assert.Single(firstPage.Items, row => row.RuleTried == CanonicalMatchRule.TrimmedUpper);
            await canonical.DecideAsync(first.ItemId,
                new(MigrationReviewDecisionKind.KeepApart, null, "The source owner confirmed two physical units."),
                Actor, default);

            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
            var secondBatch = await ProfileAsync(scope, sourceId);
            Assert.Equal(1, await canonical.EnqueueEligibleAsync(default));
            Assert.Equal(1, await canonical.RunPendingAsync(default));
            var secondPage = await canonical.ListAsync(secondBatch, MigrationReviewKind.SameSerialOneItem,
                null, null, null, 25, default);
            var carried = Assert.Single(secondPage.Items, row => row.RuleTried == CanonicalMatchRule.TrimmedUpper);
            Assert.Equal(MigrationReviewStatus.CarriedOver, carried.Status);
            Assert.True(carried.CarriedOver);
            var secondStatus = await canonical.GetAsync(secondBatch, default);
            Assert.Equal(1, secondStatus!.CarriedOverReviewItems);
            Assert.Equal(0, secondStatus.DecidedReviewItemsByDecision.Single(row =>
                row.Key == MigrationReviewDecisionKind.KeepApart).Count);

            // Same rows, new lineage: one candidate now has an equipment card, so the decision is not replayed.
            await MigrationExtractionTests.SourceFixture.InsertAsync<EquipmentCardStagingRow>(SourceDatabase, new()
            {
                [nameof(EquipmentCardStagingRow.CardId)] = 51300,
                [nameof(EquipmentCardStagingRow.ItemCode)] = "SERVER-TRIM",
                [nameof(EquipmentCardStagingRow.InternalSerial)] = "TRIM-1",
                [nameof(EquipmentCardStagingRow.CustomerCode)] = "FIC-CUSTOMER-B",
            });
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
            var lineageBatch = await ProfileAsync(scope, sourceId);
            await canonical.EnqueueEligibleAsync(default);
            await canonical.RunPendingAsync(default);
            var lineagePage = await canonical.ListAsync(lineageBatch, MigrationReviewKind.SameSerialOneItem,
                null, null, null, 25, default);
            var requeued = Assert.Single(lineagePage.Items, row => row.RuleTried == CanonicalMatchRule.TrimmedUpper);
            Assert.Equal(MigrationReviewStatus.Waiting, requeued.Status);
            var requeuedDetail = await canonical.DetailAsync(requeued.ItemId, default);
            Assert.Contains(requeuedDetail!.EarlierDecisions, decision =>
                decision.Decision == MigrationReviewDecisionKind.KeepApart && !decision.AppliedToThisBatch);

            await MigrationExtractionTests.SourceFixture.InsertAsync<SerialMasterStagingRow>(SourceDatabase,
                Serial("SERVER-TRIM", 22, "trim-1"));
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
            var changedBatch = await ProfileAsync(scope, sourceId);
            await canonical.EnqueueEligibleAsync(default);
            await canonical.RunPendingAsync(default);
            var changedPage = await canonical.ListAsync(changedBatch, MigrationReviewKind.SameSerialOneItem,
                null, null, null, 25, default);
            var changed = Assert.Single(changedPage.Items, row => row.RuleTried == CanonicalMatchRule.TrimmedUpper);
            Assert.Equal(MigrationReviewStatus.Waiting, changed.Status);
            var changedDetail = await canonical.DetailAsync(changed.ItemId, default);
            Assert.NotNull(changedDetail);
            Assert.Equal(3, changedDetail.Candidates.Count);
            Assert.Contains(changedDetail.EarlierDecisions, decision =>
                decision.Decision == MigrationReviewDecisionKind.KeepApart && !decision.AppliedToThisBatch);

            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var unprofiled = new MigrationBatch
            {
                TenantId = tenant,
                BatchId = Guid.CreateVersion7(fixture.Clock.UtcNow.AddSeconds(1)),
                ConnectionId = sourceId,
                SourceSystem = "canonical-source",
                Kind = "full",
                RequestedBy = Actor,
                RequestedAt = fixture.Clock.UtcNow.AddSeconds(1),
                FinishedAt = fixture.Clock.UtcNow.AddSeconds(1),
                Status = MigrationBatchStatus.Succeeded,
            };
            db.MigrationBatches.Add(unprofiled);
            await db.SaveChangesAsync();
            var missing = await Assert.ThrowsAsync<MigrationRuleException>(() =>
                canonical.EnqueueAsync(unprofiled.BatchId, default));
            Assert.Equal("migration_canonicalisation_profile_missing", missing.Rule);

            await MigrationExtractionTests.SourceFixture.InsertAsync<SerialQuantityStagingRow>(SourceDatabase,
                MigrationExtractionTests.SourceFixture.Quantity("SERVER-TRIM", 20, "QUARANTINE", -1m));
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
            var blockedBatch = await ExtractAndProfileAsync(scope, sourceId, expectedProfileStatus: MigrationProfileRunStatus.Blocked);
            var blocked = await Assert.ThrowsAsync<MigrationRuleException>(() =>
                canonical.EnqueueAsync(blockedBatch, default));
            Assert.Equal("migration_canonicalisation_profile_blocked", blocked.Rule);
        }
        finally
        {
            await MigrationExtractionTests.SourceFixture.DropAsync(SourceDatabase);
        }
    }

    private static List<string> JsonKeys(MigrationReviewDetail detail) =>
        detail.Candidates.Select(row => row.SourceKey).ToList();

    private static async Task<List<IReadOnlyList<string?>>> ExportAsync(CanonicalisationService service,
        Guid batchId, string find)
    {
        var rows = new List<IReadOnlyList<string?>>();
        await foreach (var row in service.ExportAsync(batchId, null, null, null, find, default)) rows.Add(row);
        return rows;
    }

    private async Task<Guid> SourceAsync(IServiceScope scope)
    {
        var sources = scope.ServiceProvider.GetRequiredService<MigrationSourceService>();
        var source = await sources.CreateAsync(new("Fictional incumbent copy", "canonical-source",
            MigrationSourceRuntime.SqlServer, MigrationSourceRoute.Separate, SourceDatabase,
            ConnectionString: MigrationExtractionTests.SourceFixture.ConnectionString(SourceDatabase)),
            Actor, default);
        await sources.SetLicenceAsync(source.SourceId,
            new(true, "FICTIONAL-CONTRACT-CANONICAL", "Read-only extraction confirmed."), Actor, default);
        return source.SourceId;
    }

    private async Task<Guid> ProfileAsync(IServiceScope scope, Guid sourceId) =>
        await ExtractAndProfileAsync(scope, sourceId, MigrationProfileRunStatus.Completed);

    private async Task<Guid> ExtractAndProfileAsync(IServiceScope scope, Guid sourceId,
        string expectedProfileStatus)
    {
        var batches = scope.ServiceProvider.GetRequiredService<MigrationBatchService>();
        var started = await batches.StartAsync(new(sourceId), Actor, default);
        Assert.Equal(MigrationBatchStatus.Queued, started.Status);
        Assert.Equal(1, await batches.RunPendingAsync(default));
        var profiles = scope.ServiceProvider.GetRequiredService<MigrationProfileService>();
        Assert.Equal(1, await profiles.RunPendingAsync(default));
        Assert.Equal(expectedProfileStatus, (await profiles.GetAsync(started.BatchId, default))!.Status);
        return started.BatchId;
    }

    private static async Task SeedAsync()
    {
        foreach (var row in new[]
        {
            Serial("SERVER-RACK-EXACT", 1, "EXACT-1"),
            Serial("SERVER-RACK-EXACT", 2, "EXACT-1"),
            Serial("SERVER-TRIM", 20, " trim-1 "),
            Serial("SERVER-TRIM", 21, "TRIM-1"),
            Serial("STORAGE-FAMILY", 30, "000-ST-1"),
            Serial("STORAGE-FAMILY", 31, "ST1"),
            Serial("NETWORK-SWITCH", 40, "CROSS-1"),
            Serial("STORAGE-SHELF", 41, "CROSS-1"),
            Serial("PC-SYSTEM", 50, null),
            Serial("PC-7", 60, "PC7-2402-1198"),
        }) await MigrationExtractionTests.SourceFixture.InsertAsync<SerialMasterStagingRow>(SourceDatabase, row);

        foreach (var item in new[] { "SERVER-RACK-EXACT", "SERVER-TRIM", "STORAGE-FAMILY", "NETWORK-SWITCH",
                     "STORAGE-SHELF", "PC-SYSTEM", "PC-7" })
            await MigrationExtractionTests.SourceFixture.InsertAsync<ItemSerialSettingStagingRow>(SourceDatabase,
                new()
                {
                    [nameof(ItemSerialSettingStagingRow.ItemCode)] = item,
                    [nameof(ItemSerialSettingStagingRow.ItemName)] = ItemName(item),
                    [nameof(ItemSerialSettingStagingRow.ManagesSerialNumbers)] = "Y",
                    [nameof(ItemSerialSettingStagingRow.ManagementMethod)] = item == "PC-SYSTEM" ? "R" : "A",
                });

        foreach (var row in new[]
        {
            Header(100, new DateTime(2026, 9, 1), "FIC-CUSTOMER-A"),
            Header(101, new DateTime(2026, 9, 5), "FIC-CUSTOMER-A"),
            Header(110, new DateTime(2026, 9, 2), "FIC-CUSTOMER-B"),
            Header(111, new DateTime(2026, 9, 3), "FIC-CUSTOMER-B", documentEntry: 9001),
        }) await MigrationExtractionTests.SourceFixture.InsertAsync<TransactionLogHeaderStagingRow>(SourceDatabase, row);
        foreach (var row in new[]
        {
            Line(100, "SERVER-RACK-EXACT", 1, 1m),
            Line(101, "SERVER-RACK-EXACT", 1, -1m),
            Line(100, "SERVER-RACK-EXACT", 2, 1m),
            Line(110, "SERVER-TRIM", 20, 1m),
            Line(111, "SERVER-TRIM", 21, 1m),
        }) await MigrationExtractionTests.SourceFixture.InsertAsync<TransactionLogLineStagingRow>(SourceDatabase, row);

        await MigrationExtractionTests.SourceFixture.InsertAsync<EquipmentCardStagingRow>(SourceDatabase, new()
        {
            [nameof(EquipmentCardStagingRow.CardId)] = 51320,
            [nameof(EquipmentCardStagingRow.ItemCode)] = "SERVER-TRIM",
            [nameof(EquipmentCardStagingRow.InternalSerial)] = " trim-1 ",
            [nameof(EquipmentCardStagingRow.CustomerCode)] = "FIC-CUSTOMER-B",
        });
        await MigrationExtractionTests.SourceFixture.InsertAsync<ServiceContractStagingRow>(SourceDatabase, new()
        {
            [nameof(ServiceContractStagingRow.ContractId)] = 991,
            [nameof(ServiceContractStagingRow.CustomerCode)] = "FIC-CUSTOMER-B",
            [nameof(ServiceContractStagingRow.StartDate)] = new DateTime(2026, 9, 2),
            [nameof(ServiceContractStagingRow.EndDate)] = new DateTime(2029, 9, 2),
            [nameof(ServiceContractStagingRow.Status)] = "A",
            [nameof(ServiceContractStagingRow.ServiceType)] = "W",
            [nameof(ServiceContractStagingRow.DocumentNumber)] = 82001,
        });
        await MigrationExtractionTests.SourceFixture.InsertAsync<ContractLineStagingRow>(SourceDatabase, new()
        {
            [nameof(ContractLineStagingRow.ContractId)] = 991,
            [nameof(ContractLineStagingRow.LineNumber)] = 0,
            [nameof(ContractLineStagingRow.ItemCode)] = "SERVER-TRIM",
            [nameof(ContractLineStagingRow.InternalSerial)] = " trim-1 ",
            [nameof(ContractLineStagingRow.EquipmentCardId)] = 51320,
            [nameof(ContractLineStagingRow.StartDate)] = new DateTime(2026, 9, 2),
            [nameof(ContractLineStagingRow.EndDate)] = new DateTime(2029, 9, 2),
        });

        await MigrationExtractionTests.SourceFixture.InsertAsync<EquipmentCardStagingRow>(SourceDatabase, new()
        {
            [nameof(EquipmentCardStagingRow.CardId)] = 51207,
            [nameof(EquipmentCardStagingRow.ItemCode)] = "PC-7",
            [nameof(EquipmentCardStagingRow.InternalSerial)] = "PC7-2402-1193",
            [nameof(EquipmentCardStagingRow.CustomerCode)] = "FIC-BRIGHTWATER",
            [nameof(EquipmentCardStagingRow.WarrantyStart)] = new DateTime(2025, 9, 1),
            [nameof(EquipmentCardStagingRow.WarrantyEnd)] = new DateTime(2028, 9, 1),
        });
    }

    private static Dictionary<string, object?> Serial(string item, int number, string? serial)
    {
        var row = MigrationExtractionTests.SourceFixture.Serial(item, number, serial ?? "", "IT hardware migration");
        row[nameof(SerialMasterStagingRow.SerialNumber)] = serial;
        return row;
    }

    private static Dictionary<string, object?> Header(int logEntry, DateTime date, string party,
        int documentEntry = 9000) => new()
    {
        [nameof(TransactionLogHeaderStagingRow.LogEntry)] = logEntry,
        [nameof(TransactionLogHeaderStagingRow.ItemCode)] = "IT-HARDWARE",
        [nameof(TransactionLogHeaderStagingRow.DocumentType)] = 20,
        [nameof(TransactionLogHeaderStagingRow.DocumentEntry)] = documentEntry,
        [nameof(TransactionLogHeaderStagingRow.DocumentLine)] = 0,
        [nameof(TransactionLogHeaderStagingRow.DocumentNumber)] = 7000 + logEntry,
        [nameof(TransactionLogHeaderStagingRow.DocumentDate)] = date,
        [nameof(TransactionLogHeaderStagingRow.CreatedDate)] = date,
        [nameof(TransactionLogHeaderStagingRow.CreatedTime)] = 900,
        [nameof(TransactionLogHeaderStagingRow.PartyCode)] = party,
    };

    private static Dictionary<string, object?> Line(int logEntry, string item, int systemNumber, decimal quantity) => new()
    {
        [nameof(TransactionLogLineStagingRow.LogEntry)] = logEntry,
        [nameof(TransactionLogLineStagingRow.ItemCode)] = item,
        [nameof(TransactionLogLineStagingRow.SystemNumber)] = systemNumber,
        [nameof(TransactionLogLineStagingRow.Quantity)] = quantity,
        [nameof(TransactionLogLineStagingRow.SerialEntry)] = systemNumber,
    };

    private static string ItemName(string item) => item switch
    {
        "SERVER-RACK-EXACT" => "RS-1100 rack server",
        "SERVER-TRIM" => "RS-2200 rack server",
        "STORAGE-FAMILY" => "ST-44 storage array",
        "NETWORK-SWITCH" => "NW-48 network switch",
        "STORAGE-SHELF" => "DS-24 drive shelf",
        "PC-SYSTEM" => "PC system-assigned desktop",
        _ => "PC-7 desktop",
    };
}
