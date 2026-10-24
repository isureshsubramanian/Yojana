using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Reports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P7-T12: the three named service-desk reports, internal document disclosure and the bounded report read.</summary>
[Collection("serialisation host")]
public sealed class BackOfficeReportTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Ticket_T69_identifier_cross_reference_includes_every_identifier_and_indents_components()
    {
        var tenant = await fixture.NewTenantAsync("rpt69");
        var rootItem = Guid.NewGuid();
        var componentItem = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var root = await UnitAsync(tenant, rootItem, "RPT690001");
        var component = await UnitAsync(tenant, componentItem, "RPT69C001");
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), root, owner,
            PartyType.Customer, EpisodeReason.Sale, At.AddHours(1), "sales", Entity));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), component, owner,
            PartyType.Customer, EpisodeReason.Sale, At.AddHours(1), "sales", Entity));
        await AssignAsync(tenant, root, IdentifierType.ManufacturerSerial, "MFG690001");
        await AssignAsync(tenant, root, IdentifierType.VendorSerial, "VEN690001", owner);
        await AssignAsync(tenant, root, IdentifierType.CustomerSerial, "CUS690001", owner);
        await AssignAsync(tenant, root, IdentifierType.CustomerAssetTag, "TAG690001", owner);
        await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), root,
            [new ChildInput(component, Slot: "CPU")], At.AddHours(3), "builder", Entity));
        var selection = await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(),
            SelectionSource.ByPastedList(["RPT690001"]), "reporter"));
        RegisterNames(rootItem, "SYS-69", "System 69", componentItem, "PART-69", "Processor board", owner, "Northwind Arcadia");

        using var scope = fixture.ScopeFor(tenant);
        var page = await scope.ServiceProvider.GetRequiredService<BackOfficeReportQueries>()
            .IdentifierCrossReferenceAsync(selection.SelectionId, null, 50, CancellationToken.None);

        Assert.Equal(2, page.Items.Count);
        var rootRow = page.Items.Single(row => row.Depth == 0);
        Assert.Equal(("RPT690001", "MFG690001", "VEN690001", "CUS690001", "TAG690001"),
            (rootRow.InternalSerial, rootRow.ManufacturerSerial, rootRow.VendorSerial,
                rootRow.CustomerSerial, rootRow.AssetTag));
        Assert.Equal("SYS-69 · System 69", rootRow.Item);
        Assert.Equal("Northwind Arcadia", rootRow.CurrentOwner);
        var childRow = page.Items.Single(row => row.Depth == 1);
        Assert.Equal(("RPT690001", "CPU", "RPT69C001", "PART-69 · Processor board"),
            (childRow.ParentSerial, childRow.Slot, childRow.InternalSerial, childRow.Item));
    }

    [DatabaseFact]
    public async Task Ticket_T34_installed_base_and_stock_filter_two_items_and_page_by_keyset()
    {
        var tenant = await fixture.NewTenantAsync("rpt34");
        var firstItem = Guid.NewGuid();
        var secondItem = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var site = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var stockA = await UnitAsync(tenant, firstItem, "RPT340001", warehouse, LocationKind.Warehouse);
        var installed = await UnitAsync(tenant, firstItem, "RPT340002", warehouse, LocationKind.Warehouse);
        var stockB = await UnitAsync(tenant, secondItem, "RPT340003", warehouse, LocationKind.Warehouse);
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), installed, owner,
            PartyType.Customer, EpisodeReason.Sale, At.AddHours(1), "sales", Entity));
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), installed,
            LocationKind.CustomerSite, "delivery", At.AddHours(2), "shipping", Entity,
            site, owner, PartyType.Customer));
        RegisterNames(firstItem, "ITEM-34A", "Portable unit", secondItem, "ITEM-34B", "Docking unit", owner, "Aurora Works");
        var locations = fixture.Services.GetRequiredService<InMemoryLocationNameRegistry>();
        locations.Register(new LocationLabel(warehouse, "WH-01", "Main stock", LocationKind.Warehouse));
        locations.Register(new LocationLabel(site, "SITE-01", "Aurora service room", LocationKind.CustomerSite));

        using var scope = fixture.ScopeFor(tenant);
        var reports = scope.ServiceProvider.GetRequiredService<BackOfficeReportQueries>();
        var filter = new InstalledBaseStockFilter([firstItem, secondItem]);
        var first = await reports.InstalledBaseAndStockAsync(filter, null, 1, CancellationToken.None);
        var second = await reports.InstalledBaseAndStockAsync(filter, first.NextCursor, 10, CancellationToken.None);
        Assert.Single(first.Items);
        Assert.Equal(2, second.Items.Count);
        Assert.Equal(3, first.Items.Concat(second.Items).Select(row => row.Serial).Distinct().Count());

        var installedPage = await reports.InstalledBaseAndStockAsync(filter with { Holding = BackOfficeHolding.InstalledBase },
            null, 50, CancellationToken.None);
        var installedRow = Assert.Single(installedPage.Items);
        Assert.Equal(("RPT340002", "Aurora Works", "SITE-01 · Aurora service room"),
            (installedRow.Serial, installedRow.CurrentOwner, installedRow.Location));
        var stock = await reports.InstalledBaseAndStockAsync(filter with { Holding = BackOfficeHolding.Stock },
            null, 50, CancellationToken.None);
        Assert.Equal(2, stock.Items.Count);
        Assert.Contains(stock.Items, row => row.Serial == "RPT340001" && row.Location == "WH-01 · Main stock");
        Assert.Contains(stock.Items, row => row.Serial == "RPT340003");
        Assert.NotEqual(stockA, stockB);
    }

    [DatabaseFact]
    public async Task Ticket_T24_as_built_list_reports_component_part_and_serial_and_flags_missing_slots()
    {
        var tenant = await fixture.NewTenantAsync("rpt24");
        var rootItem = Guid.NewGuid();
        var componentItem = Guid.NewGuid();
        var firstOwner = Guid.NewGuid();
        var secondOwner = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().RegisterBom(rootItem,
            [new ExpectedComponent("BAREBONES", componentItem, "BB-24", 1)]);
        var complete = await UnitAsync(tenant, rootItem, "RPT240001");
        var missing = await UnitAsync(tenant, rootItem, "RPT240002");
        var component = await UnitAsync(tenant, componentItem, "RPT24BB01");
        await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), complete,
            [new ChildInput(component, Slot: "BAREBONES")], At.AddHours(1), "builder", Entity));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), complete, firstOwner,
            PartyType.Customer, EpisodeReason.Sale, At.AddHours(2), "sales", Entity,
            CustomerItemRef: "F-2401"));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), missing, secondOwner,
            PartyType.Customer, EpisodeReason.Sale, At.AddHours(2), "sales", Entity,
            CustomerItemRef: "F-2402"));
        RegisterNames(rootItem, "ROOT-24", "Field system", componentItem, "BB-24", "Barebones assembly",
            firstOwner, "Fictional Meridian");
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>()
            .Register(new PartyLabel(secondOwner, "C-2402", "Fictional Solstice", PartyType.Customer));
        var selection = await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(),
            SelectionSource.ByPastedList(["RPT240001", "RPT240002"]), "reporter"));

        using var scope = fixture.ScopeFor(tenant);
        var page = await scope.ServiceProvider.GetRequiredService<BackOfficeReportQueries>()
            .AsBuiltCompletenessAsync(selection.SelectionId, null, 50, DateOnly.FromDateTime(At), CancellationToken.None);

        var completeRow = page.Items.Single(row => row.RootSerial == "RPT240001");
        Assert.Equal("F-2401", completeRow.CustomerItemReference);
        Assert.Contains(completeRow.Components, row => row.Slot == "BAREBONES"
            && row.ComponentItem == "BB-24 · Barebones assembly" && row.ComponentSerial == "RPT24BB01"
            && row.Outcome == "complete");
        var missingRow = page.Items.Single(row => row.RootSerial == "RPT240002");
        Assert.Equal("F-2402", missingRow.CustomerItemReference);
        Assert.Contains(missingRow.Components, row => row.Slot == "BAREBONES"
            && row.ComponentSerial is null && row.Outcome == "missing");
    }

    [DatabaseFact]
    public async Task Internal_lookup_returns_current_and_prior_owner_sale_chains_with_internal_note_and_cost()
    {
        var tenant = await fixture.NewTenantAsync("rptint");
        var item = Guid.NewGuid();
        var warrantyItem = Guid.NewGuid();
        var firstOwner = Guid.NewGuid();
        var secondOwner = Guid.NewGuid();
        var unit = await UnitAsync(tenant, item, "RPTINT001");
        var firstDelivery = new DocumentRef("delivery", Guid.NewGuid());
        var secondDelivery = new DocumentRef("delivery", Guid.NewGuid());
        var first = await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit, firstOwner,
            PartyType.Customer, EpisodeReason.Sale, At.AddHours(1), "sales", Entity, Document: firstDelivery));
        var second = await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit, secondOwner,
            PartyType.Customer, EpisodeReason.Transfer, At.AddDays(20), "sales", Entity, Document: secondDelivery));
        var order = new DocumentRef("sales_order", Guid.NewGuid());
        var invoice = new DocumentRef("invoice", Guid.NewGuid());
        var documents = fixture.Services.GetRequiredService<InMemoryDocumentPorts>();
        documents.Add(new InternalSaleChainFact(first.EpisodeId, order, firstDelivery, invoice, At.AddHours(2),
            At.AddHours(3), [new(1, warrantyItem, 249.50m, "USD", "CARE-3", Guid.NewGuid(), "manual price approval")],
            "prior-owner chain retained for audit"));
        documents.Add(new InternalSaleChainFact(second.EpisodeId, order, secondDelivery, invoice, At.AddDays(21),
            At.AddDays(21), [], "current-owner chain"));
        RegisterNames(item, "SYS-INT", "Internal lookup unit", warrantyItem, "WRN-3", "Three-year care",
            firstOwner, "Fictional Atlas");
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>()
            .Register(new PartyLabel(secondOwner, "C-INT2", "Fictional Beacon", PartyType.Customer));
        var numberRegistry = fixture.Services.GetRequiredService<InMemoryDocumentNumberRegistry>();
        numberRegistry.Register(new DocumentLabel(order, "SO-7001"));
        numberRegistry.Register(new DocumentLabel(firstDelivery, "DN-7001"));
        numberRegistry.Register(new DocumentLabel(secondDelivery, "DN-7002"));
        numberRegistry.Register(new DocumentLabel(invoice, "INV-7001"));

        using var scope = fixture.ScopeFor(tenant);
        var result = await scope.ServiceProvider.GetRequiredService<BackOfficeReportQueries>()
            .InternalLookupAsync("RPTINT001", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.Ownership.Count);
        Assert.Contains(result.Ownership, row => row.Owner == "Fictional Atlas" && !row.Current
            && row.SaleChains.Single().InternalNote == "prior-owner chain retained for audit"
            && row.SaleChains.Single().WarrantyLines.Single().Price == 249.50m
            && row.SaleChains.Single().WarrantyLines.Single().InternalNote == "manual price approval");
        Assert.Contains(result.Ownership, row => row.Owner == "Fictional Beacon" && row.Current);
    }

    [DatabaseFact]
    public async Task Five_hundred_unit_identifier_report_has_no_per_row_database_queries()
    {
        const int count = 500;
        var tenant = await fixture.NewTenantAsync("rpt500");
        var item = Guid.NewGuid();
        var lines = Enumerable.Range(0, count)
            .Select(index => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"RPT5{index:D5}")]))
            .ToList();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item,
            UnitOrigin.Purchased, lines, At, "receiving", Entity));
        var selectionId = Guid.NewGuid();
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.SelectionSets.Add(new SelectionSet
            {
                TenantId = tenant, SelectionId = selectionId, SourceKind = SelectionSource.Kinds.PastedList,
                SourceJson = "{}", ResolvedCount = count, CreatedAt = At, CreatedBy = "reporter",
            });
            db.SelectionSetMembers.AddRange(commissioned.Lines.Select(line => new SelectionSetMember
            {
                TenantId = tenant, SelectionId = selectionId, UnitId = line.UnitId!.Value,
            }));
            await db.SaveChangesAsync();
        }

        using var reportScope = fixture.ScopeFor(tenant);
        var reports = reportScope.ServiceProvider.GetRequiredService<BackOfficeReportQueries>();
        var (page, statements) = await StatementCounter.CountAsync(() =>
            reports.IdentifierCrossReferenceAsync(selectionId, null, count, CancellationToken.None));

        Assert.Equal(count, page.Items.Count);
        Assert.True(statements < 12, $"500-unit identifier report took {statements} statements");
    }

    private async Task<Guid> UnitAsync(TenantId tenant, Guid item, string serial, Guid? location = null, string? locationKind = null)
    {
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item,
            UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])],
            At, "receiving", Entity, LocationId: location, LocationKind: locationKind));
        return result.Lines[0].UnitId!.Value;
    }

    private Task<AssignIdentifierResult> AssignAsync(TenantId tenant, Guid unitId, string type, string value, Guid? issuer = null) =>
        InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), unitId,
            new IdentifierInput(type, value, issuer), At.AddHours(2), "identity", Entity));

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private void RegisterNames(Guid firstItem, string firstCode, string firstName,
        Guid secondItem, string secondCode, string secondName, Guid owner, string ownerName)
    {
        var itemNames = fixture.Services.GetRequiredService<InMemoryItemNamePort>();
        itemNames.Register(new ItemLabel(firstItem, firstCode, firstName));
        itemNames.Register(new ItemLabel(secondItem, secondCode, secondName));
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>()
            .Register(new PartyLabel(owner, "CUSTOMER", ownerName, PartyType.Customer));
    }
}
