using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class CoverSinceSaleTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime Sale = new(2025, 11, 4, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Obligor = Guid.NewGuid();
    private static readonly AmendmentCommitter Manager = new("quality.manager", [YojanaRole.ServiceManager]);
    private Task<T> Invoke<T>(TenantId tenant, object message) where T : class => InvokeCore<T>(tenant, message);
    private async Task<T> InvokeCore<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() });
    }
    private async Task<PolicyDraftResult> Policy(TenantId tenant, string family, string name, int months, bool stacking = false, string mode = "return_to_base", string trigger = StartTrigger.Invoice)
    {
        await Invoke<CoverageFamilyRow>(tenant, new DefineCoverageFamily(family, family switch { CoverageFamilyCode.Base => "Base warranty", "service_level" => "Service level", "accidental_damage" => "Accidental damage", "extended_term" => "Extended term", _ => "Media retention" }, stacking, stacking ? AssuranceOrService.Service : AssuranceOrService.Assurance));
        var terms = PolicyValidatorTestsTerms.Standard24() with { Duration = new(months, DurationRule.Months), Start = new(trigger, [], null),
            Coverage = new(100, 100, 0, mode), Refurbished = [], RegionScope = ["EU"], ReplacementPolicyCode = null,
            Eligibility = trigger == StartTrigger.BaseExpiry ? new EligibilityRule(EligibilityRule.WithinBaseCoverage, CoverageFamilyCode.Base) : null,
            ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc, AssuranceOrService = stacking ? AssuranceOrService.Service : AssuranceOrService.Assurance };
        var draft = await Invoke<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), "P" + Guid.NewGuid().ToString("N")[..8], name, family, stacking ? EntitlementType.Extended : EntitlementType.Manufacturer, new(2020, 1, 1), terms, "quality.user"));
        return await Invoke<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "quality.user"));
    }
    private async Task<(TenantId Tenant, Guid Unit, Guid Base, Guid Service, Guid Item, Guid Owner, string Serial)> Setup(string prefix, bool service = false)
    {
        var tenant = await fixture.NewTenantAsync(prefix, "USD", "EUR");
        var policy = await Policy(tenant, CoverageFamilyCode.Base, "Standard 12 months", 12);
        var item = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var serial = "RS11-2509-04417-" + Guid.NewGuid().ToString("N")[..6];
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(item) with { ItemCode = "RS-1100", ItemClass = "rack_servers" });
        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(new(item, "RS-1100", "RS-1100 rack server"));
        fixture.Services.GetRequiredService<InMemoryItemFamilyNamePort>().Register(new("rack_servers", "Rack servers"));
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(new(owner, "MERIDIAN", "Meridian Data Labs", PartyType.Customer));
        var units = await Invoke<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], Sale.AddDays(-1), "quality.user", Obligor));
        var unit = units.Lines[0].UnitId!.Value;
        var doc = new DocumentRef("invoice", Guid.NewGuid(), 1);
        fixture.Services.GetRequiredService<InMemoryDocumentNumberRegistry>().Register(new(doc, "INV-2025-10882"));
        var created = await Invoke<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit, CoverageFamilyCode.Base, DateOnly.FromDateTime(Sale), Obligor, Sale, "quality.user",
            new Dictionary<string, DateTime> { [StartTrigger.Invoice] = Sale }, policy.PolicyVersionId, owner, PartyType.Customer, owner, PartyType.Customer, SourceDocument: doc));
        Guid serviceId = Guid.Empty;
        if (service)
        {
            var standard = await Policy(tenant, "service_level", "Standard service", 12);
            serviceId = (await Invoke<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit, "service_level", DateOnly.FromDateTime(Sale), Obligor, Sale, "quality.user",
                new Dictionary<string, DateTime> { [StartTrigger.Invoice] = Sale }, standard.PolicyVersionId, owner, PartyType.Customer, owner, PartyType.Customer, SourceDocument: doc with { Line = 2 }))).EntitlementId;
        }
        return (tenant, unit, created.EntitlementId, serviceId, item, owner, serial);
    }
    private async Task<AttachExtendedWarrantyResult> Add(TenantId tenant, Guid unit, Guid owner, string family, decimal price, string code = "USD", string? trigger = null)
    {
        var policy = await Policy(tenant, family, family == "extended_term" ? "Extended 24 months" : "Accidental damage 24 months", 24, true, trigger: trigger ?? StartTrigger.PlanPurchase);
        var at = family == "extended_term" ? new DateTime(2026, 9, 18, 10, 0, 0, DateTimeKind.Utc) : new DateTime(2026, 3, 12, 10, 0, 0, DateTimeKind.Utc);
        var doc = new DocumentRef("invoice", Guid.NewGuid(), 2);
        fixture.Services.GetRequiredService<InMemoryDocumentNumberRegistry>().Register(new(doc, family == "extended_term" ? "INV-2026-11930" : "INV-2026-10411"));
        return await Invoke<AttachExtendedWarrantyResult>(tenant, new AttachExtendedWarranty(Guid.NewGuid(), unit, family, DateOnly.FromDateTime(at), Obligor, at, price, code, doc, at, "quality.user",
            policy.PolicyVersionId, owner, PartyType.Customer, owner, PartyType.Customer));
    }
    private async Task<CoverSinceSale> View(TenantId tenant, Guid unit)
    {
        using var scope = fixture.ScopeFor(tenant);
        return (await scope.ServiceProvider.GetRequiredService<CoverDeviationQueries>().ForUnitAsync(unit, CancellationToken.None))!;
    }
    private async Task AssertProjected(TenantId tenant, Guid unit, int count)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(count, (await db.CoverDeviations.SingleAsync(r => r.UnitId == unit)).ChangeCount);
        Assert.Equal(count, await db.CoverDeviationChanges.CountAsync(r => r.UnitId == unit));
    }

    [DatabaseFact]
    public async Task Vetted_rack_server_has_four_changes_and_1140_USD_with_the_original_invoice_baseline()
    {
        var s = await Setup("covermock", true);
        await AssertProjected(s.Tenant, s.Unit, 0);
        await Add(s.Tenant, s.Unit, s.Owner, "accidental_damage", 180);
        await AssertProjected(s.Tenant, s.Unit, 1);
        var nbd = await Policy(s.Tenant, "service_level", "Next business day on-site", 36, mode: "on_site");
        var doc = new DocumentRef("invoice", Guid.NewGuid(), 1);
        var at = new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Utc);
        fixture.Services.GetRequiredService<InMemoryDocumentNumberRegistry>().Register(new(doc, "INV-2026-10977"));
        fixture.Services.GetRequiredService<InMemoryDocumentPorts>().Add(new InvoiceDocument(doc, new("sales_order", Guid.NewGuid()), s.Owner, Obligor, at,
            [new InvoiceLine(1, Guid.NewGuid(), 1, 320, "USD", "warranty_service", nbd.PolicyCode, nbd.PolicyVersionId, new(WarrantyTargetRule.AttachedList, Serials: [s.Serial]))], Country: "DE"));
        await Invoke<ConsumedEventResult>(s.Tenant, new SalesInvoicePosted(new(Guid.NewGuid(), Obligor, at, "sales"), doc));
        await AssertProjected(s.Tenant, s.Unit, 2);
        await Invoke<AmendEntitlementResult>(s.Tenant, new AmendEntitlement(Guid.NewGuid(), s.Base, AmendmentReason.Goodwill, new(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc), "quality.user",
            NewEnd: new(2026, 11, 24), CommittedBy: Manager, Note: "Goodwill after a 21-day repair"));
        await AssertProjected(s.Tenant, s.Unit, 3);
        await Add(s.Tenant, s.Unit, s.Owner, "extended_term", 640, trigger: StartTrigger.BaseExpiry);
        await AssertProjected(s.Tenant, s.Unit, 4);
        var view = await View(s.Tenant, s.Unit);
        Assert.Equal(new DateOnly(2025, 11, 4), view.SaleDate);
        Assert.Equal("INV-2025-10882", view.SaleDocumentNumber);
        Assert.Equal("Meridian Data Labs", view.CustomerName);
        Assert.Equal(4, view.ChangeCount);
        Assert.Equal(new CoverMoney("USD", 1140), Assert.Single(view.Value));
        Assert.Equal(new[] { "added", "amended", "upgraded", "added" }, view.Changes.Select(c => c.Kind));
        Assert.Equal(new[] { 10, 9, 6, 4 }, view.Changes.Select(c => c.MonthsAfterSale));
        // June 2 is six whole months after November 4; the illustrative mockup rounds this to seven. R3 requires floor.
        Assert.Equal("Standard service", view.Families.Single(f => f.FamilyName == "Service level").AtSale!.PolicyName);
        Assert.Equal("Next business day on-site", view.Families.Single(f => f.FamilyName == "Service level").Today!.PolicyName);
        Assert.Equal(new DateOnly(2026, 11, 3), view.Families.Single(f => f.FamilyName == "Base warranty").AtSale!.EndDate);
        Assert.Equal("2026-11-03", Assert.Single(view.Changes.Single(c => c.Kind == "amended").Fields).Before);
        using var scope = fixture.ScopeFor(s.Tenant);
        var query = scope.ServiceProvider.GetRequiredService<CoverDeviationQueries>();
        var summary = await query.SummaryAsync(new(), CancellationToken.None);
        Assert.Equal((1, 1, 4), (summary.UnitsSold, summary.UnitsChanged, summary.Changes));
        Assert.Equal(new CoverMoney("USD", 1140), Assert.Single(summary.Value));
        Assert.Equal(3, summary.ByKind.Sum(k => k.Units));
        var rows = new List<IReadOnlyList<string?>>();
        await foreach (var row in query.ExportAsync(new(), CancellationToken.None)) rows.Add(row);
        if (Environment.GetEnvironmentVariable("YOJANA_COVER_PREVIEW") is { } preview)
        {
            Directory.CreateDirectory(preview);
            var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            await File.WriteAllTextAsync(Path.Combine(preview, "unit.json"), System.Text.Json.JsonSerializer.Serialize(view, json));
            await File.WriteAllTextAsync(Path.Combine(preview, "summary.json"), System.Text.Json.JsonSerializer.Serialize(summary, json));
            await File.WriteAllTextAsync(Path.Combine(preview, "page.json"), System.Text.Json.JsonSerializer.Serialize(await query.PageAsync(new(), null, 25, CancellationToken.None), json));
        }
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.Contains("Meridian Data Labs", row));
    }

    [DatabaseFact]
    public async Task Refund_without_a_recorded_amount_does_not_invent_negative_money_and_currencies_stay_separate()
    {
        var s = await Setup("covermoney");
        var added = await Add(s.Tenant, s.Unit, s.Owner, "accidental_damage", 180);
        await Add(s.Tenant, s.Unit, s.Owner, "extended_term", 640, "EUR");
        await Invoke<EntitlementChangeResult>(s.Tenant, new CloseEntitlement(Guid.NewGuid(), added.EntitlementId, EntitlementClosedReason.Refunded, new(2026, 9, 20), new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc), "quality.user"));
        var view = await View(s.Tenant, s.Unit);
        Assert.Null(view.Changes.Single(c => c.Kind == "closed_early").Amount);
        Assert.Equal(new[] { new CoverMoney("USD", 180), new CoverMoney("EUR", 640) }, view.Value);
        using var scope = fixture.ScopeFor(s.Tenant);
        var summary = await scope.ServiceProvider.GetRequiredService<CoverDeviationQueries>().SummaryAsync(new(), CancellationToken.None);
        Assert.Equal(view.Value, summary.Value);
    }

    [DatabaseFact]
    public async Task Natural_expiry_is_neutral_and_a_unit_without_sale_is_excluded()
    {
        var s = await Setup("coverexpiry");
        await Invoke<AmendEntitlementResult>(s.Tenant, new AmendEntitlement(Guid.NewGuid(), s.Base, AmendmentReason.Correction, new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), "quality.user", NewEnd: new(2026, 9, 2), CommittedBy: Manager, CustomerNotificationRef: "Customer notified"));
        // Amendment is a change, but the later expiry adds none.
        await Invoke<EntitlementChangeResult>(s.Tenant, new CloseEntitlement(Guid.NewGuid(), s.Base, EntitlementClosedReason.Expired, new(2026, 9, 3), new(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc), "quality.user"));
        await AssertProjected(s.Tenant, s.Unit, 1);
        var view = await View(s.Tenant, s.Unit);
        Assert.DoesNotContain(view.Changes, c => c.Kind == "closed_early");
        Assert.Equal("ended", view.Families[0].Change);
        var other = await Invoke<CommissionUnitsResult>(s.Tenant, new CommissionUnits(Guid.NewGuid(), s.Item, UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, "NO-SALE-" + Guid.NewGuid().ToString("N")[..6])])], Sale, "quality.user", Obligor));
        Assert.Equal("No sale recorded", (await View(s.Tenant, other.Lines[0].UnitId!.Value)).Notice);
    }

    [DatabaseFact]
    public async Task Backfill_is_resumable_idempotent_and_projection_rolls_back_with_the_entitlement_transaction()
    {
        var s = await Setup("coverfill");
        await Add(s.Tenant, s.Unit, s.Owner, "accidental_damage", 180);
        using (var scope = fixture.ScopeFor(s.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            await db.CoverDeviationChanges.ExecuteDeleteAsync();
            await db.CoverDeviationValues.ExecuteDeleteAsync();
            await db.CoverDeviations.ExecuteDeleteAsync();
        }
        using (var scope = fixture.ScopeFor(s.Tenant)) Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<CoverDeviationBackfill>().BatchAsync(1, CancellationToken.None));
        using (var scope = fixture.ScopeFor(s.Tenant)) Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<CoverDeviationBackfill>().BatchAsync(1, CancellationToken.None));
        using (var scope = fixture.ScopeFor(s.Tenant)) Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<CoverDeviationBackfill>().BatchAsync(1, CancellationToken.None));
        await AssertProjected(s.Tenant, s.Unit, 1);
        using (var scope = fixture.ScopeFor(s.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            await SuspendEntitlementHandler.Handle(new(Guid.NewGuid(), s.Base, new(2026, 9, 1), "Pending inspection", false, new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), "quality.user"), db,
                scope.ServiceProvider.GetRequiredService<EntitlementLedger>(), scope.ServiceProvider.GetRequiredService<ITenantContext>(), CancellationToken.None);
            Assert.Equal(2, (await db.CoverDeviations.SingleAsync()).ChangeCount);
            await tx.RollbackAsync();
        }
        await AssertProjected(s.Tenant, s.Unit, 1);
    }

    [DatabaseFact]
    public async Task Transfer_preserves_the_sale_lineage_is_neutral_and_a_later_change_on_the_target_rebuilds_it()
    {
        var s = await Setup("covertransfer");
        await Invoke<ReplacementPolicyRow>(s.Tenant, new DefineReplacementPolicy("CARRY", ReplacementRule.CarryRemaining, null, null, null));
        var commissioned = await Invoke<CommissionUnitsResult>(s.Tenant, new CommissionUnits(Guid.NewGuid(), s.Item, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, "TARGET-" + Guid.NewGuid().ToString("N")[..8])])], Sale, "quality.user", Obligor));
        var target = commissioned.Lines[0].UnitId!.Value;
        await Invoke<TransferEntitlementResult>(s.Tenant, new TransferEntitlement(Guid.NewGuid(), s.Base, target, Guid.NewGuid(), new(2026, 9, 1),
            new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), "quality.user", "CARRY"));
        foreach (var id in new[] { s.Unit, target })
        {
            await AssertProjected(s.Tenant, id, 0);
            var view = await View(s.Tenant, id);
            Assert.Equal(new DateOnly(2025, 11, 4), view.SaleDate);
            Assert.Equal("transferred", view.Families.Single().Change);
            Assert.All(view.Changes, c => Assert.False(c.Counted));
        }
        await Add(s.Tenant, target, s.Owner, "accidental_damage", 180);
        await AssertProjected(s.Tenant, target, 1);
    }

    [DatabaseFact]
    public async Task Price_migration_fills_recorded_creator_prices_in_small_batches_and_can_be_repeated()
    {
        var s = await Setup("coverpricemig");
        var added = await Add(s.Tenant, s.Unit, s.Owner, "accidental_damage", 180);
        using var scope = fixture.ScopeFor(s.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await db.Entitlements.Where(e => e.EntitlementId == added.EntitlementId).ExecuteUpdateAsync(u => u.SetProperty(e => e.SalePrice, (decimal?)null).SetProperty(e => e.SaleCurrency, (string?)null));
        var migration = new Yojana.Serialisation.Data.Migrations.P9T14CoverSinceSale();
        var sql = migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>().Single(o => o.Sql.Contains("CREATE TABLE #cover_prices", StringComparison.Ordinal)).Sql;
        // Migrations run without a tenant scope. An ordinary application connection deliberately pins its tenant
        // read-only, so exercise this administrative migration on an unscoped connection too.
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(SerialisationHostFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
        await command.ExecuteNonQueryAsync();
        var entitlement = await db.Entitlements.AsNoTracking().SingleAsync(e => e.EntitlementId == added.EntitlementId);
        Assert.Equal((180m, "USD"), (entitlement.SalePrice, entitlement.SaleCurrency));
        Assert.Null((await db.Entitlements.AsNoTracking().SingleAsync(e => e.EntitlementId == s.Base)).SalePrice);
    }

    [DatabaseFact]
    public async Task Report_keysets_are_stable_for_tied_dates_and_RLS_hides_other_tenants_even_without_query_filters()
    {
        var s = await Setup("coverpage");
        await Add(s.Tenant, s.Unit, s.Owner, "accidental_damage", 180);
        using var scope = fixture.ScopeFor(s.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var first = await db.CoverDeviations.AsNoTracking().SingleAsync();
        for (var i = 0; i < 3; i++) db.CoverDeviations.Add(new() { TenantId = s.Tenant, UnitId = Guid.NewGuid(), ItemId = first.ItemId, SaleDate = first.SaleDate, LastChangeAt = first.LastChangeAt, Changed = true, ChangeCount = 1 });
        await db.SaveChangesAsync();
        var query = scope.ServiceProvider.GetRequiredService<CoverDeviationQueries>();
        var p1 = await query.PageAsync(new(), null, 2, CancellationToken.None);
        var p2 = await query.PageAsync(new(), p1.NextCursor, 2, CancellationToken.None);
        Assert.Equal(4, p1.Rows.Concat(p2.Rows).Select(r => r.UnitId).Distinct().Count());
        Assert.Null(p2.NextCursor);
        var other = await fixture.NewTenantAsync("coverother");
        using var otherScope = fixture.ScopeFor(other);
        var otherDb = otherScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(0, await otherDb.CoverDeviations.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await otherDb.CoverDeviationChanges.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await otherDb.CoverDeviationValues.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await otherDb.CoverDeviationBackfillCheckpoints.IgnoreQueryFilters().CountAsync());
        using var client = await fixture.AuthenticatedClientAsync(s.Tenant);
        var response = await client.GetAsync("/api/entitlements/cover-changes?take=2");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/entitlements/cover-changes?cursor=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/units/{s.Unit}/cover-since-sale")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/entitlements/cover-changes/summary")).StatusCode);
        foreach (var format in new[] { "csv", "xlsx" })
        {
            var download = await client.GetAsync($"/api/entitlements/cover-changes/export?format={format}");
            Assert.True(download.IsSuccessStatusCode, format + ": " + await download.Content.ReadAsStringAsync());
        }
    }
}
