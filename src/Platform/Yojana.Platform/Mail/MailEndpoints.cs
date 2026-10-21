using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Security;

namespace Yojana.Platform.Mail;

public static class MailEndpoints
{
    public static IEndpointRouteBuilder MapYojanaMailEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var mail = endpoints.MapGroup("/api/platform/mail")
            .RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        mail.MapPost("/test", SendTestAsync);
        mail.MapGet("/messages/{id:guid}", MessageAsync);

        var company = endpoints.MapGroup("/api/platform/company")
            .RequireAuthorization(YojanaPolicy.ForRole(YojanaRole.TenantAdministrator));
        company.MapGet("/mail", async (CompanyMailIdentity identity, CancellationToken cancellationToken) =>
            Results.Ok(await identity.GetCurrentAsync(cancellationToken)));
        company.MapPut("/mail", PutCompanyMailAsync);
        return endpoints;
    }

    /// <summary>Sends a test message to the signed-in administrator's own address, so the transport can be checked without a real reset.</summary>
    private static async Task<IResult> SendTestAsync(ClaimsPrincipal user, PlatformDbContext db, IMailOutbox outbox,
        ITenantContext tenant, ITenantSettings settings, CompanyMailIdentity identity, MailOptions options, IClock clock,
        CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(user.FindFirstValue(YojanaClaim.UserId)!);
        var account = await db.Users.AsNoTracking().Where(row => row.Id == userId)
            .Select(row => new { row.Email, row.DisplayName }).FirstAsync(cancellationToken);
        var tenantName = await db.Tenants.AsNoTracking().Where(row => row.Id == tenant.TenantId)
            .Select(row => row.Name).FirstAsync(cancellationToken);
        var (senderName, replyTo) = await TenantMailSettings.ReadAsync(settings, cancellationToken);
        var facts = new List<(string, string)>
        {
            ("Sent through", TransportDescription(options)),
            ("Sender", $"{senderName ?? options.FromName} <{options.FromAddress}>"),
        };
        if (replyTo is not null) facts.Add(("Replies go to", replyTo));
        facts.Add(("Requested by", $"{account.DisplayName}, {clock.UtcNow:dd MMM yyyy, HH:mm} UTC"));
        var (html, text) = AccountMailLayout.Render(new AccountMailContent(tenantName, "System check",
            "Mail from Yojana reaches you",
            [$"This test message was sent for {tenantName} to check that Yojana can deliver e-mail. It arrived, so password resets, invitations and sign-in notices will too."],
            facts, null,
            ["No action is needed. If this message landed in your junk folder, mark it as safe: account e-mails come from the same address."],
            $"Sent by Yojana for {tenantName} because a tenant administrator asked for a test.",
            await identity.CompanyLineAsync(tenant.TenantId, tenantName, cancellationToken)));
        var id = await outbox.QueueAsync(new OutgoingMail(MailKind.TransportTest, tenant.TenantId, userId,
            new MailRecipient(account.Email, account.DisplayName), "Yojana test message", html, text), cancellationToken);
        return Results.Accepted($"/api/platform/mail/messages/{id}", new { mailId = id });
    }

    private static async Task<IResult> PutCompanyMailAsync(CompanyMailSettingsRequest request,
        CompanyMailIdentity identity, SecurityEventWriter events, PlatformDbContext db, ITenantContext tenant,
        HttpContext http, CancellationToken cancellationToken)
    {
        try
        {
            var (view, changed) = await identity.UpdateCurrentAsync(request.FooterLine, request.SenderName,
                request.ReplyTo, request.Version ?? string.Empty, cancellationToken);
            if (changed.Count > 0)
            {
                var actor = Guid.Parse(http.User.FindFirstValue(YojanaClaim.UserId)!);
                events.Add(SecurityEventKind.SettingsChanged, null, tenant.TenantId, actor,
                    new SecurityRequestContext(http.Connection.RemoteIpAddress?.ToString(),
                        http.Request.Headers.UserAgent.ToString()), string.Join(',', changed));
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.Ok(view);
        }
        catch (CompanyMailSettingsException exception)
        {
            return Results.Problem(title: exception.Code, detail: exception.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (TenantSettingVersionConflictException)
        {
            return Results.Problem(title: "company_settings_conflict",
                detail: "The company settings changed after they were loaded. Reload and try again.",
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static string TransportDescription(MailOptions options) => options.Transport switch
    {
        MailTransportKind.Smtp => $"SMTP · {options.Smtp.Host}:{options.Smtp.Port} ({options.Smtp.Security switch { SmtpSecurity.Tls => "TLS", SmtpSecurity.None => "unencrypted, local relay", _ => "STARTTLS" }})",
        MailTransportKind.Graph => $"Microsoft 365 · {options.Graph.SenderMailbox}",
        MailTransportKind.Pickup => "Pickup folder (development)",
        _ => "None configured",
    };

    private static async Task<IResult> MessageAsync(Guid id, PlatformDbContext db, ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        // mail_message has no row-level security (account mail may predate a tenant), so the tenant filter is here.
        var tenantId = (TenantId?)tenant.TenantId;
        var row = await db.MailMessages.AsNoTracking().Where(m => m.Id == id && m.TenantId == tenantId)
            .Select(m => new MailMessageView(m.Id, m.Kind, m.Recipient, m.Subject, m.Status, m.Transport, m.Attempts,
                m.QueuedAt, m.LastAttemptAt, m.NextAttemptAt, m.SentAt, m.LastError))
            .FirstOrDefaultAsync(cancellationToken);
        // Queued but not yet attempted looks the same as unknown; the caller polls.
        return row is null ? Results.NotFound() : Results.Ok(row);
    }
}

public sealed record CompanyMailSettingsRequest(string? FooterLine, string? SenderName, string? ReplyTo,
    string? Version);

public sealed record MailMessageView(Guid Id, string Kind, string Recipient, string Subject, string Status,
    string Transport, int Attempts, DateTime QueuedAt, DateTime? LastAttemptAt, DateTime? NextAttemptAt,
    DateTime? SentAt, string? LastError);

public sealed class MailLogMaintenance(PlatformDbContext db, IClock clock, MailOptions options)
{
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddDays(-options.LogRetentionDays);
        var total = 0;
        int deleted;
        do
        {
            // Rows still retrying are kept whatever their age; they settle within MaxAttempts.
            deleted = await db.MailMessages.Where(row => row.QueuedAt < cutoff && row.Status != Data.Platform.Entities.MailStatus.Retrying)
                .OrderBy(row => row.QueuedAt).ThenBy(row => row.Id).Take(options.MaintenanceBatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            total += deleted;
        } while (deleted == options.MaintenanceBatchSize);
        return total;
    }
}

[DisallowConcurrentExecution]
public sealed class MailLogMaintenanceJob(MailLogMaintenance maintenance) : IJob
{
    public const string JobName = "platform.mail_log_maintenance";
    public const string Cron = "0 41 2 * * ?";
    public static readonly JobKey Key = new(JobName, "database");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await maintenance.PurgeAsync(cancellationToken);
}

/// <summary>Schedules the database-wide delivery-log cleanup and warns when the deployment sends no mail.</summary>
public sealed class MailLogMaintenanceRegistrar(ISchedulerFactory schedulerFactory, MailOptions options,
    ILogger<MailLogMaintenanceRegistrar> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Transport == MailTransportKind.None)
            logger.LogWarning("{Section}:Transport is none: password resets and invitations will be recorded as failed, not sent",
                MailOptions.SectionName);
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        var detail = JobBuilder.Create<MailLogMaintenanceJob>().WithIdentity(MailLogMaintenanceJob.Key)
            .StoreDurably(true).Build();
        var trigger = TriggerBuilder.Create(TimeProvider.System)
            .WithIdentity(MailLogMaintenanceJob.Key.Name, MailLogMaintenanceJob.Key.Group)
            .ForJob(MailLogMaintenanceJob.Key)
            .WithSchedule(CronScheduleBuilder.Create(MailLogMaintenanceJob.Cron)
                .WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing))
            .Build();
        await scheduler.ScheduleJob(detail, trigger, ScheduleJobOptions.Replacing, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
