using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Yojana.Knowledge.Data;
using Yojana.Knowledge.Data.Entities;
using Yojana.Knowledge.Settings;
using Yojana.Knowledge.Contracts;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Settings;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Claims.Risk;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Workbench;
using Yojana.Web.Settings;
using Xunit.Abstractions;

namespace Yojana.Knowledge.DatabaseTests;

[Collection("knowledge host")]
public sealed class KnowledgeSettingsSummaryContributorTests(KnowledgeHostFixture fixture, ITestOutputHelper output)
{
    [DatabaseFact]
    public async Task Counts_distinct_changed_screens_and_entries_to_review_for_only_the_selected_tenant()
    {
        var tenant = await fixture.NewTenantAsync("sum");
        var other = await fixture.NewTenantAsync("sumother");
        await AddRowsAsync(tenant.Tenant, tenant.Administrator, tenant.Staff, includeSecondRows: true);
        await AddRowsAsync(other.Tenant, other.Administrator, other.Staff, includeSecondRows: false);

        using var scope = fixture.ScopeFor(tenant.Tenant);
        var contributor = scope.ServiceProvider.GetServices<ISettingsSummaryContributor>()
            .Single(candidate => candidate.Keys.Any(key => key.Key == KnowledgeScreenChangesSettingsSummaryContributor.Key));
        var line = Assert.Single(await contributor.ReadAsync(
            new HashSet<string>([KnowledgeScreenChangesSettingsSummaryContributor.Key]), default));

        Assert.Equal(2, line.Counts["screensMarked"]);
        Assert.Equal(2, line.Counts["entriesToReview"]);
    }

    [DatabaseFact]
    public async Task Settings_summary_refuses_a_portal_principal_even_if_its_membership_has_an_administrator_role()
    {
        var tenant = await fixture.NewTenantAsync("sumportal");
        using var client = await fixture.ClientAsync(tenant.Tenant, tenant.Administrator, portal: true);

        using var response = await client.GetAsync("/api/settings/summary");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [DatabaseFact]
    public async Task Settings_summary_completes_within_the_demo_data_budget()
    {
        var tenant = await fixture.NewTenantAsync("sumdemo");
        await AddRowsAsync(tenant.Tenant, tenant.Administrator, tenant.Staff, includeSecondRows: true);
        await AddDemoSerialisationRowsAsync(tenant.Tenant);
        using (var scope = fixture.ScopeFor(tenant.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
            db.ScreenChanges.Add(ScreenChange(tenant.Tenant, "settings/jurisdictions", tenant.Administrator));
            await db.SaveChangesAsync();
        }

        using var client = await fixture.ClientAsync(tenant.Tenant, tenant.Administrator);
        _ = await client.GetFromJsonAsync<SettingsSummaryResponse>("/api/settings/summary");
        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetFromJsonAsync<SettingsSummaryResponse>("/api/settings/summary");
        stopwatch.Stop();

        Assert.NotNull(response);
        Assert.Equal(8, response.Lines.Count);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100),
            $"Settings summary took {stopwatch.Elapsed.TotalMilliseconds:F2} ms.");
        output.WriteLine("UI-04 settings summary endpoint: {0:F2} ms on 558 units, 7 templates, 18 choices.",
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private async Task AddRowsAsync(TenantId tenant, Guid author, Guid directedTo, bool includeSecondRows)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
        db.ScreenChanges.Add(ScreenChange(tenant, "settings/users", author));
        db.Entries.Add(Entry(tenant, author, directedTo, needsReview: true, db));
        if (includeSecondRows)
        {
            db.ScreenChanges.AddRange(
                ScreenChange(tenant, "settings/users", author),
                ScreenChange(tenant, "settings/labels", author));
            db.Entries.AddRange(
                Entry(tenant, author, directedTo, needsReview: true, db),
                Entry(tenant, author, directedTo, needsReview: false, db));
        }
        await db.SaveChangesAsync();
    }

    private async Task AddDemoSerialisationRowsAsync(TenantId tenant)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.Units.AddRange(Enumerable.Range(0, 558).Select(_ => new Yojana.Serialisation.Data.Entities.Unit
        {
            TenantId = tenant, UnitId = Guid.CreateVersion7(), LifecycleStatus = LifecycleStatus.Released,
            Origin = UnitOrigin.Purchased, SerialisationPoint = SerialisationPoint.Receipt,
            RetentionClass = "standard_10y", CreatedAt = fixture.Clock.UtcNow,
            CreatedEventId = Guid.CreateVersion7(), CreatedEventTime = fixture.Clock.UtcNow,
        }));
        db.LabelTemplates.AddRange(Enumerable.Range(1, 7).Select(index => new LabelTemplate
        {
            TenantId = tenant, TemplateId = Guid.CreateVersion7(), CreateCommandId = Guid.CreateVersion7(),
            Name = $"Fictional server label {index}", AppliesTo = LabelTemplateAppliesTo.ItemFamily,
            AppliesToValue = Guid.NewGuid().ToString("N"), CreatedAt = fixture.Clock.UtcNow,
            CreatedBy = "summary-fixture",
        }));
        db.PrintJobs.AddRange(Enumerable.Range(1, 3).Select(index => new PrintJob
        {
            TenantId = tenant, JobId = Guid.CreateVersion7(), JobNumber = $"LBL-DEMO-{index}",
            CommandId = Guid.CreateVersion7(), PrinterRef = $"printer-{index}", RequestedBy = "summary-fixture",
            RequestedAt = fixture.Clock.UtcNow, Status = LabelPrintStatus.Sent,
        }));
        db.JurisdictionRules.AddRange(Enumerable.Range(1, 4).Select(index => DemoRule(tenant, index)));
        var claim = new Yojana.Serialisation.Data.Entities.Claim
        {
            TenantId = tenant, ClaimId = Guid.CreateVersion7(), ClaimNumber = "CLM-DEMO-1",
            CommandId = Guid.CreateVersion7(), UnitId = Guid.CreateVersion7(),
            ClaimDate = DateOnly.FromDateTime(fixture.Clock.UtcNow), ClaimantPartyRef = Guid.CreateVersion7(),
            SettlementPartyRef = Guid.CreateVersion7(), ServiceEntityId = Guid.CreateVersion7(),
            ObligorEntityId = Guid.CreateVersion7(), ServiceCountry = "GB", Currency = "GBP",
            CoverageSource = ClaimCoverageSource.Chargeable, Remedy = ClaimRemedy.Repair,
            Status = ClaimStatus.Submitted, SubmittedAt = fixture.Clock.UtcNow, SubmittedBy = "summary-fixture",
            LastEventId = Guid.CreateVersion7(), LastModifiedBy = "summary-fixture", LastModifiedAt = fixture.Clock.UtcNow,
        };
        db.Claims.Add(claim);
        db.ClaimRiskFlags.Add(new ClaimRiskFlag
        {
            TenantId = tenant, FlagId = Guid.CreateVersion7(), ClaimId = claim.ClaimId,
            RuleCode = ClaimRiskRuleCode.RepeatUnitClaims, ThresholdSnapshot = "{}", Evidence = "{}",
            RaisedAt = fixture.Clock.UtcNow, RaisedByEvaluation = ClaimRiskEvaluation.Submission,
            Status = ClaimRiskFlagStatus.Open,
        });
        await db.SaveChangesAsync();

        var choices = StandardWorkbenchChoices.Default.Choices.ToList();
        while (choices.Count < 18)
        {
            var source = StandardWorkbenchChoices.Default.Choices[choices.Count % StandardWorkbenchChoices.Default.Choices.Count];
            choices.Add(source with
            {
                Choice = $"demo_choice_{choices.Count + 1}", Label = $"Fictional demo choice {choices.Count + 1}",
                Order = 200 + choices.Count, Standard = false, Fallback = false,
            });
        }
        var configuration = StandardWorkbenchChoices.Default with { Choices = choices };
        await scope.ServiceProvider.GetRequiredService<ITenantSettings>().SetAsync(WorkbenchChoiceSettings.Key,
            JsonSerializer.Serialize(configuration, LedgerJson.Options), default);
    }

    private JurisdictionRule DemoRule(TenantId tenant, int index)
    {
        var id = Guid.CreateVersion7();
        return new JurisdictionRule
        {
            TenantId = tenant, RuleId = id, RootRuleId = id, CommandId = Guid.CreateVersion7(), Version = 1,
            Kind = JurisdictionRuleKind.StatutoryConformityTerm, Scope = JurisdictionScope.Any,
            BuyerClass = JurisdictionBuyerClass.Any, DateTested = JurisdictionDateTested.Purchase,
            EffectiveFrom = DateOnly.FromDateTime(fixture.Clock.UtcNow).AddDays(-30), Value = "24",
            ConfirmedByCounsel = index > 1,
            CounselConfirmedOn = index > 1 ? DateOnly.FromDateTime(fixture.Clock.UtcNow) : null,
            CreatedBy = "summary-fixture", ApprovedBy = "summary-fixture", CreatedAt = fixture.Clock.UtcNow,
        };
    }

    private static KnowledgeScreenChange ScreenChange(TenantId tenant, string reference, Guid actor) => new()
    {
        TenantId = tenant, ChangeId = Guid.CreateVersion7(), ScreenReference = reference,
        ScreenLabel = reference.Replace('/', ' '), Reason = "Fixture screen contract changed",
        ChangedBy = actor, ChangedAt = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
    };

    private static KnowledgeEntry Entry(TenantId tenant, Guid author, Guid directedTo, bool needsReview,
        KnowledgeDbContext db)
    {
        var questionId = Guid.CreateVersion7();
        var at = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        db.Questions.Add(new Question
        {
            TenantId = tenant, QuestionId = questionId, RecordKind = RecordKind.Unit,
            RecordId = Guid.CreateVersion7(), Title = "Fictional server service", Body = "Which cover applies?",
            Status = QuestionStatus.Open, DirectedToUser = directedTo, AskedBy = author, AskedAt = at, UpdatedAt = at,
        });
        return new KnowledgeEntry
        {
            TenantId = tenant, EntryId = Guid.CreateVersion7(), Title = "Fictional storage service",
            Problem = "A fictional storage controller needs service.", Answer = "Check its service cover.",
            Steps = "Review the serial and cover.", WrittenAgainstJson = "[]", Status = KnowledgeEntryStatus.Published,
            SourceQuestionId = questionId, CurrentVersion = 1, PublishedVersion = 1, NeedsReview = needsReview,
            NeedsReviewSince = needsReview ? at : null, AuthorId = author, CreatedAt = at,
            LastSavedBy = author, LastSavedAt = at, PublishedBy = directedTo, PublishedAt = at,
        };
    }
}
