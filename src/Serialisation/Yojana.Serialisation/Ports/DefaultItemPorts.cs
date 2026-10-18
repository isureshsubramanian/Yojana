using System.Collections.Concurrent;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Ports;

/// <summary>
/// Item and BOM facts until a connector (P8) or an in-house module (P12) implements the ports: profiles registered in
/// memory, with a tenant-neutral default for unknown items so development and tests can commission units. A host
/// replaces both registrations; this class is never the production source of truth.
/// </summary>
public sealed class InMemoryItemPorts : IItemQueryPort, IBomQueryPort
{
    private readonly ConcurrentDictionary<Guid, ItemSerialisationProfile> items = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<ExpectedComponent>> boms = new();

    /// <summary>Profile returned for an item nobody registered: serial-tracked, released at production, never re-used, complete, saleable and serviceable.</summary>
    public static ItemSerialisationProfile DefaultProfile(Guid itemId) => new(itemId, "ITEM-" + itemId.ToString("N")[..8].ToUpperInvariant(),
        ItemSerialisationProfile.TrackingModes.Serial, SerialisationPoint.ProductionRelease, "standard_10y",
        ItemSerialisationProfile.ReusePolicies.Never, ItemSerialisationProfile.Completeness.Block, IdentifierScheme.Internal);

    public bool UseDefaultForUnknownItems { get; set; } = true;

    public void Register(ItemSerialisationProfile profile) => items[profile.ItemId] = profile;

    public void RegisterBom(Guid itemId, IReadOnlyList<ExpectedComponent> components) => boms[itemId] = components;

    public Task<ItemSerialisationProfile?> GetAsync(Guid itemId, CancellationToken cancellationToken) =>
        Task.FromResult(items.TryGetValue(itemId, out var p) ? p : UseDefaultForUnknownItems ? DefaultProfile(itemId) : null);

    public Task<IReadOnlyDictionary<Guid, ItemSerialisationProfile?>> GetManyAsync(
        IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ItemSerialisationProfile?>>(itemIds.Distinct().ToDictionary(
            itemId => itemId,
            itemId => items.TryGetValue(itemId, out var profile)
                ? profile
                : UseDefaultForUnknownItems ? DefaultProfile(itemId) : null));

    public Task<IReadOnlyList<ExpectedComponent>> ExpectedSerialisedComponentsAsync(Guid itemId, DateOnly effectiveAt, CancellationToken cancellationToken) =>
        Task.FromResult(boms.TryGetValue(itemId, out var b) ? b : []);

    public Task<IReadOnlyDictionary<ExpectedComponentQuery, IReadOnlyList<ExpectedComponent>>> ExpectedSerialisedComponentsAsync(
        IReadOnlyCollection<ExpectedComponentQuery> queries, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<ExpectedComponentQuery, IReadOnlyList<ExpectedComponent>>>(queries.Distinct().ToDictionary(
            query => query,
            query => boms.TryGetValue(query.ItemId, out var components) ? components : []));
}
