using Microsoft.EntityFrameworkCore;
using System.Globalization;
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

    public async Task<VersionedTenantSetting?> GetVersionedAsync(string key, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var row = await db.TenantSettings.AsNoTracking().Where(s => s.TenantId == tenantId && s.Key == key)
            .Select(s => new { s.Value, s.UpdatedAt }).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new VersionedTenantSetting(row.Value, Version(row.UpdatedAt), row.UpdatedAt);
    }

    public async Task<VersionedTenantSetting> SetAsync(string key, string value, string expectedVersion,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var row = await db.TenantSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Key == key,
            cancellationToken);
        var currentVersion = row is null ? ITenantSettings.MissingVersion : Version(row.UpdatedAt);
        if (!string.Equals(currentVersion, expectedVersion, StringComparison.Ordinal))
            throw new TenantSettingVersionConflictException(key, expectedVersion, currentVersion);

        var updatedAt = clock.UtcNow;
        if (row is null)
        {
            row = new TenantSetting { TenantId = tenantId, Key = key, Value = value, UpdatedAt = updatedAt };
            db.TenantSettings.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException) when (expectedVersion == ITenantSettings.MissingVersion)
            {
                var current = await db.TenantSettings.AsNoTracking().Where(s => s.TenantId == tenantId && s.Key == key)
                    .Select(s => (DateTime?)s.UpdatedAt).FirstOrDefaultAsync(cancellationToken);
                if (current is null) throw;
                throw new TenantSettingVersionConflictException(key, expectedVersion, Version(current.Value));
            }
        }
        else
        {
            if (updatedAt <= row.UpdatedAt) updatedAt = row.UpdatedAt.AddTicks(1);
            var changed = await db.TenantSettings.Where(s => s.TenantId == tenantId && s.Key == key
                    && s.UpdatedAt == row.UpdatedAt)
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.Value, value)
                    .SetProperty(s => s.UpdatedAt, updatedAt), cancellationToken);
            if (changed == 0)
            {
                var current = await db.TenantSettings.AsNoTracking().Where(s => s.TenantId == tenantId && s.Key == key)
                    .Select(s => (DateTime?)s.UpdatedAt).FirstOrDefaultAsync(cancellationToken);
                throw new TenantSettingVersionConflictException(key, expectedVersion,
                    current is null ? ITenantSettings.MissingVersion : Version(current.Value));
            }
        }

        return new VersionedTenantSetting(value, Version(updatedAt), updatedAt);
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

    private static string Version(DateTime updatedAt) => updatedAt.Ticks.ToString(CultureInfo.InvariantCulture);
}
