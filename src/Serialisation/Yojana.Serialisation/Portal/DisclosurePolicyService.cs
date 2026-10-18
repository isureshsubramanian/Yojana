using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Portal;

/// <summary>Tenant disclosure configuration. Missing data means the ADR defaults, including public lookup enabled.</summary>
public sealed class DisclosurePolicyService(SerialisationDbContext db, ITenantContext tenant,
    ITenantSettings settings, IClock clock)
{
    public const string PublicEnabledSetting = "portal.public.enabled";

    public async Task<DisclosurePolicyView> GetAsync(CancellationToken cancellationToken)
    {
        var storedSwitch = await settings.GetVersionedAsync(PublicEnabledSetting, cancellationToken);
        var rows = await db.DisclosurePolicies.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId)
            .Select(row => new DisclosurePolicyRow(row.Field, row.Tier, row.Enabled))
            .ToListAsync(cancellationToken);
        var configured = rows.ToDictionary(row => row.Field, StringComparer.Ordinal);
        var complete = PortalDisclosureField.Defaults.Select(item => configured.TryGetValue(item.Key, out var row)
                ? row : new DisclosurePolicyRow(item.Key, item.Value, true))
            .OrderBy(row => row.Field, StringComparer.Ordinal).ToList();
        return new(storedSwitch is null || bool.Parse(storedSwitch.Value), complete,
            storedSwitch?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<DisclosurePolicyView> SetFieldsAsync(UpdateDisclosureFields update, string actorRef,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorRef))
            throw new CommandRefusedException("audit_actor_required", "Disclosure-policy changes need an audit actor.");
        if (update.Fields.Select(row => row.Field).Distinct(StringComparer.Ordinal).Count() != update.Fields.Count)
            throw new CommandRefusedException("disclosure_field_duplicate",
                "Each disclosure field must be supplied exactly once.");
        var supplied = update.Fields.ToDictionary(row => row.Field, StringComparer.Ordinal);
        if (supplied.Count != PortalDisclosureField.Defaults.Count
            || PortalDisclosureField.Defaults.Keys.Any(field => !supplied.ContainsKey(field)))
            throw new CommandRefusedException("disclosure_policy_incomplete",
                "The disclosure policy must classify every field in the fixed catalogue.");
        foreach (var row in supplied.Values)
        {
            if (!DisclosureTier.All.Contains(row.Tier, StringComparer.Ordinal))
                throw new CommandRefusedException("disclosure_tier_unknown", $"Unknown disclosure tier '{row.Tier}'.");
            if (row.Enabled && row.Tier == DisclosureTier.Public
                && !PortalDisclosureField.PublicSafe.Contains(row.Field))
                throw new CommandRefusedException("public_field_forbidden",
                    $"Field '{row.Field}' can never be assigned to the public tier.");
        }

        var tenantId = tenant.TenantId;
        var existing = await db.DisclosurePolicies.Where(row => row.TenantId == tenantId)
            .ToDictionaryAsync(row => row.Field, StringComparer.Ordinal, cancellationToken);
        foreach (var policy in supplied.Values)
        {
            if (!existing.TryGetValue(policy.Field, out var entity))
            {
                entity = new DisclosurePolicy { TenantId = tenantId, Field = policy.Field, Tier = policy.Tier,
                    Enabled = policy.Enabled, UpdatedAt = clock.UtcNow, UpdatedBy = actorRef };
                db.DisclosurePolicies.Add(entity);
            }
            else
            {
                entity.Tier = policy.Tier;
                entity.Enabled = policy.Enabled;
                entity.UpdatedAt = clock.UtcNow;
                entity.UpdatedBy = actorRef;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(cancellationToken);
    }

    public async Task<DisclosurePolicyView> SetPublicEnabledAsync(UpdatePublicPortalSetting update,
        CancellationToken cancellationToken)
    {
        await settings.SetAsync(PublicEnabledSetting, update.Enabled.ToString(), update.ExpectedVersion,
            cancellationToken);
        return await GetAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<string>> PublicFieldsAsync(CancellationToken cancellationToken)
    {
        var view = await GetAsync(cancellationToken);
        if (!view.PublicTierEnabled) return new HashSet<string>();
        return view.Fields.Where(row => row.Enabled && row.Tier == DisclosureTier.Public
                && PortalDisclosureField.PublicSafe.Contains(row.Field))
            .Select(row => row.Field).ToHashSet(StringComparer.Ordinal);
    }
}
