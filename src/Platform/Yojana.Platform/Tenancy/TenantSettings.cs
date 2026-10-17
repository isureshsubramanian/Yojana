using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;

namespace Yojana.Platform.Tenancy;

/// <summary>Reads and writes <c>tenant_setting</c> rows of the current tenant (temporal: every change is history).</summary>
public sealed class TenantSettings(PlatformDbContext db, ITenantContext tenant, IClock clock) : ITenantSettings
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return await db.TenantSettings.AsNoTracking().Where(s => s.TenantId == tenantId && s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var row = await db.TenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Key == key, cancellationToken);
        if (row is null)
        {
            db.TenantSettings.Add(new TenantSetting { TenantId = tenantId, Key = key, Value = value, UpdatedAt = clock.UtcNow });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = clock.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TenantSettingVersion>> HistoryAsync(string key, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return await db.TenantSettings.TemporalAll().AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.Key == key)
            .OrderByDescending(s => EF.Property<DateTime>(s, "PeriodStart"))
            .Select(s => new TenantSettingVersion(
                s.Value,
                EF.Property<DateTime>(s, "PeriodStart"),
                EF.Property<DateTime>(s, "PeriodEnd")))
            .ToListAsync(cancellationToken);
    }
}
