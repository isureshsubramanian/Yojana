using Microsoft.EntityFrameworkCore;
using Quartz;

using Yojana.Platform.Contracts.Shell;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Scheduling;

namespace Yojana.Platform.Shell;

/// <summary>
/// What the platform itself can say about a tenant before any domain module exists: people, scheduled jobs
/// and the messaging smoke evidence. Real figures only; nothing here is a placeholder value.
/// </summary>
public sealed class PlatformOverviewContributor(PlatformDbContext db, ITenantContext tenant, ISchedulerFactory schedulerFactory) : IOverviewContributor
{
    public int Order => 1000;

    public async Task<OverviewContribution> ContributeAsync(OverviewContext context, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var dayStart = context.UtcNow.Date;
        var users = await db.Memberships.AsNoTracking().CountAsync(m => m.TenantId == tenantId && m.DeactivatedAt == null, cancellationToken);
        var handledToday = await db.SmokeLogs.AsNoTracking().CountAsync(s => s.Source == "handler" && s.RecordedAt >= dayStart, cancellationToken);
        var jobsToday = await db.SmokeLogs.AsNoTracking().CountAsync(s => s.Source == "job" && s.RecordedAt >= dayStart, cancellationToken);
        var lastJob = await db.SmokeLogs.AsNoTracking().Where(s => s.Source == "job").MaxAsync(s => (DateTime?)s.RecordedAt, cancellationToken);

        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals(TenantJob.KeyFor("x", tenantId).Group), cancellationToken);
        var rows = new List<IReadOnlyList<OverviewCell>>();
        var railRows = new List<RailRow>();
        foreach (var key in keys.OrderBy(k => k.Name, StringComparer.Ordinal))
        {
            var triggers = await scheduler.GetTriggersOfJob(key, cancellationToken);
            var trigger = triggers.FirstOrDefault();
            var state = trigger is null ? TriggerState.None : await scheduler.GetTriggerState(trigger.Key, cancellationToken);
            var next = trigger?.NextFireTimeUtc;
            var previous = trigger?.PreviousFireTimeUtc;
            var (badge, text) = state switch
            {
                TriggerState.Normal => ("ok", "Scheduled"),
                TriggerState.Blocked => ("info", "Running"),
                TriggerState.Paused => ("neutral", "Paused"),
                TriggerState.Error => ("bad", "Error"),
                _ => ("neutral", "No trigger"),
            };
            rows.Add(
            [
                new OverviewCell(key.Name, Sub: trigger is ICronTrigger cron ? cron.CronExpressionString : null, Mono: true),
                new OverviewCell(previous is null ? "—" : Relative(previous.Value, context.UtcNow)),
                new OverviewCell(next is null ? "—" : Relative(next.Value, context.UtcNow)),
                new OverviewCell(text, Badge: badge),
            ]);
            railRows.Add(new RailRow(key.Name, Badge: badge, BadgeText: text, Detail: next is null ? "not scheduled" : "next " + Relative(next.Value, context.UtcNow)));
        }

        return new OverviewContribution(
            Kpis:
            [
                new OverviewKpi("People in tenant", users.ToString("N0"), context.Roles.Count == 0 ? "You hold no role here" : $"You hold {context.Roles.Count} role{(context.Roles.Count == 1 ? "" : "s")}"),
                new OverviewKpi("Scheduled jobs", keys.Count.ToString("N0"), lastJob is null ? "No run recorded yet" : "Last run " + Relative(lastJob.Value, context.UtcNow)),
                new OverviewKpi("Jobs run today", jobsToday.ToString("N0"), "Since 00:00 UTC"),
                new OverviewKpi("Messages handled today", handledToday.ToString("N0"), "Delivered after commit"),
            ],
            Panels:
            [
                new OverviewPanel(
                    "jobs",
                    "Scheduled jobs",
                    keys.Count == 0 ? null : $"{keys.Count} for this tenant",
                    [new OverviewColumn("Job", Width: 40), new OverviewColumn("Last run", Width: 22), new OverviewColumn("Next run", Width: 22), new OverviewColumn("Status", Width: 16)],
                    rows,
                    Empty: "No recurring jobs are scheduled for this tenant."),
            ],
            Rail:
            [
                new RailSection("Today", [new RailRow("Jobs run", jobsToday.ToString("N0")), new RailRow("Messages handled", handledToday.ToString("N0")), new RailRow("People in tenant", users.ToString("N0"))]),
                new RailSection("Scheduled jobs", railRows),
            ]);
    }

    private static string Relative(DateTimeOffset at, DateTime utcNow) => Relative(at.UtcDateTime, utcNow);

    private static string Relative(DateTime at, DateTime utcNow)
    {
        var delta = at - utcNow;
        var future = delta > TimeSpan.Zero;
        var span = future ? delta : -delta;
        var text = span.TotalSeconds < 60 ? "under a minute"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min"
            : span.TotalHours < 48 ? $"{(int)span.TotalHours} h"
            : $"{(int)span.TotalDays} days";
        return future ? "in " + text : text + " ago";
    }
}

/// <summary>The platform has no connections of its own; a connector module replaces this answer by registering its own source.</summary>
public sealed class NoConnectionsHealthSource : IConnectionHealthSource
{
    public Task<ConnectionHealth> GetHealthAsync(CancellationToken cancellationToken) => Task.FromResult(ConnectionHealth.None);
}

/// <summary>Assembles the shell's overview and header health from every registered source, in order.</summary>
public sealed class ShellOverviewService(IEnumerable<IOverviewContributor> contributors, IEnumerable<IConnectionHealthSource> healthSources)
{
    public async Task<ShellOverview> BuildAsync(OverviewContext context, CancellationToken cancellationToken)
    {
        var kpis = new List<OverviewKpi>();
        var panels = new List<OverviewPanel>();
        var rail = new List<RailSection>();
        foreach (var contributor in contributors.OrderBy(c => c.Order))
        {
            var contribution = await contributor.ContributeAsync(context, cancellationToken);
            kpis.AddRange(contribution.Kpis);
            panels.AddRange(contribution.Panels);
            rail.AddRange(contribution.Rail);
        }

        return new ShellOverview(context.UtcNow, kpis, panels, rail);
    }

    /// <summary>The worst status wins; "none" only when no source reports anything else.</summary>
    public async Task<ConnectionHealth> HealthAsync(CancellationToken cancellationToken)
    {
        ConnectionHealth worst = ConnectionHealth.None;
        foreach (var source in healthSources)
        {
            var health = await source.GetHealthAsync(cancellationToken);
            if (health.Severity > worst.Severity || (worst.Status == "none" && health.Status != "none"))
            {
                worst = health;
            }
        }

        return worst;
    }
}

public sealed record ShellOverview(DateTime GeneratedAt, IReadOnlyList<OverviewKpi> Kpis, IReadOnlyList<OverviewPanel> Panels, IReadOnlyList<RailSection> Rail);
