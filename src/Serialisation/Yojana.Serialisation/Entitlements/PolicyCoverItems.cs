using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Documents;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

public sealed class PolicyCoverItems(SerialisationDbContext db, ITenantContext tenant, ClaimCauseCatalogue catalogue)
{
    public async Task<IReadOnlyList<PolicyCoverItem>> ReadAsync(Guid version, CancellationToken ct)
    {
        var rows = await db.PolicyCoverItems.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.PolicyVersionId == version).OrderBy(x => x.Code).ToListAsync(ct);
        var causes = await db.PolicyCoverItemCauses.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.PolicyVersionId == version).ToListAsync(ct);
        var origins = await (from current in db.PolicyCoverItems.AsNoTracking()
            join currentVersion in db.PolicyVersions.AsNoTracking() on new { current.TenantId, current.PolicyVersionId } equals new { currentVersion.TenantId, currentVersion.PolicyVersionId }
            join previous in db.PolicyCoverItems.AsNoTracking() on new { current.TenantId, current.Code } equals new { previous.TenantId, previous.Code }
            join previousVersion in db.PolicyVersions.AsNoTracking() on new { previous.TenantId, previous.PolicyVersionId } equals new { previousVersion.TenantId, previousVersion.PolicyVersionId }
            where current.TenantId == tenant.TenantId && current.PolicyVersionId == version
                && currentVersion.PolicyCode == previousVersion.PolicyCode && previousVersion.Version <= currentVersion.Version
                // Legacy codes describe list positions, so only the same original text establishes their first version.
                && (!current.Code.StartsWith("LEGACY-") || current.CustomerWording == previous.CustomerWording)
            group previousVersion by current.Code into versions
            select new { Code = versions.Key, Since = versions.Min(x => x.Version) }).ToDictionaryAsync(x => x.Code, x => x.Since, ct);
        return rows.Select(x => Read(x, causes.Where(c => c.ItemCode == x.Code).Select(c => c.CauseCode).Order().ToList())
            with { SinceVersion = origins.GetValueOrDefault(x.Code) }).ToList();
    }

    // The persisted scope format is also written by P9T19CoverItems's SQL backfill and the entity default.
    // Both use LedgerJson's snake-case keys; changing the serialiser alone would discard saved references.
    internal static PolicyCoverItem Read(PolicyCoverItemRow row, IReadOnlyList<string> causes) => new(row.Code, row.Type,
        row.CustomerWording, row.InternalNote, row.Category,
        JsonSerializer.Deserialize<CoverPartsScope>(row.PartsScope, LedgerJson.Options)!, causes);

    public async Task WriteAsync(WarrantyPolicyVersion version, IReadOnlyList<PolicyCoverItem> items, CancellationToken ct)
    {
        if (version.Status != PolicyVersionStatus.Draft)
            throw new CommandRefusedException("policy_items_published", "Published items are immutable. Open a new draft.");
        if (items.Select(x => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Count)
            throw new CommandRefusedException("policy_items_invalid", "Item codes must be unique within the policy version.");
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Code) || item.Code.Length > 24 || item.Code.Any(c => !(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '-' or '_'))
                || item.Type is not ("covered" or "excluded") || string.IsNullOrWhiteSpace(item.CustomerWording) || item.CustomerWording.Length > 500
                || item.Category is not (null or "cause" or "part" or "condition" or "service")
                || item.CauseCodes is null || item.PartsScope is null || item.PartsScope.PartCategories is null || item.PartsScope.ItemFamilies is null
                || item.CauseCodes.Distinct().Count() != item.CauseCodes.Count
                || item.PartsScope.PartCategories.Concat(item.PartsScope.ItemFamilies).Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100))
                throw new CommandRefusedException("policy_item_invalid", "Check the item code, wording, type, category and scope.");
        }
        // Coupled to publication in PolicyHandlers: the parent rowversion is the shared concurrency seam for its
        // child items and publication. Force an update even with a fixed clock; separating these writes lets a
        // concurrent publish commit editable items. Both commands run in the same database transaction.
        if (db.Entry(version).State is not (EntityState.Detached or EntityState.Added))
            db.Entry(version).Property(x => x.LastModifiedAt).IsModified = true;
        await catalogue.EnsureDefaultsAsync(ct);
        var known = await db.ClaimCauses.Where(x => x.TenantId == tenant.TenantId).Select(x => x.Code).ToListAsync(ct);
        known.AddRange(db.ClaimCauses.Local.Where(x => x.TenantId == tenant.TenantId).Select(x => x.Code));
        if (items.SelectMany(x => x.CauseCodes).Except(known).Any())
            throw new CommandRefusedException("claim_cause_unknown", "An item names an unknown claim cause.");
        var existing = await db.PolicyCoverItems.Where(x => x.TenantId == tenant.TenantId && x.PolicyVersionId == version.PolicyVersionId).ToListAsync(ct);
        var links = await db.PolicyCoverItemCauses.Where(x => x.TenantId == tenant.TenantId && x.PolicyVersionId == version.PolicyVersionId).ToListAsync(ct);
        foreach (var row in existing.Where(x => items.All(i => i.Code != x.Code))) db.PolicyCoverItems.Remove(row);
        foreach (var link in links.Where(x => !items.Any(i => i.Code == x.ItemCode && i.CauseCodes.Contains(x.CauseCode)))) db.PolicyCoverItemCauses.Remove(link);
        foreach (var item in items)
        {
            var row = existing.SingleOrDefault(x => x.Code == item.Code);
            if (row is null)
            {
                row = new() { TenantId = tenant.TenantId, PolicyVersionId = version.PolicyVersionId, Code = item.Code, Type = item.Type, CustomerWording = item.CustomerWording };
                db.PolicyCoverItems.Add(row);
            }
            row.Type = item.Type; row.CustomerWording = item.CustomerWording; row.InternalNote = item.InternalNote;
            row.Category = item.Category; row.PartsScope = JsonSerializer.Serialize(item.PartsScope, LedgerJson.Options);
            foreach (var cause in item.CauseCodes.Where(c => !links.Any(l => l.ItemCode == item.Code && l.CauseCode == c)))
                db.PolicyCoverItemCauses.Add(new() { TenantId = tenant.TenantId, PolicyVersionId = version.PolicyVersionId, ItemCode = item.Code, CauseCode = cause });
        }
    }
}

public static class UpdatePolicyCoverItemsHandler
{
    public static async Task<PolicyCoverItemPage> Handle(UpdatePolicyCoverItems command, SerialisationDbContext db,
        PolicyCoverItems items, ITenantContext tenant, IClock clock, CancellationToken ct)
    {
        var row = await db.PolicyVersions.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.PolicyVersionId == command.PolicyVersionId, ct)
            ?? throw new CommandRefusedException("policy_not_found", "Policy version not found.");
        DocumentVersion.RequireExpected(command.ExpectedVersion, row.RowVersion, row.LastModifiedBy, row.LastModifiedAt);
        await items.WriteAsync(row, command.Items, ct);
        row.LastModifiedAt = clock.UtcNow; row.LastModifiedBy = command.ActorRef;
        await db.SaveChangesAsync(ct);
        return new(command.Items.Take(100).ToList(), command.Items.Count, 0, 100, DocumentVersion.Encode(row.RowVersion));
    }
}
