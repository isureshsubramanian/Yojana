using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Imports;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Selection;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P7-T03..T06: named service-desk reproductions and import exit criterion.</summary>
[Collection("serialisation host")]
public sealed class ImportTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Ticket_T49_raw_whitespace_is_kept_while_the_identity_rule_normalises_it()
    {
        var tenant = await fixture.NewTenantAsync("imp49");
        var item = Guid.NewGuid();
        var scheme = RegisterScheme(item);
        var order = new DocumentRef("production_order", Guid.NewGuid());
        await ProvisionalUnitsAsync(tenant, item, order, 1);
        var raw = "  " + scheme.Format(0, 1) + "  ";
        var csv = Encoding.UTF8.GetBytes("Serial;Note\r\n\"" + raw + "\";\"quoted;value\"\r\n");

        var result = await RunAsync(tenant, new RunImport(Guid.NewGuid(), ImportSource.VendorAsn,
            new("vendor.csv", ImportFormat.Csv, csv), At.AddHours(1), Entity, "ignored", order,
            new("Serial"), CommittedBy: Committer()));

        var job = Assert.Single(result.Jobs);
        var row = Assert.Single(job.Rows);
        Assert.Equal(ImportOutcome.Applied, row.Outcome);
        Assert.Equal(raw, row.RawValue);
        Assert.Equal(scheme.Format(0, 1).Replace("-", "", StringComparison.Ordinal), row.NormalisedValue);
        Assert.NotNull((await StoredJobAsync(tenant, job.JobId)).OriginalFileReference);
    }

    [DatabaseFact]
    public async Task Ticket_T58_xlsx_is_read_and_a_clean_original_is_archived()
    {
        var tenant = await fixture.NewTenantAsync("imp58x");
        var item = Guid.NewGuid();
        var scheme = RegisterScheme(item);
        var order = new DocumentRef("production_order", Guid.NewGuid());
        await ProvisionalUnitsAsync(tenant, item, order, 1);
        var xlsx = await XlsxAsync(["Serial"], [[scheme.Format(0, 2)]]);

        var result = await RunAsync(tenant, new RunImport(Guid.NewGuid(), ImportSource.UserUpload,
            new("serials.xlsx", ImportFormat.Xlsx, xlsx), At.AddHours(1), Entity, "ignored", order,
            new("Serial"), CommittedBy: Committer()));

        var job = Assert.Single(result.Jobs);
        Assert.Equal("applied", job.Status);
        Assert.Equal(1, job.Applied);
        Assert.NotNull((await StoredJobAsync(tenant, job.JobId)).OriginalFileReference);
        Assert.Null(job.ConvertedHash);
        using var scope = fixture.ScopeFor(tenant);
        Assert.Contains(scope.ServiceProvider.GetServices<ISpreadsheetReader>(), reader => reader.CanRead(ImportFormat.Xls));
    }

    [DatabaseFact]
    public async Task Ticket_T58_legacy_conversion_keeps_both_hashes_and_archives_both_copies()
    {
        var tenant = await fixture.NewTenantAsync("imp58l");
        var item = Guid.NewGuid();
        var scheme = RegisterScheme(item);
        var order = new DocumentRef("production_order", Guid.NewGuid());
        await ProvisionalUnitsAsync(tenant, item, order, 1);
        var converted = Encoding.UTF8.GetBytes("converted-copy-owned-by-test");

        using var scope = fixture.ScopeFor(tenant);
        var services = scope.ServiceProvider;
        var service = new ImportService(
            services.GetRequiredService<SerialisationDbContext>(),
            [new FakeLegacySpreadsheetReader(scheme.Format(0, 7), converted)],
            services.GetRequiredService<IImportFileStore>(),
            services.GetRequiredService<IDocumentNumberPort>(),
            services.GetRequiredService<IPartyNamePort>(),
            services.GetRequiredService<IItemNamePort>(),
            services.GetRequiredService<IdentifierService>(),
            services.GetRequiredService<AssignIdentifiersService>(),
            services.GetRequiredService<IMessageBus>(),
            services.GetRequiredService<ITenantContext>(),
            services.GetRequiredService<IClock>());
        var result = await service.RunAsync(new RunImport(Guid.NewGuid(), ImportSource.UserUpload,
            new("legacy.xls", ImportFormat.Xls, Encoding.UTF8.GetBytes("original-copy-owned-by-test")),
            At.AddHours(1), Entity, "supervisor", order, new("Serial"), CommittedBy: Committer()), CancellationToken.None);

        var job = Assert.Single(result.Jobs);
        Assert.Equal(1, job.Applied);
        Assert.NotNull(job.ConvertedHash);
        Assert.NotEqual(job.OriginalHash, job.ConvertedHash);
        var stored = await services.GetRequiredService<SerialisationDbContext>().ImportJobs.SingleAsync(row => row.JobId == job.JobId);
        Assert.NotNull(stored.OriginalFileReference);
        Assert.NotNull(stored.ConvertedFileReference);
        await using var convertedCopy = await services.GetRequiredService<IImportFileStore>()
            .OpenReadAsync(stored.ConvertedFileReference, CancellationToken.None);
        using var convertedBytes = new MemoryStream();
        await convertedCopy.Content.CopyToAsync(convertedBytes);
        Assert.Equal(converted, convertedBytes.ToArray());
        var download = await services.GetRequiredService<ImportFileDownloads>()
            .OpenAsync(job.JobId, ImportFileCopyKind.Converted, "supervisor", [YojanaRole.SerialAdministrator], CancellationToken.None);
        Assert.Equal("legacy.xlsx", download.FileName);
        await download.Content.DisposeAsync();
        Assert.True(await services.GetRequiredService<SerialisationDbContext>().ImportFileDownloadAudits
            .AnyAsync(row => row.JobId == job.JobId && row.CopyKind == ImportFileCopyKind.Converted));
        var history = await services.GetRequiredService<ImportQueries>().HistoryAsync(
            new(FileName: "legacy", Hash: job.OriginalHash), null, 10, CancellationToken.None);
        Assert.Equal(job.JobId, Assert.Single(history.Items).JobId);
    }

    [DatabaseFact]
    public async Task Download_refuses_a_hash_mismatch_without_writing_an_audit_row()
    {
        var tenant = await fixture.NewTenantAsync("imphash");
        var item = Guid.NewGuid();
        var scheme = RegisterScheme(item);
        var order = new DocumentRef("production_order", Guid.NewGuid());
        await ProvisionalUnitsAsync(tenant, item, order, 1);
        var result = await RunAsync(tenant, new RunImport(Guid.NewGuid(), ImportSource.UserUpload,
            new("integrity.csv", ImportFormat.Csv, Encoding.UTF8.GetBytes($"Serial\r\n{scheme.Format(0, 1)}\r\n")),
            At, Entity, "ignored", order, new("Serial"), CommittedBy: Committer()));
        var jobId = Assert.Single(result.Jobs).JobId;
        using var scope = fixture.ScopeFor(tenant);
        var operational = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var job = await operational.ImportJobs.SingleAsync(x => x.JobId == jobId);
        var fileId = Guid.ParseExact(job.OriginalFileReference![12..], "N");
        var fileDb = scope.ServiceProvider.GetRequiredService<ImportFilesDbContext>();
        var row = await fileDb.Files.SingleAsync(x => x.FileId == fileId);
        row.Content = Encoding.UTF8.GetBytes("tampered");
        row.ContentLength = row.Content.LongLength;
        await fileDb.SaveChangesAsync();
        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => scope.ServiceProvider
            .GetRequiredService<ImportFileDownloads>().OpenAsync(jobId, ImportFileCopyKind.Original, "supervisor", [YojanaRole.SerialAdministrator], CancellationToken.None));
        Assert.Equal("import_file_hash_mismatch", refusal.Rule);
        Assert.False(await operational.ImportFileDownloadAudits.AnyAsync(x => x.JobId == jobId));
    }

    [DatabaseFact]
    public async Task Download_is_limited_to_the_uploader_and_the_administrator_and_service_manager_roles()
    {
        var tenant = await fixture.NewTenantAsync("impdownloadrole");
        var item = Guid.NewGuid();
        var scheme = RegisterScheme(item);
        var order = new DocumentRef("production_order", Guid.NewGuid());
        await ProvisionalUnitsAsync(tenant, item, order, 1);
        var uploader = Guid.NewGuid();
        RunImportResult result;
        using (var importScope = fixture.ScopeFor(tenant))
            result = await importScope.ServiceProvider.GetRequiredService<ImportService>().RunAsync(new RunImport(Guid.NewGuid(),
                ImportSource.UserUpload, new("role-check.csv", ImportFormat.Csv,
                    Encoding.UTF8.GetBytes($"Serial\r\n{scheme.Format(0, 1)}\r\n")),
                At, Entity, uploader.ToString("D"), order, new("Serial"), CommittedBy: Committer()), CancellationToken.None);
        var jobId = Assert.Single(result.Jobs).JobId;

        // Another user with the uploader's own import role may see the history but not download the file.
        using (var colleague = await fixture.AuthenticatedClientAsync(tenant, YojanaRole.Quality))
        {
            using (var response = await colleague.GetAsync($"/api/imports/{jobId:D}/files/original"))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var history = await colleague.GetFromJsonAsync<ImportFileHistoryPage>("/api/imports/history?fileName=role-check");
            Assert.False(Assert.Single(history!.Items).MayDownload);
        }
        using (var outsider = await fixture.AuthenticatedClientAsync(tenant, YojanaRole.CustomerService))
        using (var response = await outsider.GetAsync($"/api/imports/{jobId:D}/files/original"))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using (var own = await fixture.AuthenticatedClientAsync(tenant, YojanaRole.Quality, uploader))
        {
            using (var response = await own.GetAsync($"/api/imports/{jobId:D}/files/original"))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var history = await own.GetFromJsonAsync<ImportFileHistoryPage>("/api/imports/history?fileName=role-check");
            Assert.True(Assert.Single(history!.Items).MayDownload);
        }
        foreach (var role in new[] { YojanaRole.TenantAdministrator, YojanaRole.SerialAdministrator, YojanaRole.ServiceManager })
        {
            using var client = await fixture.AuthenticatedClientAsync(tenant, role);
            using var response = await client.GetAsync($"/api/imports/{jobId:D}/files/original");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var scope = fixture.ScopeFor(tenant);
        var audits = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>()
            .ImportFileDownloadAudits.Where(row => row.JobId == jobId).ToListAsync();
        Assert.Equal(4, audits.Count);
        Assert.Contains(audits, row => row.ActorRef == uploader.ToString("D"));
    }

    [DatabaseFact]
    public async Task Ticket_T66b_two_hundred_vendor_rows_with_one_hundred_duplicates_assign_once_and_reimport_applies_nothing()
    {
        var tenant = await fixture.NewTenantAsync("imp66b");
        var item = Guid.NewGuid();
        var scheme = RegisterScheme(item);
        var order = new DocumentRef("production_order", Guid.NewGuid());
        await ProvisionalUnitsAsync(tenant, item, order, 200);
        var unique = Enumerable.Range(1, 100).Select(index => scheme.Format(0, index)).ToList();
        var content = Encoding.UTF8.GetBytes("Serial\r\n" + string.Join("\r\n", unique.Concat(unique)) + "\r\n");
        var command = new RunImport(Guid.NewGuid(), ImportSource.VendorAsn, new("corrected.csv", ImportFormat.Csv, content),
            At.AddHours(1), Entity, "ignored", order, new("Serial"), CommittedBy: Committer());

        var first = await RunAsync(tenant, command);
        var firstJob = Assert.Single(first.Jobs);
        Assert.Equal(100, firstJob.Applied);
        Assert.Equal(100, firstJob.Skipped);
        Assert.Equal(0, firstJob.Rejected);

        var second = await RunAsync(tenant, command with { CommandId = Guid.NewGuid() });
        Assert.True(second.Replayed);
        Assert.Equal(firstJob.JobId, Assert.Single(second.Jobs).JobId);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(100, await db.Identifiers.CountAsync(row => row.TenantId == tenant && row.Type == IdentifierType.VendorSerial && row.ValidTo == null));
        Assert.Equal(200, await db.Units.CountAsync(row => row.TenantId == tenant));
    }

    [DatabaseFact]
    public async Task Compare_mode_file_replay_returns_the_intake_job_and_original_rows()
    {
        var tenant = await fixture.NewTenantAsync("impcmpreplay");
        var target = new DocumentRef("production_order", Guid.NewGuid());
        var content = Encoding.UTF8.GetBytes("Serial\r\nCMP-0001\r\nCMP-0002\r\n");
        var command = new RunImport(Guid.NewGuid(), ImportSource.UserUpload,
            new("compare.csv", ImportFormat.Csv, content), At, Entity, "ignored", target,
            Mode: ImportMode.Compare);

        var first = await RunAsync(tenant, command);
        var firstJob = Assert.Single(first.Jobs);
        Assert.False(first.Replayed);
        Assert.Equal(ImportJobStatus.Intake, firstJob.Status);

        var commandReplay = await RunAsync(tenant, command with
        {
            File = new("compare.csv", ImportFormat.Csv, Encoding.UTF8.GetBytes("Serial\r\nREGENERATED-0001\r\n")),
        });
        Assert.True(commandReplay.Replayed);
        Assert.Equal(firstJob.JobId, Assert.Single(commandReplay.Jobs).JobId);

        var replay = await RunAsync(tenant, command with { CommandId = Guid.NewGuid() });
        var replayedJob = Assert.Single(replay.Jobs);
        Assert.True(replay.Replayed);
        Assert.Equal(firstJob.JobId, replayedJob.JobId);
        Assert.Equal(firstJob.Rows.Select(row => row.RowResultId), replayedJob.Rows.Select(row => row.RowResultId));
    }

    [DatabaseFact]
    public async Task Replay_returns_a_matching_job_left_in_intake_by_a_crashed_worker()
    {
        var tenant = await fixture.NewTenantAsync("impcrash");
        var target = new DocumentRef("production_order", Guid.NewGuid(), 3);
        var content = Encoding.UTF8.GetBytes("Serial\r\nCRASH-0001\r\n");
        var jobId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.ImportJobs.Add(new ImportJob
            {
                TenantId = tenant,
                JobId = jobId,
                CommandId = jobId,
                Source = ImportSource.UserUpload,
                FileName = "crashed.csv",
                OriginalHash = Hash(content),
                Format = ImportFormat.Csv,
                Mode = ImportMode.Compare,
                TargetType = target.Type,
                TargetId = target.Id,
                TargetLine = target.Line,
                Status = ImportJobStatus.Intake,
                ActorRef = "worker",
                RecordedTime = At,
            });
            db.ImportRowResults.Add(new ImportRowResult
            {
                TenantId = tenant,
                RowResultId = rowId,
                JobId = jobId,
                RowNumber = 2,
                Sheet = "CSV",
                RawValue = "CRASH-0001",
                NormalisedValue = "CRASH0001",
                Outcome = ImportOutcome.Skipped,
                RecordedTime = At,
            });
            await db.SaveChangesAsync();
        }

        var replay = await RunAsync(tenant, new RunImport(Guid.NewGuid(), ImportSource.UserUpload,
            new("crashed.csv", ImportFormat.Csv, content), At.AddMinutes(1), Entity, "ignored", target,
            Mode: ImportMode.Compare));

        var job = Assert.Single(replay.Jobs);
        Assert.True(replay.Replayed);
        Assert.Equal(jobId, job.JobId);
        Assert.Equal(rowId, Assert.Single(job.Rows).RowResultId);
    }

    [DatabaseFact]
    public async Task Parallel_duplicate_uploads_return_one_compare_intake_job()
    {
        var tenant = await fixture.NewTenantAsync("impparallel");
        var target = new DocumentRef("production_order", Guid.NewGuid());
        var content = Encoding.UTF8.GetBytes("Serial\r\nPAR-0001\r\nPAR-0002\r\n");
        var firstCommand = new RunImport(Guid.NewGuid(), ImportSource.VendorAsn,
            new("parallel.csv", ImportFormat.Csv, content), At, Entity, "ignored", target,
            Mode: ImportMode.Compare);
        var secondCommand = firstCommand with { CommandId = Guid.NewGuid() };

        var results = await Task.WhenAll(RunAsync(tenant, firstCommand), RunAsync(tenant, secondCommand));

        Assert.Single(results, result => !result.Replayed);
        Assert.Single(results, result => result.Replayed);
        Assert.Single(results.SelectMany(result => result.Jobs).Select(job => job.JobId).Distinct());
        Assert.All(results, result => Assert.Equal(2, Assert.Single(result.Jobs).Rows.Count));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(1, await db.ImportJobs.CountAsync(row => row.TenantId == tenant));
        Assert.Equal(2, await db.ImportRowResults.CountAsync(row => row.TenantId == tenant));
    }

    [DatabaseFact]
    public async Task Case_138_utf8_bom_tab_file_splits_by_order_and_file_name_matching_supplies_a_target()
    {
        var tenant = await fixture.NewTenantAsync("imp138");
        var item = Guid.NewGuid();
        var scheme = RegisterScheme(item);
        var orderA = new DocumentRef("production_order", Guid.NewGuid());
        var orderB = new DocumentRef("production_order", Guid.NewGuid());
        await ProvisionalUnitsAsync(tenant, item, orderA, 2);
        await ProvisionalUnitsAsync(tenant, item, orderB, 1);
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var numberA = "PO-A" + suffix;
        var numberB = "PO-B" + suffix;
        var registry = fixture.Services.GetRequiredService<InMemoryDocumentNumberRegistry>();
        registry.Register(new(orderA, numberA));
        registry.Register(new(orderB, numberB));
        var splitContent = new UTF8Encoding(true).GetBytes("Serial\tOrder Number\tNote\r\n"
            + $"{scheme.Format(0, 11)}\t{numberA}\t\"first line\nsecond line\"\r\n"
            + $"{scheme.Format(0, 12)}\t{numberB}\tplain\r\n");

        var splitCommand = new RunImport(Guid.NewGuid(), ImportSource.VendorAsn,
            new("multi-order.tsv", ImportFormat.Csv, splitContent), At.AddHours(1), Entity, "ignored",
            Mapping: new("Serial", "Order Number"), MappingName: "vendor-tab", CommittedBy: Committer());
        var split = await RunAsync(tenant, splitCommand);
        Assert.Equal(2, split.Jobs.Count);
        Assert.Contains(split.Jobs, job => job.Target == orderA && job.TargetName == numberA && job.Applied == 1);
        Assert.Contains(split.Jobs, job => job.Target == orderB && job.TargetName == numberB && job.Applied == 1);
        foreach (var job in split.Jobs) Assert.NotNull((await StoredJobAsync(tenant, job.JobId)).OriginalFileReference);
        var splitReplay = await RunAsync(tenant, splitCommand);
        Assert.True(splitReplay.Replayed);
        Assert.Equal(split.Jobs.Select(job => job.JobId).Order(), splitReplay.Jobs.Select(job => job.JobId).Order());

        var fileMatched = await RunAsync(tenant, new RunImport(Guid.NewGuid(), ImportSource.UserUpload,
            new($"{numberA}-serials.csv", ImportFormat.Csv,
                Encoding.UTF8.GetBytes($"Serial\r\n{scheme.Format(0, 13)}\r\n")),
            At.AddHours(2), Entity, "ignored", Mapping: new("Serial"), CommittedBy: Committer()));
        Assert.Equal(orderA, Assert.Single(fileMatched.Jobs).Target);
        Assert.Equal(numberA, fileMatched.Jobs[0].TargetName);
        Assert.Equal(1, fileMatched.Jobs[0].Applied);
    }

    [DatabaseFact]
    public async Task Ticket_T58_three_pl_feed_completes_pending_shipment_and_captures_customer_asset_tag()
    {
        var tenant = await fixture.NewTenantAsync("imp58f");
        var item = Guid.NewGuid();
        var serial = "F" + Guid.NewGuid().ToString("N")[..7].ToUpperInvariant();
        var receipt = new ReceiptDocument(new("receipt", Guid.NewGuid()), Guid.NewGuid(), At,
            [new(1, item, 1, [new(serial)], Guid.NewGuid(), LocationKind.Warehouse)]);
        var ports = fixture.Services.GetRequiredService<InMemoryDocumentPorts>();
        ports.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(new(Guid.NewGuid(), Entity, At, "test"), receipt.Document));
        var shipment = new ShipmentDocument(new("delivery", Guid.NewGuid()), Guid.NewGuid(), At.AddDays(1),
            [new(1, item, 1, [])], Guid.NewGuid());
        ports.Add(shipment);
        await InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(new(Guid.NewGuid(), Entity, At.AddDays(1), "test"), shipment.Document));

        var feed = await InvokeAsync<ReceiveThreePlFeedResult>(tenant, new ReceiveThreePlFeed(Guid.NewGuid(), "FICT-3PL-0001",
            shipment.Document, 1, At.AddDays(1).AddMinutes(-5), [new(serial, CustomerAssetTag: "ASSET-0001")], "feed", Entity));

        Assert.Equal(1, feed.Shipped);
        Assert.Equal(1, feed.AssetTagsAssigned);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var tag = await db.Identifiers.SingleAsync(row => row.TenantId == tenant && row.Type == IdentifierType.CustomerAssetTag && row.ValidTo == null);
        Assert.Equal("ASSET-0001", tag.RawValue);
        Assert.Equal(1, await db.ThreePlFeedMessages.CountAsync(row => row.TenantId == tenant));
        Assert.Equal(At.AddDays(1).AddMinutes(-5), (await db.Events.SingleAsync(row => row.TenantId == tenant && row.EventType == UnitEventType.Shipped)).EventTime);
    }

    [DatabaseFact]
    public async Task Case_142_reviewed_compare_result_assigns_and_becomes_an_import_job_selection_source()
    {
        var tenant = await fixture.NewTenantAsync("imp142");
        var item = Guid.NewGuid();
        var scheme = RegisterScheme(item);
        var order = new DocumentRef("production_order", Guid.NewGuid());
        await ProvisionalUnitsAsync(tenant, item, order, 6);
        var collision = (await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item,
            UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.VendorSerial, scheme.Format(0, 5))])],
            At, "test", Entity, Document: new("receipt", Guid.NewGuid())))).Lines.Single().UnitId!.Value;
        var owner = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(new(owner, "HBR-001", "Harbour Repair Cooperative", PartyType.Customer));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), collision, owner, PartyType.Customer,
            EpisodeReason.Sale, At.AddMinutes(1), "test", Entity));
        var aValues = new[] { scheme.Format(0, 1), scheme.Format(0, 2), scheme.Format(0, 4), scheme.Format(0, 4), scheme.Format(0, 5) };
        var bValues = new[] { scheme.Format(0, 2), scheme.Format(0, 3) };
        var jobA = Assert.Single((await RunAsync(tenant, CompareIntake("a.csv", aValues, order))).Jobs);
        var jobB = Assert.Single((await RunAsync(tenant, CompareIntake("b.csv", bValues, order))).Jobs);

        var compared = await InvokeAsync<CompareImportsResult>(tenant,
            new CompareImports(Guid.NewGuid(), jobA.JobId, jobB.JobId, null, "reviewer"));
        Assert.False(compared.Replayed);
        Assert.Equal(1, compared.OnlyInA);
        Assert.Equal(1, compared.OnlyInB);
        Assert.Equal(2, compared.Both);
        Assert.Equal(2, compared.Duplicates);
        Assert.Equal(1, compared.ExistingCollisions);
        var collided = Assert.Single(compared.Rows, row => row.ComparisonPartition == ImportComparisonPartition.ExistingCollision);
        Assert.Equal("Harbour Repair Cooperative", collided.CollisionOwnerName);
        Assert.NotNull(collided.CollisionUnitName);

        var comparisonReplay = await InvokeAsync<CompareImportsResult>(tenant,
            new CompareImports(Guid.NewGuid(), jobA.JobId, jobB.JobId, null, "reviewer"));
        Assert.True(comparisonReplay.Replayed);
        Assert.Equal(compared.ComparisonJobId, comparisonReplay.ComparisonJobId);
        Assert.Equal(compared.Rows.Select(row => row.RowResultId), comparisonReplay.Rows.Select(row => row.RowResultId));

        var commonFromA = Assert.Single(compared.Rows, row => row.RawValue == scheme.Format(0, 2) && row.Rule == "comparison_a");
        var applied = await InvokeAsync<ApplyImportReviewResult>(tenant, new ApplyImportReview(Guid.NewGuid(), compared.ComparisonJobId,
            [new(commonFromA.RowResultId, ImportReviewAction.Assign)], At.AddHours(2), Entity, "reviewer", Committer()));
        Assert.Equal(2, applied.Assigned);
        Assert.Equal(5, applied.Skipped);
        using (var lifecycleScope = fixture.ScopeFor(tenant))
        {
            var lifecycleDb = lifecycleScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(ImportJobStatus.HandedOff, (await lifecycleDb.ImportJobs.SingleAsync(
                row => row.TenantId == tenant && row.JobId == compared.ComparisonJobId)).Status);
            Assert.All(await lifecycleDb.ImportJobs.Where(row => row.TenantId == tenant
                    && (row.JobId == jobA.JobId || row.JobId == jobB.JobId)).ToListAsync(),
                row => Assert.Equal(ImportJobStatus.Compared, row.Status));
        }

        var selection = await InvokeAsync<ResolveSelectionSetResult>(tenant,
            new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.ByImportJob(compared.ComparisonJobId), "reviewer"));
        Assert.Equal(2, selection.Resolved);
        Assert.Empty(selection.Unknown);

        using var scope = fixture.ScopeFor(tenant);
        var queries = scope.ServiceProvider.GetRequiredService<ImportQueries>();
        var firstRows = await queries.RowsAsync(compared.ComparisonJobId, null, 2, CancellationToken.None);
        Assert.NotNull(firstRows);
        Assert.Equal(2, firstRows.Items.Count);
        Assert.NotNull(firstRows.NextCursor);
        var secondRows = await queries.RowsAsync(compared.ComparisonJobId, firstRows.NextCursor, 2, CancellationToken.None);
        Assert.NotNull(secondRows);
        Assert.Equal(2, secondRows.Items.Count);
        Assert.Empty(firstRows.Items.Select(row => row.RowResultId).Intersect(secondRows.Items.Select(row => row.RowResultId)));
        var firstJobs = await queries.PageAsync(null, 1, CancellationToken.None);
        Assert.Single(firstJobs.Items);
        Assert.NotNull(firstJobs.NextCursor);
        Assert.Single((await queries.PageAsync(firstJobs.NextCursor, 1, CancellationToken.None)).Items);
        // A compare result stores no file of its own, so upload history lists the uploaded inputs and not the result.
        Assert.Empty((await queries.HistoryAsync(new(Status: ImportJobStatus.HandedOff), null, 10, CancellationToken.None)).Items);
        var history = await queries.HistoryAsync(new(), null, 10, CancellationToken.None);
        Assert.NotEmpty(history.Items);
        Assert.DoesNotContain(history.Items, row => row.JobId == compared.ComparisonJobId);
    }

    [DatabaseFact]
    public async Task Import_file_database_is_separate_tenant_scoped_and_round_trips()
    {
        var tenant = await fixture.NewTenantAsync("impfiles");
        var otherTenant = await fixture.NewTenantAsync("impfiles2");
        string reference;
        var bytes = Encoding.UTF8.GetBytes("fictional import bytes");
        using (var scope = fixture.ScopeFor(tenant))
        {
            var backend = scope.ServiceProvider.GetServices<IImportFileBackend>().Single(x => x.Scheme == "database");
            await using var input = new MemoryStream(bytes);
            var stored = await backend.PutAsync(Guid.NewGuid(), "fictional.csv", ImportFileCopyKind.Original, input, At, CancellationToken.None);
            reference = stored.Reference;
            Assert.Equal(Hash(bytes), stored.Sha256);
            await using var opened = await backend.OpenReadAsync(reference, CancellationToken.None);
            using var copy = new MemoryStream();
            await opened.Content.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }
        using (var scope = fixture.ScopeFor(otherTenant))
        {
            var backend = scope.ServiceProvider.GetServices<IImportFileBackend>().Single(x => x.Scheme == "database");
            var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => backend.OpenReadAsync(reference, CancellationToken.None));
            Assert.Equal("import_file_missing", refusal.Rule);
        }
    }

    [DatabaseFact]
    public async Task Setting_change_affects_new_files_only_and_old_references_still_resolve()
    {
        var tenant = await fixture.NewTenantAsync("impset");
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<TenantImportFileSettings>();
        var current = await settings.GetAsync(CancellationToken.None);
        var store = scope.ServiceProvider.GetRequiredService<IImportFileStore>();
        var bytes = Encoding.UTF8.GetBytes("first");
        await using var first = new MemoryStream(bytes);
        var database = await store.PutAsync(Guid.NewGuid(), "first.csv", ImportFileCopyKind.Original, first, At, CancellationToken.None);
        Assert.StartsWith("database:", database.Reference);
        await settings.SetAsync(new(TenantImportFileSettings.File, current.ArchiveStore, 90,
            ImportFileArchiveAgeUnit.Days, current.Version), CancellationToken.None);
        await using var second = new MemoryStream(Encoding.UTF8.GetBytes("second"));
        var file = await store.PutAsync(Guid.NewGuid(), "second.csv", ImportFileCopyKind.Original, second, At, CancellationToken.None);
        Assert.StartsWith("file:", file.Reference);
        await using var old = await store.OpenReadAsync(database.Reference, CancellationToken.None);
        Assert.Equal(bytes.Length, old.ContentLength);
    }

    [DatabaseFact]
    public async Task Legacy_content_migration_verifies_nulls_archives_resumes_and_remains_downloadable()
    {
        var tenant = await fixture.NewTenantAsync("impmove");
        var jobId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("Serial\r\nFIC-001\r\n");
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.ImportJobs.Add(new ImportJob { TenantId = tenant, JobId = jobId, CommandId = jobId,
                Source = ImportSource.UserUpload, FileName = "legacy.csv", OriginalHash = Hash(bytes), Format = ImportFormat.Csv,
                Mode = ImportMode.Compare, Status = ImportJobStatus.Compared, ActorRef = "supervisor", RecordedTime = At,
                OriginalContent = bytes });
            await db.SaveChangesAsync();
            var settings = scope.ServiceProvider.GetRequiredService<TenantImportFileSettings>();
            var current = await settings.GetAsync(CancellationToken.None);
            await settings.SetAsync(new(TenantImportFileSettings.Database, current.ArchiveStore, 1,
                ImportFileArchiveAgeUnit.Days, current.Version), CancellationToken.None);
            var maintenance = scope.ServiceProvider.GetRequiredService<ImportFileMaintenance>();
            var first = await maintenance.RunBatchAsync(CancellationToken.None);
            var second = await maintenance.RunBatchAsync(CancellationToken.None);
            Assert.Equal(1, first.MigratedCopies);
            Assert.Equal(1, first.ArchivedCopies);
            Assert.Equal((0, 0), (second.MigratedCopies, second.ArchivedCopies));
            var job = await db.ImportJobs.SingleAsync(x => x.JobId == jobId);
            Assert.Null(job.OriginalContent);
            Assert.True(job.OriginalArchived);
            var download = await scope.ServiceProvider.GetRequiredService<ImportFileDownloads>()
                .OpenAsync(jobId, ImportFileCopyKind.Original, "auditor", [YojanaRole.SerialAdministrator], CancellationToken.None);
            await download.Content.DisposeAsync();
            Assert.True(await db.ImportFileDownloadAudits.AnyAsync(x => x.JobId == jobId && x.ActorRef == "auditor"));
        }
    }

    [DatabaseFact]
    public async Task Archive_move_covers_every_active_and_archive_backend_combination()
    {
        foreach (var active in new[] { TenantImportFileSettings.File, TenantImportFileSettings.Database })
        foreach (var archive in new[] { "folder-archive", "database-archive" })
        {
            var tenant = await fixture.NewTenantAsync("arcmatrix");
            var jobId = Guid.NewGuid();
            var bytes = Encoding.UTF8.GetBytes($"{active} to {archive}");
            using var scope = fixture.ScopeFor(tenant);
            await ConfigureAsync(scope, active, archive);
            var store = scope.ServiceProvider.GetRequiredService<IImportFileStore>();
            await using var input = new MemoryStream(bytes);
            var stored = await store.PutAsync(jobId, "matrix.csv", ImportFileCopyKind.Original, input, At, CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.ImportJobs.Add(Job(tenant, jobId, bytes, stored.Reference, stored.ContentLength));
            await db.SaveChangesAsync();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            Assert.True(At <= clock.UtcNow.AddDays(-1), $"test import time {At:O} must precede archive cutoff {clock.UtcNow.AddDays(-1):O}");
            Assert.True(await db.ImportJobs.AnyAsync(x => x.JobId == jobId && x.OriginalFileReference != null));

            var result = await scope.ServiceProvider.GetRequiredService<ImportFileMaintenance>().RunBatchAsync(CancellationToken.None);
            Assert.Equal(1, result.ArchivedCopies);
            var job = await db.ImportJobs.AsNoTracking().SingleAsync(x => x.JobId == jobId);
            Assert.StartsWith($"archive:{archive}:", job.OriginalFileReference);
            Assert.True(job.OriginalArchived);
            Assert.Null(job.OriginalFileCleanupReference);
            await AssertActiveGoneAsync(scope, active, jobId);
            var download = await scope.ServiceProvider.GetRequiredService<ImportFileDownloads>().OpenAsync(jobId,
                ImportFileCopyKind.Original, "supervisor", [], CancellationToken.None);
            await using (download.Content)
            using (var output = new MemoryStream())
            {
                await download.Content.CopyToAsync(output);
                Assert.Equal(bytes, output.ToArray());
            }
        }
    }

    [DatabaseFact]
    public async Task Archive_resume_is_safe_after_copy_and_after_repoint()
    {
        foreach (var stage in new[] { "copied", "repointed" })
        {
            var tenant = await fixture.NewTenantAsync("arcresume");
            var jobId = Guid.NewGuid();
            var bytes = Encoding.UTF8.GetBytes("resumable archive " + stage);
            using var scope = fixture.ScopeFor(tenant);
            await ConfigureAsync(scope, TenantImportFileSettings.Database, "database-archive");
            var store = scope.ServiceProvider.GetRequiredService<IImportFileStore>();
            await using var input = new MemoryStream(bytes);
            var stored = await store.PutAsync(jobId, "resume.csv", ImportFileCopyKind.Original, input, At, CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.ImportJobs.Add(Job(tenant, jobId, bytes, stored.Reference, stored.ContentLength));
            await db.SaveChangesAsync();
            fixture.Services.GetRequiredService<TestImportFileArchiveObserver>().FailOnce(jobId, stage);
            var maintenance = scope.ServiceProvider.GetRequiredService<ImportFileMaintenance>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.RunBatchAsync(CancellationToken.None));
            await maintenance.RunBatchAsync(CancellationToken.None);

            var job = await db.ImportJobs.AsNoTracking().SingleAsync(x => x.JobId == jobId);
            Assert.StartsWith("archive:database-archive:database:", job.OriginalFileReference);
            Assert.Null(job.OriginalFileCleanupReference);
            await AssertActiveGoneAsync(scope, TenantImportFileSettings.Database, jobId);
            Assert.Equal(1, await ArchiveDatabaseCountAsync(scope, jobId));
        }
    }

    [DatabaseFact]
    public async Task Switching_archive_store_keeps_old_reference_and_missing_store_is_named()
    {
        var tenant = await fixture.NewTenantAsync("arcswitch");
        var jobId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("old archive remains readable");
        using var scope = fixture.ScopeFor(tenant);
        await ConfigureAsync(scope, TenantImportFileSettings.Database, "folder-archive");
        var store = scope.ServiceProvider.GetRequiredService<IImportFileStore>();
        await using var input = new MemoryStream(bytes);
        var stored = await store.PutAsync(jobId, "switch.csv", ImportFileCopyKind.Original, input, At, CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.ImportJobs.Add(Job(tenant, jobId, bytes, stored.Reference, stored.ContentLength));
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<ImportFileMaintenance>().RunBatchAsync(CancellationToken.None);
        var settings = scope.ServiceProvider.GetRequiredService<TenantImportFileSettings>();
        var current = await settings.GetAsync(CancellationToken.None);
        await settings.SetAsync(new(current.Storage, "database-archive", current.ArchiveAge,
            current.ArchiveAgeUnit, current.Version), CancellationToken.None);
        await using var old = await store.OpenReadAsync((await db.ImportJobs.AsNoTracking().SingleAsync(x => x.JobId == jobId)).OriginalFileReference!, CancellationToken.None);
        Assert.Equal(bytes.Length, old.ContentLength);

        var missing = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            store.OpenReadAsync("archive:retired-store:file:v1:AA==", CancellationToken.None));
        Assert.Equal("import_file_archive_store_unavailable", missing.Rule);
        var missingJobId = Guid.NewGuid();
        var missingBytes = Encoding.UTF8.GetBytes("reference in a retired archive store");
        db.ImportJobs.Add(Job(tenant, missingJobId, missingBytes, "archive:retired-store:file:v1:AA==", missingBytes.LongLength));
        await db.SaveChangesAsync();
        var downloadRefusal = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            scope.ServiceProvider.GetRequiredService<ImportFileDownloads>().OpenAsync(missingJobId,
                ImportFileCopyKind.Original, "supervisor", [], CancellationToken.None));
        Assert.Equal("import_file_archive_store_unavailable", downloadRefusal.Rule);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ImportFileMaintenance>()
            .UnavailableArchiveReferencesAsync(CancellationToken.None));
    }

    [DatabaseFact]
    public async Task One_file_that_fails_verification_is_skipped_and_the_rest_still_archive()
    {
        var tenant = await fixture.NewTenantAsync("arcpoison");
        using var scope = fixture.ScopeFor(tenant);
        await ConfigureAsync(scope, TenantImportFileSettings.Database, "database-archive");
        var store = scope.ServiceProvider.GetRequiredService<IImportFileStore>();
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var poisonId = Guid.NewGuid();
        var goodId = Guid.NewGuid();
        var poisonBytes = Encoding.UTF8.GetBytes("stored bytes that no longer match the job hash");
        var goodBytes = Encoding.UTF8.GetBytes("healthy file archives normally");
        await using (var input = new MemoryStream(poisonBytes))
        {
            var stored = await store.PutAsync(poisonId, "poison.csv", ImportFileCopyKind.Original, input, At, CancellationToken.None);
            // The job records the hash of different bytes, so archive verification refuses this copy.
            db.ImportJobs.Add(Job(tenant, poisonId, Encoding.UTF8.GetBytes("what was really uploaded"), stored.Reference, stored.ContentLength));
        }
        await using (var input = new MemoryStream(goodBytes))
        {
            var stored = await store.PutAsync(goodId, "good.csv", ImportFileCopyKind.Original, input, At.AddMinutes(1), CancellationToken.None);
            var good = Job(tenant, goodId, goodBytes, stored.Reference, stored.ContentLength);
            good.RecordedTime = At.AddMinutes(1);
            db.ImportJobs.Add(good);
        }
        await db.SaveChangesAsync();

        var result = await scope.ServiceProvider.GetRequiredService<ImportFileMaintenance>().RunBatchAsync(CancellationToken.None);

        Assert.Equal(1, result.ArchivedCopies);
        Assert.Equal(1, result.FailedCopies);
        var jobs = await db.ImportJobs.AsNoTracking().Where(x => x.JobId == poisonId || x.JobId == goodId).ToListAsync();
        Assert.StartsWith("archive:database-archive:", jobs.Single(x => x.JobId == goodId).OriginalFileReference);
        var poison = jobs.Single(x => x.JobId == poisonId);
        Assert.StartsWith("database:v1:", poison.OriginalFileReference);
        Assert.Null(poison.OriginalFileCleanupReference);
    }

    [DatabaseFact]
    public async Task Old_style_folder_and_flagged_database_archives_move_to_named_store()
    {
        var tenant = await fixture.NewTenantAsync("arclegacy");
        using var scope = fixture.ScopeFor(tenant);
        await ConfigureAsync(scope, TenantImportFileSettings.Database, "folder-archive");
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var tenantPart = tenant.Value.ToString("N");
        var folderJob = Guid.NewGuid();
        var folderBytes = Encoding.UTF8.GetBytes("old in-root archive");
        var relative = Path.Combine(tenantPart, "archive", "2026", "09", $"{folderJob:N}-original.csv");
        var path = Path.Combine(SerialisationHostFixture.ImportFileRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, folderBytes);
        var folderReference = "file:v1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(relative));
        var oldFolderJob = Job(tenant, folderJob, folderBytes, folderReference, folderBytes.LongLength);
        oldFolderJob.OriginalArchived = true;
        db.ImportJobs.Add(oldFolderJob);

        var databaseJob = Guid.NewGuid();
        var databaseBytes = Encoding.UTF8.GetBytes("old flagged database archive");
        var fileDb = scope.ServiceProvider.GetRequiredService<ImportFilesDbContext>();
        var fileId = Guid.NewGuid();
        fileDb.Files.Add(new StoredImportFileRow { TenantId = tenant, FileId = fileId, JobId = databaseJob,
            CopyKind = ImportFileCopyKind.Original, FileName = "flagged.csv", Content = databaseBytes,
            ContentLength = databaseBytes.LongLength, Sha256 = Hash(databaseBytes), StoredAt = At, ArchivedAt = At.AddDays(1) });
        var oldDatabaseJob = Job(tenant, databaseJob, databaseBytes, $"database:v1:{fileId:N}", databaseBytes.LongLength);
        oldDatabaseJob.OriginalArchived = true;
        db.ImportJobs.Add(oldDatabaseJob);
        await fileDb.SaveChangesAsync(); await db.SaveChangesAsync();

        var result = await scope.ServiceProvider.GetRequiredService<ImportFileMaintenance>().RunBatchAsync(CancellationToken.None);
        Assert.Equal(2, result.ArchivedCopies);
        Assert.False(File.Exists(path));
        Assert.False(await fileDb.Files.AnyAsync(x => x.JobId == databaseJob));
        Assert.All(await db.ImportJobs.AsNoTracking().Where(x => x.JobId == folderJob || x.JobId == databaseJob).ToListAsync(),
            row => Assert.StartsWith("archive:folder-archive:file:", row.OriginalFileReference));
    }

    [DatabaseFact]
    public async Task Archive_database_is_tenant_isolated()
    {
        var tenant = await fixture.NewTenantAsync("arciso");
        var other = await fixture.NewTenantAsync("arciso2");
        var jobId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("tenant owned archive");
        string reference;
        using (var scope = fixture.ScopeFor(tenant))
        {
            await ConfigureAsync(scope, TenantImportFileSettings.Database, "database-archive");
            var store = scope.ServiceProvider.GetRequiredService<IImportFileStore>();
            await using var input = new MemoryStream(bytes);
            var stored = await store.PutAsync(jobId, "tenant.csv", ImportFileCopyKind.Original, input, At, CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.ImportJobs.Add(Job(tenant, jobId, bytes, stored.Reference, stored.ContentLength)); await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ImportFileMaintenance>().RunBatchAsync(CancellationToken.None);
            reference = (await db.ImportJobs.AsNoTracking().SingleAsync(x => x.JobId == jobId)).OriginalFileReference!;
        }
        using (var scope = fixture.ScopeFor(other))
        {
            var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() =>
                scope.ServiceProvider.GetRequiredService<IImportFileStore>().OpenReadAsync(reference, CancellationToken.None));
            Assert.Equal("import_file_missing", refusal.Rule);
        }
    }

    private static ImportJob Job(TenantId tenant, Guid jobId, byte[] bytes, string reference, long length) => new()
    {
        TenantId = tenant, JobId = jobId, CommandId = jobId, Source = ImportSource.UserUpload,
        FileName = "archive.csv", OriginalHash = Hash(bytes), Format = ImportFormat.Csv, Mode = ImportMode.Compare,
        Status = ImportJobStatus.Compared, ActorRef = "supervisor", RecordedTime = At,
        OriginalFileReference = reference, OriginalLength = length,
    };

    private static async Task ConfigureAsync(IServiceScope scope, string active, string archive)
    {
        var settings = scope.ServiceProvider.GetRequiredService<TenantImportFileSettings>();
        var current = await settings.GetAsync(CancellationToken.None);
        await settings.SetAsync(new(active, archive, 1, ImportFileArchiveAgeUnit.Days, current.Version), CancellationToken.None);
        var saved = await settings.GetAsync(CancellationToken.None);
        Assert.Equal((active, archive, 1, ImportFileArchiveAgeUnit.Days),
            (saved.Storage, saved.ArchiveStore, saved.ArchiveAge, saved.ArchiveAgeUnit));
    }

    private static async Task AssertActiveGoneAsync(IServiceScope scope, string active, Guid jobId)
    {
        if (active == TenantImportFileSettings.Database)
            Assert.False(await scope.ServiceProvider.GetRequiredService<ImportFilesDbContext>().Files.AnyAsync(x => x.JobId == jobId));
        else
            Assert.Empty(Directory.Exists(SerialisationHostFixture.ImportFileRoot)
                ? Directory.EnumerateFiles(SerialisationHostFixture.ImportFileRoot, $"{jobId:N}-*", SearchOption.AllDirectories)
                : Enumerable.Empty<string>());
    }

    private static async Task<int> ArchiveDatabaseCountAsync(IServiceScope scope, Guid jobId)
    {
        var options = scope.ServiceProvider.GetRequiredService<ImportFileStorageOptions>();
        var store = options.ArchiveStores.Single(x => x.Name == "database-archive");
        var dbOptions = new DbContextOptionsBuilder<ImportFilesDbContext>().UseSqlServer(store.ConnectionString!, sql =>
            sql.MigrationsHistoryTable("__migrations", ImportFilesDbContext.SchemaName)).Options;
        await using var db = new ImportFilesDbContext(dbOptions, scope.ServiceProvider.GetRequiredService<ITenantContext>());
        return await db.Files.CountAsync(x => x.JobId == jobId);
    }

    private async Task<RunImportResult> RunAsync(TenantId tenant, RunImport command)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<ImportService>().RunAsync(command with { ActorRef = "supervisor" }, CancellationToken.None);
    }

    private async Task<ImportJob> StoredJobAsync(TenantId tenant, Guid jobId)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().ImportJobs.AsNoTracking()
            .SingleAsync(row => row.JobId == jobId);
    }

    private RunImport CompareIntake(string fileName, IReadOnlyList<string> values, DocumentRef order) =>
        new(Guid.NewGuid(), ImportSource.UserUpload,
            new(fileName, ImportFormat.Csv, Encoding.UTF8.GetBytes("Serial\r\n" + string.Join("\r\n", values) + "\r\n")),
            At.AddHours(1), Entity, "ignored", order, new("Serial"), Mode: ImportMode.Compare, CommittedBy: Committer());

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private SerialSchemeDefinition RegisterScheme(Guid item)
    {
        var code = "IM" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var scheme = new SerialSchemeDefinition(code, IdentifierType.VendorSerial, code, null, "-", 4);
        fixture.Services.GetRequiredService<InMemorySerialSchemes>().Register(scheme);
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(item) with { SerialScheme = code });
        return scheme;
    }

    private async Task ProvisionalUnitsAsync(TenantId tenant, Guid item, DocumentRef order, int count)
    {
        var prefix = "P" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var lines = Enumerable.Range(1, count).Select(index => new CommissionLine(
            [new IdentifierInput(IdentifierType.Provisional, $"{prefix}-{index:0000}")])).ToList();
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item,
            UnitOrigin.Manufactured, lines, At, "line", Entity, Document: order));
        Assert.Equal(count, result.Commissioned);
    }

    private async Task<byte[]> XlsxAsync(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        await using var output = new MemoryStream();
        await new TabularExporter().WriteAsync("xlsx", new TabularExport(headers, Rows(rows)), output, CancellationToken.None);
        return output.ToArray();
    }

    private static async IAsyncEnumerable<IReadOnlyList<string?>> Rows(IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        foreach (var row in rows) yield return row;
        await Task.CompletedTask;
    }

    private static CorrectionCommitter Committer() => new("supervisor", [YojanaRole.ProductionSupervisor]);

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private sealed class FakeLegacySpreadsheetReader(string serial, byte[] converted) : ISpreadsheetReader
    {
        public bool CanRead(string format) => format == ImportFormat.Xls;

        public Task<SpreadsheetReadResult> ReadAsync(Stream input, CancellationToken cancellationToken) =>
            Task.FromResult(new SpreadsheetReadResult([
                new("Legacy", 1, [new("Serial")]),
                new("Legacy", 2, [new(serial)]),
            ], converted));
    }
}
