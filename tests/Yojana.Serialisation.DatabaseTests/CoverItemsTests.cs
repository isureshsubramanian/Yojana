using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Migrations;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class CoverItemsTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Entity = Guid.NewGuid();
    private sealed class FixedClock(DateTime at) : IClock { public DateTime UtcNow => at; }
    private async Task<T> Invoke<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() });
    }
    private static PolicyCoverItem Item(string code, string wording, string type = "covered", string? cause = null, string? category = "cause") =>
        new(code, type, wording, "Internal service instruction: verify the seal", category, CoverPartsScope.All, cause is null ? [] : [cause]);
    private async Task<PolicyDraftResult> Policy(TenantId tenant, string family, string name, IReadOnlyList<PolicyCoverItem> items, IReadOnlyList<UsageLimitTerm>? usage = null)
    {
        await Invoke<CoverageFamilyRow>(tenant, new DefineCoverageFamily(family, family == CoverageFamilyCode.Base ? "Base warranty" : family == "service_level" ? "Service level" : name, family != CoverageFamilyCode.Base, AssuranceOrService.Assurance));
        var terms = PolicyValidatorTestsTerms.Standard24() with { Duration = new(family == CoverageFamilyCode.Base ? 12 : family == "service_level" ? 36 : 24, DurationRule.Months), Items = items, UsageLimits = usage ?? [],
            Start = new(StartTrigger.Invoice, [], null), ReplacementPolicyCode = null, Refurbished = [], RegionScope = ["WORLD"], ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc };
        return await Invoke<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), family.ToUpperInvariant(), name, family,
            EntitlementType.Manufacturer, new(2020, 1, 1), terms, "quality.user"));
    }
    private sealed record SetupData(TenantId Tenant, Guid Unit, Guid Item, Guid Owner, Guid BasePolicy, Guid DamagePolicy, Guid Service, Guid Damage, Guid Future);
    private async Task<SetupData> Setup()
    {
        var tenant = await fixture.NewTenantAsync("items", "USD");
        var item = Guid.NewGuid(); var owner = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(item) with { ItemCode = "RS-1100", ItemClass = "rack_servers" });
        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(new(item, "RS-1100", "RS-1100 rack server"));
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(new(owner, "MERIDIAN", "Meridian Data Labs", PartyType.Customer));
        var unit = (await Invoke<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, "RS11-2509-04417")])], new DateTime(2025, 10, 1, 9, 0, 0, DateTimeKind.Utc), "quality.user", Entity))).Lines[0].UnitId!.Value;
        var hardware = Item("BASE-HW", "Hardware failure in normal use", cause: "STOPPED_WORKING");
        var parts = Item("BASE-PART", "Fans, power supplies, drives, memory", category: "part");
        var baseline = await Policy(tenant, CoverageFamilyCode.Base, "Standard 12 months", [hardware, parts,
            Item("BASE-FW", "Firmware and BIOS faults"), Item("BASE-LAB", "Repair labour", category: "service"),
            Item("BASE-DIAG", "Hardware diagnostics", category: "service"), Item("BASE-REPAIR", "Repair or replacement", category: "service"),
            Item("EX-LIQ", "Liquid damage", "excluded", "LIQUID_SPILL"),
            Item("EX-COSM", "Cosmetic damage that does not affect use", "excluded", "COSMETIC_DAMAGE", "condition"),
            Item("EX-THEFT", "Theft or loss", "excluded", "STOLEN_LOST"), Item("EX-MISUSE", "Damage on purpose or from misuse", "excluded"),
            Item("EX-WAIT", "Damage in the first 30 days after buying this cover", "excluded", category: "condition"),
            Item("EX-CONS", "Consumables", "excluded", category: "part"), Item("EX-SOFT", "Software support", "excluded", category: "service"),
            Item("EX-MEDIA", "Keeping a failed drive", "excluded", category: "service")]);
        var damage = await Policy(tenant, "accidental_damage", "Accidental damage", [
            Item("ADP-DROP", "Accidental drops and knocks", cause: "DROPPED_KNOCKED"), Item("ADP-LIQ", "Liquid spills", cause: "LIQUID_SPILL"),
            Item("ADP-SURGE", "Power surge damage", cause: "POWER_SURGE"), Item("ADP-LIMIT", "Up to 2 claims while the cover runs", category: "condition")], [new("claims", 2)]);
        var service = await Policy(tenant, "service_level", "Next business day", [Item("NBD-VISIT", "Engineer on site next business day", category: "service")], [new("visits", 4, "contract_year")]);
        var extended = await Policy(tenant, "extended_term", "Extended term", [hardware, parts]);
        var ids = new List<Guid>();
        foreach (var p in new[] { baseline, damage, service, extended })
        {
            await Invoke<PolicyDraftResult>(tenant, new PublishPolicyVersion(p.PolicyVersionId, "quality.user"));
            var family = p == baseline ? CoverageFamilyCode.Base : p == damage ? "accidental_damage" : p == service ? "service_level" : "extended_term";
            var start = p == extended ? new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc)
                : p == baseline ? new DateTime(2025, 11, 25, 0, 0, 0, DateTimeKind.Utc)
                : p == damage ? new DateTime(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc)
                : new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);
            ids.Add((await Invoke<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit, family, new(2026, 1, 1), Entity, At.AddDays(-1), "quality.user",
                new Dictionary<string, DateTime> { [StartTrigger.Invoice] = start }, p.PolicyVersionId, owner, PartyType.Customer, owner, PartyType.Customer))).EntitlementId);
        }
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        (await db.EntitlementUsageLimits.SingleAsync(x => x.EntitlementId == ids[2])).UsedValue = 1;
        await db.SaveChangesAsync();
        return new(tenant, unit, item, owner, baseline.PolicyVersionId, damage.PolicyVersionId, ids[2], ids[1], ids[3]);
    }
    private WhatsCoveredQuery Query(IServiceProvider sp, DateTime? at = null) => ActivatorUtilities.CreateInstance<WhatsCoveredQuery>(sp, new FixedClock(at ?? At));

    [DatabaseFact]
    public async Task Vetted_unit_merges_four_live_covers_with_eleven_covered_seven_excluded_and_both_liquid_answers()
    {
        var s = await Setup(); using var scope = fixture.ScopeFor(s.Tenant);
        var query = Query(scope.ServiceProvider); var data = (await query.ReadAsync(s.Unit, CancellationToken.None))!;
        Assert.Equal((4, 11, 7), (data.Covers.Count, data.CoveredCount, data.ExcludedCount));
        Assert.Equal("RS11-2509-04417", data.Serial); Assert.Equal("RS-1100 rack server", data.ItemName);
        var liquid = Assert.Single(data.Items, x => x.CauseCodes.Contains("LIQUID_SPILL"));
        Assert.True(liquid.Covered); Assert.Equal("Accidental damage", Assert.Single(liquid.Grants).Cover.FamilyName);
        Assert.Equal("Base warranty", Assert.Single(liquid.Excludes).Cover.FamilyName);
        Assert.Equal(3, Assert.Single(data.Covers.Single(x => x.EntitlementId == s.Service).Usage).Remaining);
        Assert.Equal("contract_year", Assert.Single(data.Covers.Single(x => x.EntitlementId == s.Service).Usage).Period);
        Assert.Equal(2, Assert.Single(data.Covers.Single(x => x.EntitlementId == s.Damage).Usage).Remaining);
        Assert.All(data.Items.Where(x => !x.Covered), x => Assert.Equal("No · not offered on this item", x.CanBeCovered));
        Assert.Contains("unit’s country", data.Notice);
        Assert.Equal(2, WhatsCoveredQuery.Page(data, "covered", null, 0, 2).Items.Count);
        Assert.Equal(11, WhatsCoveredQuery.Page(data, "covered", null, 0, 2).Total);
        Assert.Single(WhatsCoveredQuery.Page(data, null, "Liquid", 0, 25).Items);
        Assert.Single(WhatsCoveredQuery.Page(data, null, "EX-LIQ", 0, 25).Items);
        if (Environment.GetEnvironmentVariable("YOJANA_ITEMS_PREVIEW") is { } preview)
        {
            Directory.CreateDirectory(preview);
            var policyQueries = scope.ServiceProvider.GetRequiredService<PolicyQueries>();
            var damage = (await policyQueries.DetailAsync(s.DamagePolicy, CancellationToken.None))!;
            var draft = await Invoke<PolicyDraftResult>(s.Tenant, new CreatePolicyDraft(Guid.NewGuid(), damage.PolicyCode, damage.Name, damage.CoverageFamily,
                damage.EntitlementType, new(2026, 10, 1), null, "quality.user"));
            await Invoke<PolicyCoverItemPage>(s.Tenant, new UpdatePolicyCoverItems(draft.PolicyVersionId, [..damage.Terms.Items!,
                Item("EX-COSM", "Cosmetic damage that does not affect use", "excluded", "COSMETIC_DAMAGE", "condition"),
                Item("EX-MISUSE", "Damage on purpose or from misuse", "excluded"), Item("EX-THEFT", "Theft or loss", "excluded", "STOLEN_LOST"),
                Item("EX-WAIT", "Damage in the first 30 days after buying this cover", "excluded", category: "condition"),
                Item("LEGACY-1", "Consumables, batteries after 12 months", "excluded", category: null)]));
            var policies = await policyQueries.ListAsync(null, CancellationToken.None);
            var versions = new Dictionary<string, IReadOnlyList<PolicyVersionRow>>();
            var details = new Dictionary<Guid, PolicyVersionDetail>();
            foreach (var policy in policies)
            {
                var rows = await policyQueries.VersionsAsync(policy.PolicyCode, CancellationToken.None);
                versions[policy.PolicyCode] = rows;
                foreach (var row in rows) details[row.PolicyVersionId] = (await policyQueries.DetailAsync(row.PolicyVersionId, CancellationToken.None))!;
            }
            var units = scope.ServiceProvider.GetRequiredService<Yojana.Serialisation.Queries.UnitQueries>();
            var output = new { whats = data, unit = await units.DetailAsync(s.Unit, CancellationToken.None), history = await units.HistoryAsync(s.Unit, 0, 20, CancellationToken.None),
                entitlements = await scope.ServiceProvider.GetRequiredService<EntitlementQueries>().DetailForUnitAsync(s.Unit, new(2026, 9, 1), CancellationToken.None),
                comparison = await scope.ServiceProvider.GetRequiredService<CoverDeviationQueries>().ForUnitAsync(s.Unit, CancellationToken.None),
                guidance = await query.GuidanceAsync(s.Unit, "LIQUID_SPILL", CancellationToken.None), policies, versions, details,
                families = await policyQueries.FamiliesAsync(CancellationToken.None), causes = ClaimCauseCatalogue.Defaults, entity = Entity,
                itemNames = await fixture.Services.GetRequiredService<IItemNamePort>().NamesAsync([s.Item], CancellationToken.None),
                partyNames = await fixture.Services.GetRequiredService<IPartyNamePort>().NamesAsync([s.Owner], CancellationToken.None) };
            await File.WriteAllTextAsync(Path.Combine(preview, "data.json"), System.Text.Json.JsonSerializer.Serialize(output, System.Text.Json.JsonSerializerOptions.Web));
        }
    }

    [DatabaseFact]
    public async Task Future_cover_and_two_different_grants_are_all_retained_with_their_own_limits_and_dates()
    {
        var s = await Setup(); using var scope = fixture.ScopeFor(s.Tenant);
        var query = Query(scope.ServiceProvider);
        var current = (await query.ReadAsync(s.Unit, CancellationToken.None))!;
        var future = current.Covers.Single(x => x.EntitlementId == s.Future);
        Assert.True(future.Future); Assert.Equal(new DateOnly(2026, 11, 25), future.From);
        var hardware = current.Items.Single(x => x.CustomerWording == "Hardware failure in normal use");
        Assert.Equal(2, hardware.Grants.Count); Assert.Contains(hardware.Grants, x => x.Cover.Future);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var e = await db.Entitlements.SingleAsync(x => x.EntitlementId == s.Future);
        e.StartDate = new(2026, 8, 1); e.EndDate = new(2028, 8, 1); await db.SaveChangesAsync();
        var changed = (await query.ReadAsync(s.Unit, CancellationToken.None))!.Items.Single(x => x.CauseCodes.Contains("STOPPED_WORKING"));
        Assert.Equal(2, changed.Grants.Count); Assert.Equal(2, changed.Grants.Select(x => x.Cover.Until).Distinct().Count());
    }

    [DatabaseFact]
    public async Task Guidance_explains_grants_exclusions_and_never_guesses_meter_consumption()
    {
        var s = await Setup(); using var scope = fixture.ScopeFor(s.Tenant);
        var guidance = (await Query(scope.ServiceProvider).GuidanceAsync(s.Unit, "LIQUID_SPILL", CancellationToken.None))!;
        Assert.True(guidance.Covered); Assert.Single(guidance.Excludes);
        var usage = Assert.Single(Assert.Single(guidance.Grants).Usage);
        Assert.Equal(2m, usage.Now); Assert.Null(usage.AfterThisClaim);
        Assert.Equal("Liquid spills", guidance.SayToCustomer); Assert.Contains("Guidance only", guidance.Notice);
        var no = (await Query(scope.ServiceProvider).GuidanceAsync(s.Unit, "STOLEN_LOST", CancellationToken.None))!;
        Assert.False(no.Covered); Assert.Equal("Theft or loss", no.SayToCustomer);
    }

    [DatabaseFact]
    public async Task Published_items_refuse_edits_and_a_new_draft_copies_them_without_changing_sold_units()
    {
        var s = await Setup();
        var refused = await Assert.ThrowsAsync<CommandRefusedException>(() => Invoke<PolicyCoverItemPage>(s.Tenant, new UpdatePolicyCoverItems(s.BasePolicy, [])));
        Assert.Equal("policy_items_published", refused.Rule);
        var next = await Invoke<PolicyDraftResult>(s.Tenant, new CreatePolicyDraft(Guid.NewGuid(), CoverageFamilyCode.Base.ToUpperInvariant(), "Base warranty", CoverageFamilyCode.Base,
            EntitlementType.Manufacturer, new(2026, 10, 1), null, "quality.user"));
        using var scope = fixture.ScopeFor(s.Tenant); var store = scope.ServiceProvider.GetRequiredService<PolicyCoverItems>();
        var copied = await store.ReadAsync(next.PolicyVersionId, CancellationToken.None);
        Assert.Equal(14, copied.Count);
        Assert.All(copied, x => Assert.Equal(1, x.SinceVersion));
        await Invoke<PolicyCoverItemPage>(s.Tenant, new UpdatePolicyCoverItems(next.PolicyVersionId, [Item("CHANGED", "Changed draft wording")]));
        Assert.Equal(14, (await store.ReadAsync(s.BasePolicy, CancellationToken.None)).Count);
        Assert.DoesNotContain((await Query(scope.ServiceProvider).ReadAsync(s.Unit, CancellationToken.None))!.Items, x => x.CustomerWording == "Changed draft wording");
    }

    [DatabaseFact]
    public async Task Legacy_backfill_keeps_order_text_and_version_is_uncategorised_and_can_run_twice()
    {
        var tenant = await fixture.NewTenantAsync("legacy-items");
        var draft = await Policy(tenant, CoverageFamilyCode.Base, "Base warranty", []);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var row = await db.PolicyVersions.SingleAsync(x => x.PolicyVersionId == draft.PolicyVersionId);
            row.Exclusions = "[\"Consumables\",\"Batteries after 12 months\",\"Consumables\"]";
            await db.SaveChangesAsync();
        }
        await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(SerialisationHostFixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = P9T19CoverItems.LegacyBackfill;
            await command.ExecuteNonQueryAsync();
            await command.ExecuteNonQueryAsync();
        }
        using var read = fixture.ScopeFor(tenant);
        var items = await read.ServiceProvider.GetRequiredService<PolicyCoverItems>().ReadAsync(draft.PolicyVersionId, CancellationToken.None);
        Assert.Equal(["LEGACY-1", "LEGACY-2", "LEGACY-3"], items.Select(x => x.Code));
        Assert.Equal(["Consumables", "Batteries after 12 months", "Consumables"], items.Select(x => x.CustomerWording));
        Assert.All(items, x => { Assert.Null(x.Category); Assert.Equal("excluded", x.Type); Assert.Empty(x.CauseCodes); Assert.Empty(x.PartsScope.PartCategories); Assert.Empty(x.PartsScope.ItemFamilies); });
    }

    [DatabaseFact]
    public async Task Referenced_cause_can_be_renamed_or_deactivated_but_not_deleted()
    {
        var s = await Setup();
        var causes = ClaimCauseCatalogue.Defaults;
        Assert.Equal("claim_cause_referenced", (await Assert.ThrowsAsync<CommandRefusedException>(() => Invoke<ClaimCausePage>(s.Tenant,
            new UpdateClaimCauses(causes.Where(x => x.Code != "LIQUID_SPILL").ToList())))).Rule);
        var updated = await Invoke<ClaimCausePage>(s.Tenant, new UpdateClaimCauses(causes.Select(x => x.Code == "LIQUID_SPILL" ? x with { CustomerWording = "Accidental spill", Active = false } : x).ToList()));
        Assert.Contains(updated.Items, x => x.Code == "LIQUID_SPILL" && !x.Active && x.CustomerWording == "Accidental spill");
    }

    [DatabaseFact]
    public async Task Customer_document_has_no_internal_notes_and_uses_the_pdf_port_when_available()
    {
        var s = await Setup(); using var scope = fixture.ScopeFor(s.Tenant);
        var query = Query(scope.ServiceProvider);
        var sheet = (await new UnitCustomerDocument(query).CreateAsync(s.Unit, "en", CancellationToken.None))!;
        Assert.Contains("Liquid spills", sheet.Html); Assert.DoesNotContain("Internal service instruction", sheet.Html);
        Assert.DoesNotContain("ADP-LIQ", sheet.Html); Assert.Null(sheet.PdfBytes); Assert.Equal("RS11-2509-04417", sheet.MergeValues["unit.serial"]);
        var pdf = (await new UnitCustomerDocument(query, new PdfStub()).CreateAsync(s.Unit, "en", CancellationToken.None))!;
        Assert.Equal("%PDF-probe"u8.ToArray(), pdf.PdfBytes);
    }
    private sealed class PdfStub : IPdfRenderer
    {
        public Task<byte[]> RenderAsync(PdfRenderRequest request, CancellationToken ct)
        {
            Assert.Contains("Liquid spills", request.Html); Assert.DoesNotContain("Internal service instruction", request.Html);
            return Task.FromResult("%PDF-probe"u8.ToArray());
        }
    }

    [DatabaseFact]
    public async Task Cause_metadata_does_not_change_adjudication_and_claim_references_also_prevent_deletion()
    {
        var s = await Setup();
        SubmitClaim Command(string? cause) => new(Guid.NewGuid(), s.Unit, null, new(2026, 9, 1), s.Owner, s.Owner, Entity, Entity, "DE", "USD",
            [new(ClaimLineKind.Parts, CoverageFamilyCode.Base, 100, Description: "Fan stopped")], [], ClaimRemedy.Repair, "CALL-RECORDED", At, "quality.user",
            ReportedCauseCode: cause, ReportedDescription: cause is null ? null : "The fan stopped working", OccurredDate: cause is null ? null : new(2026, 8, 31));
        var a = await Invoke<SubmitClaimResult>(s.Tenant, Command(null));
        var b = await Invoke<SubmitClaimResult>(s.Tenant, Command("SOMETHING_ELSE"));
        var reviewer = new ClaimReviewer("quality.manager", [YojanaRole.ServiceManager]);
        var first = await Invoke<AdjudicateClaimResult>(s.Tenant, new AdjudicateClaim(Guid.NewGuid(), a.ClaimId, At.AddMinutes(1), "quality.manager", reviewer));
        var second = await Invoke<AdjudicateClaimResult>(s.Tenant, new AdjudicateClaim(Guid.NewGuid(), b.ClaimId, At.AddMinutes(2), "quality.manager", reviewer));
        Assert.Equal((first.Status, first.CoverageSource, first.DenialReason), (second.Status, second.CoverageSource, second.DenialReason));
        Assert.Equal(Assert.Single(first.Lines).ApprovedAmount, Assert.Single(second.Lines).ApprovedAmount);
        using var scope = fixture.ScopeFor(s.Tenant);
        var claim = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Claims.SingleAsync(x => x.ClaimId == b.ClaimId);
        Assert.Equal("SOMETHING_ELSE", claim.ReportedCauseCode); Assert.Equal("The fan stopped working", claim.ReportedDescription); Assert.Equal(new DateOnly(2026, 8, 31), claim.OccurredDate);
        Assert.Equal("claim_cause_referenced", (await Assert.ThrowsAsync<CommandRefusedException>(() => Invoke<ClaimCausePage>(s.Tenant,
            new UpdateClaimCauses(ClaimCauseCatalogue.Defaults.Where(x => x.Code != "SOMETHING_ELSE").ToList())))).Rule);
    }

    [DatabaseFact]
    public async Task New_tables_are_tenant_isolated_and_http_reads_are_member_only_while_published_writes_return_conflict()
    {
        var s = await Setup(); var other = await fixture.NewTenantAsync("other-items");
        using (var scope = fixture.ScopeFor(other))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(0, await db.PolicyCoverItems.IgnoreQueryFilters().CountAsync());
            Assert.Equal(0, await db.PolicyCoverItemCauses.IgnoreQueryFilters().CountAsync());
            Assert.Equal(0, await db.ClaimCauses.IgnoreQueryFilters().CountAsync());
        }
        using var member = await fixture.AuthenticatedClientAsync(s.Tenant, YojanaRole.ServiceDesk);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/units/{s.Unit}/whats-covered?take=2")).StatusCode);
        var itemPage = await member.GetFromJsonAsync<PolicyCoverItemPage>($"/api/warranty-policies/versions/{s.BasePolicy}/items?take=1");
        Assert.Single(itemPage!.Items); Assert.Equal(14, itemPage.Total);
        var causePage = await member.GetFromJsonAsync<ClaimCausePage>("/api/claim-causes?take=2");
        Assert.Equal(2, causePage!.Items.Count); Assert.Equal(8, causePage.Total);
        var guidance = await member.GetFromJsonAsync<ClaimGuidance>($"/api/units/{s.Unit}/claim-guidance?cause=LIQUID_SPILL");
        Assert.True(guidance!.Covered);
        using var sheet = await member.GetAsync($"/api/units/{s.Unit}/whats-covered/sheet.pdf");
        Assert.Equal(HttpStatusCode.OK, sheet.StatusCode); Assert.Equal("text/html", sheet.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("Internal service instruction", await sheet.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/warranty-policies/versions/{s.BasePolicy}/items", new UpdatePolicyCoverItems(s.BasePolicy, []))).StatusCode);
        using var manager = await fixture.AuthenticatedClientAsync(s.Tenant, YojanaRole.ServiceManager);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PutAsJsonAsync($"/api/warranty-policies/versions/{s.BasePolicy}/items", new UpdatePolicyCoverItems(s.BasePolicy, [], ExpectedVersion: itemPage.Version))).StatusCode);
        using var outsider = await fixture.AuthenticatedClientAsync(other, YojanaRole.ServiceDesk);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/units/{s.Unit}/whats-covered")).StatusCode);
        using var anonymous = fixture.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/units/{s.Unit}/whats-covered")).StatusCode);
    }
    [DatabaseFact]
    public async Task Reads_use_six_serialisation_queries_for_four_covers_and_each_cover_uses_its_local_day()
    {
        var s = await Setup(); using var scope = fixture.ScopeFor(s.Tenant);
        var original = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var counter = new ReadCounter();
        var options = new DbContextOptionsBuilder<SerialisationDbContext>(scope.ServiceProvider.GetRequiredService<DbContextOptions<SerialisationDbContext>>()).AddInterceptors(counter).Options;
        await using var db = new SerialisationDbContext(options, scope.ServiceProvider.GetRequiredService<ITenantContext>());
        db.Database.SetCommandTimeout(10);
        var query = ActivatorUtilities.CreateInstance<WhatsCoveredQuery>(scope.ServiceProvider, db, new FixedClock(At));
        Assert.Equal(4, (await query.ReadAsync(s.Unit, CancellationToken.None))!.Covers.Count);
        Assert.Equal(6, counter.Count);
        var cover = await original.Entitlements.SingleAsync(x => x.EntitlementId == s.Damage);
        cover.EndDate = new(2026, 8, 31); cover.StartTimeZone = "Europe/Berlin"; await original.SaveChangesAsync();
        var atMidnight = Query(scope.ServiceProvider, new DateTime(2026, 8, 31, 23, 30, 0, DateTimeKind.Utc));
        Assert.DoesNotContain((await atMidnight.ReadAsync(s.Unit, CancellationToken.None))!.Covers, x => x.EntitlementId == s.Damage);
        cover.StartTimeZone = "Etc/UTC"; await original.SaveChangesAsync();
        Assert.Contains((await atMidnight.ReadAsync(s.Unit, CancellationToken.None))!.Covers, x => x.EntitlementId == s.Damage && x.AsOf == new DateOnly(2026, 8, 31));
    }
    private sealed class ReadCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }

    [DatabaseFact]
    public async Task Catalogue_http_stamps_the_actor_and_rls_blocks_a_cross_tenant_insert()
    {
        var tenant = await fixture.NewTenantAsync("cause-actor"); var other = await fixture.NewTenantAsync("cause-other");
        var user = Guid.NewGuid();
        using var manager = await fixture.AuthenticatedClientAsync(tenant, YojanaRole.ServiceManager, user);
        using var response = await manager.PutAsJsonAsync("/api/claim-causes", new UpdateClaimCauses([..ClaimCauseCatalogue.Defaults, new("CUSTOM_CAUSE", "Another cause")], "forged.actor", ClaimCauseCatalogue.Version(ClaimCauseCatalogue.Defaults)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(user.ToString("D"), (await db.ClaimCauses.SingleAsync(x => x.Code == "CUSTOM_CAUSE")).UpdatedBy);
        }
        using (var scope = fixture.ScopeFor(other))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.ClaimCauses.Add(new Yojana.Serialisation.Data.Entities.ClaimCauseRow { TenantId = tenant, Code = "FOREIGN_CAUSE", CustomerWording = "Must not enter" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [DatabaseFact]
    public async Task Long_legacy_wording_is_preserved_flagged_and_copyable_while_blanks_and_invalid_json_are_skipped()
    {
        var tenant = await fixture.NewTenantAsync("legacy-long");
        var policy = await Policy(tenant, CoverageFamilyCode.Base, "Base warranty", []);
        var invalid = await Policy(tenant, "legacy_invalid", "Legacy invalid", []);
        var wording = new string('W', 1201);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var row = await db.PolicyVersions.SingleAsync(x => x.PolicyVersionId == policy.PolicyVersionId);
            row.Exclusions = System.Text.Json.JsonSerializer.Serialize(new[] { "", " ", "\t\r\n\u00a0\u2003\u3000", wording });
            await db.SaveChangesAsync();
        }
        await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(SerialisationHostFixture.ConnectionString))
        {
            var messages = new List<string>();
            connection.InfoMessage += (_, args) => messages.Add(args.Message);
            await connection.OpenAsync();
            await using var transaction = (Microsoft.Data.SqlClient.SqlTransaction)await connection.BeginTransactionAsync();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SET XACT_ABORT ON;
                EXEC sys.sp_set_session_context @key=N'tenant_id', @value=@fixture_tenant;
                ALTER TABLE serialisation.warranty_policy_version NOCHECK CONSTRAINT ck_warranty_policy_version_exclusions_json;
                UPDATE serialisation.warranty_policy_version SET exclusions=N'not JSON' WHERE tenant_id=@fixture_tenant AND policy_version_id=@version;
                """;
            command.Parameters.AddWithValue("@fixture_tenant", tenant.Value);
            command.Parameters.AddWithValue("@version", invalid.PolicyVersionId);
            await command.ExecuteNonQueryAsync();
            try
            {
                command.CommandText = P9T19CoverItems.LegacyBackfill;
                await command.ExecuteNonQueryAsync();
                await command.ExecuteNonQueryAsync();
                Assert.Contains(messages, message => message.Contains("skipped invalid JSON rows: 1", StringComparison.Ordinal));
                command.CommandText = "SELECT is_enabled FROM sys.security_policies WHERE name=N'tenant_policy_serialisation';";
                Assert.True((bool)(await command.ExecuteScalarAsync())!);
                command.CommandText = """
                    UPDATE serialisation.warranty_policy_version SET exclusions=N'[]' WHERE tenant_id=@fixture_tenant AND policy_version_id=@version;
                    ALTER TABLE serialisation.warranty_policy_version WITH CHECK CHECK CONSTRAINT ck_warranty_policy_version_exclusions_json;
                    """;
                await command.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        using var read = fixture.ScopeFor(tenant);
        var items = read.ServiceProvider.GetRequiredService<PolicyCoverItems>();
        var imported = Assert.Single(await items.ReadAsync(policy.PolicyVersionId, CancellationToken.None));
        Assert.Equal("LEGACY-4", imported.Code); Assert.Equal(wording, imported.CustomerWording);
        Assert.True(imported.TooLongForCustomerSheet); Assert.Null(imported.Category);
        Assert.Empty(await items.ReadAsync(invalid.PolicyVersionId, CancellationToken.None));
        await Invoke<PolicyCoverItemPage>(tenant, new UpdatePolicyCoverItems(policy.PolicyVersionId, [imported]));
        Assert.Equal("policy_item_invalid", (await Assert.ThrowsAsync<CommandRefusedException>(() =>
            Invoke<PolicyCoverItemPage>(tenant, new UpdatePolicyCoverItems(policy.PolicyVersionId, [imported with { CustomerWording = wording + "changed" }])))).Rule);
        await Invoke<PolicyDraftResult>(tenant, new PublishPolicyVersion(policy.PolicyVersionId, "quality.user"));
        var draft = await Invoke<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), CoverageFamilyCode.Base.ToUpperInvariant(),
            "Base warranty", CoverageFamilyCode.Base, EntitlementType.Manufacturer, new(2026, 9, 1), null, "quality.user"));
        Assert.Equal(wording, Assert.Single(await items.ReadAsync(draft.PolicyVersionId, CancellationToken.None)).CustomerWording);
    }

    [DatabaseFact]
    public async Task Availability_checks_published_item_mapping_today_eligibility_and_unknown_region_without_guessing()
    {
        var s = await Setup(); using var scope = fixture.ScopeFor(s.Tenant);
        var query = Query(scope.ServiceProvider);
        async Task<string> Answer() => (await query.ReadAsync(s.Unit, CancellationToken.None))!.Items.Single(x => x.CauseCodes.Contains("STOLEN_LOST")).CanBeCovered;
        Assert.Equal("No · not offered on this item", await Answer());
        var policy = await Policy(s.Tenant, "loss_cover", "Loss protection", [Item("LOSS", "Loss protection", cause: "STOLEN_LOST")]);
        var mappings = fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();
        mappings.Register(new ItemWarrantyPolicy(s.Item, "loss_cover", "LOSS_COVER", false));
        Assert.Equal("No · not offered on this item", await Answer());
        await Invoke<PolicyDraftResult>(s.Tenant, new PublishPolicyVersion(policy.PolicyVersionId, "quality.user"));
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var row = await db.PolicyVersions.SingleAsync(x => x.PolicyVersionId == policy.PolicyVersionId);
        row.Eligibility = System.Text.Json.JsonSerializer.Serialize(new EligibilityRule(EligibilityRule.WithinBaseCoverage, CoverageFamilyCode.Base), Yojana.Serialisation.Ledger.LedgerJson.Options);
        row.StartTrigger = StartTrigger.PlanPurchase;
        await db.SaveChangesAsync();
        Assert.Equal("Yes · Loss protection, until 2028-08-31", await Answer());
        row.Eligibility = System.Text.Json.JsonSerializer.Serialize(new EligibilityRule(EligibilityRule.WithinDaysOfBaseStart, CoverageFamilyCode.Base, 30), Yojana.Serialisation.Ledger.LedgerJson.Options);
        await db.SaveChangesAsync();
        Assert.Equal("No", await Answer());
        row.Eligibility = null; row.RegionScope = "[\"DE\"]";
        await db.SaveChangesAsync();
        Assert.Equal("—", await Answer());
        row.RegionScope = "[\"WORLD\"]";
        db.PolicyCoverItemCauses.Remove(await db.PolicyCoverItemCauses.SingleAsync(x => x.PolicyVersionId == row.PolicyVersionId));
        await db.SaveChangesAsync();
        Assert.Equal("No", await Answer());
    }

    [DatabaseFact]
    public async Task Multi_cause_item_has_one_line_and_one_customer_wording_while_future_only_grants_stay_covered()
    {
        var s = await Setup(); using var scope = fixture.ScopeFor(s.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.PolicyCoverItemCauses.Add(new() { TenantId = s.Tenant, PolicyVersionId = s.DamagePolicy, ItemCode = "ADP-LIQ", CauseCode = "DROPPED_KNOCKED" });
        db.PolicyCoverItems.Remove(await db.PolicyCoverItems.SingleAsync(x => x.PolicyVersionId == s.DamagePolicy && x.Code == "ADP-DROP"));
        var future = await db.Entitlements.SingleAsync(x => x.EntitlementId == s.Future);
        db.PolicyCoverItems.Add(new() { TenantId = s.Tenant, PolicyVersionId = future.PolicyVersionId, Code = "FUTURE-SERVICE", Type = "covered", CustomerWording = "Future rack inspection" });
        await db.SaveChangesAsync();
        var data = (await Query(scope.ServiceProvider).ReadAsync(s.Unit, CancellationToken.None))!;
        var liquid = Assert.Single(data.Items, x => x.Grants.Any(g => g.Code == "ADP-LIQ"));
        Assert.Equal(["DROPPED_KNOCKED", "LIQUID_SPILL"], liquid.CauseCodes);
        Assert.Equal(["Dropped or knocked", "Liquid spill"], liquid.CauseNames);
        Assert.Single(liquid.Grants); Assert.Single(liquid.Excludes); Assert.True(liquid.Covered);
        var futureLine = Assert.Single(data.Items, x => x.CustomerWording == "Future rack inspection");
        Assert.True(futureLine.Covered); Assert.Empty(futureLine.Excludes); Assert.True(Assert.Single(futureLine.Grants).Cover.Future);
        Assert.Equal(data.Items.Count(x => x.Covered), data.CoveredCount);
        Assert.Equal(data.Items.Count(x => !x.Covered), data.ExcludedCount);
        var sheet = (await new UnitCustomerDocument(Query(scope.ServiceProvider)).CreateAsync(s.Unit, "en", CancellationToken.None))!;
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(sheet.Html, "Liquid spills"));
        Assert.Contains("from 2026-11-25", sheet.Html);
        var sections = WhatsCoveredQuery.Sections(data, new(Search: "rack", SectionPages: true));
        Assert.Equal(1, sections.CoveredTotal); Assert.Equal(0, sections.ExcludedTotal); Assert.Single(sections.Items);
        Assert.False(data.CustomerEmailEnabled);
    }

    [DatabaseFact]
    public async Task Promises_sharing_a_cause_keep_their_own_lines_and_a_shared_exclusion_is_named_on_each_but_counted_once()
    {
        // Found 2026-10-20 on the demo: joining every item that shares a cause collapsed hardware, parts, firmware and
        // goodwill promises into one line naming the base warranty three times.
        var s = await Setup(); using var scope = fixture.ScopeFor(s.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.PolicyCoverItemCauses.Add(new() { TenantId = s.Tenant, PolicyVersionId = s.BasePolicy, ItemCode = "BASE-FW", CauseCode = "STOPPED_WORKING" });
        db.PolicyCoverItemCauses.Add(new() { TenantId = s.Tenant, PolicyVersionId = s.DamagePolicy, ItemCode = "ADP-DROP", CauseCode = "LIQUID_SPILL" });
        await db.SaveChangesAsync();
        var query = Query(scope.ServiceProvider);
        var data = (await query.ReadAsync(s.Unit, CancellationToken.None))!;

        Assert.Equal((11, 7), (data.CoveredCount, data.ExcludedCount));
        var hardware = Assert.Single(data.Items, x => x.CustomerWording == "Hardware failure in normal use");
        Assert.Equal(["Base warranty", "Extended term"], hardware.Grants.Select(x => x.Cover.FamilyName).Order());
        Assert.Single(data.Items, x => x.CustomerWording == "Firmware and BIOS faults" && x.Covered);
        Assert.All(data.Items, x => Assert.Equal(x.Grants.Count, x.Grants.Select(g => g.Cover.EntitlementId).Distinct().Count()));
        foreach (var wording in new[] { "Liquid spills", "Accidental drops and knocks" })
            Assert.Equal("EX-LIQ", Assert.Single(Assert.Single(data.Items, x => x.CustomerWording == wording).Excludes).Code);
        Assert.DoesNotContain(data.Items, x => x.CustomerWording == "Liquid damage");

        var guidance = (await query.GuidanceAsync(s.Unit, "LIQUID_SPILL", CancellationToken.None))!;
        Assert.Equal("EX-LIQ", Assert.Single(guidance.Excludes).Code);
        Assert.Equal("Accidental drops and knocks · Liquid spills", guidance.SayToCustomer);
    }

    [DatabaseFact]
    public async Task Inactive_cause_is_typed_refusal_and_concurrent_first_claims_seed_defaults_once()
    {
        var s = await Setup();
        SubmitClaim Command() => new(Guid.NewGuid(), s.Unit, null, new(2026, 9, 1), s.Owner, s.Owner, Entity, Entity, "DE", "USD",
            [new(ClaimLineKind.Parts, CoverageFamilyCode.Base, 100, Description: "Fan stopped")], [], ClaimRemedy.Repair, "CALL-RECORDED", At, "quality.user", ReportedCauseCode: "STOPPED_WORKING");
        var existingClaim = await Invoke<SubmitClaimResult>(s.Tenant, Command());
        await Invoke<ClaimCausePage>(s.Tenant, new UpdateClaimCauses(ClaimCauseCatalogue.Defaults.Select(x => x.Code == "STOPPED_WORKING" ? x with { Active = false } : x).ToList()));
        Assert.Equal("claim_cause_inactive", (await Assert.ThrowsAsync<CommandRefusedException>(() => Invoke<SubmitClaimResult>(s.Tenant, Command()))).Rule);
        using (var existing = fixture.ScopeFor(s.Tenant))
            Assert.Equal("STOPPED_WORKING", (await existing.ServiceProvider.GetRequiredService<SerialisationDbContext>().Claims.SingleAsync(x => x.ClaimId == existingClaim.ClaimId)).ReportedCauseCode);
        // No item or claim refers to the catalogue on this tenant yet, so two first claims exercise lazy seeding.
        var fresh = await fixture.NewTenantAsync("causes-race", "USD");
        var item = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(item));
        var unit = (await Invoke<CommissionUnitsResult>(fresh, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, "RACE-SERVER-01")])], At.AddDays(-1), "quality.user", Entity))).Lines[0].UnitId!.Value;
        var commands = new[] { Command() with { UnitId = unit }, Command() with { UnitId = unit } };
        var results = await Task.WhenAll(commands.Select(x => Invoke<SubmitClaimResult>(fresh, x)));
        Assert.Equal(2, results.Select(x => x.ClaimId).Distinct().Count());
        using var scope = fixture.ScopeFor(fresh);
        Assert.Equal(8, await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().ClaimCauses.CountAsync());
    }

    [DatabaseFact]
    public async Task Item_and_catalogue_http_edits_require_versions_and_stale_edits_return_document_changed()
    {
        var tenant = await fixture.NewTenantAsync("items-conflict");
        var draft = await Policy(tenant, CoverageFamilyCode.Base, "Base warranty", []);
        using var manager = await fixture.AuthenticatedClientAsync(tenant, YojanaRole.ServiceManager);
        var path = $"/api/warranty-policies/versions/{draft.PolicyVersionId}/items";
        var loaded = (await manager.GetFromJsonAsync<PolicyCoverItemPage>(path))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PutAsJsonAsync(path, new UpdatePolicyCoverItems(draft.PolicyVersionId, []))).StatusCode);
        var command = new UpdatePolicyCoverItems(draft.PolicyVersionId, [Item("FAN", "Fan failure")], ExpectedVersion: loaded.Version);
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(path, command)).StatusCode);
        using var stale = await manager.PutAsJsonAsync(path, command);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var problem = await stale.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("document_changed", problem.GetProperty("rule").GetString()); Assert.True(problem.TryGetProperty("unblockingStep", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PutAsJsonAsync("/api/claim-causes", new UpdateClaimCauses(ClaimCauseCatalogue.Defaults))).StatusCode);
        var causes = (await manager.GetFromJsonAsync<ClaimCausePage>("/api/claim-causes"))!;
        var edit = new UpdateClaimCauses(causes.Items.Select(x => x with { CustomerWording = x.CustomerWording + " today" }).ToList(), ExpectedVersion: causes.Version);
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync("/api/claim-causes", edit)).StatusCode);
        using var staleCause = await manager.PutAsJsonAsync("/api/claim-causes", edit);
        Assert.Equal(HttpStatusCode.Conflict, staleCause.StatusCode);
        Assert.Equal("document_changed", (await staleCause.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("rule").GetString());
        using var admin = await fixture.AuthenticatedClientAsync(tenant, YojanaRole.TenantAdministrator);
        var current = (await admin.GetFromJsonAsync<ClaimCausePage>("/api/claim-causes"))!;
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync("/api/claim-causes", new UpdateClaimCauses(current.Items, ExpectedVersion: current.Version))).StatusCode);
    }

    [DatabaseFact]
    public async Task Entitlement_creation_reads_terms_without_any_cover_item_queries_and_customer_sheet_humanises_meters()
    {
        var s = await Setup();
        var policy = await Policy(s.Tenant, "inspection", "Rack inspection", [Item("RACK", "Rack inspection")], [new("nbd_visits", 4)]);
        await Invoke<PolicyDraftResult>(s.Tenant, new PublishPolicyVersion(policy.PolicyVersionId, "quality.user"));
        using (PolicyCommandCounter.Capture(out var commands))
        {
            await Invoke<CreateEntitlementResult>(s.Tenant, new CreateEntitlement(Guid.NewGuid(), s.Unit, "inspection", new(2026, 1, 1), Entity, At, "quality.user",
                new Dictionary<string, DateTime> { [StartTrigger.Invoice] = At }, policy.PolicyVersionId));
            Assert.NotEmpty(commands);
            Assert.DoesNotContain(commands, sql => sql.Contains("policy_cover_item", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(commands, sql => sql.Contains("warranty_policy_version", StringComparison.OrdinalIgnoreCase));
            Assert.True(commands.Count < 30, $"Expected a bounded entitlement path; got {commands.Count} commands.");
        }
        using var scope = fixture.ScopeFor(s.Tenant);
        var query = Query(scope.ServiceProvider);
        var sheet = (await new UnitCustomerDocument(query).CreateAsync(s.Unit, "en", CancellationToken.None))!;
        Assert.DoesNotContain("nbd_visits", sheet.Html); Assert.Contains("next-business-day visits", sheet.Html);
        var guidance = (await query.GuidanceAsync(s.Unit, "LIQUID_SPILL", CancellationToken.None))!;
        Assert.All(guidance.Grants.SelectMany(x => x.Usage), x => Assert.Null(x.AfterThisClaim));
    }

}
