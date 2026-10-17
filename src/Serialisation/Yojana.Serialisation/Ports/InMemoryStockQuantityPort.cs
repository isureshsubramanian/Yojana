using System.Collections.Concurrent;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Ports;

/// <summary>Development and test stock view; a host replaces IStockQuantityPort with its Inventory or connector adapter.</summary>
public sealed class InMemoryStockQuantityPort : IStockQuantityPort
{
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<OpenStockQuantity>> quantities = new();

    public void Set(Guid itemId, IReadOnlyList<OpenStockQuantity> openStock) => quantities[itemId] = openStock;

    public Task<IReadOnlyList<OpenStockQuantity>> GetOpenStockAsync(Guid itemId, DateTime effectiveAt, CancellationToken cancellationToken) =>
        Task.FromResult(quantities.TryGetValue(itemId, out var value) ? value : (IReadOnlyList<OpenStockQuantity>)[]);
}
