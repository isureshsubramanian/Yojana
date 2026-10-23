using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Connectors.CX02;

public sealed record Cx02OutboundUnit(string ItemNumber, string SerialNumber, string? LocationCode = null);

public interface ICx02OutboundFacts
{
    Task<Cx02OutboundUnit?> UnitAsync(Guid connectionId, Guid unitId, string? suppliedSerial,
        CancellationToken cancellationToken);
    Task<string?> ReferenceAsync(Guid connectionId, string entityType, string reference,
        CancellationToken cancellationToken);
}

internal sealed class Cx02OutboundFacts(
    IntegrationDbContext db,
    ITenantContext tenant,
    ICertificateUnitReadPort units) : ICx02OutboundFacts
{
    public async Task<Cx02OutboundUnit?> UnitAsync(Guid connectionId, Guid unitId, string? suppliedSerial,
        CancellationToken cancellationToken)
    {
        var fact = await units.ReadAsync(unitId, cancellationToken);
        if (fact is null) return null;
        var item = await ReferenceAsync(connectionId, "item", fact.ItemId.ToString(), cancellationToken);
        var serials = await units.PrimarySerialsAsync([unitId], cancellationToken);
        var serial = string.IsNullOrWhiteSpace(suppliedSerial) ? serials.GetValueOrDefault(unitId) : suppliedSerial;
        return item is null || string.IsNullOrWhiteSpace(serial) ? null : new(item, serial);
    }

    public async Task<string?> ReferenceAsync(Guid connectionId, string entityType, string reference,
        CancellationToken cancellationToken)
    {
        if (reference.StartsWith("unit-owner/", StringComparison.Ordinal)
            && Guid.TryParse(reference[11..], out var unitId))
        {
            var unit = await units.ReadAsync(unitId, cancellationToken);
            return unit?.Ownership?.HolderRef is { } owner
                ? await ReferenceAsync(connectionId, "party", owner.ToString(), cancellationToken) : null;
        }
        var qualified = reference.Split(':', 2);
        var resource = qualified.Length == 2 ? qualified[0] : null;
        var candidate = qualified.Length == 2 ? qualified[1] : reference;
        if (!Guid.TryParse(candidate.Split('/').Last(), out var id))
            return string.IsNullOrWhiteSpace(reference) || reference == "owner" ? null : reference;
        var matches = await db.ExternalIdMaps.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == connectionId && row.EntityType == entityType && row.ModuleId == id
                && row.ValidTo == null)
            .Select(row => row.ExternalId).Take(2).ToListAsync(cancellationToken);
        if (matches.Count > 1)
            throw new Cx02OutboundRuleException("The remote reference is ambiguous on this connection.");
        return matches.FirstOrDefault() is { } mapped
            ? resource is null ? mapped : resource + ":" + mapped
            : null;
    }
}

internal sealed class Cx02OutboundRuleException(string message) : Exception(message);
