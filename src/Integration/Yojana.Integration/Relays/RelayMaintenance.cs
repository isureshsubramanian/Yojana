using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;
using Yojana.Integration.Data;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Relays;

public sealed class RelayMaintenance(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    ITenantSettings settings, RelayRegistry registry, ILogger<RelayMaintenance> logger)
{
    public async Task ProbeAsync(CancellationToken ct)
    {
        var pending = await db.SiteRelays.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.Status == "active"
            && x.ErpVersion != null && x.ErpVersion != x.ProbedErpVersion).OrderBy(x => x.RelayId).Take(100).ToListAsync(ct);
        foreach (var relay in pending)
        {
            try
            {
                var (_, profile) = await registry.ProfileAsync(relay.ConnectionId, ct);
                await profile.ProbeAsync(relay.ConnectionId, relay.ErpVersion!, ct);
                await db.SiteRelays.Where(x => x.TenantId == tenant.TenantId && x.RelayId == relay.RelayId
                    && x.ErpVersion == relay.ErpVersion).ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.ProbedErpVersion, relay.ErpVersion), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { logger.LogWarning("Relay release probe failed; it remains pending for the next tenant job."); }
        }
    }

    public async Task PurgeAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var rawRetention = await settings.GetAsync("integration.relay.retention_hours", ct);
        var hours = rawRetention is null ? 24 : int.TryParse(rawRetention, out var configured) && configured is >= 1 and <= 720
            ? configured : throw new InvalidOperationException("Relay retention must be between 1 and 720 hours.");
        var before = now.AddHours(-hours);
        await db.RelayRequests.Where(x => x.TenantId == tenant.TenantId && x.ExpiresAt <= now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.PayloadJson, "").SetProperty(x => x.ResultJson, (string?)null), ct);
        await db.RelayRequests.Where(x => x.TenantId == tenant.TenantId && x.ExpiresAt < before).ExecuteDeleteAsync(ct);
        await db.RelayNonces.Where(x => x.TenantId == tenant.TenantId && x.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        await db.SiteRelays.Where(x => x.TenantId == tenant.TenantId && x.PreviousSecretExpiresAt <= now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.PreviousSigningKeyEnvelope, (string?)null)
                .SetProperty(x => x.PreviousSecretExpiresAt, (DateTime?)null), ct);
    }
}

[DisallowConcurrentExecution]
public sealed class RelayPurgeJob(ITenantContextSetter setter, RelayMaintenance maintenance) : TenantJob(setter)
{
    public const string JobName = "integration.site-relay-purge";
    public const string Cron = "15 0/5 * * * ?";
    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) => maintenance.PurgeAsync(cancellationToken);
}

[DisallowConcurrentExecution]
public sealed class RelayProbeJob(ITenantContextSetter setter, RelayMaintenance maintenance) : TenantJob(setter)
{
    public const string JobName = "integration.site-relay-probe";
    public const string Cron = "20 * * * * ?";
    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) => maintenance.ProbeAsync(cancellationToken);
}
