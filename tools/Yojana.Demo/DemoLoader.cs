using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Wolverine;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Seeding;
using Yojana.Platform.Security;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Jurisdictions;
using Yojana.Serialisation.Imports;
using Yojana.Serialisation.Portal;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Queries;
using Yojana.Serialisation.Refurbishments;
using Yojana.Serialisation.Replacements;
using Yojana.Serialisation.TimeZones;

namespace Yojana.Demo;

public sealed class DemoLoader(IServiceProvider services, DemoCatalogue catalogue, DateOnly today)
{
    private const string Source = "demo.it-hardware.connector";
    private readonly TenantId tenant = DevelopmentSeeder.DevTenantId;
    private readonly InMemoryDocumentPorts documents = services.GetRequiredService<InMemoryDocumentPorts>();
    private readonly InMemoryWarrantyPorts warranties = services.GetRequiredService<InMemoryWarrantyPorts>();
    private readonly Dictionary<string, List<DemoUnit>> units = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> policies = new(StringComparer.Ordinal);
    private string actor = "demo-loader";
    private int assembled;
    private int claimsCreated;
    private int recoveriesApproved;
    private int p5ClaimsCreated;
    private CertificateDrainCounts lastCertificateDrain;
    private DemoUnit? recoveryDimm;
    private DemoUnit? recoveryNvme;
    private DemoUnit? expiredPsu;
    private Guid activeRecallCampaignId;
    private Guid endedGoodwillCampaignId;
    private static readonly Dictionary<string, int> ExpectedUnitCounts = new(StringComparer.Ordinal)
    {
        ["RS-1100"] = 36, ["RT-1200T"] = 1, ["RT-N1"] = 2, ["RS-1520"] = 2, ["RG-4800"] = 1,
        ["SS-1110"] = 2, ["SS-1112"] = 2, ["NX-4850"] = 4, ["NR-220"] = 3, ["DW-300"] = 48,
        ["LB-14"] = 8, ["PF-400"] = 5, ["MD-64R"] = 172, ["ND-384"] = 96, ["HD-20T"] = 28,
        ["PS-1600"] = 92, ["BM-2"] = 45, ["GX-80"] = 8,
    };
    private static readonly Dictionary<string, string> SerialPrefixes = new(StringComparer.Ordinal)
    {
        ["RS-1100"] = "01", ["RT-1200T"] = "02", ["RT-N1"] = "03", ["RS-1520"] = "04",
        ["RG-4800"] = "05", ["SS-1110"] = "06", ["SS-1112"] = "07", ["NX-4850"] = "08",
        ["NR-220"] = "09", ["DW-300"] = "10", ["LB-14"] = "11", ["PF-400"] = "12",
        ["MD-64R"] = "13", ["ND-384"] = "14", ["HD-20T"] = "15", ["PS-1600"] = "16",
        ["BM-2"] = "17", ["GX-80"] = "18", ["AD-90"] = "19", ["AC-65"] = "20",
    };

    private Guid LegalEntityId => Party("LE-ASTERRA").Id;
    private Guid UkLegalEntityId => Party("LE-ASTERRA-UK").Id;
    private Guid ServiceEntityId => Party("S-ASTERRA").Id;
    private Guid WarehouseId => Location("LDS-WA-01").Id;
    private Guid QuarantineId => Location("LDS-Q1").Id;
    private Guid QuarantineOverflowId => Location("LDS-Q2").Id;
    private Guid RepairLocationId => Location("LDS-RB-01").Id;
    private Guid AffectedPsuLotId => DemoIds.FromName("lot:KPL-2511");
    private Guid OrdinaryPsuLotId => DemoIds.FromName("lot:KPL-2601");
    private Guid OldPsuLotId => DemoIds.FromName("lot:KPL-2410");
    private DocumentRef ProductionOrder => Document("MO-2026-0901", "production_order");

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await StepAsync("verify the DEV tenant and configure currencies", SetUpTenantAsync, cancellationToken);
        await StepAsync("import starter jurisdiction data", ImportJurisdictionStarterAsync, cancellationToken);
        await StepAsync("define and publish warranty policies", SetUpPoliciesAsync, cancellationToken);
        await StepAsync("configure return and refurbishment policies", SetUpReturnPoliciesAsync, cancellationToken);
        await StepAsync("post supplier receipts and create inbound cover", PostReceiptsAsync, cancellationToken);
        await StepAsync("commission manufactured units", CommissionManufacturedAsync, cancellationToken);
        await StepAsync("resolve the IT-hardware serial plan", ResolveUnitsAsync, cancellationToken);
        await StepAsync("assemble and release finished goods", AssembleAndReleaseAsync, cancellationToken);
        await StepAsync("record PSU and BMC firmware history", RecordFirmwareAsync, cancellationToken);
        await StepAsync("post shipments, deliveries and invoices", PostSalesAsync, cancellationToken);
        await StepAsync("create outbound entitlements", CreateOutboundEntitlementsAsync, cancellationToken);
        await StepAsync("drain entitlement certificate notifications", WaitForAutomaticCertificatesAndReportAsync, cancellationToken);
        await StepAsync("create recall and goodwill campaigns", CreateCampaignsAsync, cancellationToken);
        await StepAsync("submit and progress 27 claims", CreateClaimsAsync, cancellationToken);
        await StepAsync("load RMA, replacement and refurbishment stories", CreatePhaseFiveScenariosAsync, cancellationToken);
        await StepAsync("finish automatic release and warranty certificates", WaitForAutomaticCertificatesAndReportAsync, cancellationToken);
        await StepAsync("load certificate and label stories", CreatePhaseSixScenariosAsync, cancellationToken);
        await StepAsync("load import, reconciliation and portal stories", CreatePhaseSevenScenariosAsync, cancellationToken);
        await PrintSummaryAsync(cancellationToken);
    }

    private async Task CreatePhaseSevenScenariosAsync(CancellationToken cancellationToken)
    {
        await CreatePortalUserAsync(cancellationToken);
        var importJobs = await CreateImportScenariosAsync(cancellationToken);
        await InvokeAsync<CompareImportsResult>(new CompareImports(
            DemoIds.FromName("p7:compare:vendor-clean"), importJobs.Vendor, importJobs.Clean, null, actor),
            cancellationToken);
        await CreateCustomerReconciliationAsync(cancellationToken);
        await CreateThreePlFeedAsync(cancellationToken);
        await ConfigureDisclosurePolicyAsync(cancellationToken);
    }

    private async Task CreatePortalUserAsync(CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var admin = await db.Users.AsNoTracking().SingleAsync(
            user => user.Email == $"admin@{DevelopmentSeeder.EmailDomain}", cancellationToken);
        const string email = "portal.nandira@dev.yojana.local";
        var user = await db.Users.Include(row => row.Memberships)
            .SingleOrDefaultAsync(row => row.Email == email, cancellationToken);
        if (user is null)
        {
            user = new AppUser
            {
                Id = DemoIds.FromName("user:portal:nandira"),
                Email = email,
                DisplayName = "Asha Mehra",
                PasswordHash = admin.PasswordHash,
                CreatedAt = DateTime.UtcNow,
            };
            db.Users.Add(user);
        }
        else
        {
            user.DisplayName = "Asha Mehra";
            user.PasswordHash = admin.PasswordHash;
            user.IsActive = true;
        }

        var membership = user.Memberships.SingleOrDefault(row => row.TenantId == tenant);
        if (membership is null)
            user.Memberships.Add(new UserTenantMembership
            {
                UserId = user.Id,
                TenantId = tenant,
                CustomerPartyRef = Party("C-NANDIRA").Id,
            });
        else
        {
            membership.CustomerPartyRef = Party("C-NANDIRA").Id;
            membership.Roles = [];
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<P7ImportJobs> CreateImportScenariosAsync(CancellationToken cancellationToken)
    {
        var eventTime = DemoDates.At(today.AddDays(-6), 9);
        var cleanValues = new[] { Units("RS-1100")[0].Serial, Units("DW-300")[0].Serial, "P7CLEAN0001" };
        var clean = await InvokeAsync<RunImportResult>(new RunImport(
            DemoIds.FromName("p7:import:clean"), ImportSource.UserUpload,
            new ImportFile("clean-customer-list.csv", ImportFormat.Csv,
                Encoding.UTF8.GetBytes("Serial\r\n" + string.Join("\r\n", cleanValues) + "\r\n")),
            eventTime, LegalEntityId, actor, ProductionOrder, new ImportColumnMapping("Serial"),
            "p7-clean", ImportMode.Compare), cancellationToken);

        var rejectedXlsx = await XlsxWithNumericIdentifierAsync(Units("RS-1100")[1].Serial,
            cancellationToken);
        var spreadsheet = await InvokeAsync<RunImportResult>(new RunImport(
            DemoIds.FromName("p7:import:xlsx-rejected"), ImportSource.UserUpload,
            new ImportFile("installation-register.xlsx", ImportFormat.Xlsx, rejectedXlsx),
            eventTime.AddMinutes(5), LegalEntityId, actor, ProductionOrder, new ImportColumnMapping("Serial"),
            "p7-spreadsheet", ImportMode.Compare), cancellationToken);

        var unique = new List<string>
        {
            Units("RS-1100")[0].Serial,
            Units("DW-300")[0].Serial,
            Units("DW-300")[6].Serial,
        };
        unique.AddRange(Enumerable.Range(1, 97).Select(index => $"P7VENDOR{index:D4}"));
        var vendorValues = unique.Concat(unique).ToList();
        var vendor = await InvokeAsync<RunImportResult>(new RunImport(
            DemoIds.FromName("p7:import:vendor-200"), ImportSource.VendorAsn,
            new ImportFile("vendor-serials-200.csv", ImportFormat.Csv,
                Encoding.UTF8.GetBytes("Serial\r\n" + string.Join("\r\n", vendorValues) + "\r\n")),
            eventTime.AddMinutes(10), LegalEntityId, actor, ProductionOrder, new ImportColumnMapping("Serial"),
            "p7-vendor", ImportMode.Compare), cancellationToken);
        await CreateArchivedLegacySpreadsheetHistoryAsync(eventTime.AddMinutes(15), cancellationToken);

        var cleanJob = clean.Jobs.Single();
        var spreadsheetJob = spreadsheet.Jobs.Single();
        var vendorJob = vendor.Jobs.Single();
        if (spreadsheetJob.Rejected == 0)
            throw new InvalidOperationException("the spreadsheet demo must retain at least one rejected row");
        if (vendorJob.Rows.Count != 200 || vendorJob.Rows.Select(row => row.NormalisedValue).Distinct().Count() != 100)
            throw new InvalidOperationException("the vendor demo must contain 200 rows and 100 unique values");
        return new(cleanJob.JobId, spreadsheetJob.JobId, vendorJob.JobId);
    }

    private async Task CreateArchivedLegacySpreadsheetHistoryAsync(DateTime eventTime, CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var jobId = DemoIds.FromName("p7:import:legacy-field-register");
        if (await db.ImportJobs.AnyAsync(row => row.JobId == jobId, cancellationToken)) return;
        // Older than the two-year default archive age, so the demo shows a file already moved to the archive.
        var archiveTime = eventTime.AddYears(-3);
        var original = Encoding.UTF8.GetBytes("Fictional legacy spreadsheet bytes for download history");
        var converted = await XlsxAsync(["Serial"], [[Units("RS-1100")[2].Serial], [Units("RS-1100")[3].Serial]], cancellationToken);
        var files = scope.ServiceProvider.GetRequiredService<IImportFileStore>();
        await using var originalStream = new MemoryStream(original, writable: false);
        await using var convertedStream = new MemoryStream(converted, writable: false);
        var storedOriginal = await files.PutAsync(jobId, "field-register.xls", ImportFileCopyKind.Original,
            originalStream, archiveTime, cancellationToken);
        var storedConverted = await files.PutAsync(jobId, "field-register.xlsx", ImportFileCopyKind.Converted,
            convertedStream, archiveTime, cancellationToken);
        db.ImportJobs.Add(new ImportJob
        {
            TenantId = tenant, JobId = jobId, CommandId = jobId, Source = ImportSource.UserUpload,
            FileName = "field-register.xls", OriginalHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(),
            ConvertedHash = Convert.ToHexString(SHA256.HashData(converted)).ToLowerInvariant(), Format = ImportFormat.Xls,
            Mode = ImportMode.Compare, Status = ImportJobStatus.Compared, AppliedCount = 2, ActorRef = actor,
            RecordedTime = archiveTime, CompletedTime = archiveTime.AddMinutes(1),
            OriginalFileReference = storedOriginal.Reference, ConvertedFileReference = storedConverted.Reference,
            OriginalLength = storedOriginal.ContentLength, ConvertedLength = storedConverted.ContentLength,
            OriginalArchived = false, ConvertedArchived = false,
        });
        await db.SaveChangesAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<ImportFileMaintenance>().RunBatchAsync(cancellationToken);
        var archived = await db.ImportJobs.AsNoTracking().SingleAsync(row => row.JobId == jobId, cancellationToken);
        if (!archived.OriginalArchived || !archived.OriginalFileReference!.StartsWith("archive:", StringComparison.Ordinal))
            throw new InvalidOperationException("the legacy spreadsheet demo must be moved into the configured archive store");
    }

    private async Task CreateCustomerReconciliationAsync(CancellationToken cancellationToken)
    {
        var customer = Party("C-NANDIRA");
        var owned = Units("RS-1100")[0];
        var verify = Units("DW-300")[0];
        var reject = Units("DW-300")[6];
        var reconciliation = await InvokeAsync<CustomerListReconciliationPage>(new ReconcileCustomerList(
            DemoIds.FromName("p7:reconciliation:nandira"), customer.Id,
            new CustomerListSource(Pasted:
            [
                new CustomerListInput(owned.Serial, IdentifierType.InternalSerial, "PO-NANDIRA-2026", "SYSTEM-01"),
                new CustomerListInput(verify.Serial, IdentifierType.InternalSerial, "PO-NANDIRA-USED", "DESKTOP-USED-01"),
                new CustomerListInput(reject.Serial, IdentifierType.InternalSerial, "PO-NANDIRA-QUERY", "DESKTOP-QUERY-01"),
                new CustomerListInput("P7-NOT-RECORDED", IdentifierType.InternalSerial),
            ]), DemoDates.At(today.AddDays(-4), 9), actor, 20), cancellationToken);
        if (reconciliation.Owned != 1 || reconciliation.OwnedByAnotherParty != 2 || reconciliation.NotFound != 1)
            throw new InvalidOperationException("the reconciliation demo must show owned, other-owner and not-found outcomes");

        var verifiedAssertion = reconciliation.Items.Single(row => row.Input == verify.Serial).AssertionId!.Value;
        var rejectedAssertion = reconciliation.Items.Single(row => row.Input == reject.Serial).AssertionId!.Value;
        await InvokeAsync<CustomerAssertionDecision>(new VerifyCustomerAssertion(
            DemoIds.FromName("p7:assertion:verify"), verifiedAssertion, "used-equipment-delivery:NAN-2026-014",
            DemoDates.At(today.AddDays(-3), 11), LegalEntityId, actor, [YojanaRole.SerialAdministrator]), cancellationToken);
        await InvokeAsync<CustomerAssertionDecision>(new RejectCustomerAssertion(
            DemoIds.FromName("p7:assertion:reject"), rejectedAssertion,
            "The supplied delivery schedule names a different asset.", actor,
            [YojanaRole.OrderDeskSupervisor]), cancellationToken);
    }

    private async Task CreateThreePlFeedAsync(CancellationToken cancellationToken)
    {
        var unit = Units("DW-300")[40];
        var postedAt = DemoDates.At(today.AddDays(-2), 8);
        var shipmentRef = new DocumentRef("shipment", DemoIds.FromName("p7:shipment:3pl"));
        var shipment = new ShipmentDocument(shipmentRef, Party("C-NANDIRA").Id, postedAt,
            [new DocumentLine(1, Item("DW-300").Id, 1, [])], DemoIds.FromName("site:C-NANDIRA"),
            Party("L-PARCELORA").Id, CustomerPoRef: "PO-NANDIRA-3PL-01");
        documents.Add(shipment);
        await InvokeAsync<ConsumedEventResult>(new SalesShipmentPosted(
            Envelope("p7:shipment:3pl", postedAt), shipmentRef), cancellationToken);
        var result = await InvokeAsync<ReceiveThreePlFeedResult>(new ReceiveThreePlFeed(
            DemoIds.FromName("p7:feed:3pl"), "PCL-3PL-2026-0042", shipmentRef, 1,
            postedAt.AddHours(3), [new ThreePlFeedUnit(unit.Serial, CustomerAssetTag: "NAN-ASSET-0042")],
            actor, LegalEntityId), cancellationToken);
        if (result.Shipped != 1 || result.AssetTagsAssigned != 1 || result.Rejected != 0)
            throw new InvalidOperationException("the 3PL demo must ship one unit and capture its customer asset tag");
    }

    private async Task ConfigureDisclosurePolicyAsync(CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        var policy = scope.ServiceProvider.GetRequiredService<DisclosurePolicyService>();
        var current = await policy.GetAsync(cancellationToken);
        var changed = current.Fields.Select(row => row.Field == PortalDisclosureField.ItemName
            ? row with { Tier = DisclosureTier.Owner }
            : row).ToList();
        await policy.SetFieldsAsync(new UpdateDisclosureFields(changed), actor, cancellationToken);
    }

    private static async Task<byte[]> XlsxAsync(IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string?>> rows, CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        await new TabularExporter().WriteAsync(ImportFormat.Xlsx,
            new TabularExport(headers, Rows(rows)), output, cancellationToken);
        return output.ToArray();
    }

    private static async Task<byte[]> XlsxWithNumericIdentifierAsync(string validSerial,
        CancellationToken cancellationToken)
    {
        var bytes = await XlsxAsync(["Serial"], [[validSerial], ["42"]], cancellationToken);
        using var stream = new MemoryStream();
        await stream.WriteAsync(bytes, cancellationToken);
        stream.Position = 0;
        using (var document = SpreadsheetDocument.Open(stream, true))
        {
            var sheet = document.WorkbookPart!.WorksheetParts.Single().Worksheet
                ?? throw new InvalidOperationException("the generated spreadsheet has no worksheet");
            var cell = sheet.Descendants<Row>().ElementAt(2).Elements<Cell>().First();
            cell.DataType = CellValues.Number;
            cell.CellValue = new CellValue("42");
            sheet.Save();
        }
        return stream.ToArray();
    }

    private static async IAsyncEnumerable<IReadOnlyList<string?>> Rows(
        IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        foreach (var row in rows) yield return row;
        await Task.CompletedTask;
    }

    private async Task ImportJurisdictionStarterAsync(CancellationToken cancellationToken)
    {
        Guid packId;
        using (var scope = TenantScope())
        {
            var catalogue = await scope.ServiceProvider.GetRequiredService<JurisdictionCatalogueService>()
                .CatalogueAsync(null, null, cancellationToken);
            var pack = catalogue.RulePacks.Single(row => row.Code == "eu-starter");
            if (pack.Imported) return;
            packId = pack.RulePackId;
        }

        // The demo deliberately opts into starter data so its EU behaviour remains visible and reproducible.
        await InvokeAsync<RulePackImported>(new ImportRulePack(DemoIds.FromName("jurisdiction-pack:eu-starter"),
            packId, actor), cancellationToken);
    }

    private async Task WaitForAutomaticCertificatesAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            using var scope = TenantScope();
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var issueSettings = await scope.ServiceProvider.GetRequiredService<ReleaseRecordIssuePointSettings>()
                .GetAsync(cancellationToken);
            var releasedUnits = await (from unit in db.Units.AsNoTracking()
                                       join assignment in db.ItemAssignments.AsNoTracking()
                                           on new { unit.TenantId, unit.UnitId }
                                           equals new { assignment.TenantId, assignment.UnitId }
                                       where unit.LifecycleStatus == LifecycleStatus.Released && assignment.ValidTo == null
                                       select new { unit.UnitId, assignment.ItemId }).ToListAsync(cancellationToken);
            var built = (await db.BuildRecords.AsNoTracking().Select(row => row.UnitId).Distinct()
                .ToListAsync(cancellationToken)).ToHashSet();
            var sold = (await db.Episodes.AsNoTracking().Where(row => row.Reason == EpisodeReason.Sale
                    && row.PartyType == PartyType.Customer
                    && (row.Kind == EpisodeKind.Ownership
                        || row.Kind == EpisodeKind.Custody && row.LocationKind == LocationKind.CustomerSite))
                .Select(row => row.UnitId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
            var itemClasses = catalogue.Items.Values.ToDictionary(row => row.Id,
                row => row.Serialisation?.ItemClass, EqualityComparer<Guid>.Default);
            var releaseExpected = releasedUnits.Count(row =>
            {
                var issuePoint = ReleaseRecordIssuePointSettings.Resolve(issueSettings,
                    itemClasses.GetValueOrDefault(row.ItemId));
                return issuePoint == ReleaseRecordIssuePoint.AtBuildRelease
                    ? built.Contains(row.UnitId) || sold.Contains(row.UnitId)
                    : issuePoint == ReleaseRecordIssuePoint.AtFirstShipment && sold.Contains(row.UnitId);
            });
            var releaseActual = await db.CertificateInstances.AsNoTracking()
                .Where(row => row.DocumentKind == CertificateDocumentKind.ReleaseRecord
                    && row.Status == CertificateStatus.Issued)
                .Select(row => row.UnitId).Distinct().CountAsync(cancellationToken);
            var warrantyExpected = await (from current in db.Current.AsNoTracking()
                                          join entitlement in db.Entitlements.AsNoTracking()
                                              on new { current.TenantId, current.UnitId }
                                              equals new { entitlement.TenantId, entitlement.UnitId }
                                          where current.LifecycleStatus == LifecycleStatus.Released
                                              && current.OwnerPartyRef != null
                                              && entitlement.Direction == EntitlementDirection.Outbound
                                              && entitlement.Status != EntitlementStatus.Closed
                                          select current.UnitId).Distinct().CountAsync(cancellationToken);
            var warrantyActual = await db.CertificateInstances.AsNoTracking()
                .Where(row => row.DocumentKind == CertificateDocumentKind.WarrantyCertificate
                    && row.Status == CertificateStatus.Issued)
                .Select(row => row.UnitId).Distinct().CountAsync(cancellationToken);
            var coverageEvents = (await db.Events.AsNoTracking()
                    .Where(row => row.EventType.StartsWith("entitlement.") && row.Attributes != null)
                    .Select(row => new { row.EventId, row.Attributes })
                    .ToListAsync(cancellationToken))
                .Where(row => IsCoverageChangedSource(row.Attributes!))
                .Select(row => row.EventId)
                .ToHashSet();
            var coverageReceipts = coverageEvents.Count == 0
                ? 0
                : await db.CertificateTriggers.AsNoTracking()
                    .Where(row => row.DocumentKind == CertificateDocumentKind.WarrantyCertificate
                        && coverageEvents.Contains(row.TriggeringEventId))
                    .Select(row => row.TriggeringEventId).Distinct().CountAsync(cancellationToken);
            if (releaseActual >= releaseExpected && warrantyActual >= warrantyExpected
                && coverageReceipts >= coverageEvents.Count)
            {
                lastCertificateDrain = new(releaseActual, releaseExpected, warrantyActual, warrantyExpected,
                    coverageReceipts, coverageEvents.Count);
                return;
            }
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException($"certificate drain timed out: release {releaseActual}/{releaseExpected}, warranty {warrantyActual}/{warrantyExpected}, coverage notifications {coverageReceipts}/{coverageEvents.Count}");
            await Task.Delay(200, cancellationToken);
        }
    }

    private async Task WaitForAutomaticCertificatesAndReportAsync(CancellationToken cancellationToken)
    {
        await WaitForAutomaticCertificatesAsync(cancellationToken);
        Console.Write($"release {lastCertificateDrain.ReleaseActual}/{lastCertificateDrain.ReleaseExpected}, "
            + $"warranty {lastCertificateDrain.WarrantyActual}/{lastCertificateDrain.WarrantyExpected}, "
            + $"coverage notifications {lastCertificateDrain.CoverageActual}/{lastCertificateDrain.CoverageExpected}; ");
    }

    private readonly record struct CertificateDrainCounts(int ReleaseActual, int ReleaseExpected,
        int WarrantyActual, int WarrantyExpected, int CoverageActual, int CoverageExpected);

    private static bool IsCoverageChangedSource(string attributes)
    {
        using var document = JsonDocument.Parse(attributes);
        var root = document.RootElement;
        return (!root.TryGetProperty("direction", out var direction)
                || !string.Equals(direction.GetString(), EntitlementDirection.Inbound, StringComparison.Ordinal))
            && root.TryGetProperty("coverage_before", out _)
            && root.TryGetProperty("_state", out var state)
            && state.TryGetProperty("live_entitlement_ids", out _);
    }

    private async Task CreatePhaseSixScenariosAsync(CancellationToken cancellationToken)
    {
        var templateIds = await CreateLabelTemplatesAsync(cancellationToken);
        await CreateLabelPrintJobsAsync(templateIds, cancellationToken);
        await CreateTenantCertificateLayoutAsync(cancellationToken);

        var salesOrder = new DocumentRef("sales_order", DemoIds.FromName("order:hpc"));
        var selection = await InvokeAsync<ResolveSelectionSetResult>(new ResolveSelectionSet(
            DemoIds.FromName("certificate-selection:order:hpc"), SelectionSource.ByDocument(salesOrder), actor), cancellationToken);
        var issued = await InvokeAsync<CertificateRunResult>(new IssueCertificate(
            DemoIds.FromName("certificate-issue:order:hpc"), selection.SelectionId,
            CertificateDocumentKind.ReleaseRecord, CertificateFormType.CertificateOfConformity,
            LegalEntityId, "en", "Copy requested for the demo sales order", true, actor), cancellationToken);
        if (issued.Kpis.ReprintAsCopy != selection.Resolved || issued.Kpis.NotIssuable != 0)
            throw new InvalidOperationException($"every shipped sales-order unit needs a Copy; got Copy {issued.Kpis.ReprintAsCopy}/{selection.Resolved}, not issuable {issued.Kpis.NotIssuable}");
    }

    private async Task<Dictionary<string, Guid>> CreateLabelTemplatesAsync(CancellationToken cancellationToken)
    {
        var samples = new Dictionary<string, DemoUnit>(StringComparer.Ordinal)
        {
            ["server"] = Units("RS-1100")[0],
            ["server_node"] = Units("RT-N1")[0],
            ["storage"] = Units("SS-1110")[0],
            ["network"] = Units("NX-4850")[0],
            ["client"] = Units("DW-300")[0],
            ["printer"] = Units("PF-400")[0],
            ["component"] = Units("MD-64R")[0],
        };
        var templateIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (itemClass, sample) in samples)
        {
            var created = await InvokeAsync<LabelTemplateCreated>(new CreateLabelTemplate(
                DemoIds.FromName($"label-template:{itemClass}"),
                $"{ItemFamilyName(itemClass)} asset label",
                LabelTemplateAppliesTo.ItemFamily, itemClass, actor), cancellationToken);
            templateIds[itemClass] = created.TemplateId;
            var previewUnit = sample.UnitId;
            var notice = itemClass == "server" ? "Asterra Works server asset" : "Asterra Works equipment asset";
            var first = await SaveLabelVersionAsync(created.TemplateId, itemClass, 1, notice, previewUnit,
                requireRevision: itemClass == "network", cancellationToken);
            await InvokeAsync<LabelTemplatePreview>(new PreviewLabelTemplate(first.TemplateVersionId, previewUnit, actor), cancellationToken);
            await InvokeAsync<LabelTemplateVersionChanged>(new ActivateLabelTemplateVersion(
                DemoIds.FromName($"label-activate:{itemClass}:v1"), first.TemplateVersionId,
                DemoDates.At(today.AddDays(-199), 9), actor), cancellationToken);
        }

        var serverId = templateIds["server"];
        var serverSample = samples["server"].UnitId;
        var approved = "Tampering may affect warranty coverage; see warranty terms.";
        var second = await SaveLabelVersionAsync(serverId, "server", 2, approved, serverSample, false, cancellationToken);
        await InvokeAsync<LabelTemplatePreview>(new PreviewLabelTemplate(second.TemplateVersionId, serverSample, actor), cancellationToken);
        await InvokeAsync<LabelTemplateVersionChanged>(new ActivateLabelTemplateVersion(
            DemoIds.FromName("label-activate:server:v2"), second.TemplateVersionId,
            DemoDates.At(today.AddDays(-99), 9), actor), cancellationToken);

        var draft = await SaveLabelVersionAsync(serverId, "server", 3,
            "Opening this enclosure will make the warranty void.", serverSample, false, cancellationToken);
        await InvokeAsync<LabelTemplateVersionChanged>(new RequestLabelLegalReview(
            DemoIds.FromName("label-legal-review:server:v3"), draft.TemplateVersionId, actor), cancellationToken);
        return templateIds;
    }

    private static string ItemFamilyName(string code)
    {
        var text = code.Replace('_', ' ');
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private Task<LabelTemplateVersionSaved> SaveLabelVersionAsync(Guid templateId, string itemClass, int version,
        string notice, Guid previewUnit, bool requireRevision, CancellationToken cancellationToken)
    {
        var layout = "^XA^FO24,20^A0N,28,28^FD{{item_code}}^FS^FO24,55^FD{{serial}}^FS"
            + (requireRevision ? "^FO24,90^FDRev {{revision}}^FS" : "")
            + $"^FO24,125^FD{notice}^FS^XZ";
        var bindings = new List<LabelBinding>
        {
            new("item_code", LabelBindableField.ItemCode, true),
            new("serial", LabelBindableField.InternalSerial, true),
        };
        if (requireRevision) bindings.Add(new("revision", LabelBindableField.ItemRevision, true));
        return InvokeAsync<LabelTemplateVersionSaved>(new SaveLabelTemplateDraft(
            DemoIds.FromName($"label-save:{itemClass}:v{version}"), templateId, null,
            DemoDates.At(today.AddDays(version == 1 ? -200 : version == 2 ? -100 : -1), 9), null,
            LabelLayoutKind.Zpl, layout, [notice], bindings, LabelSymbology.Code128,
            previewUnit, false, actor), cancellationToken);
    }

    private async Task CreateLabelPrintJobsAsync(Dictionary<string, Guid> templates,
        CancellationToken cancellationToken)
    {
        var at = DemoDates.At(today, 15);
        var server = await InvokeAsync<LabelPrintResult>(new PrintLabel(DemoIds.FromName("label-print:server"),
            Units("RS-1100")[0].UnitId, templates["server"], null, "line-2", at, LegalEntityId, actor), cancellationToken);
        await InvokeAsync<LabelPrintResult>(new PrintLabel(DemoIds.FromName("label-print:printer"),
            Units("PF-400")[0].UnitId, templates["printer"], null, "line-2", at.AddMinutes(1), LegalEntityId, actor), cancellationToken);
        var rejected = await InvokeAsync<LabelPrintResult>(new PrintLabel(DemoIds.FromName("label-print:network-missing-revision"),
            Units("NX-4850")[1].UnitId, templates["network"], null, "line-2", at.AddMinutes(2), LegalEntityId, actor), cancellationToken);
        if (rejected.Status != LabelPrintStatus.Rejected || rejected.EmptyRequiredField != LabelBindableField.ItemRevision)
            throw new InvalidOperationException("network demo label must be rejected because item.revision is empty");
        await InvokeAsync<LabelPrintResult>(new ReprintLabel(DemoIds.FromName("label-reprint:server"),
            server.JobId, "line-2", at.AddMinutes(3), LegalEntityId, actor), cancellationToken);
    }

    private async Task CreateTenantCertificateLayoutAsync(CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        var templates = scope.ServiceProvider.GetRequiredService<CertificateTemplateService>();
        var catalogue = await templates.ListAsync(cancellationToken);
        var starter = catalogue.Single(row => row.BuiltIn && row.DocumentKind == CertificateDocumentKind.ReleaseRecord
            && row.FormType == CertificateFormType.CertificateOfConformity);
        var existing = catalogue.SingleOrDefault(row => row.Code == "DEMO-COC");
        var detail = existing is null
            ? await templates.CreateAsync(new CreateCertificateTemplateRequest("DEMO-COC", "Asterra Works C of C",
                CertificateDocumentKind.ReleaseRecord, CertificateFormType.CertificateOfConformity, null,
                starter.TemplateId), actor, cancellationToken)
            : await templates.GetAsync(existing.TemplateId, cancellationToken)
                ?? throw new InvalidOperationException("DEMO-COC disappeared while loading the demo");
        var assets = scope.ServiceProvider.GetRequiredService<TenantAssetService>();
        var logo = await assets.UploadLogoAsync(DemoIds.FromName("certificate-logo:asterra"), Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"36\" viewBox=\"0 0 120 36\"><rect width=\"120\" height=\"36\" rx=\"5\" fill=\"#24384f\"/><path d=\"M12 27L24 8l12 19h-7l-5-8-5 8z\" fill=\"#ffffff\"/></svg>"),
            LegalEntityId, actor, "asterra-works-logo.svg", cancellationToken);
        var starterDetail = await templates.GetAsync(starter.TemplateId, cancellationToken)
            ?? throw new InvalidOperationException("The C of C starter disappeared while loading the demo");
        var starterVersion = starterDetail.Versions.OrderByDescending(row => row.Version).First();
        var active = detail.Versions.FirstOrDefault(row => row.Status == CertificateTemplateStatus.Active);
        var assignedAsset = active?.BrandingAssets.GetValueOrDefault(LegalEntityId.ToString());
        if (active?.LayoutBody == starterVersion.LayoutBody && assignedAsset == logo.Asset.AssetId) return;

        var existingDraft = detail.Versions.FirstOrDefault(row => row.Status == CertificateTemplateStatus.Draft);
        var saved = await templates.SaveDraftAsync(detail.TemplateId, new SaveCertificateTemplateDraftRequest(
            existingDraft?.TemplateVersionId, starterVersion.Language, starterVersion.PayloadSchemaVersion, starterVersion.LayoutBody,
            starterVersion.PageSize, starterVersion.Orientation, starterVersion.MarginTopMm, starterVersion.MarginRightMm, starterVersion.MarginBottomMm,
            starterVersion.MarginLeftMm, starterVersion.HeaderBody, starterVersion.FooterBody, null,
            new Dictionary<string, Guid> { [LegalEntityId.ToString()] = logo.Asset.AssetId },
            new Dictionary<string, string>(starterVersion.StaticText, StringComparer.Ordinal)
            {
                ["issuer_display_name"] = "Asterra Works",
                ["issuer_address"] = "14 Meridian Yard · Suryanagar",
                ["issuer_registration_code"] = "AW-LEGAL-01",
            }, starterVersion.Fields), actor, cancellationToken);
        if (saved.Version is null) throw new InvalidOperationException(string.Join("; ", saved.Validation.Errors));
        await templates.ActivateAsync(detail.TemplateId, saved.Version.TemplateVersionId, actor, cancellationToken);
    }

    private async Task SetUpTenantAsync(CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        if (!await db.Tenants.AnyAsync(row => row.Id == tenant, cancellationToken))
            throw new InvalidOperationException("DEV tenant is missing; run 'make seed' before 'make demo'.");
        var admin = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Email == $"admin@{DevelopmentSeeder.EmailDomain}", cancellationToken)
            ?? throw new InvalidOperationException("DEV administrator is missing; run 'make seed' before 'make demo'.");
        actor = admin.Id.ToString();
        var currency = scope.ServiceProvider.GetRequiredService<ITenantCurrency>();
        await currency.SetAsync("EUR", cancellationToken);
        await currency.SetAcceptedAsync(["GBP", "INR", "USD"], cancellationToken);
        var timeZones = scope.ServiceProvider.GetRequiredService<TenantTimeZoneSettings>();
        var currentTimeZones = await timeZones.GetAsync(cancellationToken);
        var expectedOverrides = new[] { new LegalEntityTimeZoneOverride(UkLegalEntityId, "Europe/London") };
        if (currentTimeZones.TenantTimeZone != "Asia/Kolkata"
            || !currentTimeZones.LegalEntityOverrides.SequenceEqual(expectedOverrides))
        {
            await timeZones.SetAsync(new UpdateTenantTimeZoneSettings("Asia/Kolkata", expectedOverrides,
                currentTimeZones.Version), actor, cancellationToken);
        }
        var certificateSettings = scope.ServiceProvider.GetRequiredService<ReleaseRecordIssuePointSettings>();
        var currentCertificateSettings = await certificateSettings.GetAsync(cancellationToken);
        var certificateOverrides = new[]
        {
            new ReleaseRecordIssuePointOverride("server", ReleaseRecordIssuePoint.AtFirstShipment),
            new ReleaseRecordIssuePointOverride("server_node", ReleaseRecordIssuePoint.AtFirstShipment),
        };
        if (currentCertificateSettings.DefaultIssuePoint != ReleaseRecordIssuePoint.AtBuildRelease
            || !currentCertificateSettings.ItemClassOverrides.SequenceEqual(certificateOverrides))
        {
            await certificateSettings.SetAsync(new UpdateReleaseRecordIssuePointSettings(
                ReleaseRecordIssuePoint.AtBuildRelease, certificateOverrides,
                currentCertificateSettings.Version), cancellationToken);
        }
        var serialisation = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var firstReceipt = Document("GR-DIMM-2601", "receipt");
        var existingAnchorFact = await serialisation.Events.AsNoTracking()
            .Where(@event => @event.DocumentType == firstReceipt.Type && @event.DocumentId == firstReceipt.Id
                && @event.EventType == UnitEventType.Commissioned)
            .OrderBy(@event => @event.EventTime).Select(@event => (DateTime?)@event.EventTime).FirstOrDefaultAsync(cancellationToken);
        if (existingAnchorFact is { } receiptAt)
            today = DateOnly.FromDateTime(receiptAt).AddDays(110);
    }

    private async Task SetUpPoliciesAsync(CancellationToken cancellationToken)
    {
        foreach (var family in new[]
        {
            new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance),
            new DefineCoverageFamily("goodwill", "Goodwill cover", false, AssuranceOrService.Assurance),
            new DefineCoverageFamily("service_contract", "Service contract", false, AssuranceOrService.Service),
            new DefineCoverageFamily(CoverageFamilyCode.Supplier, "Supplier warranty", false, AssuranceOrService.Assurance),
        })
        {
            await InvokeAsync<CoverageFamilyRow>(family, cancellationToken);
        }

        await InvokeAsync<ReplacementPolicyRow>(new DefineReplacementPolicy("DEMO-REPLACE-90",
            ReplacementRule.MaxRemainingOrMinDays, 90, null, "Keep remaining cover or grant at least 90 days"), cancellationToken);
        await InvokeAsync<ReplacementPolicyRow>(new DefineReplacementPolicy("DEMO-PRESERVE",
            ReplacementRule.CarryRemaining, null, null, "Keep the original start date and term"), cancellationToken);

        var definitions = new[]
        {
            Policy("DEMO-SERVER-36", "Server 36-month on-site NBD", CoverageFamilyCode.Base, 36, "on_site", travel: 100, responseHours: 8),
            Policy("DEMO-STORAGE-60", "Storage 60-month on-site", CoverageFamilyCode.Base, 60, "on_site", travel: 100, responseHours: 8),
            Policy("DEMO-NETWORK-60", "Network 60-month return-to-base", CoverageFamilyCode.Base, 60, "return_to_base"),
            Policy("DEMO-CLIENT-12", "Desktop and laptop 12-month return-to-base", CoverageFamilyCode.Base, 12, "return_to_base", regions: ["IN", "EU", "UK"]),
            Policy("DEMO-PRINTER-12H", "Printer 12-month or 2,000 print-hour cover", CoverageFamilyCode.Base, 12, "return_to_base",
                durationBasis: "both", usage: [new UsageLimitTerm("print_hours", 2000)]),
            Policy("DEMO-ACCESSORY-12", "Accessory 12-month proof-of-purchase cover", CoverageFamilyCode.Base, 12, "return_to_base", type: EntitlementType.Dealer),
            Policy("DEMO-GOODWILL-12", "Goodwill repair authority", "goodwill", 12, "return_to_base", type: EntitlementType.Goodwill),
            Policy("DEMO-NETWORK-CONTRACT", "Managed network service contract", "service_contract", 24, "on_site", travel: 100,
                responseHours: 4, type: EntitlementType.Contract, assurance: AssuranceOrService.Service),
            SupplierPolicy("DEMO-SUP-DIMM-36", "DIMM supplier 36-month cover", 36),
            SupplierPolicy("DEMO-SUP-NVME-60", "NVMe supplier 60-month cover", 60),
            SupplierPolicy("DEMO-SUP-HDD-60", "HDD supplier 60-month cover", 60),
            SupplierPolicy("DEMO-SUP-PSU-24", "PSU supplier 24-month cover", 24),
            SupplierPolicy("DEMO-SUP-PSU-12-OLD", "Historical PSU supplier 12-month cover", 12),
            SupplierPolicy("DEMO-SUP-GPU-36", "GPU supplier 36-month cover", 36),
        };
        foreach (var definition in definitions)
            policies[definition.Code] = await EnsurePolicyAsync(definition, cancellationToken);

        warranties.Register(new VendorWarrantyTerm(Party("V-MEMRYX").Id, Item("MD-64R").Id, policies["DEMO-SUP-DIMM-36"]));
        warranties.Register(new VendorWarrantyTerm(Party("V-FLASHORA").Id, Item("ND-384").Id, policies["DEMO-SUP-NVME-60"]));
        warranties.Register(new VendorWarrantyTerm(Party("V-DISKHAVEN").Id, Item("HD-20T").Id, policies["DEMO-SUP-HDD-60"]));
        warranties.Register(new VendorWarrantyTerm(Party("V-VOLTARY").Id, Item("PS-1600").Id, policies["DEMO-SUP-PSU-24"]));
        warranties.Register(new VendorWarrantyTerm(Party("V-VOLTARY-HIST").Id, Item("PS-1600").Id, policies["DEMO-SUP-PSU-12-OLD"]));
        warranties.Register(new VendorWarrantyTerm(Party("V-TENSORLOOM").Id, Item("GX-80").Id, policies["DEMO-SUP-GPU-36"]));
    }

    private async Task SetUpReturnPoliciesAsync(CancellationToken cancellationToken)
    {
        foreach (var reason in new[]
        {
            new DefineReturnReason("warranty_defect", "Warranty defect", RestockFeeRule.None),
            new DefineReturnReason("dead_on_arrival", "Dead on arrival", RestockFeeRule.None, DoaWindowDays: 14),
            new DefineReturnReason("seller_error", "Seller error", RestockFeeRule.None),
            new DefineReturnReason("goodwill_exchange", "Goodwill exchange", RestockFeeRule.Percentage, 10m),
            new DefineReturnReason("usage_limit_exceeded", "Usage limit exceeded", RestockFeeRule.None),
        })
        {
            await InvokeAsync<ReturnReasonRow>(reason, cancellationToken);
        }

        using var scope = TenantScope();
        await scope.ServiceProvider.GetRequiredService<RefurbishmentPolicySettings>().SetAsync(new RefurbishmentPolicy(
            RefurbishmentIdentity.SameUnitNewSerial,
            3,
            [
                new RefurbishmentItemClassPolicy("storage", RefurbishmentIdentity.SameUnitNewSerial, 3),
                new RefurbishmentItemClassPolicy("printer", RefurbishmentIdentity.SameUnitSameSerial, 2),
            ],
            "DEMO-REPLACE-90"), cancellationToken);
    }

    private async Task PostReceiptsAsync(CancellationToken cancellationToken)
    {
        await ReceiptAsync("GR-DIMM-2601", Party("V-MEMRYX").Id, today.AddDays(-110),
            [Line("MD-64R", 1, 172)], 172, cancellationToken);
        await ReceiptAsync("GR-NVME-2601", Party("V-FLASHORA").Id, today.AddDays(-106),
            [Line("ND-384", 1, 96)], 96, cancellationToken);
        await ReceiptAsync("GR-HDD-2601", Party("V-DISKHAVEN").Id, today.AddDays(-102),
            [Line("HD-20T", 1, 28)], 28, cancellationToken);
        await ReceiptAsync("GR-PSU-OLD", Party("V-VOLTARY-HIST").Id, today.AddDays(-400),
            [Line("PS-1600", 1, 12, 1, OldPsuLotId)], 12, cancellationToken);
        await ReceiptAsync("GR-PSU-2601", Party("V-VOLTARY").Id, today.AddDays(-98),
            [Line("PS-1600", 1, 12, 13, AffectedPsuLotId), Line("PS-1600", 2, 68, 25, OrdinaryPsuLotId)], 80, cancellationToken);
        await ReceiptAsync("GR-BMC-2601", Party("V-RACKSPIRE").Id, today.AddDays(-96),
            [Line("BM-2", 1, 45)], 0, cancellationToken);
        await ReceiptAsync("GR-GPU-2601", Party("V-TENSORLOOM").Id, today.AddDays(-94),
            [Line("GX-80", 1, 8)], 8, cancellationToken);
        await ReceiptAsync("GR-FINISHED-2601", Party("V-RACKSPIRE").Id, today.AddDays(-90),
        [
            Line("NX-4850", 1, 4), Line("NR-220", 2, 3), Line("DW-300", 3, 48),
            Line("LB-14", 4, 8), Line("PF-400", 5, 5),
        ], 0, cancellationToken);
    }

    private async Task CommissionManufacturedAsync(CancellationToken cancellationToken)
    {
        var plan = new[]
        {
            ("RS-1100", 36), ("RT-1200T", 1), ("RT-N1", 2), ("RS-1520", 2),
            ("RG-4800", 1), ("SS-1110", 2), ("SS-1112", 2),
        };
        var at = DemoDates.At(today.AddDays(-88), 8);
        foreach (var (code, count) in plan)
        {
            var lines = Enumerable.Range(1, count)
                .Select(sequence => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, Serial(code, sequence))], $"{code}-{sequence:D4}"))
                .ToList();
            var result = await InvokeAsync<CommissionUnitsResult>(new CommissionUnits(
                DemoIds.FromName($"commission:{code}"), Item(code).Id, UnitOrigin.Manufactured, lines, at, actor,
                LegalEntityId, Document: ProductionOrder, LocationId: WarehouseId, LocationKind: LocationKind.Warehouse, Source: Source), cancellationToken);
            if (result.Refused > 0)
                throw new InvalidOperationException($"commission {code}: {result.Refused} line(s) were refused");
        }
    }

    private async Task ResolveUnitsAsync(CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var rows = await (from identifier in db.Identifiers.AsNoTracking()
                          join current in db.Current.AsNoTracking() on new { identifier.TenantId, identifier.UnitId } equals new { current.TenantId, current.UnitId }
                          // Resolve by the original deterministic plan even after refurbishment supersedes a serial;
                          // command ids and scenario indexes remain stable across loader replays.
                          where identifier.Type == IdentifierType.InternalSerial
                          select new { identifier.UnitId, identifier.RawValue, current.ItemId, current.LotId })
            .ToListAsync(cancellationToken);
        var codeByItem = catalogue.Items.Values.ToDictionary(item => item.Id, item => item.Code);
        foreach (var row in rows.Where(row => codeByItem.ContainsKey(row.ItemId)))
        {
            var code = codeByItem[row.ItemId];
            if (!ExpectedUnitCounts.TryGetValue(code, out var expectedCount)
                || !Enumerable.Range(1, expectedCount).Any(sequence => Serial(code, sequence) == row.RawValue)) continue;
            if (!units.TryGetValue(code, out var list)) units[code] = list = [];
            list.Add(new DemoUnit(row.UnitId, code, row.RawValue, row.LotId));
        }
        foreach (var list in units.Values) list.Sort((left, right) => string.CompareOrdinal(left.Serial, right.Serial));
        foreach (var (code, count) in ExpectedUnitCounts)
        {
            if (Units(code).Count != count)
                throw new InvalidOperationException($"resolve {code}: expected {count} units, found {Units(code).Count}");
        }
    }

    private async Task AssembleAndReleaseAsync(CancellationToken cancellationToken)
    {
        var dimms = new Queue<DemoUnit>(Units("MD-64R"));
        var nvmes = new Queue<DemoUnit>(Units("ND-384"));
        var hdds = new Queue<DemoUnit>(Units("HD-20T"));
        var bmcs = new Queue<DemoUnit>(Units("BM-2"));
        var gpus = new Queue<DemoUnit>(Units("GX-80"));
        var affected = new Queue<DemoUnit>(Units("PS-1600").Where(unit => unit.LotId == AffectedPsuLotId));
        var ordinary = new Queue<DemoUnit>(Units("PS-1600").Where(unit => unit.LotId == OrdinaryPsuLotId));
        var old = new Queue<DemoUnit>(Units("PS-1600").Where(unit => unit.LotId == OldPsuLotId));
        var assemblyIndex = 0;

        foreach (var (parent, index) in Units("RS-1100").Select((unit, index) => (unit, index)))
        {
            var psus = index < 6 ? Take(affected, 2) : Take(ordinary, 2);
            await AssembleAsync(parent, Components(Take(dimms, 4), "DIMM", Take(nvmes, 2), "NVME", psus, "PSU", Take(bmcs, 1), "BMC"), assemblyIndex++, cancellationToken);
        }

        foreach (var parent in Units("RT-N1"))
        {
            var installedDimms = Take(dimms, 4);
            if (recoveryDimm is null) recoveryDimm = installedDimms[0];
            await AssembleAsync(parent, Components(installedDimms, "DIMM", Take(nvmes, 2), "NVME", Take(ordinary, 2), "PSU", Take(bmcs, 1), "BMC"), assemblyIndex++, cancellationToken);
        }

        await AssembleAsync(Units("RT-1200T")[0], Units("RT-N1").Select((unit, index) => new ChildInput(unit.UnitId, Relation: AssemblyRelation.Kit, Slot: $"NODE-{index + 1}")).ToList(), assemblyIndex++, cancellationToken);
        foreach (var (parent, index) in Units("RS-1520").Select((unit, index) => (unit, index)))
        {
            var psus = index == 0 ? Take(old, 2) : Take(ordinary, 2);
            if (index == 0) expiredPsu = psus[0];
            await AssembleAsync(parent, Components(Take(dimms, 6), "DIMM", Take(nvmes, 2), "NVME", psus, "PSU", Take(bmcs, 1), "BMC"), assemblyIndex++, cancellationToken);
        }

        var gpuPsus = Take(ordinary, 2).Concat(Take(old, 2)).ToList();
        await AssembleAsync(Units("RG-4800")[0], Components(Take(dimms, 8), "DIMM", Take(nvmes, 4), "NVME",
            gpuPsus, "PSU", Take(bmcs, 1), "BMC", Take(gpus, 8), "GPU"), assemblyIndex++, cancellationToken);
        foreach (var parent in Units("SS-1110"))
        {
            var installedNvme = Take(nvmes, 6);
            if (recoveryNvme is null) recoveryNvme = installedNvme[0];
            await AssembleAsync(parent, Components(installedNvme, "BAY", Take(hdds, 6), "BAYH", Take(old, 2), "PSU", Take(bmcs, 1), "BMC"), assemblyIndex++, cancellationToken);
        }
        foreach (var parent in Units("SS-1112"))
            await AssembleAsync(parent, Components(Take(hdds, 8), "BAY", Take(old, 2), "PSU", Take(bmcs, 1), "BMC"), assemblyIndex++, cancellationToken);

        if (new[] { dimms, nvmes, hdds, bmcs, gpus, affected, ordinary, old }.Any(queue => queue.Count != 0))
            throw new InvalidOperationException("assembly plan did not consume every planned component");

        var releaseAt = DemoDates.At(today.AddDays(-82), 10);
        var manufactured = new[] { "RS-1100", "RT-N1", "RT-1200T", "RS-1520", "RG-4800", "SS-1110", "SS-1112" }
            .SelectMany(Units).ToList();
        foreach (var unit in manufactured)
        {
            await InvokeAsync<ReleaseUnitResult>(new ReleaseUnit(DemoIds.FromName($"release:{unit.Serial}"), unit.UnitId,
                releaseAt, actor, LegalEntityId, $"QA-DEMO-{unit.Serial}", ProductionOrder, "2026.10", WarehouseId,
                "{\"result\":\"pass\",\"profile\":\"development-demo\"}"), cancellationToken);
        }
    }

    private async Task RecordFirmwareAsync(CancellationToken cancellationToken)
    {
        var at = DemoDates.At(today.AddDays(-80), 11);
        var affectedParents = Units("RS-1100").Take(6).ToList();
        foreach (var (parent, index) in affectedParents.Select((unit, index) => (unit, index)))
        {
            var value = $"1.2.{index % 3}";
            await AttributeAsync(parent, "psu.firmware", value, at.AddMinutes(index), cancellationToken);
        }
        foreach (var (psu, index) in Units("PS-1600").Where(unit => unit.LotId == AffectedPsuLotId).Select((unit, index) => (unit, index)))
            await AttributeAsync(psu, "psu.firmware", $"1.2.{index % 3}", at.AddMinutes(20 + index), cancellationToken);
        foreach (var (server, index) in Units("RS-1100").Select((unit, index) => (unit, index)))
            await AttributeAsync(server, "bmc.firmware", index < 14 ? "3.4.0" : "3.5.1", at.AddHours(1).AddMinutes(index), cancellationToken);
        await AttributeAsync(Units("NX-4850")[0], "item.revision", "B3", at.AddHours(2), cancellationToken);
    }

    private async Task PostSalesAsync(CancellationToken cancellationToken)
    {
        var sales = SalesPlan();
        foreach (var sale in sales)
        {
            var legalEntityId = LegalEntityFor(sale.Country);
            var shippedAt = DemoDates.At(today.AddDays(-sale.DaysAgo), 9);
            var shipmentRef = Document(sale.ShipmentNumber, "shipment");
            var orderRef = new DocumentRef("sales_order", DemoIds.FromName("order:" + sale.Key));
            var lines = LinesFor(sale.Units);
            var shipment = new ShipmentDocument(shipmentRef, sale.InvoiceParty.Id, shippedAt, lines,
                DemoIds.FromName("site:" + sale.EndOwner.Code), Party("L-PARCELORA").Id, false, orderRef,
                $"PO-{sale.EndOwner.Code}-{today.Year}", IsInvoiced: true);
            documents.Add(shipment);
            var shipmentAlreadyPosted = await HasDocumentEventsAsync(shipmentRef, UnitEventType.Shipped,
                sale.Units, cancellationToken);
            await InvokeAsync<ConsumedEventResult>(new SalesShipmentPosted(
                Envelope("shipment:" + sale.Key, shippedAt, legalEntityId), shipmentRef), cancellationToken);

            var deliveredAt = shippedAt.AddDays(2);
            var deliveryRef = new DocumentRef("delivery_confirmation", DemoIds.FromName("delivery:" + sale.Key));
            documents.Add(new DeliveryConfirmation(deliveryRef, shipmentRef, deliveredAt, shipment.ShipToLocationId));
            await InvokeAsync<ConsumedEventResult>(new SalesDeliveryConfirmed(
                Envelope("delivery:" + sale.Key, deliveredAt, legalEntityId), deliveryRef), cancellationToken);

            var invoiceRef = Document(sale.InvoiceNumber, "invoice");
            var invoiceLines = sale.Units.GroupBy(unit => unit.Code).Select((group, index) =>
                new InvoiceLine(index + 1, Item(group.Key).Id, group.Count(), 1000m + index * 250m, sale.Currency)).ToList();
            documents.Add(new InvoiceDocument(invoiceRef, orderRef, sale.InvoiceParty.Id, legalEntityId, shippedAt.AddDays(1), invoiceLines,
                shipment.CustomerPoRef, sale.Country, Channel: sale.Channel));
            await InvokeAsync<ConsumedEventResult>(new SalesInvoicePosted(
                Envelope("invoice:" + sale.Key, shippedAt.AddDays(1), legalEntityId), invoiceRef), cancellationToken);

            if (!shipmentAlreadyPosted)
            {
                await WaitForCurrentOwnerAsync(sale.Units, sale.InvoiceParty.Id, sale.EndOwner.Id, cancellationToken);
                await WaitForEntitlementRequirementsAsync(sale.Units, cancellationToken);
                if (sale.InvoiceParty.Id != sale.EndOwner.Id)
                {
                    foreach (var unit in sale.Units)
                    {
                        await InvokeAsync<OpenOwnershipResult>(new OpenOwnership(DemoIds.FromName($"end-owner:{sale.Key}:{unit.Serial}"), unit.UnitId,
                            sale.EndOwner.Id, PartyType.Customer, EpisodeReason.Sale, deliveredAt.AddHours(2), actor, legalEntityId,
                            EpisodeSource.Registration, Document: invoiceRef, CustomerPoRef: shipment.CustomerPoRef), cancellationToken);
                        await WaitForEntitlementRequirementsAsync([unit], cancellationToken);
                        await InvokeAsync<ChangeCustodyResult>(new ChangeCustody(DemoIds.FromName($"end-custody:{sale.Key}:{unit.Serial}"), unit.UnitId,
                            LocationKind.CustomerSite, EpisodeReason.Sale, deliveredAt.AddHours(2), actor, legalEntityId,
                            shipment.ShipToLocationId, sale.EndOwner.Id, PartyType.Customer, EpisodeSource.Registration, Document: invoiceRef), cancellationToken);
                    }
                }
            }
        }

        var at3pl = Units("LB-14")[7];
        await InvokeAsync<ChangeCustodyResult>(new ChangeCustody(DemoIds.FromName("custody:3pl:" + at3pl.Serial), at3pl.UnitId,
            LocationKind.At3pl, EpisodeReason.Transfer, DemoDates.At(today.AddDays(-20)), actor, LegalEntityId,
            DemoIds.FromName("location:parcelora-3pl"), Party("L-PARCELORA").Id, PartyType.ThreePl, "warehouse-transfer"), cancellationToken);
    }

    private async Task CreateOutboundEntitlementsAsync(CancellationToken cancellationToken)
    {
        foreach (var sale in SalesPlan())
        {
            var invoice = Document(sale.InvoiceNumber, "invoice");
            var soldAt = DemoDates.At(today.AddDays(-sale.DaysAgo), 9).AddDays(1);
            foreach (var unit in sale.Units)
            {
                var code = PolicyCodeFor(unit.Code);
                await CreateEntitlementAsync(unit, policies[code], CoverageFamilyCode.Base, sale.EndOwner, invoice,
                    DateOnly.FromDateTime(soldAt), soldAt, sale.Country, sale.Channel, cancellationToken);
            }
        }

        var printer = Units("PF-400")[0];
        var studioSale = SalesPlan().Single(sale => sale.Key == "studio");
        var studioAt = DemoDates.At(today.AddDays(-studioSale.DaysAgo), 10);
        await CreateEntitlementAsync(printer, policies["DEMO-GOODWILL-12"], "goodwill", studioSale.EndOwner,
            Document(studioSale.InvoiceNumber, "invoice"), DateOnly.FromDateTime(studioAt), studioAt, studioSale.Country, studioSale.Channel, cancellationToken);
        var goodwillPrinter = Units("PF-400")[3];
        var goodwillSale = SalesPlan().Single(sale => sale.Key == "p5-goodwill");
        var goodwillAt = DemoDates.At(today.AddDays(-goodwillSale.DaysAgo), 10);
        await CreateEntitlementAsync(goodwillPrinter, policies["DEMO-GOODWILL-12"], "goodwill", goodwillSale.EndOwner,
            Document(goodwillSale.InvoiceNumber, "invoice"), DateOnly.FromDateTime(goodwillAt), goodwillAt,
            goodwillSale.Country, goodwillSale.Channel, cancellationToken);
        var contractSwitch = Units("NX-4850")[2];
        var ispSale = SalesPlan().Single(sale => sale.Key == "isp");
        var ispAt = DemoDates.At(today.AddDays(-ispSale.DaysAgo), 10);
        await CreateEntitlementAsync(contractSwitch, policies["DEMO-NETWORK-CONTRACT"], "service_contract", ispSale.EndOwner,
            Document(ispSale.InvoiceNumber, "invoice"), DateOnly.FromDateTime(ispAt), ispAt, ispSale.Country, ispSale.Channel, cancellationToken);

        var hospitalSale = SalesPlan().Single(sale => sale.Key == "hospital");
        var hospitalAt = DemoDates.At(today.AddDays(-hospitalSale.DaysAgo), 10);
        await CreateEntitlementAsync(recoveryDimm!, policies["DEMO-SERVER-36"], CoverageFamilyCode.Base, hospitalSale.EndOwner,
            Document(hospitalSale.InvoiceNumber, "invoice"), DateOnly.FromDateTime(hospitalAt), hospitalAt, hospitalSale.Country, hospitalSale.Channel, cancellationToken);
        await CreateEntitlementAsync(expiredPsu!, policies["DEMO-SERVER-36"], CoverageFamilyCode.Base, hospitalSale.EndOwner,
            Document(hospitalSale.InvoiceNumber, "invoice"), DateOnly.FromDateTime(hospitalAt), hospitalAt, hospitalSale.Country, hospitalSale.Channel, cancellationToken);
        var hpcSale = SalesPlan().Single(sale => sale.Key == "hpc");
        var hpcAt = DemoDates.At(today.AddDays(-hpcSale.DaysAgo), 10);
        await CreateEntitlementAsync(recoveryNvme!, policies["DEMO-STORAGE-60"], CoverageFamilyCode.Base, hpcSale.EndOwner,
            Document(hpcSale.InvoiceNumber, "invoice"), DateOnly.FromDateTime(hpcAt), hpcAt, hpcSale.Country, hpcSale.Channel, cancellationToken);
    }

    private async Task CreateClaimsAsync(CancellationToken cancellationToken)
    {
        var dates = DemoDates.Spread(today, 27, 26, 1);
        var hospital = Party("C-VELMORA");
        var hpc = Party("C-NANDIRA");
        var bank = Party("C-ASHVANE");
        var engineering = Party("C-TORRENFOLD");
        var studio = Party("C-MIREVA");
        var isp = Party("C-CLOUDMERE");
        var definitions = new List<ClaimPlan>
        {
            Claim("twin-dimm", Units("RT-N1")[0], hospital, "EUR", recoveryDimm!, ClaimLineKind.Parts, 420m, "Failed 64 GB registered DIMM", ClaimAction.Adjudicate),
            Claim("storage-nvme", Units("SS-1110")[0], hpc, "GBP", recoveryNvme!, ClaimLineKind.Parts, 1380m, "3.84 TB NVMe media failure", ClaimAction.Adjudicate),
            Claim("expired-vendor-psu", Units("RS-1520")[0], hospital, "INR", expiredPsu!, ClaimLineKind.Parts, 31000m, "1600 W PSU replacement", ClaimAction.Adjudicate),
            Claim("laptop-out-of-region", Units("LB-14")[0], bank, "USD", null, ClaimLineKind.Parts, 650m, "Main board repair", ClaimAction.Adjudicate, country: "US"),
            Claim("printer-goodwill", Units("PF-400")[0], studio, "EUR", null, ClaimLineKind.Parts, 280m, "Print-head module", ClaimAction.Adjudicate, family: "goodwill", measurements: new Dictionary<string, decimal> { ["print_hours"] = 1840 }),
            new("accessory-proof", null, bank, "GBP", "GB", [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 74m, Description: "Replacement 65 W charger")], ClaimAction.Adjudicate,
                UnknownIdentifier: "AC65-POP-0042", ItemId: Item("AC-65").Id, ProofRef: "POP-AC65-0042", ProofDate: today.AddDays(-90)),
            Claim("desktop-chargeable", Units("DW-300")[0], bank, "EUR", null, ClaimLineKind.Parts, 225m, "Liquid-damaged main board", ClaimAction.Adjudicate, family: "accidental_damage"),
            Claim("unauthorised-dimm", Units("RS-1100")[10], hpc, "EUR", null, ClaimLineKind.Parts, 390m, "Memory-channel damage", ClaimAction.ManualDeny),
            new("client-partial", Units("LB-14")[1], bank, "GBP", "IN",
                [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 120m, Description: "Display cable"), new ClaimLineInput(ClaimLineKind.Travel, CoverageFamilyCode.Base, 45m, Description: "Engineer travel")], ClaimAction.Adjudicate),
            Claim("switch-contract", Units("NX-4850")[2], isp, "USD", null, ClaimLineKind.Labour, 180m, "On-site diagnostics", ClaimAction.Adjudicate, family: "service_contract"),
            Claim("server-settled-closed", Units("RS-1100")[2], hpc, "EUR", null, ClaimLineKind.Labour, 160m, "Thermal remediation", ClaimAction.Close),
            Claim("router-settled", Units("NR-220")[0], isp, "INR", null, ClaimLineKind.Parts, 24000m, "WAN module", ClaimAction.Settle),
            Claim("printer-settled", Units("PF-400")[1], studio, "GBP", null, ClaimLineKind.Labour, 90m, "Extrusion calibration", ClaimAction.Settle),
            Claim("server-approved", Units("RS-1100")[3], hpc, "EUR", null, ClaimLineKind.Parts, 510m, "Cooling fan module", ClaimAction.Adjudicate),
            Claim("desktop-approved", Units("DW-300")[1], bank, "INR", null, ClaimLineKind.Labour, 7200m, "Intermittent boot diagnosis", ClaimAction.Adjudicate),
            Claim("evidence-thermal", Units("RS-1100")[4], hpc, "EUR", null, ClaimLineKind.Labour, 155m, "Thermal shutdown investigation", ClaimAction.Evidence),
            Claim("evidence-router", Units("NR-220")[1], isp, "GBP", null, ClaimLineKind.Parts, 210m, "Carrier trace required", ClaimAction.Evidence),
            Claim("evidence-printer", Units("PF-400")[2], studio, "INR", null, ClaimLineKind.Labour, 8700m, "Print sample review", ClaimAction.Evidence),
            Claim("evidence-laptop", Units("LB-14")[2], bank, "USD", null, ClaimLineKind.Parts, 340m, "Battery diagnostic", ClaimAction.Evidence),
            Claim("evidence-switch", Units("NX-4850")[3], isp, "EUR", null, ClaimLineKind.Parts, 460m, "Optics fault", ClaimAction.Evidence),
            Claim("submitted-server", Units("RS-1100")[5], hpc, "EUR", null, ClaimLineKind.Parts, 330m, "Power-cycle fault", ClaimAction.Submit),
            Claim("submitted-laptop", Units("LB-14")[3], bank, "GBP", null, ClaimLineKind.Labour, 95m, "Keyboard inspection", ClaimAction.Submit),
            Claim("submitted-desktop", Units("DW-300")[2], bank, "INR", null, ClaimLineKind.Parts, 8200m, "Display adapter", ClaimAction.Submit),
            Claim("no-fault-found", Units("DW-300")[3], bank, "EUR", null, ClaimLineKind.Labour, 80m, "Intermittent noise", ClaimAction.NoFaultDeny),
            Claim("server-replacement", Units("RS-1100")[31], bank, "USD", null, ClaimLineKind.Parts, 890m, "Management controller replacement", ClaimAction.Adjudicate, remedy: ClaimRemedy.Replacement),
            Claim("recall-campaign", Units("RS-1100")[0], hpc, "EUR", null, ClaimLineKind.Parts, 360m,
                "Affected power-supply inspection", ClaimAction.Adjudicate, campaignId: activeRecallCampaignId, claimDate: today),
            Claim("ended-goodwill-campaign", Units("RS-1100")[10], hpc, "EUR", null, ClaimLineKind.Labour, 145m,
                "Management-controller firmware update", ClaimAction.Adjudicate, campaignId: endedGoodwillCampaignId, claimDate: today),
        };

        var recoveryIds = new List<(Guid Id, decimal Requested)>();
        for (var index = 0; index < definitions.Count; index++)
        {
            var plan = definitions[index];
            var day = plan.ClaimDate ?? dates[index];
            var eventTime = DemoDates.At(day, 10);
            var submit = new SubmitClaim(DemoIds.FromName("claim:" + plan.Key), plan.Unit?.UnitId,
                plan.UnknownIdentifier is null ? null : new DocumentSerial(plan.UnknownIdentifier, IdentifierType: IdentifierType.InternalSerial),
                day, plan.Customer.Id, plan.Customer.Id, ServiceEntityId, LegalEntityId, plan.Country, plan.Currency,
                plan.Lines, [new ClaimEvidenceInput(ClaimEvidenceKind.Attachment, "DEMO-EVIDENCE-" + plan.Key.ToUpperInvariant(), "Development review evidence")],
                plan.Remedy, "CHOICE-" + plan.Key.ToUpperInvariant(), eventTime, actor, plan.ProofRef, plan.ProofDate, plan.ItemId, plan.CampaignId);
            var submitted = await InvokeAsync<SubmitClaimResult>(submit, cancellationToken);
            if (!submitted.Replayed) claimsCreated++;

            if (plan.Action == ClaimAction.Submit) continue;
            if (plan.Action == ClaimAction.Evidence)
            {
                await InvokeAsync<ClaimChangeResult>(new RequestClaimEvidence(DemoIds.FromName("evidence:" + plan.Key), submitted.ClaimId,
                    "Please provide diagnostic logs and a clear label photograph.", eventTime.AddHours(1), actor), cancellationToken);
                continue;
            }

            var denial = plan.Action switch
            {
                ClaimAction.ManualDeny => ClaimManualDenialReason.UnauthorisedPartsDamage,
                ClaimAction.NoFaultDeny => ClaimManualDenialReason.NoFaultFound,
                _ => null,
            };
            var note = denial == ClaimManualDenialReason.UnauthorisedPartsDamage
                ? "Inspection found a third-party DIMM fitted in slot A2; the unauthorised part damaged the memory channel."
                : denial == ClaimManualDenialReason.NoFaultFound ? "Bench diagnostics completed twice without reproducing the reported fault." : null;
            var adjudicated = await InvokeAsync<AdjudicateClaimResult>(new AdjudicateClaim(DemoIds.FromName("adjudicate:" + plan.Key), submitted.ClaimId,
                eventTime.AddHours(2), actor, new ClaimReviewer(actor, [YojanaRole.ServiceManager]), denial, note), cancellationToken);
            recoveryIds.AddRange(adjudicated.RecoveryClaimIds.Select(id => (id, adjudicated.Lines.Single(line => line.ApprovedAmount > 0).ApprovedAmount)));
            if (plan.Action is ClaimAction.Settle or ClaimAction.Close)
                await InvokeAsync<ClaimChangeResult>(new SettleClaim(DemoIds.FromName("settle:" + plan.Key), submitted.ClaimId, eventTime.AddDays(1), actor), cancellationToken);
            if (plan.Action == ClaimAction.Close)
                await InvokeAsync<ClaimChangeResult>(new CloseClaim(DemoIds.FromName("close:" + plan.Key), submitted.ClaimId, eventTime.AddDays(2), actor), cancellationToken);
        }

        foreach (var (recovery, index) in recoveryIds.Take(2).Select((row, index) => (row, index)))
        {
            var amount = index == 0 ? recovery.Requested : decimal.Round(recovery.Requested * 0.6m, 2);
            var note = index == 0 ? "Vendor accepted the diagnostic evidence in full."
                : "Vendor excluded expedited freight and approved the component value only.";
            var answer = await InvokeAsync<RecoveryClaimChangeResult>(new ApproveRecoveryClaim(DemoIds.FromName("vendor-answer:" + recovery.Id),
                recovery.Id, amount, DemoDates.At(today, 9 + index), actor, VendorReference: $"VA-2026-{714 + index}", VendorNote: note), cancellationToken);
            if (!answer.Replayed) recoveriesApproved++;
        }
    }

    private async Task CreatePhaseFiveScenariosAsync(CancellationToken cancellationToken)
    {
        await StepAsync("  warranty server refurbishment", CreateServerReturnAndRefurbishmentAsync, cancellationToken);
        await WaitForAutomaticCertificatesAsync(cancellationToken);
        await StepAsync("  RMA stages and receipt exceptions", CreateRmaStageAndExceptionCasesAsync, cancellationToken);
        await WaitForAutomaticCertificatesAsync(cancellationToken);
        await StepAsync("  DOA, seller-error, goodwill and chargeable replacements", CreateReplacementReasonCasesAsync, cancellationToken);
        await WaitForAutomaticCertificatesAsync(cancellationToken);
        await StepAsync("  advance replacement due dates and overdue run", CreateAdvanceReplacementCasesAsync, cancellationToken);
        await WaitForAutomaticCertificatesAsync(cancellationToken);
        await StepAsync("  loan conversion", CreateLoanConversionAsync, cancellationToken);
        await WaitForAutomaticCertificatesAsync(cancellationToken);
        await StepAsync("  supplier return and replacement", CreateVendorReplacementAsync, cancellationToken);
        await WaitForAutomaticCertificatesAsync(cancellationToken);
        await StepAsync("  replacement-link correction", CreateReplacementCorrectionAsync, cancellationToken);
        await WaitForAutomaticCertificatesAsync(cancellationToken);
    }

    private async Task CreateServerReturnAndRefurbishmentAsync(CancellationToken cancellationToken)
    {
        var original = Units("RS-1100")[31];
        var replacement = Units("RS-1100")[32];
        var bank = Party("C-ASHVANE");
        var requestedAt = DemoDates.At(today.AddDays(-2), 8);
        var rma = await RequestAndAuthoriseRmaAsync("server-refurbishment", original, bank,
            requestedAt, CoverageFamilyCode.Base, "Management controller not responding", cancellationToken);
        var claim = await ClaimSourceAsync("server-replacement", cancellationToken);
        var shipped = await CreateAndShipReplacementAsync("server-advance", ReplacementSourceKind.Claim,
            claim.ClaimId, claim.EntitlementId, replacement, ReplacementReason.WarrantyDefect,
            requestedAt.AddHours(3), requestedAt.AddHours(4), "USD", "IN", cancellationToken,
            returnDue: today.AddDays(28), nonReturnAmount: 2890m, returnReason: "warranty_defect");

        var receivedAt = DemoDates.At(today.AddDays(-1), 9);
        await ReceiveRmaAsync("server-refurbishment", rma, original, ConditionGrades.Defective,
            "write_down_to_salvage", receivedAt, ReturnOutcome.Accepted, QuarantineId, cancellationToken);
        await WaitForLinkStateAsync(shipped.ReplacementLinkId, ReplacementLinkState.ClosedReturned, cancellationToken);

        List<(Guid LinkId, Guid UnitId)> retained;
        using (var scope = TenantScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var raw = await (from link in db.AssemblyLinks.AsNoTracking()
                             join assignment in db.ItemAssignments.AsNoTracking()
                                 on new { link.TenantId, UnitId = link.ChildUnitId!.Value }
                                 equals new { assignment.TenantId, assignment.UnitId }
                             where link.ParentUnitId == original.UnitId && link.ChildUnitId != null
                                 && assignment.ItemId == Item("ND-384").Id && assignment.ValidTo == null
                             orderby link.InstalledAt, link.LinkId
                             select new { link.LinkId, UnitId = link.ChildUnitId!.Value }).ToListAsync(cancellationToken);
            retained = raw.Select(row => (row.LinkId, row.UnitId)).ToList();
        }
        await InvokeAsync<RecordDisassemblyResult>(new RecordDisassembly(DemoIds.FromName("p5:server-retained-drives"),
            original.UnitId, retained.Select(row => row.LinkId).ToList(), receivedAt.AddMinutes(10), actor,
            LegalEntityId, "customer_retained", new DocumentRef("rma", rma.RmaId, 1)), cancellationToken);
        foreach (var (linkId, unitId) in retained)
        {
            await InvokeAsync<ChangeCustodyResult>(new ChangeCustody(DemoIds.FromName("p5:retained-drive:" + linkId), unitId,
                LocationKind.CustomerSite, "customer_retained", receivedAt.AddMinutes(11), actor, LegalEntityId,
                DemoIds.FromName("site:" + bank.Code), bank.Id, PartyType.Customer, "rma",
                Document: new DocumentRef("rma", rma.RmaId, 1)), cancellationToken);
        }

        await DispositionRmaAsync("server-refurbishment", rma, RmaDisposition.Refurbish,
            receivedAt.AddHours(1), cancellationToken);
        Guid workOrderId;
        using (var scope = TenantScope())
        {
            workOrderId = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().RefurbishmentWorkOrders.AsNoTracking()
                .Where(row => row.SourceRmaLineId == rma.RmaLineId).Select(row => row.WorkOrderId).SingleAsync(cancellationToken);
        }

        var replacementController = await CommissionScenarioUnitAsync("server-controller", "BM-2", "17990001",
            receivedAt.AddMinutes(30), WarehouseId, LocationKind.Warehouse, cancellationToken);
        Guid controllerLinkId;
        using (var scope = TenantScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            controllerLinkId = await (from link in db.AssemblyLinks.AsNoTracking()
                                      join assignment in db.ItemAssignments.AsNoTracking()
                                          on new { link.TenantId, UnitId = link.ChildUnitId!.Value }
                                          equals new { assignment.TenantId, assignment.UnitId }
                                      where link.ParentUnitId == original.UnitId && link.ChildUnitId != null
                                          && assignment.ItemId == Item("BM-2").Id && assignment.ValidTo == null
                                      orderby link.RemovedAt == null descending, link.InstalledAt
                                      select link.LinkId).FirstAsync(cancellationToken);
        }

        var operations = new[]
        {
            (10, "Diagnose management-controller failure", 0.5m),
            (20, "Replace management controller part", 0.75m),
            (30, "Clear controller configuration and platform secrets", 0.25m),
            (40, "Apply current management-controller and platform firmware", 0.5m),
            (50, "Run burn-in and key-function tests", 1.0m),
        };
        var operationIds = new Dictionary<int, Guid>();
        foreach (var (sequence, description, hours) in operations)
        {
            var added = await InvokeAsync<RefurbishmentOperationResult>(new AddRefurbishmentOperation(
                DemoIds.FromName($"p5:server-refurb-operation:{sequence}"), workOrderId, sequence, description,
                actor, hours, receivedAt.AddMinutes(40 + sequence), actor, LegalEntityId), cancellationToken);
            operationIds[sequence] = added.OperationId;
            await InvokeAsync<RefurbishmentOperationResult>(new CompleteRefurbishmentOperation(
                DemoIds.FromName($"p5:server-refurb-complete:{sequence}"), workOrderId, added.OperationId,
                hours, receivedAt.AddMinutes(45 + sequence), actor, LegalEntityId), cancellationToken);
        }

        await InvokeAsync<RefurbishmentPartResult>(new RemoveRefurbishmentPart(
            DemoIds.FromName("p5:server-controller-remove"), workOrderId, controllerLinkId,
            RemovedPartDestination.VendorReturn, receivedAt.AddHours(2), actor, LegalEntityId,
            VendorPartyRef: Party("V-RACKSPIRE").Id), cancellationToken);
        await InvokeAsync<RefurbishmentPartResult>(new InstallRefurbishmentPart(
            DemoIds.FromName("p5:server-controller-install"), workOrderId,
            new ChildInput(replacementController.UnitId, Slot: "BMC-01"), 184m, "GBP",
            receivedAt.AddHours(2).AddMinutes(10), actor, LegalEntityId, RepairLocationId), cancellationToken);
        await InvokeAsync<RefurbishmentCostResult>(new RecordRefurbishmentCost(
            DemoIds.FromName("p5:server-graded-value"), workOrderId, RefurbishmentCostKind.GradedReceipt,
            720m, "GBP", "Defective receipt value", receivedAt.AddHours(2).AddMinutes(20), actor, LegalEntityId), cancellationToken);
        await InvokeAsync<RefurbishmentLabourResult>(new RecordRefurbishmentLabour(
            DemoIds.FromName("p5:server-refurb-labour"), workOrderId, operationIds[20], 3.5m, 50m,
            "GBP", receivedAt.AddHours(3), actor, LegalEntityId), cancellationToken);
        await InvokeAsync<RecordSanitisationResult>(new RecordSanitisation(
            DemoIds.FromName("p5:server-sanitisation"), original.UnitId, "Asterra Compute Works", "RS-1100",
            original.Serial, "embedded configuration storage", SanitisationMethod.Clear, "Asterra service image",
            "read-back verification", "SAN-DEMO-0001", receivedAt.AddHours(4), actor, LegalEntityId,
            ToolVersion: "7.2", Document: new DocumentRef("refurbishment_work_order", workOrderId)), cancellationToken);
        await InvokeAsync<RecordTestResult>(new RecordTest(DemoIds.FromName("p5:server-test-key-functions"), original.UnitId,
            RefurbishmentTestKind.KeyFunctions, UnitTestResult.Pass,
            "{\"burn_in_hours\":24,\"memory_errors\":0,\"network_ports_passed\":2}", "Asterra bench 4",
            receivedAt.AddHours(5), actor, LegalEntityId, new DocumentRef("refurbishment_work_order", workOrderId)), cancellationToken);
        await InvokeAsync<RecordTestResult>(new RecordTest(DemoIds.FromName("p5:server-test-power"), original.UnitId,
            "power-and-thermal", UnitTestResult.Pass, "{\"cycles\":20,\"peak_celsius\":68}", "Asterra bench 4",
            receivedAt.AddHours(5).AddMinutes(10), actor, LegalEntityId, new DocumentRef("refurbishment_work_order", workOrderId)), cancellationToken);
        await AttributeAsync(original, "bmc.firmware", "4.3.0", receivedAt.AddHours(5).AddMinutes(20), cancellationToken);
        await AttributeAsync(original, "bios.firmware", "2.14", receivedAt.AddHours(5).AddMinutes(21), cancellationToken);
        await InvokeAsync<RecordUsageReadingResult>(new RecordUsageReading(DemoIds.FromName("p5:server-power-hours"),
            original.UnitId, "power_on_hours", 5812m, "hours", receivedAt.AddHours(5).AddMinutes(30), actor,
            LegalEntityId, Document: new DocumentRef("refurbishment_work_order", workOrderId)), cancellationToken);
        await InvokeAsync<ReleaseRefurbishmentResult>(new ReleaseRefurbishment(
            DemoIds.FromName("p5:server-refurb-release"), workOrderId, ConditionGrades.RefurbishedA,
            "refurbished_standard_cost", receivedAt.AddHours(6), actor, LegalEntityId, WarehouseId,
            NewSerial: "01990001", FCode: "F5", CCode: "C6"), cancellationToken);
    }

    private async Task CreateRmaStageAndExceptionCasesAsync(CancellationToken cancellationToken)
    {
        var bank = Party("C-ASHVANE");
        var engineering = Party("C-TORRENFOLD");
        var cloudmere = Party("C-CLOUDMERE");
        var hospital = Party("C-VELMORA");

        await RequestRmaAsync("stage-requested", Units("DW-300")[11], bank,
            DemoDates.At(today.AddDays(-2), 9), CoverageFamilyCode.Base, "Awaiting coverage review", cancellationToken);
        await RequestAndAuthoriseRmaAsync("stage-authorised", Units("DW-300")[12], bank,
            DemoDates.At(today.AddDays(-3), 9), CoverageFamilyCode.Base, "Return authorised; customer arranging collection", cancellationToken);

        var inTransit = await RequestAndAuthoriseRmaAsync("stage-in-transit", Units("DW-300")[25], hospital,
            DemoDates.At(today.AddDays(-4), 9), CoverageFamilyCode.Base, "Return collected and travelling to the warehouse", cancellationToken);
        await DispatchRmaAsync("stage-in-transit", inTransit, DemoDates.At(today.AddDays(-2), 14),
            "Asterline Freight", "AF-204781", cancellationToken);

        var received = await RequestAndAuthoriseRmaAsync("stage-received", Units("DW-300")[17], engineering,
            DemoDates.At(today.AddDays(-5), 9), CoverageFamilyCode.Base, "Workstation intermittently restarts", cancellationToken);
        await ReceiveRmaAsync("stage-received", received, Units("DW-300")[17], ConditionGrades.Defective,
            "repairable_value", DemoDates.At(today.AddDays(-3), 10), ReturnOutcome.Accepted, QuarantineOverflowId, cancellationToken);

        var closed = await RequestAndAuthoriseRmaAsync("stage-closed", Units("DW-300")[18], engineering,
            DemoDates.At(today.AddDays(-7), 9), CoverageFamilyCode.Base, "Unused return approved", cancellationToken);
        await ReceiveRmaAsync("stage-closed", closed, Units("DW-300")[18], ConditionGrades.OpenBox,
            "open_box_value", DemoDates.At(today.AddDays(-5), 10), ReturnOutcome.Accepted, QuarantineOverflowId, cancellationToken);
        await DispositionRmaAsync("stage-closed", closed, RmaDisposition.ReturnToStock,
            DemoDates.At(today.AddDays(-4), 11), cancellationToken);
        await CloseRmaAsync("stage-closed", closed, DemoDates.At(today.AddDays(-4), 12), cancellationToken);

        var dispositioned = await RequestAndAuthoriseRmaAsync("stage-dispositioned", Units("DW-300")[26], hospital,
            DemoDates.At(today.AddDays(-6), 9), CoverageFamilyCode.Base, "Replacement approved and awaiting shipment", cancellationToken);
        await ReceiveRmaAsync("stage-dispositioned", dispositioned, Units("DW-300")[26], ConditionGrades.Defective,
            "repairable_value", DemoDates.At(today.AddDays(-4), 10), ReturnOutcome.Accepted, QuarantineOverflowId, cancellationToken);
        await DispositionRmaAsync("stage-dispositioned", dispositioned, RmaDisposition.Replace,
            DemoDates.At(today.AddDays(-3), 11), cancellationToken);

        var provisional = await RequestAndAuthoriseRmaAsync("unauthorised-provisional", Units("DW-300")[21], cloudmere,
            DemoDates.At(today.AddDays(-6), 9), CoverageFamilyCode.Base, "Carton received without a readable unit label", cancellationToken);
        await ReceiveRmaAsync("unauthorised-provisional", provisional, null, ConditionGrades.Used,
            "hold_pending_identity", DemoDates.At(today.AddDays(-4), 10), ReturnOutcome.Unauthorised,
            QuarantineOverflowId, cancellationToken, "MAC and carton identify one candidate; confirmation is still required");

        var mismatch = await RequestAndAuthoriseRmaAsync("serial-mismatch", Units("DW-300")[22], cloudmere,
            DemoDates.At(today.AddDays(-6), 10), CoverageFamilyCode.Base, "Returned serial differs from the authorised unit", cancellationToken);
        await ReceiveRmaAsync("serial-mismatch", mismatch, Units("DW-300")[23], ConditionGrades.Defective,
            "hold_for_serial_resolution", DemoDates.At(today.AddDays(-4), 11), ReturnOutcome.Accepted,
            QuarantineId, cancellationToken, "Received serial belongs to the same customer but is not the authorised unit");
    }

    private async Task CreateReplacementReasonCasesAsync(CancellationToken cancellationToken)
    {
        var engineering = Party("C-TORRENFOLD");
        var cloudmere = Party("C-CLOUDMERE");
        var studio = Party("C-MIREVA");

        var doaOriginal = Units("DW-300")[14];
        var doa = await RequestAndAuthoriseRmaAsync("doa-workstation", doaOriginal, engineering,
            DemoDates.At(today.AddDays(-6), 9), CoverageFamilyCode.Base, "Dead on arrival during initial power-on", cancellationToken);
        await ReceiveRmaAsync("doa-workstation", doa, doaOriginal, ConditionGrades.OpenBox, "unused_open_box",
            DemoDates.At(today.AddDays(-4), 10), ReturnOutcome.Accepted, QuarantineId, cancellationToken);
        await DispositionRmaAsync("doa-workstation", doa, RmaDisposition.Replace,
            DemoDates.At(today.AddDays(-3), 9), cancellationToken);
        await CreateAndShipReplacementAsync("doa-workstation", ReplacementSourceKind.Rma, doa.RmaId,
            doa.EntitlementId!.Value, Units("DW-300")[34], ReplacementReason.DeadOnArrival,
            DemoDates.At(today.AddDays(-3), 10), DemoDates.At(today.AddDays(-3), 11), "EUR", "DE", cancellationToken,
            replacementPolicy: "DEMO-PRESERVE", conditionAllowed: ReplacementConditionAllowed.New,
            returnReason: "dead_on_arrival");

        var sellerOriginal = Units("DW-300")[19];
        var sellerClaim = await CreateApprovedClaimAsync("seller-error-exchange", sellerOriginal, cloudmere,
            CoverageFamilyCode.Base, DemoDates.At(today.AddDays(-6), 9), "Wrong workstation configuration shipped", cancellationToken);
        var sellerRma = await RequestAndAuthoriseRmaAsync("seller-error-exchange", sellerOriginal, cloudmere,
            DemoDates.At(today.AddDays(-6), 12), CoverageFamilyCode.Base, "Wrong configuration supplied", cancellationToken);
        await CreateAndShipReplacementAsync("seller-error-exchange", ReplacementSourceKind.Claim, sellerClaim.ClaimId,
            sellerClaim.EntitlementId, Units("DW-300")[35], ReplacementReason.SellerError,
            DemoDates.At(today.AddDays(-5), 10), DemoDates.At(today.AddDays(-4), 9), "INR", "IN", cancellationToken,
            replacementPolicy: "DEMO-PRESERVE", returnReason: "seller_error");
        await ReceiveRmaAsync("seller-error-exchange", sellerRma, sellerOriginal, ConditionGrades.New, "full_new_value",
            DemoDates.At(today.AddDays(-3), 10), ReturnOutcome.Accepted, QuarantineId, cancellationToken);
        await DispositionRmaAsync("seller-error-exchange", sellerRma, RmaDisposition.RestockAsNew,
            DemoDates.At(today.AddDays(-2), 10), cancellationToken);

        var goodwillOriginal = Units("PF-400")[3];
        var goodwillClaim = await CreateApprovedClaimAsync("goodwill-fee", goodwillOriginal, studio, "goodwill",
            DemoDates.At(today.AddDays(-5), 9), "Goodwill replacement approved after cosmetic return", cancellationToken);
        await CreateAndShipReplacementAsync("goodwill-fee", ReplacementSourceKind.Claim, goodwillClaim.ClaimId,
            goodwillClaim.EntitlementId, Units("PF-400")[4], ReplacementReason.Goodwill,
            DemoDates.At(today.AddDays(-3), 9), DemoDates.At(today.AddDays(-2), 9), "GBP", "GB", cancellationToken,
            replacementPolicy: "DEMO-REPLACE-90", returnReason: "goodwill_exchange", restockingFeeBasis: 250m);

        var usagePrinter = Units("PF-400")[0];
        await InvokeAsync<RecordUsageReadingResult>(new RecordUsageReading(DemoIds.FromName("p5:printer-usage-limit"),
            usagePrinter.UnitId, "print_hours", 2140m, "hours", DemoDates.At(today.AddDays(-6), 8), actor,
            LegalEntityId), cancellationToken);
        var chargeable = await RequestAndAuthoriseRmaAsync("printer-chargeable", usagePrinter, studio,
            DemoDates.At(today.AddDays(-5), 10), "usage_exhausted", "2,140 print hours exceeds the 2,000-hour cover limit", cancellationToken);
        await ReceiveRmaAsync("printer-chargeable", chargeable, usagePrinter, ConditionGrades.Used, "chargeable_repair_value",
            DemoDates.At(today.AddDays(-3), 11), ReturnOutcome.Accepted, QuarantineOverflowId, cancellationToken);
        await DispositionRmaAsync("printer-chargeable", chargeable, RmaDisposition.RepairAndReturn,
            DemoDates.At(today.AddDays(-2), 11), cancellationToken);
    }

    private async Task CreateAdvanceReplacementCasesAsync(CancellationToken cancellationToken)
    {
        var bank = Party("C-ASHVANE");
        var engineering = Party("C-TORRENFOLD");
        var cloudmere = Party("C-CLOUDMERE");
        var hospital = Party("C-VELMORA");
        var links = new List<ShipWarrantyReplacementResult>();
        for (var index = 0; index < 3; index++)
        {
            var original = Units("DW-300")[8 + index];
            var rma = await RequestAndAuthoriseRmaAsync($"advance-overdue-{index}", original, bank,
                DemoDates.At(today.AddDays(-14 + index), 9), CoverageFamilyCode.Base,
                "Advance replacement awaiting the failed workstation", cancellationToken);
            links.Add(await CreateAndShipReplacementAsync($"advance-overdue-{index}", ReplacementSourceKind.Rma,
                rma.RmaId, rma.EntitlementId!.Value, Units("DW-300")[28 + index], ReplacementReason.WarrantyDefect,
                DemoDates.At(today.AddDays(-12 + index), 9), DemoDates.At(today.AddDays(-11 + index), 9),
                "INR", "IN", cancellationToken, returnDue: today.AddDays(-3 + index),
                nonReturnAmount: 1240m + index * 110m, returnReason: "warranty_defect"));
        }

        var dueCases = new[]
        {
            ("engineering", Units("DW-300")[15], engineering, Units("DW-300")[31], "EUR", "DE", 3),
            ("cloudmere", Units("DW-300")[20], cloudmere, Units("DW-300")[32], "INR", "IN", 5),
            ("hospital", Units("DW-300")[24], hospital, Units("DW-300")[33], "GBP", "GB", 7),
        };
        foreach (var (key, original, party, replacement, currency, country, dueDays) in dueCases)
        {
            var rma = await RequestAndAuthoriseRmaAsync("advance-due-" + key, original, party,
                DemoDates.At(today.AddDays(-7), 9), CoverageFamilyCode.Base,
                "Advance replacement with a scheduled return", cancellationToken);
            links.Add(await CreateAndShipReplacementAsync("advance-due-" + key, ReplacementSourceKind.Rma,
                rma.RmaId, rma.EntitlementId!.Value, replacement, ReplacementReason.WarrantyDefect,
                DemoDates.At(today.AddDays(-6), 9), DemoDates.At(today.AddDays(-5), 9), currency, country,
                cancellationToken, returnDue: today.AddDays(dueDays), nonReturnAmount: 1180m,
                returnReason: "warranty_defect"));
        }

        await InvokeAsync<ExtendReturnDueResult>(new ExtendReturnDue(DemoIds.FromName("p5:advance-extension"),
            links[3].ReplacementLinkId, today.AddDays(10), "Customer collection window agreed",
            DemoDates.At(today.AddDays(-1), 8), actor), cancellationToken);
        await InvokeAsync<ReplacementOverdueRunResult>(new RunReplacementOverdue(today,
            DemoDates.At(today.AddDays(-1), 23)), cancellationToken);
        await InvokeAsync<BillNonReturnResult>(new BillNonReturn(DemoIds.FromName("p5:advance-billed"),
            links[0].ReplacementLinkId, NonReturnPriceBasis.FairMarketValue, 900m, today.AddDays(-1),
            DemoDates.At(today.AddDays(-1), 23, 30), actor, "Original unit not returned after the overdue reminder"), cancellationToken);
    }

    private async Task CreateLoanConversionAsync(CancellationToken cancellationToken)
    {
        var original = Units("DW-300")[16];
        var loaner = Units("DW-300")[38];
        var customer = Party("C-TORRENFOLD");
        var issuedAt = DemoDates.At(today.AddDays(-5), 9);
        var site = DemoIds.FromName("site:" + customer.Code);
        await InvokeAsync<ChangeCustodyResult>(new ChangeCustody(DemoIds.FromName("p5:loan-custody"), loaner.UnitId,
            LocationKind.CustomerSite, EpisodeReason.Loan, issuedAt, actor, LegalEntityId, site,
            customer.Id, PartyType.Customer, "loaner_issue"), cancellationToken);
        var loanDocument = new DocumentRef("loaner_issue", DemoIds.FromName("p5:loan-document"), 1);
        documents.Add(new LoanerIssue(loanDocument, customer.Id, issuedAt, site, today.AddDays(7),
            [new DocumentLine(1, Item("DW-300").Id, 1, [new DocumentSerial(loaner.Serial, IdentifierType: IdentifierType.InternalSerial)])]));
        await InvokeAsync<ConvertLoanToReplacementResult>(new ConvertLoanToReplacement(
            DemoIds.FromName("p5:loan-conversion"), loanDocument, original.UnitId,
            await OriginalEntitlementIdAsync(original.UnitId, CoverageFamilyCode.Base, cancellationToken), loaner.UnitId,
            ReplacementReason.WarrantyDefect, today.AddDays(-3), DemoDates.At(today.AddDays(-3), 10),
            LegalEntityId, "DE", false, false, ActorRef: actor), cancellationToken);
    }

    private async Task CreateVendorReplacementAsync(CancellationToken cancellationToken)
    {
        ComponentFact component;
        using (var scope = TenantScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var raw = await (from link in db.AssemblyLinks.AsNoTracking()
                             join assignment in db.ItemAssignments.AsNoTracking()
                                 on new { link.TenantId, UnitId = link.ChildUnitId!.Value }
                                 equals new { assignment.TenantId, assignment.UnitId }
                             join identifier in db.Identifiers.AsNoTracking()
                                 on new { link.TenantId, UnitId = link.ChildUnitId!.Value }
                                 equals new { identifier.TenantId, identifier.UnitId }
                             where link.ChildUnitId != null && link.RemovedAt == null
                                 && assignment.ItemId == Item("MD-64R").Id && assignment.ValidTo == null
                                 && identifier.Type == IdentifierType.InternalSerial && identifier.ValidTo == null
                                 && link.ParentUnitId != Units("RS-1100")[31].UnitId
                             orderby link.ParentUnitId, link.LinkId
                             select new { link.LinkId, link.ParentUnitId, UnitId = link.ChildUnitId!.Value, identifier.RawValue })
                .FirstAsync(cancellationToken);
            component = new(raw.LinkId, raw.ParentUnitId, raw.UnitId, raw.RawValue);
        }

        var customer = Party("C-NANDIRA");
        var preparedAt = DemoDates.At(today.AddDays(-8), 9);
        await InvokeAsync<RecordDisassemblyResult>(new RecordDisassembly(DemoIds.FromName("p5:vendor-component-remove"),
            component.ParentUnitId, [component.LinkId], preparedAt, actor, LegalEntityId, "service_vendor_return"), cancellationToken);
        await InvokeAsync<OpenOwnershipResult>(new OpenOwnership(DemoIds.FromName("p5:vendor-component-owner"),
            component.UnitId, customer.Id, PartyType.Customer, EpisodeReason.ServiceInstallation, preparedAt.AddMinutes(1),
            actor, LegalEntityId, EpisodeSource.Service, Document: Document("IN-HPC-2601", "invoice")), cancellationToken);
        await InvokeAsync<ChangeCustodyResult>(new ChangeCustody(DemoIds.FromName("p5:vendor-component-custody"),
            component.UnitId, LocationKind.CustomerSite, EpisodeReason.ServiceInstallation, preparedAt.AddMinutes(2),
            actor, LegalEntityId, DemoIds.FromName("site:" + customer.Code), customer.Id, PartyType.Customer,
            EpisodeSource.Service), cancellationToken);

        var rma = await RequestAndAuthoriseRmaAsync("vendor-component", new DemoUnit(component.UnitId, "MD-64R", component.Serial, null),
            customer, DemoDates.At(today.AddDays(-7), 9), CoverageFamilyCode.Supplier,
            "Registered DIMM returned to its supplier", cancellationToken);
        await ReceiveRmaAsync("vendor-component", rma, new DemoUnit(component.UnitId, "MD-64R", component.Serial, null),
            ConditionGrades.Defective, "supplier_return_value", DemoDates.At(today.AddDays(-5), 10),
            ReturnOutcome.Accepted, QuarantineId, cancellationToken);
        await DispositionRmaAsync("vendor-component", rma, RmaDisposition.ReturnToVendor,
            DemoDates.At(today.AddDays(-4), 10), cancellationToken, Party("V-MEMRYX").Id);

        var replacement = await CommissionScenarioUnitAsync("vendor-component-replacement", "MD-64R", "13990001",
            DemoDates.At(today.AddDays(-4), 11), WarehouseId, LocationKind.Warehouse, cancellationToken);
        await InvokeAsync<OpenOwnershipResult>(new OpenOwnership(DemoIds.FromName("p5:vendor-replacement-owner"),
            replacement.UnitId, Party("V-MEMRYX").Id, PartyType.Vendor, EpisodeReason.Receipt,
            DemoDates.At(today.AddDays(-3), 9), actor, LegalEntityId, EpisodeSource.Receipt), cancellationToken);
        await InvokeAsync<ChangeCustodyResult>(new ChangeCustody(DemoIds.FromName("p5:vendor-replacement-custody"),
            replacement.UnitId, LocationKind.Vendor, EpisodeReason.Receipt, DemoDates.At(today.AddDays(-3), 9, 1),
            actor, LegalEntityId, PartyRef: Party("V-MEMRYX").Id, PartyType: PartyType.Vendor,
            Source: EpisodeSource.Receipt), cancellationToken);
        await InvokeAsync<ReceiveVendorReplacementResult>(new ReceiveVendorReplacement(
            DemoIds.FromName("p5:vendor-replacement-received"), rma.RmaLineId,
            await LiveEntitlementIdAsync(component.UnitId, CoverageFamilyCode.Supplier, cancellationToken),
            replacement.UnitId, WarehouseId, today.AddDays(-1), DemoDates.At(today.AddDays(-1), 10),
            LegalEntityId, ActorRef: actor), cancellationToken);
    }

    private async Task CreateReplacementCorrectionAsync(CancellationToken cancellationToken)
    {
        var correctionId = DemoIds.FromName("p5:wrong-replacement-link:correction");
        using (var scope = TenantScope())
        {
            if (await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Corrections.AsNoTracking()
                    .AnyAsync(row => row.CorrectionId == correctionId, cancellationToken)) return;
        }

        var original = Units("DW-300")[13];
        var recorded = Units("DW-300")[36];
        var actual = Units("DW-300")[37];
        var rma = await RequestAndAuthoriseRmaAsync("wrong-replacement-link", original, Party("C-ASHVANE"),
            DemoDates.At(today.AddDays(-7), 10), CoverageFamilyCode.Base,
            "Replacement unit was scanned incorrectly at shipping", cancellationToken);
        await CreateAndShipReplacementAsync("wrong-replacement-link", ReplacementSourceKind.Rma, rma.RmaId,
            rma.EntitlementId!.Value, recorded, ReplacementReason.WarrantyDefect,
            DemoDates.At(today.AddDays(-6), 10), DemoDates.At(today.AddDays(-5), 10), "INR", "IN",
            cancellationToken, returnReason: "warranty_defect");
        var reason = CorrectReplacementLinkReason.WrongUnitScannedAtShipping;
        await InvokeAsync<CorrectReplacementLinkResult>(new CorrectReplacementLink(correctionId, recorded.UnitId,
            actual.UnitId, reason, DemoDates.At(today.AddDays(-4), 10), actor, LegalEntityId,
            "Warehouse count and customer scan agree on the physical unit",
            new CorrectionMetadata(correctionId, reason, "Physical delivery evidence checked",
                EvidenceRefs: ["DEMO-CYCLE-COUNT", "DEMO-CUSTOMER-SCAN"],
                InitiatingRole: CorrectionInitiatingRole.CustomerService,
                Approvers: [new CorrectionApprover(actor, [YojanaRole.ServiceManager])]),
            new CorrectionCommitter(actor, [YojanaRole.ServiceManager])), cancellationToken);
    }

    private async Task<RmaScenario> RequestRmaAsync(string key, DemoUnit unit, DemoCatalogue.CatalogueParty party,
        DateTime requestedAt, string coverageFamily, string description, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync<RequestRmaResult>(new RequestRma(DemoIds.FromName("p5:rma-request:" + key),
            DateOnly.FromDateTime(requestedAt), party.Id, party.Id, ServiceEntityId, LegalEntityId,
            CountryFor(party.Code), CurrencyFor(party.Code),
            [new RmaRequestLineInput(UnitId: unit.UnitId, CoverageFamily: coverageFamily, RequestedAmount: 500m,
                ClaimLineKind: ClaimLineKind.Parts, Description: description)], requestedAt, actor), cancellationToken);
        return new(result.RmaId, result.LineIds.Single(), null);
    }

    private async Task<RmaScenario> RequestAndAuthoriseRmaAsync(string key, DemoUnit unit,
        DemoCatalogue.CatalogueParty party, DateTime requestedAt, string coverageFamily, string description,
        CancellationToken cancellationToken)
    {
        var requested = await RequestRmaAsync(key, unit, party, requestedAt, coverageFamily, description, cancellationToken);
        var authorised = await InvokeAsync<AuthoriseRmaResult>(new AuthoriseRma(
            DemoIds.FromName("p5:rma-authorise:" + key), requested.RmaId, requestedAt.AddHours(1), actor), cancellationToken);
        var line = authorised.Lines.Single();
        return requested with { EntitlementId = line.EntitlementId };
    }

    private async Task ReceiveRmaAsync(string key, RmaScenario rma, DemoUnit? received, string grade,
        string valuationRule, DateTime receivedAt, string outcome, Guid locationId, CancellationToken cancellationToken,
        string? exceptionNote = null)
    {
        await InvokeAsync<ReceiveReturnResult>(new ReceiveReturn(DemoIds.FromName("p5:rma-receive:" + key),
            rma.RmaId, locationId,
            [new ReturnReceiptLineInput(rma.RmaLineId,
                received is null ? null : new DocumentSerial(received.Serial, IdentifierType: IdentifierType.InternalSerial),
                grade, valuationRule, outcome, exceptionNote)], receivedAt.AddHours(-8), receivedAt, receivedAt,
            actor), cancellationToken);
    }

    private async Task DispatchRmaAsync(string key, RmaScenario rma, DateTime dispatchedAt,
        string? carrierName, string? trackingReference, CancellationToken cancellationToken)
    {
        await InvokeAsync<RecordReturnDispatchedResult>(new RecordReturnDispatched(
            DemoIds.FromName("p5:rma-dispatch:" + key), rma.RmaId, dispatchedAt, carrierName,
            trackingReference, actor), cancellationToken);
    }

    private async Task DispositionRmaAsync(string key, RmaScenario rma, string disposition, DateTime at,
        CancellationToken cancellationToken, Guid? vendorPartyRef = null)
    {
        await InvokeAsync<DispositionRmaResult>(new DispositionRma(DemoIds.FromName("p5:rma-disposition:" + key),
            rma.RmaId, [new RmaDispositionLineInput(rma.RmaLineId, disposition, vendorPartyRef)],
            DateOnly.FromDateTime(at), at, actor), cancellationToken);
    }

    private async Task CloseRmaAsync(string key, RmaScenario rma, DateTime at, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync<CloseRmaResult>(new CloseRma(DemoIds.FromName("p5:rma-close:" + key),
            rma.RmaId, at, actor), cancellationToken);
        if (!result.Closed)
            throw new InvalidOperationException($"RMA demo '{key}' has open follow-ups: {string.Join(", ", result.OpenFollowUps.Select(row => row.Code))}");
    }

    private async Task<ShipWarrantyReplacementResult> CreateAndShipReplacementAsync(string key, string sourceKind,
        Guid sourceId, Guid sourceEntitlementId, DemoUnit replacement, string reason, DateTime createdAt,
        DateTime shippedAt, string currency, string country, CancellationToken cancellationToken,
        DateOnly? returnDue = null, decimal? nonReturnAmount = null, string? replacementPolicy = null,
        string conditionAllowed = ReplacementConditionAllowed.Any, string? returnReason = null,
        decimal restockingFeeBasis = 0m)
    {
        var created = await InvokeAsync<CreateWarrantyReplacementResult>(new CreateWarrantyReplacement(
            DemoIds.FromName("p5:replacement-create:" + key), sourceKind, sourceId, sourceEntitlementId,
            replacement.UnitId, reason, ReplacementPricePolicy.Zero, 0m, currency, country, false, false,
            createdAt, LegalEntityId, actor, conditionAllowed, replacementPolicy, returnDue,
            returnDue is null ? null : NonReturnPriceBasis.List, returnReason, restockingFeeBasis,
            nonReturnAmount), cancellationToken);
        var shipped = await InvokeAsync<ShipWarrantyReplacementResult>(new ShipWarrantyReplacement(
            DemoIds.FromName("p5:replacement-ship:" + key), created.Document.Document.WarrantyReplacementId,
            DateOnly.FromDateTime(shippedAt), shippedAt, actor), cancellationToken);
        // Shipping transfers entitlement coverage. Let the durable coverage subscriber re-adjudicate affected service
        // records before this deterministic scenario advances another command against those same rowversion rows.
        await WaitForAutomaticCertificatesAsync(cancellationToken);
        return shipped;
    }

    private async Task<ApprovedClaim> CreateApprovedClaimAsync(string key, DemoUnit unit,
        DemoCatalogue.CatalogueParty customer, string coverageFamily, DateTime submittedAt, string description,
        CancellationToken cancellationToken)
    {
        var submitted = await InvokeAsync<SubmitClaimResult>(new SubmitClaim(DemoIds.FromName("p5:claim:" + key),
            unit.UnitId, null, DateOnly.FromDateTime(submittedAt), customer.Id, customer.Id, ServiceEntityId,
            LegalEntityId, CountryFor(customer.Code), CurrencyFor(customer.Code),
            [new ClaimLineInput(ClaimLineKind.Parts, coverageFamily, 500m, Description: description)],
            [new ClaimEvidenceInput(ClaimEvidenceKind.Attachment, "DEMO-P5-" + key.ToUpperInvariant(), "Demo evidence")],
            ClaimRemedy.Replacement, "P5-CHOICE-" + key.ToUpperInvariant(), submittedAt, actor), cancellationToken);
        if (!submitted.Replayed) p5ClaimsCreated++;
        var adjudicated = await InvokeAsync<AdjudicateClaimResult>(new AdjudicateClaim(
            DemoIds.FromName("p5:claim-adjudicate:" + key), submitted.ClaimId, submittedAt.AddHours(1), actor,
            new ClaimReviewer(actor, [YojanaRole.ServiceManager])), cancellationToken);
        var entitlementId = adjudicated.Lines.Single().EntitlementId
            ?? throw new InvalidOperationException($"P5 claim '{key}' did not resolve to an entitlement.");
        return new(submitted.ClaimId, entitlementId);
    }

    private async Task<ApprovedClaim> ClaimSourceAsync(string key, CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var commandId = DemoIds.FromName("claim:" + key);
        var row = await (from claim in db.Claims.AsNoTracking()
                         join line in db.ClaimLines.AsNoTracking()
                             on new { claim.TenantId, claim.ClaimId } equals new { line.TenantId, line.ClaimId }
                         where claim.CommandId == commandId && line.EntitlementId != null
                         orderby line.Sequence
                         select new { claim.ClaimId, EntitlementId = line.EntitlementId!.Value })
            .FirstAsync(cancellationToken);
        return new(row.ClaimId, row.EntitlementId);
    }

    private async Task<Guid> LiveEntitlementIdAsync(Guid unitId, string family, CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements.AsNoTracking()
            .Where(row => row.UnitId == unitId && row.CoverageFamily == family && row.Status != EntitlementStatus.Closed)
            .OrderByDescending(row => row.CreatedAt).Select(row => row.EntitlementId).FirstAsync(cancellationToken);
    }

    private async Task<Guid> OriginalEntitlementIdAsync(Guid unitId, string family, CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements.AsNoTracking()
            .Where(row => row.UnitId == unitId && row.CoverageFamily == family)
            .OrderBy(row => row.CreatedAt).Select(row => row.EntitlementId).FirstAsync(cancellationToken);
    }

    private async Task<DemoUnit> CommissionScenarioUnitAsync(string key, string itemCode, string serial,
        DateTime at, Guid locationId, string locationKind, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync<CommissionUnitsResult>(new CommissionUnits(DemoIds.FromName("p5:commission:" + key),
            Item(itemCode).Id, UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])],
            at, actor, LegalEntityId, LocationId: locationId, LocationKind: locationKind, Source: Source), cancellationToken);
        var line = result.Lines.Single();
        if (line.UnitId is null) throw new InvalidOperationException($"P5 scenario unit '{key}' was not commissioned: {string.Join(", ", line.Reasons)}");
        return new(line.UnitId.Value, itemCode, serial, null);
    }

    private async Task WaitForLinkStateAsync(Guid replacementLinkId, string state, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            using var scope = TenantScope();
            var current = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().ReplacementLinks.AsNoTracking()
                .Where(row => row.ReplacementLinkId == replacementLinkId).Select(row => row.State).SingleAsync(cancellationToken);
            if (current == state) return;
            await Task.Delay(100, cancellationToken);
        }
        throw new InvalidOperationException($"Replacement link {replacementLinkId} did not reach '{state}'.");
    }

    private async Task WaitForCurrentOwnerAsync(IReadOnlyList<DemoUnit> saleUnits, Guid invoicePartyRef, Guid endOwnerPartyRef,
        CancellationToken cancellationToken)
    {
        var unitIds = saleUnits.Select(unit => unit.UnitId).ToList();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            using var scope = TenantScope();
            var owned = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Current.AsNoTracking()
                .CountAsync(row => unitIds.Contains(row.UnitId)
                    && (row.OwnerPartyRef == invoicePartyRef || row.OwnerPartyRef == endOwnerPartyRef), cancellationToken);
            if (owned == unitIds.Count) return;
            await Task.Delay(100, cancellationToken);
        }
        throw new InvalidOperationException($"Sales posting did not establish ownership for all {unitIds.Count} unit(s).");
    }

    private async Task<bool> HasDocumentEventsAsync(DocumentRef document, string eventType,
        IReadOnlyList<DemoUnit> documentUnits, CancellationToken cancellationToken)
    {
        var unitIds = documentUnits.Select(unit => unit.UnitId).ToList();
        using var scope = TenantScope();
        var recorded = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Events.AsNoTracking()
            .Where(row => row.DocumentType == document.Type && row.DocumentId == document.Id
                && row.EventType == eventType && unitIds.Contains(row.UnitId))
            .Select(row => row.UnitId).Distinct().CountAsync(cancellationToken);
        return recorded == unitIds.Count;
    }

    private async Task WaitForEntitlementRequirementsAsync(IReadOnlyList<DemoUnit> saleUnits,
        CancellationToken cancellationToken)
    {
        var unitIds = saleUnits.Select(unit => unit.UnitId).ToList();
        for (var attempt = 0; attempt < 300; attempt++)
        {
            using var scope = TenantScope();
            var pending = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().EntitlementRequirements.AsNoTracking()
                .AnyAsync(row => unitIds.Contains(row.UnitId) && row.Status == EntitlementRequirementStatus.Open, cancellationToken);
            if (!pending) return;
            await Task.Delay(100, cancellationToken);
        }
        throw new InvalidOperationException($"Entitlement requirements did not settle for all {unitIds.Count} unit(s).");
    }

    private static string CountryFor(string partyCode) => partyCode switch
    {
        "C-ASHVANE" or "C-CLOUDMERE" => "IN",
        "C-NANDIRA" or "C-TORRENFOLD" => "DE",
        _ => "GB",
    };

    private static string CurrencyFor(string partyCode) => partyCode switch
    {
        "C-ASHVANE" or "C-CLOUDMERE" => "INR",
        "C-NANDIRA" or "C-TORRENFOLD" => "EUR",
        _ => "GBP",
    };

    private async Task CreateCampaignsAsync(CancellationToken cancellationToken)
    {
        var at = DemoDates.At(today.AddDays(-10), 9);
        activeRecallCampaignId = DemoIds.FromName("campaign:RC-PSU-1600-26A");
        var recallCriteria = new List<CampaignCriterionInput>
        {
            new(CampaignCriterionKind.Child, ChildItemId: Item("PS-1600").Id, ChildLotId: AffectedPsuLotId),
            new(CampaignCriterionKind.Firmware, FirmwareValue: "1.2.0", FirmwareKey: "psu.firmware"),
            new(CampaignCriterionKind.Firmware, FirmwareValue: "1.2.1", FirmwareKey: "psu.firmware"),
            new(CampaignCriterionKind.Firmware, FirmwareValue: "1.2.2", FirmwareKey: "psu.firmware"),
        };
        var recall = await InvokeAsync<CampaignResult>(new CreateCampaign(activeRecallCampaignId, "RC-PSU-1600-26A",
            "PSU-1600 lot KPL-2511 firmware inspection", CampaignCoverageSource.Recall, "RECALL-PSU-26A",
            recallCriteria, at, actor, CampaignReason.Reliability,
            "Power-supply components from the affected lot can degrade on the listed firmware releases.", today.AddMonths(6)), cancellationToken);
        if (recall.Campaign.Status is CampaignStatus.Draft or CampaignStatus.Selected)
        {
            await InvokeAsync<SelectCampaignUnitsResult>(new SelectCampaignUnits(DemoIds.FromName("campaign-select:RC-PSU-1600-26A"), activeRecallCampaignId,
                at.AddHours(1), LegalEntityId, actor), cancellationToken);
            await InvokeAsync<CampaignResult>(new UpdateCampaign(DemoIds.FromName("campaign-activate:RC-PSU-1600-26A"), activeRecallCampaignId,
                "RC-PSU-1600-26A", "PSU-1600 lot KPL-2511 firmware inspection", CampaignCoverageSource.Recall, "RECALL-PSU-26A",
                CampaignStatus.Active, recallCriteria, at.AddHours(2), actor, CampaignReason.Reliability,
                "Power-supply components from the affected lot can degrade on the listed firmware releases.", today.AddMonths(6)), cancellationToken);
        }

        endedGoodwillCampaignId = DemoIds.FromName("campaign:GW-BMC-RS1100-26");
        var goodwillCriteria = new List<CampaignCriterionInput>
        {
            new(CampaignCriterionKind.Child, ChildItemId: Item("BM-2").Id),
            new(CampaignCriterionKind.IdentifierRange, Scheme: IdentifierScheme.Internal,
                RangeStart: Serial("RS-1100", 1), RangeEnd: Serial("RS-1100", 36)),
            new(CampaignCriterionKind.Firmware, FirmwareValue: "3.4.0", FirmwareKey: "bmc.firmware"),
        };
        var goodwill = await InvokeAsync<CampaignResult>(new CreateCampaign(endedGoodwillCampaignId, "GW-BMC-RS1100-26",
            "RS-1100 management-controller firmware update", CampaignCoverageSource.Goodwill, "GOODWILL-BMC-26",
            goodwillCriteria, at.AddMinutes(10), actor, CampaignReason.Firmware,
            "Bring eligible management controllers onto the supported firmware release.", today.AddDays(-3),
            PartsPct: 100m, LabourPct: 50m, TravelPct: 0m), cancellationToken);
        if (goodwill.Campaign.Status is CampaignStatus.Draft or CampaignStatus.Selected)
        {
            await InvokeAsync<SelectCampaignUnitsResult>(new SelectCampaignUnits(DemoIds.FromName("campaign-select:GW-BMC-RS1100-26"), endedGoodwillCampaignId,
                at.AddHours(1), LegalEntityId, actor), cancellationToken);
            await InvokeAsync<CampaignResult>(new UpdateCampaign(DemoIds.FromName("campaign-activate:GW-BMC-RS1100-26"), endedGoodwillCampaignId,
                "GW-BMC-RS1100-26", "RS-1100 management-controller firmware update", CampaignCoverageSource.Goodwill, "GOODWILL-BMC-26",
                CampaignStatus.Active, goodwillCriteria, at.AddHours(2), actor, CampaignReason.Firmware,
                "Bring eligible management controllers onto the supported firmware release.", today.AddDays(-3),
                PartsPct: 100m, LabourPct: 50m, TravelPct: 0m), cancellationToken);
        }
        else if (goodwill.Campaign.Status == CampaignStatus.Active
            && (goodwill.Campaign.PartsPct != 100m || goodwill.Campaign.LabourPct != 50m || goodwill.Campaign.TravelPct != 0m))
        {
            // A dataset created before campaign percentages existed is upgraded once; later runs see the target values
            // and remain no-ops like the rest of this loader.
            await InvokeAsync<CampaignResult>(new UpdateCampaign(DemoIds.FromName("campaign-cover:GW-BMC-RS1100-26"), endedGoodwillCampaignId,
                "GW-BMC-RS1100-26", "RS-1100 management-controller firmware update", CampaignCoverageSource.Goodwill, "GOODWILL-BMC-26",
                CampaignStatus.Active, goodwillCriteria, at.AddHours(2), actor, CampaignReason.Firmware,
                "Bring eligible management controllers onto the supported firmware release.", today.AddDays(-3),
                PartsPct: 100m, LabourPct: 50m, TravelPct: 0m), cancellationToken);
        }
    }

    private async Task PrintSummaryAsync(CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var unitCount = await db.Units.CountAsync(cancellationToken);
        var entitlementCount = await db.Entitlements.CountAsync(cancellationToken);
        var claimCount = await db.Claims.CountAsync(cancellationToken);
        var recoveryCount = await db.RecoveryClaims.CountAsync(cancellationToken);
        var campaignCount = await db.Campaigns.CountAsync(cancellationToken);
        Console.WriteLine();
        Console.WriteLine("IT-hardware demo dataset is ready for tenant DEV.");
        var rmaCounts = await db.Rmas.AsNoTracking().GroupBy(row => row.Stage).Select(group => new { Stage = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Stage, row => row.Count, cancellationToken);
        var replacementCount = await db.WarrantyReplacements.CountAsync(cancellationToken);
        var awaitingCount = await db.ReplacementLinks.CountAsync(row => row.State == ReplacementLinkState.AwaitingReturn, cancellationToken);
        var overdueCount = await db.ReplacementLinks.CountAsync(row => row.OverdueEventId != null && row.State == ReplacementLinkState.AwaitingReturn, cancellationToken);
        var heldCustomers = await db.PartyReplacementHolds.CountAsync(row => row.LiftedAt == null, cancellationToken);
        var refurbishmentCount = await db.RefurbishmentWorkOrders.CountAsync(cancellationToken);
        var certificateCount = await db.CertificateInstances.CountAsync(cancellationToken);
        var certificateTriggerCount = await db.CertificateTriggers.CountAsync(cancellationToken);
        var refusedCertificateTriggerCount = await db.CertificateTriggers.CountAsync(row => row.Outcome == "refused", cancellationToken);
        var labelTemplateCount = await db.LabelTemplates.CountAsync(cancellationToken);
        var printJobCount = await db.PrintJobs.CountAsync(cancellationToken);
        var rejectedPrintJobCount = await db.PrintJobs.CountAsync(row => row.Status == LabelPrintStatus.Rejected, cancellationToken);
        Console.WriteLine($"Units {unitCount}; assembled finished goods {assembled}; entitlements {entitlementCount}; claims {claimCount}; recovery claims {recoveryCount}; campaigns {campaignCount}.");
        Console.WriteLine($"RMAs {rmaCounts.Values.Sum()} ({string.Join(", ", RmaStage.All.Select(stage => $"{stage} {rmaCounts.GetValueOrDefault(stage)}"))}); replacements {replacementCount}; awaiting return {awaitingCount}; overdue {overdueCount}; customers on hold {heldCustomers}; refurbishment work orders {refurbishmentCount}.");
        Console.WriteLine($"Certificates {certificateCount}; certificate triggers {certificateTriggerCount} ({refusedCertificateTriggerCount} refused); label templates {labelTemplateCount}; print jobs {printJobCount} ({rejectedPrintJobCount} rejected).");
        Console.WriteLine($"This run added {claimsCreated + p5ClaimsCreated} claim(s) and approved {recoveriesApproved} vendor answer(s); stable command ids make re-runs no-ops.");
    }

    private async Task ReceiptAsync(string number, Guid vendor, DateOnly day, IReadOnlyList<DocumentLine> lines,
        int expectedInbound, CancellationToken cancellationToken)
    {
        var reference = Document(number, "receipt");
        var at = DemoDates.At(day, 8);
        documents.Add(new ReceiptDocument(reference, vendor, at, lines));
        var envelope = Envelope("receipt:" + number, at);
        var posted = await InvokeAsync<ConsumedEventResult>(new PurchasingReceiptPosted(envelope, reference), cancellationToken);
        if (posted.Exceptions.Count > 0) throw new InvalidOperationException($"receipt {number}: {string.Join("; ", posted.Exceptions)}");
        for (var attempt = 0; expectedInbound > 0 && attempt < 300; attempt++)
        {
            using var scope = TenantScope();
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var count = await db.Entitlements.AsNoTracking().CountAsync(row => row.Direction == EntitlementDirection.Inbound
                && row.SourceDocumentType == reference.Type && row.SourceDocumentId == reference.Id, cancellationToken);
            if (count >= expectedInbound) break;
            if (attempt == 299) throw new InvalidOperationException($"receipt {number}: inbound entitlement handler created {count} of {expectedInbound} expected rows");
            await Task.Delay(100, cancellationToken);
        }
        await InvokeAsync<CreateInboundEntitlementsResult>(new CreateInboundEntitlements(envelope, reference), cancellationToken);
    }

    private DocumentLine Line(string code, int line, int count, int start = 1, Guid? lot = null) =>
        new(line, Item(code).Id, count, Enumerable.Range(start, count).Select(sequence => new DocumentSerial(Serial(code, sequence),
            IdentifierType: IdentifierType.InternalSerial)).ToList(), WarehouseId, LocationKind.Warehouse, lot, SerialCaptureMethod: SerialCaptureMethodValue.Scanned);

    private async Task AssembleAsync(DemoUnit parent, IReadOnlyList<ChildInput> children, int index, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync<RecordAssemblyResult>(new RecordAssembly(DemoIds.FromName("assembly:" + parent.Serial), parent.UnitId,
            children, DemoDates.At(today.AddDays(-86), 8).AddMinutes(index), actor, LegalEntityId, Document: ProductionOrder), cancellationToken);
        if (result.LinkIds.Count > 0) assembled++;
    }

    private static List<ChildInput> Components(params object[] groups)
    {
        var result = new List<ChildInput>();
        for (var index = 0; index < groups.Length; index += 2)
        {
            var children = (IReadOnlyList<DemoUnit>)groups[index];
            var prefix = (string)groups[index + 1];
            result.AddRange(children.Select((unit, childIndex) => new ChildInput(unit.UnitId, Slot: $"{prefix}-{childIndex + 1:D2}")));
        }
        return result;
    }

    private async Task AttributeAsync(DemoUnit unit, string key, string value, DateTime at, CancellationToken cancellationToken) =>
        await InvokeAsync<RecordUnitAttributesResult>(new RecordUnitAttributes(DemoIds.FromName($"attribute:{unit.Serial}:{key}:{value}"), unit.UnitId,
            new Dictionary<string, string?> { [key] = value }, at, Source, LegalEntityId), cancellationToken);

    private async Task CreateEntitlementAsync(DemoUnit unit, Guid policyVersionId, string family, DemoCatalogue.CatalogueParty owner,
        DocumentRef invoice, DateOnly contractDate, DateTime eventTime, string country, string channel, CancellationToken cancellationToken)
    {
        // A sold unit whose item requires cover on sale gets it from the entitlement-requirement handler, which runs
        // off the bus after the invoice. Let it finish, then strengthen delivery fallback evidence to the invoice or
        // create only what the sale did not.
        if (await LiveEntitlementAfterRequirementsAsync(unit.UnitId, family, cancellationToken) is { } live)
        {
            if (live.StartBasis != StartTrigger.Invoice)
            {
                await InvokeAsync<EntitlementChangeResult>(new RebaseEntitlementStart(
                    DemoIds.FromName($"entitlement-rebase:{family}:{unit.Serial}"), live.EntitlementId,
                    StartTrigger.Invoice, eventTime, $"{invoice.Type}:{invoice.Id:N}", eventTime.AddHours(4), actor), cancellationToken);
            }
            return;
        }
        if (await HasAnyEntitlementAsync(unit.UnitId, family, cancellationToken)) return;
        await InvokeAsync<CreateEntitlementResult>(new CreateEntitlement(DemoIds.FromName($"entitlement:{family}:{unit.Serial}"), unit.UnitId,
            family, contractDate, LegalEntityFor(country), eventTime.AddHours(3), actor,
            new Dictionary<string, DateTime> { [StartTrigger.Invoice] = eventTime }, policyVersionId,
            owner.Id, PartyType.EndUser, owner.Id, PartyType.EndUser, "new", channel, country,
            StartEvidenceRef: invoice.Id.ToString("N"), SourceDocument: invoice, CustomerPoRef: $"PO-{owner.Code}-{today.Year}",
            BuyerKind: RefurbishedBuyerKind.Business), cancellationToken);
    }

    private async Task<LiveEntitlementFact?> LiveEntitlementAfterRequirementsAsync(Guid unitId, string family, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var scope = TenantScope();
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var live = await db.Entitlements.AsNoTracking().Where(row => row.UnitId == unitId && row.CoverageFamily == family
                    && row.Status != EntitlementStatus.Closed)
                .Select(row => new LiveEntitlementFact(row.EntitlementId, row.StartBasis)).FirstOrDefaultAsync(cancellationToken);
            if (live is not null) return live;
            if (!await db.EntitlementRequirements.AsNoTracking().AnyAsync(row => row.UnitId == unitId
                    && row.Status == EntitlementRequirementStatus.Open, cancellationToken)) return null;
            await Task.Delay(100, cancellationToken);
        }

        return null;
    }

    private async Task<bool> HasAnyEntitlementAsync(Guid unitId, string family, CancellationToken cancellationToken)
    {
        using var scope = TenantScope();
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements.AsNoTracking()
            .AnyAsync(row => row.UnitId == unitId && row.CoverageFamily == family, cancellationToken);
    }

    private async Task<Guid> EnsurePolicyAsync(PolicyDefinition definition, CancellationToken cancellationToken)
    {
        using (var scope = TenantScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var existing = await db.PolicyVersions.AsNoTracking().Where(row => row.PolicyCode == definition.Code)
                .OrderByDescending(row => row.Version).FirstOrDefaultAsync(cancellationToken);
            if (existing?.Status == PolicyVersionStatus.Published) return existing.PolicyVersionId;
            if (existing?.Status == PolicyVersionStatus.Draft)
            {
                var published = await InvokeAsync<PolicyDraftResult>(new PublishPolicyVersion(existing.PolicyVersionId, actor), cancellationToken);
                return published.PolicyVersionId;
            }
        }

        var draft = await InvokeAsync<PolicyDraftResult>(new CreatePolicyDraft(DemoIds.FromName("policy:" + definition.Code), definition.Code,
            definition.Name, definition.Family, definition.Type, today.AddDays(-500), definition.Terms, actor), cancellationToken);
        var result = await InvokeAsync<PolicyDraftResult>(new PublishPolicyVersion(draft.PolicyVersionId, actor), cancellationToken);
        return result.PolicyVersionId;
    }

    private PolicyDefinition Policy(string code, string name, string family, int months, string serviceMode,
        decimal travel = 0, int? responseHours = null, IReadOnlyList<string>? regions = null, string durationBasis = "time",
        IReadOnlyList<UsageLimitTerm>? usage = null, string type = EntitlementType.Manufacturer, string assurance = AssuranceOrService.Assurance) =>
        new(code, name, family, type, new PolicyTerms(new DurationRule(months, DurationRule.Months), durationBasis, usage ?? [],
            new StartRule(StartTrigger.Invoice, [new StartFallback(StartTrigger.Deliver)], 30), new CoverageTerms(100, 100, travel, serviceMode, responseHours,
                responseHours is null ? null : "business_hours"), ["misuse", "unauthorised_modification", "consumables"],
            regions ?? ["IN", "GB", "DE", "FR", "US"], "yes", "full", null, [], 30, assurance, PolicyTerms.TimeZoneUtc,
            ServiceContractRegulated: assurance == AssuranceOrService.Service) with { ReplacementPolicyCode = "DEMO-REPLACE-90" });

    private PolicyDefinition SupplierPolicy(string code, string name, int months) =>
        new(code, name, CoverageFamilyCode.Supplier, EntitlementType.Manufacturer,
            new PolicyTerms(new DurationRule(months, DurationRule.Months), "time", [], new StartRule(StartTrigger.Receipt, [], null),
                new CoverageTerms(100, 0, 0, "return_to_base"), [], ["IN", "GB", "DE", "US"], "no", "full", null, [],
                null, AssuranceOrService.Assurance, PolicyTerms.TimeZoneUtc) with { ReplacementPolicyCode = "DEMO-REPLACE-90" });

    private IReadOnlyList<SalePlan> SalesPlan() =>
    [
        new("hpc", "DN-HPC-2601", "IN-HPC-2601", Party("C-NANDIRA"), Party("C-NANDIRA"),
            [.. Units("RS-1100").Take(31), .. Units("SS-1110"), .. Units("NX-4850").Take(2)], "DE", "EUR", "direct", 60),
        new("hospital", "DN-HOSP-2601", "IN-HOSP-2601", Party("R-KEPLORA"), Party("C-VELMORA"),
            [Units("RT-1200T")[0], Units("RS-1520")[0], Units("LB-14")[4], Units("LB-14")[5]], "GB", "GBP", "reseller", 55),
        new("bank", "DN-BANK-2601", "IN-BANK-2601", Party("D-GRIDLOOM"), Party("C-ASHVANE"),
            [Units("RS-1100")[31], .. Units("SS-1112"), .. Units("DW-300").Take(6), .. Units("LB-14").Take(4)], "IN", "INR", "distributor", 50),
        new("engineering", "DN-ENG-2601", "IN-ENG-2601", Party("R-CIRRAVUE"), Party("C-TORRENFOLD"),
            [Units("RS-1520")[1], Units("RG-4800")[0], Units("DW-300")[6], Units("DW-300")[7]], "DE", "EUR", "reseller", 46),
        new("studio", "DN-STUDIO-2601", "IN-STUDIO-2601", Party("C-MIREVA"), Party("C-MIREVA"),
            [.. Units("PF-400").Take(3)], "GB", "GBP", "direct", 42),
        new("isp", "DN-ISP-2601", "IN-ISP-2601", Party("C-CLOUDMERE"), Party("C-CLOUDMERE"),
            [.. Units("NX-4850").Skip(2), .. Units("NR-220")], "IN", "INR", "direct", 38),
        new("p5-bank", "DN-P5-BANK", "IN-P5-BANK", Party("C-ASHVANE"), Party("C-ASHVANE"),
            [.. Units("DW-300").Skip(8).Take(6)], "IN", "INR", "direct", 25),
        new("p5-engineering", "DN-P5-ENG", "IN-P5-ENG", Party("R-CIRRAVUE"), Party("C-TORRENFOLD"),
            [.. Units("DW-300").Skip(14).Take(5)], "DE", "EUR", "reseller", 10),
        new("p5-cloudmere", "DN-P5-CLOUD", "IN-P5-CLOUD", Party("C-CLOUDMERE"), Party("C-CLOUDMERE"),
            [.. Units("DW-300").Skip(19).Take(5)], "IN", "INR", "direct", 18),
        new("p5-hospital", "DN-P5-HOSP", "IN-P5-HOSP", Party("R-KEPLORA"), Party("C-VELMORA"),
            [.. Units("DW-300").Skip(24).Take(4)], "GB", "GBP", "reseller", 15),
        new("p5-goodwill", "DN-P5-GOODWILL", "IN-P5-GOODWILL", Party("C-MIREVA"), Party("C-MIREVA"),
            [Units("PF-400")[3]], "GB", "GBP", "direct", 20),
    ];

    private List<DocumentLine> LinesFor(IReadOnlyList<DemoUnit> saleUnits) => saleUnits.GroupBy(unit => unit.Code)
        .Select((group, index) => new DocumentLine(index + 1, Item(group.Key).Id, group.Count(),
            group.Select(unit => new DocumentSerial(unit.Serial, IdentifierType: IdentifierType.InternalSerial)).ToList(),
            SerialCaptureMethod: SerialCaptureMethodValue.Scanned)).ToList();

    private ClaimPlan Claim(string key, DemoUnit unit, DemoCatalogue.CatalogueParty customer, string currency, DemoUnit? lineUnit,
        string kind, decimal amount, string description, ClaimAction action, string country = "IN", string family = CoverageFamilyCode.Base,
        IReadOnlyDictionary<string, decimal>? measurements = null, string remedy = ClaimRemedy.Repair, Guid? campaignId = null, DateOnly? claimDate = null) =>
        new(key, unit, customer, currency, country, [new ClaimLineInput(kind, family, amount, lineUnit?.UnitId, description, measurements)], action,
            Remedy: remedy, CampaignId: campaignId, ClaimDate: claimDate);

    private string PolicyCodeFor(string itemCode) => itemCode switch
    {
        "RS-1100" or "RT-1200T" or "RT-N1" or "RS-1520" or "RG-4800" => "DEMO-SERVER-36",
        "SS-1110" or "SS-1112" => "DEMO-STORAGE-60",
        "NX-4850" or "NR-220" => "DEMO-NETWORK-60",
        "DW-300" or "LB-14" => "DEMO-CLIENT-12",
        "PF-400" => "DEMO-PRINTER-12H",
        "AD-90" or "AC-65" => "DEMO-ACCESSORY-12",
        _ => throw new InvalidOperationException($"No outbound demo policy is assigned to {itemCode}.")
    };

    private DemoCatalogue.CatalogueItem Item(string code) => catalogue.Items.TryGetValue(code, out var item)
        ? item : throw new InvalidOperationException($"Catalogue item '{code}' is missing.");
    private DemoCatalogue.CatalogueParty Party(string code) => catalogue.Parties.TryGetValue(code, out var party)
        ? party : throw new InvalidOperationException($"Catalogue party '{code}' is missing.");
    private DemoCatalogue.CatalogueLocation Location(string code) => catalogue.Locations.TryGetValue(code, out var location)
        ? location : throw new InvalidOperationException($"Catalogue location '{code}' is missing.");
    private List<DemoUnit> Units(string code) => units.TryGetValue(code, out var rows) ? rows : [];
    private DocumentRef Document(string number, string type) => catalogue.Documents.TryGetValue(number, out var row)
        ? new DocumentRef(row.Type, row.Id) : new DocumentRef(type, DemoIds.FromName("document:" + number));
    private Guid LegalEntityFor(string country) => country == "GB" ? UkLegalEntityId : LegalEntityId;

    private ThinEnvelope Envelope(string name, DateTime at, Guid? legalEntityId = null) =>
        new(DemoIds.FromName("message:" + name), legalEntityId ?? LegalEntityId, at, Source);
    private static string Serial(string code, int sequence) => SerialPrefixes.TryGetValue(code, out var prefix)
        ? $"{prefix}{sequence:D6}"
        : throw new InvalidOperationException($"Serial prefix for catalogue item '{code}' is missing.");

    private IServiceScope TenantScope()
    {
        var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        return scope;
    }

    private async Task<T> InvokeAsync<T>(object message, CancellationToken cancellationToken) where T : class
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var scope = services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message,
                    new DeliveryOptions { TenantId = tenant.ToString() }, cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 4)
            {
                await Task.Delay(50 * (attempt + 1), cancellationToken);
            }
        }
    }

    private static async Task StepAsync(string name, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        Console.Write($"• {name} ... ");
        try
        {
            await work(cancellationToken);
            Console.WriteLine("done");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"{name}: {exception.Message}", exception);
        }
    }

    private static List<DemoUnit> Take(Queue<DemoUnit> queue, int count)
    {
        if (queue.Count < count) throw new InvalidOperationException($"component plan exhausted: wanted {count}, found {queue.Count}");
        return Enumerable.Range(0, count).Select(_ => queue.Dequeue()).ToList();
    }

    private sealed record PolicyDefinition(string Code, string Name, string Family, string Type, PolicyTerms Terms);
    private sealed record DemoUnit(Guid UnitId, string Code, string Serial, Guid? LotId);
    private sealed record LiveEntitlementFact(Guid EntitlementId, string? StartBasis);
    private sealed record RmaScenario(Guid RmaId, Guid RmaLineId, Guid? EntitlementId);
    private sealed record ApprovedClaim(Guid ClaimId, Guid EntitlementId);
    private sealed record ComponentFact(Guid LinkId, Guid ParentUnitId, Guid UnitId, string Serial);
    private sealed record P7ImportJobs(Guid Clean, Guid Spreadsheet, Guid Vendor);
    private sealed record SalePlan(string Key, string ShipmentNumber, string InvoiceNumber, DemoCatalogue.CatalogueParty InvoiceParty,
        DemoCatalogue.CatalogueParty EndOwner, IReadOnlyList<DemoUnit> Units, string Country, string Currency, string Channel, int DaysAgo);
    private sealed record ClaimPlan(string Key, DemoUnit? Unit, DemoCatalogue.CatalogueParty Customer, string Currency, string Country,
        IReadOnlyList<ClaimLineInput> Lines, ClaimAction Action, string Remedy = ClaimRemedy.Repair, string? UnknownIdentifier = null,
        Guid? ItemId = null, string? ProofRef = null, DateOnly? ProofDate = null, Guid? CampaignId = null, DateOnly? ClaimDate = null);
    private enum ClaimAction { Submit, Evidence, Adjudicate, ManualDeny, NoFaultDeny, Settle, Close }
}
