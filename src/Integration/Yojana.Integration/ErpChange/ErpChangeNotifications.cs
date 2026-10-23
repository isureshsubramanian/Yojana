using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Mail;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.ErpChange;

internal sealed record StoredErpChangeNotificationSettings(bool DigestEnabled, int DigestHour,
    IReadOnlyList<string> MasterDataRecipientRoles, IReadOnlyList<string> StockRecipientRoles);

public sealed class ErpChangeNotificationSettings(ITenantSettings settings, ITenantContext tenant,
    IClock clock, IntegrationDbContext db, IConnectionAuditSink audit)
{
    public const string SettingKey = "integration.erp_change.notifications";
    public static readonly IReadOnlyList<string> DefaultMasterDataRoles = [YojanaRole.ItemMasterOwner];
    public static readonly IReadOnlyList<string> DefaultStockRoles = [YojanaRole.InventoryController];

    public async Task<ErpChangeNotificationSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetVersionedAsync(SettingKey, cancellationToken);
        var value = stored is null
            ? Normalise(new(true, 8, DefaultMasterDataRoles, DefaultStockRoles))
            : Read(stored.Value);
        return View(value, stored?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<ErpChangeNotificationSettingsView> SetAsync(
        UpdateErpChangeNotificationSettings request, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new CommandRefusedException("audit_actor_required", "An authenticated actor is required.");
        var run = await db.ErpChangeRuns.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.Status == ErpChangeRunStatus.Active, cancellationToken)
            ?? throw new CommandRefusedException("erp_change_run_not_found",
                "An active ERP change is required to change its notifications.");
        var before = await GetAsync(cancellationToken);
        var value = Normalise(new(request.DigestEnabled, request.DigestHour,
            request.MasterDataRecipientRoles, request.StockRecipientRoles));
        var saved = await settings.SetAsync(SettingKey,
            JsonSerializer.Serialize(value, IntegrationContracts.Json), request.ExpectedVersion,
            cancellationToken);
        await audit.AppendAsync(new(tenant.TenantId, TimeOrderedId.New(clock.UtcNow), run.TargetConnectionId,
            ConnectionAuditEventType.ErpChangeNotificationSettingsChanged, actor, clock.UtcNow,
            JsonSerializer.Serialize(new
            {
                before = new
                {
                    digest_enabled = before.DigestEnabled, digest_hour = before.DigestHour,
                    master_data_recipient_roles = before.MasterDataRecipientRoles,
                    stock_recipient_roles = before.StockRecipientRoles,
                },
                after = new
                {
                    digest_enabled = value.DigestEnabled, digest_hour = value.DigestHour,
                    master_data_recipient_roles = value.MasterDataRecipientRoles,
                    stock_recipient_roles = value.StockRecipientRoles,
                },
            }, IntegrationContracts.Json)), cancellationToken);
        return View(value, saved.Version);
    }

    private static StoredErpChangeNotificationSettings Read(string json)
    {
        try
        {
            return Normalise(JsonSerializer.Deserialize<StoredErpChangeNotificationSettings>(json,
                IntegrationContracts.Json) ?? new(true, 8, DefaultMasterDataRoles, DefaultStockRoles));
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Tenant setting '{SettingKey}' is invalid.", exception);
        }
    }

    private static StoredErpChangeNotificationSettings Normalise(StoredErpChangeNotificationSettings value)
    {
        if (value.DigestHour is < 0 or > 23)
            throw new CommandRefusedException("erp_change_digest_hour_invalid",
                "The digest hour must be a whole tenant-local hour from 0 to 23.");
        return value with
        {
            MasterDataRecipientRoles = Roles(value.MasterDataRecipientRoles, "master-data"),
            StockRecipientRoles = Roles(value.StockRecipientRoles, "stock"),
        };
    }

    private static List<string> Roles(IReadOnlyList<string>? supplied, string kind)
    {
        var roles = supplied?.Select(role => role?.Trim() ?? "").Where(role => role.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() ?? [];
        if (roles.Count == 0)
            throw new CommandRefusedException("erp_change_notification_roles_required",
                $"Choose at least one {kind} recipient role.");
        if (roles.Any(role => !YojanaRole.IsKnown(role)))
            throw new CommandRefusedException("erp_change_notification_role_invalid",
                "Every notification recipient must be a known tenant role.");
        if (roles.Any(role => role is YojanaRole.IntegrationOperator or YojanaRole.TenantAdministrator))
            throw new CommandRefusedException("erp_change_notification_role_excluded",
                "Integration operators and tenant administrators use the ERP change tab and do not receive these mails.");
        // A mail links to a screen the recipient must be able to open: only the roles the ErpChangeRead policy
        // admits besides operators and administrators (PlatformSetup) may be chosen; the settings card offers the
        // same two (web/src/integration/erpChangeModel.ts, notificationRecipientRoles). Keep all three in step.
        if (roles.Any(role => role is not (YojanaRole.ItemMasterOwner or YojanaRole.InventoryController)))
            throw new CommandRefusedException("erp_change_notification_role_without_access",
                "Only item master owners and inventory controllers can open the ERP change screens these mails link to.");
        return roles;
    }

    private static ErpChangeNotificationSettingsView View(StoredErpChangeNotificationSettings value,
        string version) => new(value.DigestEnabled, value.DigestHour, value.MasterDataRecipientRoles,
        value.StockRecipientRoles, version);
}

public sealed class ErpChangeWorkService(IntegrationDbContext db, ITenantContext tenant,
    ErpChangeNotificationSettings settings)
{
    public async Task<ErpChangeMyWorkView?> ForRolesAsync(IReadOnlyCollection<string> roles,
        CancellationToken cancellationToken)
    {
        var run = await db.ErpChangeRuns.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.Status == ErpChangeRunStatus.Active)
            .Select(row => new
            {
                row.ErpChangeRunId, row.CurrentStep, row.SourceConnectionId, row.TargetConnectionId,
            }).SingleOrDefaultAsync(cancellationToken);
        if (run is null) return null;
        var connectionIds = new[] { run.SourceConnectionId, run.TargetConnectionId };
        var names = await db.Connections.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && connectionIds.Contains(row.ConnectionId))
            .Select(row => new { row.ConnectionId, row.DisplayLabel })
            .ToDictionaryAsync(row => row.ConnectionId, row => row.DisplayLabel, cancellationToken);
        var configured = await settings.GetAsync(cancellationToken);
        var itemOwner = roles.Intersect(configured.MasterDataRecipientRoles, StringComparer.Ordinal).Any();
        var inventory = roles.Intersect(configured.StockRecipientRoles, StringComparer.Ordinal).Any();
        var master = run.CurrentStep == ErpChangeStep.MasterData && itemOwner
            ? await db.ErpChangeMasterDataExceptions.CountAsync(row => row.TenantId == tenant.TenantId
                && row.ErpChangeRunId == run.ErpChangeRunId && row.Status == ErpChangeExceptionStatus.Open,
                cancellationToken) : 0;
        var covered = 0;
        var other = 0;
        if (run.CurrentStep == ErpChangeStep.Stock && inventory)
        {
            var open = db.ErpChangeStockDifferences.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ErpChangeRunId == run.ErpChangeRunId && row.IsCurrent
                && row.Status == ErpChangeDifferenceStatus.Open);
            covered = await open.CountAsync(row => row.HasLiveEntitlement, cancellationToken);
            var signedOff = await db.ErpChangeStockSignoffs.AsNoTracking().AnyAsync(row =>
                row.TenantId == tenant.TenantId && row.ErpChangeRunId == run.ErpChangeRunId, cancellationToken);
            if (!signedOff) other = await open.CountAsync(row => !row.HasLiveEntitlement, cancellationToken);
        }
        var divergences = run.CurrentStep == ErpChangeStep.DualRun && inventory
            ? await db.ErpChangeDivergences.CountAsync(row => row.TenantId == tenant.TenantId
                && row.ErpChangeRunId == run.ErpChangeRunId && row.Kind == ErpChangeDivergenceKind.Stock
                && row.Status == ErpChangeDivergenceStatus.Open, cancellationToken) : 0;
        var path = run.CurrentStep == ErpChangeStep.MasterData
            ? "/integration/erp-change/master-data" : "/integration/erp-change/stock";
        return new(run.ErpChangeRunId, run.CurrentStep,
            names.GetValueOrDefault(run.SourceConnectionId) ?? "Current connection",
            names.GetValueOrDefault(run.TargetConnectionId) ?? "New connection",
            master, covered, other, divergences, path);
    }
}

public sealed class ErpChangeNotifications(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    ITenantStaffDirectory staff,
    IMailOutbox mail,
    MailOptions mailOptions,
    IConnectionAuditSink audit,
    ErpChangeNotificationSettings settings,
    ErpChangeWorkService work,
    ITenantTimeZoneReadPort timeZones)
{
    public async Task StepOpenedAsync(Guid runId, int step, CancellationToken cancellationToken)
    {
        if (step is not (ErpChangeStep.MasterData or ErpChangeStep.Stock)) return;
        await SendAsync(runId, step, ErpChangeNoticeKind.StepOpen, "once", cancellationToken);
    }

    public async Task FirstStockDivergenceAsync(Guid runId, CancellationToken cancellationToken) =>
        await SendAsync(runId, ErpChangeStep.DualRun, ErpChangeNoticeKind.StepOpen, "once",
            cancellationToken);

    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var run = await db.ErpChangeRuns.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.Status == ErpChangeRunStatus.Active)
            .Select(row => new { row.ErpChangeRunId, row.CurrentStep }).SingleOrDefaultAsync(cancellationToken);
        if (run is null) return;
        if (run.CurrentStep is ErpChangeStep.MasterData or ErpChangeStep.Stock)
            await StepOpenedAsync(run.ErpChangeRunId, run.CurrentStep, cancellationToken);
        else if (run.CurrentStep == ErpChangeStep.DualRun && await db.ErpChangeDivergences.AsNoTracking()
                     .AnyAsync(row => row.TenantId == tenant.TenantId
                         && row.ErpChangeRunId == run.ErpChangeRunId
                         && row.Kind == ErpChangeDivergenceKind.Stock, cancellationToken))
            await FirstStockDivergenceAsync(run.ErpChangeRunId, cancellationToken);

        var configured = await settings.GetAsync(cancellationToken);
        if (!configured.DigestEnabled) return;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(await timeZones.GetAsync(cancellationToken));
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(clock.UtcNow, DateTimeKind.Utc), zone);
        if (local.Hour != configured.DigestHour) return;
        await SendAsync(run.ErpChangeRunId, run.CurrentStep, ErpChangeNoticeKind.MorningDigest,
            DateOnly.FromDateTime(local).ToString("yyyy-MM-dd"), cancellationToken);
    }

    private async Task SendAsync(Guid runId, int step, string kind, string occurrenceKey,
        CancellationToken cancellationToken)
    {
        var run = await db.ErpChangeRuns.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.ErpChangeRunId == runId
            && row.Status == ErpChangeRunStatus.Active && row.CurrentStep == step, cancellationToken);
        if (run is null) return;
        var configured = await settings.GetAsync(cancellationToken);
        var roles = step == ErpChangeStep.MasterData
            ? configured.MasterDataRecipientRoles : configured.StockRecipientRoles;
        var existingRoles = await db.ErpChangeNotices.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ErpChangeRunId == runId && row.Kind == kind
                && row.Step == step && row.OccurrenceKey == occurrenceKey)
            .Select(row => row.RecipientRole).ToListAsync(cancellationToken);
        var pendingRoles = roles.Except(existingRoles, StringComparer.Ordinal).ToList();
        if (pendingRoles.Count == 0) return;
        var roleRecipients = new Dictionary<string, IReadOnlyList<TenantStaffNotificationRecipient>>(
            StringComparer.Ordinal);
        foreach (var role in pendingRoles)
            roleRecipients[role] = await staff.NotificationRecipientsAsync(null, role, cancellationToken);
        var recipients = roleRecipients.Values.SelectMany(value => value)
            .DistinctBy(recipient => recipient.UserId).ToList();
        var workView = await work.ForRolesAsync(roles, cancellationToken);
        if (workView is null || (kind == ErpChangeNoticeKind.MorningDigest && workView.Total == 0)) return;
        var content = Content(workView, kind);
        var absoluteLink = mailOptions.Link(workView.ScreenPath);
        foreach (var recipient in recipients)
        {
            var text = $"{content.Text}\n\nOpen this work: {absoluteLink}";
            var html = $"<p>{WebUtility.HtmlEncode(content.Text)}</p><p><a href=\"{WebUtility.HtmlEncode(absoluteLink)}\">Open this work</a></p>";
            await mail.QueueAsync(new(content.MailKind, tenant.TenantId, recipient.UserId,
                new(recipient.Email, recipient.Name), content.Subject, html, text), cancellationToken);
        }
        var countsJson = JsonSerializer.Serialize(new
        {
            master_data_exceptions = workView.MasterDataExceptions,
            covered_stock_differences = workView.CoveredStockDifferences,
            other_stock_differences_awaiting_sign_off = workView.OtherStockDifferencesAwaitingSignOff,
            stock_type_divergences = workView.StockTypeDivergences,
        }, IntegrationContracts.Json);
        foreach (var role in pendingRoles)
        {
            db.ErpChangeNotices.Add(new()
            {
                TenantId = tenant.TenantId,
                ErpChangeNoticeId = TimeOrderedId.New(clock.UtcNow),
                ErpChangeRunId = runId,
                Kind = kind,
                Step = step,
                RecipientRole = role,
                OccurrenceKey = occurrenceKey,
                RecipientCount = roleRecipients[role].Select(recipient => recipient.UserId).Distinct().Count(),
                CountsJson = countsJson,
                LinkPath = workView.ScreenPath,
                SentAt = clock.UtcNow,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var role in pendingRoles)
            await audit.AppendAsync(new(tenant.TenantId, TimeOrderedId.New(clock.UtcNow),
                run.TargetConnectionId, ConnectionAuditEventType.ErpChangeMailBatchQueued,
                "system:erp-change-notifications", clock.UtcNow, JsonSerializer.Serialize(new
                {
                    kind, run = runId, step, recipient_role = role,
                    recipient_count = roleRecipients[role].Select(recipient => recipient.UserId).Distinct().Count(),
                }, IntegrationContracts.Json)), cancellationToken);
    }

    private static MailContent Content(ErpChangeMyWorkView work, string kind)
    {
        var prefix = $"Step {work.CurrentStep} of the change from \"{work.SourceName}\" to \"{work.TargetName}\" is open.";
        if (work.CurrentStep == ErpChangeStep.MasterData)
            return new("integration.erp_change_master_data", "ERP change: master-data exceptions need you",
                $"{prefix} {work.MasterDataExceptions:N0} master-data exceptions need your decision.");
        if (work.CurrentStep == ErpChangeStep.Stock)
            return new("integration.erp_change_stock", "ERP change: stock differences need you",
                $"{prefix} {work.CoveredStockDifferences:N0} differences on covered units block it; "
                + $"{work.OtherStockDifferencesAwaitingSignOff:N0} others need your sign-off.");
        return new("integration.erp_change_stock_divergence", "ERP change: stock divergences need you",
            $"{prefix} {work.StockTypeDivergences:N0} stock-type divergences need your review.");
    }

    private sealed record MailContent(string MailKind, string Subject, string Text);
}

internal static class ErpChangeNoticeKind
{
    public const string StepOpen = "step_open";
    public const string MorningDigest = "morning_digest";
}

[DisallowConcurrentExecution]
public sealed class ErpChangeNotificationJob(ITenantContextSetter setter, ErpChangeNotifications notifications)
    : TenantJob(setter)
{
    public const string JobName = "integration.erp-change-notifications";
    public const string Cron = "10 0 * * * ?";

    protected override Task ExecuteForTenant(IJobExecutionContext context,
        CancellationToken cancellationToken) => notifications.EvaluateAsync(cancellationToken);
}
