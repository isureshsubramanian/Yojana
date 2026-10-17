using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Seeding;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Queries;

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
    private DemoUnit? recoveryDimm;
    private DemoUnit? recoveryNvme;
    private DemoUnit? expiredPsu;
    private Guid activeRecallCampaignId;
    private Guid endedGoodwillCampaignId;
    private static readonly Dictionary<string, int> ExpectedUnitCounts = new(StringComparer.Ordinal)
    {
        ["RS-1100"] = 36, ["RT-1200T"] = 1, ["RT-N1"] = 2, ["RS-1520"] = 2, ["RG-4800"] = 1,
        ["SS-1110"] = 2, ["SS-1112"] = 2, ["NX-4850"] = 4, ["NR-220"] = 3, ["DW-300"] = 8,
        ["LB-14"] = 8, ["PF-400"] = 3, ["MD-64R"] = 172, ["ND-384"] = 96, ["HD-20T"] = 28,
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
    private Guid ServiceEntityId => Party("S-ASTERRA").Id;
    private Guid WarehouseId => DemoIds.FromName("location:asterra-demo-warehouse");
    private Guid AffectedPsuLotId => DemoIds.FromName("lot:KPL-2511");
    private Guid OrdinaryPsuLotId => DemoIds.FromName("lot:KPL-2601");
    private Guid OldPsuLotId => DemoIds.FromName("lot:KPL-2410");
    private DocumentRef ProductionOrder => Document("MO-2026-0901", "production_order");

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await StepAsync("verify the DEV tenant and configure currencies", SetUpTenantAsync, cancellationToken);
        await StepAsync("define and publish warranty policies", SetUpPoliciesAsync, cancellationToken);
        await StepAsync("post supplier receipts and create inbound cover", PostReceiptsAsync, cancellationToken);
        await StepAsync("commission manufactured units", CommissionManufacturedAsync, cancellationToken);
        await StepAsync("resolve the 513-unit serial plan", ResolveUnitsAsync, cancellationToken);
        await StepAsync("assemble and release finished goods", AssembleAndReleaseAsync, cancellationToken);
        await StepAsync("record PSU and BMC firmware history", RecordFirmwareAsync, cancellationToken);
        await StepAsync("post shipments, deliveries and invoices", PostSalesAsync, cancellationToken);
        await StepAsync("create outbound entitlements", CreateOutboundEntitlementsAsync, cancellationToken);
        await StepAsync("create recall and goodwill campaigns", CreateCampaignsAsync, cancellationToken);
        await StepAsync("submit and progress 27 claims", CreateClaimsAsync, cancellationToken);
        await PrintSummaryAsync(cancellationToken);
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
            Line("NX-4850", 1, 4), Line("NR-220", 2, 3), Line("DW-300", 3, 8),
            Line("LB-14", 4, 8), Line("PF-400", 5, 3),
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
                          where identifier.ValidTo == null && identifier.Type == IdentifierType.InternalSerial
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
    }

    private async Task PostSalesAsync(CancellationToken cancellationToken)
    {
        var sales = SalesPlan();
        foreach (var sale in sales)
        {
            var shippedAt = DemoDates.At(today.AddDays(-sale.DaysAgo), 9);
            var shipmentRef = Document(sale.ShipmentNumber, "shipment");
            var orderRef = new DocumentRef("sales_order", DemoIds.FromName("order:" + sale.Key));
            var lines = LinesFor(sale.Units);
            var shipment = new ShipmentDocument(shipmentRef, sale.InvoiceParty.Id, shippedAt, lines,
                DemoIds.FromName("site:" + sale.EndOwner.Code), Party("L-PARCELORA").Id, false, orderRef,
                $"PO-{sale.EndOwner.Code}-{today.Year}", IsInvoiced: true);
            documents.Add(shipment);
            await InvokeAsync<ConsumedEventResult>(new SalesShipmentPosted(Envelope("shipment:" + sale.Key, shippedAt), shipmentRef), cancellationToken);

            var deliveredAt = shippedAt.AddDays(2);
            var deliveryRef = new DocumentRef("delivery_confirmation", DemoIds.FromName("delivery:" + sale.Key));
            documents.Add(new DeliveryConfirmation(deliveryRef, shipmentRef, deliveredAt, shipment.ShipToLocationId));
            await InvokeAsync<ConsumedEventResult>(new SalesDeliveryConfirmed(Envelope("delivery:" + sale.Key, deliveredAt), deliveryRef), cancellationToken);

            var invoiceRef = Document(sale.InvoiceNumber, "invoice");
            var invoiceLines = sale.Units.GroupBy(unit => unit.Code).Select((group, index) =>
                new InvoiceLine(index + 1, Item(group.Key).Id, group.Count(), 1000m + index * 250m, sale.Currency)).ToList();
            documents.Add(new InvoiceDocument(invoiceRef, orderRef, sale.InvoiceParty.Id, LegalEntityId, shippedAt.AddDays(1), invoiceLines,
                shipment.CustomerPoRef, sale.Country, Channel: sale.Channel));
            await InvokeAsync<ConsumedEventResult>(new SalesInvoicePosted(Envelope("invoice:" + sale.Key, shippedAt.AddDays(1)), invoiceRef), cancellationToken);

            if (sale.InvoiceParty.Id != sale.EndOwner.Id)
            {
                foreach (var unit in sale.Units)
                {
                    await InvokeAsync<OpenOwnershipResult>(new OpenOwnership(DemoIds.FromName($"end-owner:{sale.Key}:{unit.Serial}"), unit.UnitId,
                        sale.EndOwner.Id, PartyType.EndUser, EpisodeReason.Sale, deliveredAt.AddHours(2), actor, LegalEntityId,
                        EpisodeSource.Registration, Document: invoiceRef, CustomerPoRef: shipment.CustomerPoRef), cancellationToken);
                    await InvokeAsync<ChangeCustodyResult>(new ChangeCustody(DemoIds.FromName($"end-custody:{sale.Key}:{unit.Serial}"), unit.UnitId,
                        LocationKind.CustomerSite, EpisodeReason.Sale, deliveredAt.AddHours(2), actor, LegalEntityId,
                        shipment.ShipToLocationId, sale.EndOwner.Id, PartyType.EndUser, EpisodeSource.Registration, Document: invoiceRef), cancellationToken);
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
            Claim("server-replacement", Units("RS-1100")[6], hpc, "USD", null, ClaimLineKind.Parts, 890m, "System board replacement", ClaimAction.Adjudicate, remedy: ClaimRemedy.Replacement),
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
        Console.WriteLine($"Units {unitCount}; assembled finished goods {assembled}; entitlements {entitlementCount}; claims {claimCount}; recovery claims {recoveryCount}; campaigns {campaignCount}.");
        Console.WriteLine($"This run added {claimsCreated} claim(s) and approved {recoveriesApproved} vendor answer(s); stable command ids make re-runs no-ops.");
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
        // off the bus after the invoice. Let it finish, then create only what the sale did not.
        if (await LiveEntitlementAfterRequirementsAsync(unit.UnitId, family, cancellationToken)) return;
        await InvokeAsync<CreateEntitlementResult>(new CreateEntitlement(DemoIds.FromName($"entitlement:{family}:{unit.Serial}"), unit.UnitId,
            family, contractDate, LegalEntityId, eventTime.AddHours(3), actor,
            new Dictionary<string, DateTime> { [StartTrigger.Invoice] = eventTime }, policyVersionId,
            owner.Id, PartyType.EndUser, owner.Id, PartyType.EndUser, "new", channel, country,
            StartEvidenceRef: invoice.Id.ToString("N"), SourceDocument: invoice, CustomerPoRef: $"PO-{owner.Code}-{today.Year}",
            BuyerKind: RefurbishedBuyerKind.Business), cancellationToken);
    }

    private async Task<bool> LiveEntitlementAfterRequirementsAsync(Guid unitId, string family, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var scope = TenantScope();
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            if (await db.Entitlements.AsNoTracking().AnyAsync(row => row.UnitId == unitId && row.CoverageFamily == family
                    && row.Status != EntitlementStatus.Closed, cancellationToken)) return true;
            if (!await db.EntitlementRequirements.AsNoTracking().AnyAsync(row => row.UnitId == unitId
                    && row.Status == EntitlementRequirementStatus.Open, cancellationToken)) return false;
            await Task.Delay(100, cancellationToken);
        }

        return false;
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
            new StartRule(StartTrigger.Invoice, [], null), new CoverageTerms(100, 100, travel, serviceMode, responseHours,
                responseHours is null ? null : "business_hours"), ["misuse", "unauthorised_modification", "consumables"],
            regions ?? ["IN", "GB", "DE", "FR", "US"], "yes", "full", null, [], 30, assurance, PolicyTerms.TimeZoneUtc,
            ServiceContractRegulated: assurance == AssuranceOrService.Service));

    private PolicyDefinition SupplierPolicy(string code, string name, int months) =>
        new(code, name, CoverageFamilyCode.Supplier, EntitlementType.Manufacturer,
            new PolicyTerms(new DurationRule(months, DurationRule.Months), "time", [], new StartRule(StartTrigger.Receipt, [], null),
                new CoverageTerms(100, 0, 0, "return_to_base"), [], ["IN", "GB", "DE", "US"], "no", "full", null, [],
                null, AssuranceOrService.Assurance, PolicyTerms.TimeZoneUtc));

    private IReadOnlyList<SalePlan> SalesPlan() =>
    [
        new("hpc", "DN-HPC-2601", "IN-HPC-2601", Party("C-NANDIRA"), Party("C-NANDIRA"),
            [.. Units("RS-1100").Take(32), .. Units("SS-1110"), .. Units("NX-4850").Take(2)], "DE", "EUR", "direct", 60),
        new("hospital", "DN-HOSP-2601", "IN-HOSP-2601", Party("R-KEPLORA"), Party("C-VELMORA"),
            [Units("RT-1200T")[0], Units("RS-1520")[0], Units("LB-14")[4], Units("LB-14")[5]], "GB", "GBP", "reseller", 55),
        new("bank", "DN-BANK-2601", "IN-BANK-2601", Party("D-GRIDLOOM"), Party("C-ASHVANE"),
            [.. Units("SS-1112"), .. Units("DW-300").Take(6), .. Units("LB-14").Take(4)], "IN", "INR", "distributor", 50),
        new("engineering", "DN-ENG-2601", "IN-ENG-2601", Party("R-CIRRAVUE"), Party("C-TORRENFOLD"),
            [Units("RS-1520")[1], Units("RG-4800")[0], Units("DW-300")[6], Units("DW-300")[7]], "DE", "EUR", "reseller", 46),
        new("studio", "DN-STUDIO-2601", "IN-STUDIO-2601", Party("C-MIREVA"), Party("C-MIREVA"),
            [.. Units("PF-400")], "GB", "GBP", "direct", 42),
        new("isp", "DN-ISP-2601", "IN-ISP-2601", Party("C-CLOUDMERE"), Party("C-CLOUDMERE"),
            [.. Units("NX-4850").Skip(2), .. Units("NR-220")], "IN", "INR", "direct", 38),
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
    private List<DemoUnit> Units(string code) => units.TryGetValue(code, out var rows) ? rows : [];
    private DocumentRef Document(string number, string type) => catalogue.Documents.TryGetValue(number, out var row)
        ? new DocumentRef(row.Type, row.Id) : new DocumentRef(type, DemoIds.FromName("document:" + number));
    private ThinEnvelope Envelope(string name, DateTime at) => new(DemoIds.FromName("message:" + name), LegalEntityId, at, Source);
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
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message,
            new DeliveryOptions { TenantId = tenant.ToString() }, cancellationToken);
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
    private sealed record SalePlan(string Key, string ShipmentNumber, string InvoiceNumber, DemoCatalogue.CatalogueParty InvoiceParty,
        DemoCatalogue.CatalogueParty EndOwner, IReadOnlyList<DemoUnit> Units, string Country, string Currency, string Channel, int DaysAgo);
    private sealed record ClaimPlan(string Key, DemoUnit? Unit, DemoCatalogue.CatalogueParty Customer, string Currency, string Country,
        IReadOnlyList<ClaimLineInput> Lines, ClaimAction Action, string Remedy = ClaimRemedy.Repair, string? UnknownIdentifier = null,
        Guid? ItemId = null, string? ProofRef = null, DateOnly? ProofDate = null, Guid? CampaignId = null, DateOnly? ClaimDate = null);
    private enum ClaimAction { Submit, Evidence, Adjudicate, ManualDeny, NoFaultDeny, Settle, Close }
}
