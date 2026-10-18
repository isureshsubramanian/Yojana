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
        Assert.NotNull(job.OriginalArchiveReference);
        Assert.Null(job.InboxReference);
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
        Assert.NotNull(job.OriginalArchiveReference);
        Assert.Null(job.ConvertedHash);
        Assert.Contains(fixture.Services.GetServices<ISpreadsheetReader>(), reader => reader.CanRead(ImportFormat.Xls));
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
        Assert.NotNull(job.OriginalArchiveReference);
        Assert.NotNull(job.ConvertedArchiveReference);
        Assert.Null(job.InboxReference);
        var stored = await services.GetRequiredService<SerialisationDbContext>().ImportJobs.SingleAsync(row => row.JobId == job.JobId);
        Assert.Equal(converted, stored.ConvertedContent);
        Assert.NotNull(stored.OriginalContent);
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

        var split = await RunAsync(tenant, new RunImport(Guid.NewGuid(), ImportSource.VendorAsn,
            new("multi-order.tsv", ImportFormat.Csv, splitContent), At.AddHours(1), Entity, "ignored",
            Mapping: new("Serial", "Order Number"), MappingName: "vendor-tab", CommittedBy: Committer()));
        Assert.Equal(2, split.Jobs.Count);
        Assert.Contains(split.Jobs, job => job.Target == orderA && job.TargetName == numberA && job.Applied == 1);
        Assert.Contains(split.Jobs, job => job.Target == orderB && job.TargetName == numberB && job.Applied == 1);
        Assert.All(split.Jobs, job => Assert.NotNull(job.OriginalArchiveReference));

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
        Assert.Equal(1, compared.OnlyInA);
        Assert.Equal(1, compared.OnlyInB);
        Assert.Equal(2, compared.Both);
        Assert.Equal(2, compared.Duplicates);
        Assert.Equal(1, compared.ExistingCollisions);
        var collided = Assert.Single(compared.Rows, row => row.ComparisonPartition == ImportComparisonPartition.ExistingCollision);
        Assert.Equal("Harbour Repair Cooperative", collided.CollisionOwnerName);
        Assert.NotNull(collided.CollisionUnitName);

        var commonFromA = Assert.Single(compared.Rows, row => row.RawValue == scheme.Format(0, 2) && row.Rule == "comparison_a");
        var applied = await InvokeAsync<ApplyImportReviewResult>(tenant, new ApplyImportReview(Guid.NewGuid(), compared.ComparisonJobId,
            [new(commonFromA.RowResultId, ImportReviewAction.Assign)], At.AddHours(2), Entity, "reviewer", Committer()));
        Assert.Equal(2, applied.Assigned);
        Assert.Equal(5, applied.Skipped);

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
    }

    private async Task<RunImportResult> RunAsync(TenantId tenant, RunImport command)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<ImportService>().RunAsync(command with { ActorRef = "supervisor" }, CancellationToken.None);
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
