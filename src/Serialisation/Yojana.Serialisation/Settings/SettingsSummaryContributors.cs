using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Settings;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Claims.Risk;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Workbench;

namespace Yojana.Serialisation.Settings;

public abstract class SerialisationSettingsSummaryContributor
{
    protected static readonly IReadOnlyList<string> AdministratorRoles = [YojanaRole.TenantAdministrator];

    protected static IReadOnlyList<SettingsSummaryLine> Line(
        string key,
        IReadOnlyDictionary<string, long> counts,
        string? attention = null) =>
        [new SettingsSummaryLine(key, counts, attention)];
}

public sealed class ScanningSettingsSummaryContributor(ScanningSettings settings)
    : SerialisationSettingsSummaryContributor, ISettingsSummaryContributor
{
    // Hidden coupling: this key must also appear as summaryKey in web/src/settings/registry.ts. The architecture
    // cross-check protects both otherwise-independent sides from silently drifting.
    public const string Key = "scanning";

    public IReadOnlyList<SettingsSummaryKey> Keys { get; } = [new(Key, AdministratorRoles)];

    public async Task<IReadOnlyList<SettingsSummaryLine>> ReadAsync(
        IReadOnlySet<string> keys,
        CancellationToken cancellationToken)
    {
        if (!keys.Contains(Key)) return [];
        var policy = await settings.GetAsync(cancellationToken);
        return Line(Key, new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["endPauseMs"] = policy.EndPauseMs,
            ["repeatWindowMs"] = policy.RepeatWindowMs,
        });
    }
}

public sealed class LabelsSettingsSummaryContributor(
    SerialisationDbContext db,
    ITenantContext tenant) : SerialisationSettingsSummaryContributor, ISettingsSummaryContributor
{
    // Hidden coupling: this key must also appear as summaryKey in web/src/settings/registry.ts. The architecture
    // cross-check protects both otherwise-independent sides from silently drifting.
    public const string Key = "labels";

    public IReadOnlyList<SettingsSummaryKey> Keys { get; } = [new(Key, AdministratorRoles)];

    public async Task<IReadOnlyList<SettingsSummaryLine>> ReadAsync(
        IReadOnlySet<string> keys,
        CancellationToken cancellationToken)
    {
        if (!keys.Contains(Key)) return [];
        // There is no printer catalogue in this module. The existing print-job filter defines known printers as
        // distinct references that the tenant has used, so the landing summary follows that same source of truth.
        var rows = await db.Database.SqlQuery<CountRow>($"""
            SELECT grouped.metric AS [Metric], COUNT_BIG(DISTINCT grouped.value) AS [Value]
            FROM
            (
                SELECT N'templates' AS metric, CONVERT(nvarchar(36), template.template_id) AS value
                FROM [serialisation].[label_template] AS template
                WHERE template.tenant_id = {tenant.TenantId.Value}

                UNION ALL

                SELECT N'printers' AS metric, job.printer_ref AS value
                FROM [serialisation].[print_job] AS job
                WHERE job.tenant_id = {tenant.TenantId.Value}
                  AND job.printer_ref IS NOT NULL
            ) AS grouped
            GROUP BY grouped.metric
            """).ToListAsync(cancellationToken);
        var counts = Counts("printers", "templates");
        foreach (var row in rows) counts[row.Metric] = row.Value;
        return Line(Key, counts);
    }

    private static Dictionary<string, long> Counts(params string[] names) =>
        names.ToDictionary(name => name, _ => 0L, StringComparer.Ordinal);

    private sealed class CountRow
    {
        public required string Metric { get; init; }
        public long Value { get; init; }
    }
}

public sealed class JurisdictionsSettingsSummaryContributor(
    SerialisationDbContext db,
    ITenantContext tenant,
    IClock clock) : SerialisationSettingsSummaryContributor, ISettingsSummaryContributor
{
    // Hidden coupling: this key must also appear as summaryKey in web/src/settings/registry.ts. The architecture
    // cross-check protects both otherwise-independent sides from silently drifting.
    public const string Key = "jurisdictions";
    public const string AwaitingLegalConfirmation = "awaiting_legal_confirmation";

    public IReadOnlyList<SettingsSummaryKey> Keys { get; } = [new(Key, AdministratorRoles)];

    public async Task<IReadOnlyList<SettingsSummaryLine>> ReadAsync(
        IReadOnlySet<string> keys,
        CancellationToken cancellationToken)
    {
        if (!keys.Contains(Key)) return [];
        var today = DateOnly.FromDateTime(clock.UtcNow);
        var row = await db.Database.SqlQuery<JurisdictionCount>($"""
            SELECT COUNT_BIG(*) AS [RuleSets],
                   COALESCE(SUM(CASE WHEN [rule].confirmed_by_counsel = CAST(0 AS bit) THEN CAST(1 AS bigint) ELSE CAST(0 AS bigint) END), 0) AS [AwaitingLegal]
            FROM [serialisation].[jurisdiction_rule] AS [rule]
            WHERE [rule].tenant_id = {tenant.TenantId.Value}
              AND [rule].effective_from <= {today}
              AND ([rule].effective_to IS NULL OR [rule].effective_to > {today})
            """).SingleAsync(cancellationToken);
        return Line(Key, new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["ruleSets"] = row.RuleSets,
        }, row.AwaitingLegal > 0 ? AwaitingLegalConfirmation : null);
    }

    private sealed class JurisdictionCount
    {
        public long RuleSets { get; init; }
        public long AwaitingLegal { get; init; }
    }
}

public sealed class WorkbenchChoicesSettingsSummaryContributor(
    WorkbenchChoiceSettings choices,
    CorrectionApprovalSettings approvals) : SerialisationSettingsSummaryContributor, ISettingsSummaryContributor
{
    // Hidden coupling: this key must also appear as summaryKey in web/src/settings/registry.ts. The architecture
    // cross-check protects both otherwise-independent sides from silently drifting.
    public const string Key = "workbench_choices";

    public IReadOnlyList<SettingsSummaryKey> Keys { get; } = [new(Key, AdministratorRoles)];

    public async Task<IReadOnlyList<SettingsSummaryLine>> ReadAsync(
        IReadOnlySet<string> keys,
        CancellationToken cancellationToken)
    {
        if (!keys.Contains(Key)) return [];

        // TIGHT COUPLING (existing tenant-setting storage): workbench choices and correction approvals are owned by
        // Serialisation but persisted through Platform's ITenantSettings service. Reading those two existing settings
        // is the only migration-free source of the configured rules; replacing this with SerialisationDbContext would
        // make this summary disagree with the settings page. The matching warning is on both setting wrappers.
        var configuration = await choices.GetConfigurationAsync(cancellationToken);
        var policy = await approvals.GetAsync(cancellationToken);
        var shown = configuration.Choices.Where(choice => choice.Status == WorkbenchChoiceStatus.Shown).ToList();
        var twoApprovers = shown.LongCount(choice => choice.Commands.Any(command =>
            policy.Commands.FirstOrDefault(rule => rule.Command == command) is { } rule &&
            ((rule.RequiredRoles?.Count ?? 0) > 0 || (rule.Conditions ?? []).Any(condition =>
                condition.Fact == CorrectionApprovalFact.Always &&
                condition.Action == CorrectionApprovalCondition.NeedsApprover))));
        return Line(Key, new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["choices"] = shown.Count,
            ["twoApprovers"] = twoApprovers,
        });
    }
}

public sealed class ClaimsRiskSettingsSummaryContributor(
    SerialisationDbContext db,
    ITenantContext tenant) : SerialisationSettingsSummaryContributor, ISettingsSummaryContributor
{
    // Hidden coupling: this key must also appear as summaryKey in web/src/settings/registry.ts. The architecture
    // cross-check protects both otherwise-independent sides from silently drifting.
    public const string Key = "claims_risk";

    public IReadOnlyList<SettingsSummaryKey> Keys { get; } = [new(Key, AdministratorRoles)];

    public async Task<IReadOnlyList<SettingsSummaryLine>> ReadAsync(
        IReadOnlySet<string> keys,
        CancellationToken cancellationToken)
    {
        if (!keys.Contains(Key)) return [];
        var count = await db.ClaimRiskFlags.AsNoTracking()
            .Where(flag => flag.TenantId == tenant.TenantId && flag.Status == ClaimRiskFlagStatus.Open)
            .Select(flag => flag.ClaimId)
            .Distinct()
            .LongCountAsync(cancellationToken);
        return Line(Key, new Dictionary<string, long>(StringComparer.Ordinal) { ["claimsHeld"] = count });
    }
}
