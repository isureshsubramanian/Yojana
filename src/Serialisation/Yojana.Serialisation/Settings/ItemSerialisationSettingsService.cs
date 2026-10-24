using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Settings;

/// <summary>Edits the Serialisation-owned portion of an item profile without reaching into item-master persistence.</summary>
public sealed class ItemSerialisationSettingsService(
    SerialisationDbContext db,
    IItemQueryPort items,
    ITenantContext tenant,
    IClock clock)
{
    public async Task<ItemSerialisationSettingsRow> GetAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var source = await items.GetAsync(itemId, cancellationToken)
            ?? throw new CommandRefusedException("item_unknown", "The item is not known to the host.", "choose an item from the item list");
        var json = await db.ItemSerialisationSettings.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.ItemId == itemId)
            .Select(row => row.ModelNumbersJson)
            .SingleOrDefaultAsync(cancellationToken);
        var modelNumbers = json is null
            ? source.ModelNumbers ?? []
            : JsonSerializer.Deserialize<string[]>(json) ?? [];
        return new(itemId, source.ItemCode, modelNumbers);
    }

    public async Task<ItemSerialisationSettingsRow> SetAsync(
        Guid itemId,
        UpdateItemModelNumbers update,
        string actorRef,
        CancellationToken cancellationToken)
    {
        var source = await items.GetAsync(itemId, cancellationToken)
            ?? throw new CommandRefusedException("item_unknown", "The item is not known to the host.", "choose an item from the item list");
        var modelNumbers = Clean(update.ModelNumbers);
        var row = await db.ItemSerialisationSettings
            .SingleOrDefaultAsync(value => value.TenantId == tenant.TenantId && value.ItemId == itemId, cancellationToken);
        if (row is null)
        {
            row = new ItemSerialisationSettings
            {
                TenantId = tenant.TenantId,
                ItemId = itemId,
                LastModifiedBy = actorRef,
            };
            db.ItemSerialisationSettings.Add(row);
        }

        row.ModelNumbersJson = JsonSerializer.Serialize(modelNumbers);
        row.LastModifiedAt = clock.UtcNow;
        row.LastModifiedBy = actorRef;
        await db.SaveChangesAsync(cancellationToken);
        return new(itemId, source.ItemCode, modelNumbers);
    }

    internal static IReadOnlyList<string> Clean(IReadOnlyList<string>? values)
    {
        if (values is null) return [];
        if (values.Count > 20)
            throw new CommandRefusedException("item_model_numbers_too_many", "An item can have at most 20 model numbers.");
        var clean = values.Select(value => value?.Trim() ?? "").ToList();
        if (clean.Any(string.IsNullOrWhiteSpace))
            throw new CommandRefusedException("item_model_number_blank", "Model numbers cannot be blank.");
        if (clean.Any(value => value.Length > 64))
            throw new CommandRefusedException("item_model_number_too_long", "Each model number can have at most 64 characters.");
        if (clean.Select(ModelNumberRules.Normalise).Distinct(StringComparer.Ordinal).Count() != clean.Count)
            throw new CommandRefusedException("item_model_number_duplicate", "Model numbers must be unique ignoring case and spaces.");
        return clean;
    }
}
