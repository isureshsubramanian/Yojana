using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Settings;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Settings;
using Yojana.Serialisation.Workbench;
using Xunit;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class SettingsSummaryContributorTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Each_serialisation_summary_counts_only_its_tenant()
    {
        var tenant = new TenantId(Guid.CreateVersion7());
        var other = new TenantId(Guid.CreateVersion7());
        await AddTenantsAsync(tenant, other);
        await AddSerialisationRowsAsync(tenant, includeSecondRows: true);
        await AddSerialisationRowsAsync(other, includeSecondRows: false);

        using (var otherScope = fixture.ScopeFor(other))
        {
            var settings = otherScope.ServiceProvider.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>();
            var empty = StandardWorkbenchChoices.Default with { Choices = [] };
            await settings.SetAsync(WorkbenchChoiceSettings.Key, JsonSerializer.Serialize(empty, LedgerJson.Options), default);
        }

        using var scope = fixture.ScopeFor(tenant);
        var labels = await ReadAsync(scope, LabelsSettingsSummaryContributor.Key);
        Assert.Equal(2, labels.Counts["printers"]);
        Assert.Equal(2, labels.Counts["templates"]);

        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var jurisdictionsContributor = new JurisdictionsSettingsSummaryContributor(db,
            scope.ServiceProvider.GetRequiredService<ITenantContext>(), new FixedClock(At));
        var jurisdictions = Assert.Single(await jurisdictionsContributor.ReadAsync(
            new HashSet<string>([JurisdictionsSettingsSummaryContributor.Key]), default));
        Assert.Equal(2, jurisdictions.Counts["ruleSets"]);
        Assert.Null(jurisdictions.Attention);

        var currentRule = await db.JurisdictionRules.FirstAsync(rule => rule.TenantId == tenant
            && rule.EffectiveFrom <= DateOnly.FromDateTime(At)
            && (rule.EffectiveTo == null || rule.EffectiveTo > DateOnly.FromDateTime(At)));
        currentRule.ConfirmedByCounsel = false;
        currentRule.CounselConfirmedOn = null;
        await db.SaveChangesAsync();
        jurisdictions = Assert.Single(await jurisdictionsContributor.ReadAsync(
            new HashSet<string>([JurisdictionsSettingsSummaryContributor.Key]), default));
        Assert.Equal(JurisdictionsSettingsSummaryContributor.AwaitingLegalConfirmation, jurisdictions.Attention);

        var workbench = await ReadAsync(scope, WorkbenchChoicesSettingsSummaryContributor.Key);
        Assert.Equal(StandardWorkbenchChoices.Default.Choices.Count(choice => choice.Status == WorkbenchChoiceStatus.Shown),
            workbench.Counts["choices"]);
        Assert.Equal(3, workbench.Counts["twoApprovers"]);

        var claims = await ReadAsync(scope, ClaimsRiskSettingsSummaryContributor.Key);
        Assert.Equal(2, claims.Counts["claimsHeld"]);

        var scanning = await ReadAsync(scope, ScanningSettingsSummaryContributor.Key);
        Assert.Equal(ScanningSettings.DefaultEndPauseMs, scanning.Counts["endPauseMs"]);
        Assert.Equal(ScanningSettings.DefaultRepeatWindowMs, scanning.Counts["repeatWindowMs"]);
    }

    private static async Task<SettingsSummaryLine> ReadAsync(IServiceScope scope, string key)
    {
        var contributor = scope.ServiceProvider.GetServices<ISettingsSummaryContributor>()
            .Single(candidate => candidate.Keys.Any(candidateKey => candidateKey.Key == key));
        return Assert.Single(await contributor.ReadAsync(new HashSet<string>([key]), default));
    }

    private async Task AddTenantsAsync(TenantId tenant, TenantId other)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Tenants.AddRange(
            new Tenant { Id = tenant, Code = $"SS{Guid.NewGuid():N}"[..20], Name = "Fictional Storage Systems", CreatedAt = At },
            new Tenant { Id = other, Code = $"SO{Guid.NewGuid():N}"[..20], Name = "Fictional Network Systems", CreatedAt = At });
        await db.SaveChangesAsync();
    }

    private async Task AddSerialisationRowsAsync(TenantId tenant, bool includeSecondRows)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.LabelTemplates.Add(Label(tenant, "Server asset label"));
        db.PrintJobs.Add(Print(tenant, "printer-a"));
        db.JurisdictionRules.Add(Rule(tenant, confirmed: true));
        var firstClaim = Claim(tenant);
        db.Claims.Add(firstClaim);
        db.ClaimRiskFlags.Add(Flag(tenant, firstClaim.ClaimId, ClaimRiskRuleCode.RepeatUnitClaims));

        if (includeSecondRows)
        {
            db.LabelTemplates.Add(Label(tenant, "Storage asset label"));
            db.PrintJobs.AddRange(Print(tenant, "printer-a"), Print(tenant, "printer-b"));
            db.JurisdictionRules.AddRange(Rule(tenant, confirmed: true),
                Rule(tenant, confirmed: false, effectiveTo: DateOnly.FromDateTime(At).AddDays(-1)),
                Rule(tenant, confirmed: false, effectiveFrom: DateOnly.FromDateTime(At).AddDays(1)));
            var secondClaim = Claim(tenant);
            var decidedClaim = Claim(tenant);
            db.Claims.AddRange(secondClaim, decidedClaim);
            db.ClaimRiskFlags.AddRange(
                Flag(tenant, firstClaim.ClaimId, ClaimRiskRuleCode.RepeatFault),
                Flag(tenant, secondClaim.ClaimId, ClaimRiskRuleCode.ChannelMismatch),
                Flag(tenant, decidedClaim.ClaimId, ClaimRiskRuleCode.PartNotReturned, ClaimRiskFlagStatus.Cleared));
        }
        await db.SaveChangesAsync();
    }

    private static LabelTemplate Label(TenantId tenant, string name) => new()
    {
        TenantId = tenant, TemplateId = Guid.CreateVersion7(), CreateCommandId = Guid.CreateVersion7(),
        Name = name, AppliesTo = LabelTemplateAppliesTo.ItemFamily, AppliesToValue = Guid.NewGuid().ToString("N"),
        CreatedAt = At, CreatedBy = "summary-fixture",
    };

    private static PrintJob Print(TenantId tenant, string printer) => new()
    {
        TenantId = tenant, JobId = Guid.CreateVersion7(), JobNumber = "LBL-" + Guid.NewGuid().ToString("N")[..12],
        CommandId = Guid.CreateVersion7(), PrinterRef = printer, RequestedBy = "summary-fixture", RequestedAt = At,
        Status = LabelPrintStatus.Sent,
    };

    private static JurisdictionRule Rule(TenantId tenant, bool confirmed, DateOnly? effectiveFrom = null,
        DateOnly? effectiveTo = null)
    {
        var id = Guid.CreateVersion7();
        return new JurisdictionRule
        {
            TenantId = tenant, RuleId = id, RootRuleId = id, CommandId = Guid.CreateVersion7(), Version = 1,
            Kind = JurisdictionRuleKind.StatutoryConformityTerm, Scope = JurisdictionScope.Any,
            BuyerClass = JurisdictionBuyerClass.Any, DateTested = JurisdictionDateTested.Purchase,
            EffectiveFrom = effectiveFrom ?? DateOnly.FromDateTime(At).AddDays(-30), EffectiveTo = effectiveTo, Value = "24",
            ConfirmedByCounsel = confirmed, CounselConfirmedOn = confirmed ? DateOnly.FromDateTime(At) : null,
            CreatedBy = "summary-fixture", ApprovedBy = "summary-fixture", CreatedAt = At,
            ClosedAt = effectiveTo is null ? null : At,
        };
    }

    private static Claim Claim(TenantId tenant) => new()
    {
        TenantId = tenant, ClaimId = Guid.CreateVersion7(), ClaimNumber = "CLM-" + Guid.NewGuid().ToString("N")[..12],
        CommandId = Guid.CreateVersion7(), UnitId = Guid.CreateVersion7(), ClaimDate = DateOnly.FromDateTime(At),
        ClaimantPartyRef = Guid.CreateVersion7(), SettlementPartyRef = Guid.CreateVersion7(),
        ServiceEntityId = Guid.CreateVersion7(), ObligorEntityId = Guid.CreateVersion7(), ServiceCountry = "GB",
        Currency = "GBP", CoverageSource = ClaimCoverageSource.Chargeable, Remedy = ClaimRemedy.Repair,
        Status = ClaimStatus.Submitted, SubmittedAt = At, SubmittedBy = "summary-fixture",
        LastEventId = Guid.CreateVersion7(), LastModifiedBy = "summary-fixture", LastModifiedAt = At,
    };

    private static ClaimRiskFlag Flag(TenantId tenant, Guid claimId, string rule,
        string status = ClaimRiskFlagStatus.Open) => new()
    {
        TenantId = tenant, FlagId = Guid.CreateVersion7(), ClaimId = claimId, RuleCode = rule,
        ThresholdSnapshot = "{}", Evidence = "{}", RaisedAt = At,
        RaisedByEvaluation = ClaimRiskEvaluation.Submission, Status = status,
        DecidedAt = status == ClaimRiskFlagStatus.Open ? null : At,
        DecidedBy = status == ClaimRiskFlagStatus.Open ? null : "summary-reviewer",
        DecisionReason = status == ClaimRiskFlagStatus.Open ? null : "Reviewed in fixture",
    };

    private sealed record FixedClock(DateTime UtcNow) : IClock;
}
