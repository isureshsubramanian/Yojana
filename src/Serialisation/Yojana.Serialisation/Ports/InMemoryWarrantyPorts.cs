using System.Collections.Concurrent;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Ports;

/// <summary>
/// Item policies, customer contracts, time zones, open claims and certificate issue until a connector or an in-house
/// module implements the ports. Tests and development register facts here; nothing is assumed for an item nobody
/// registered. Never the production source.
/// <para>
/// Certificates (P6) default to <b>issued</b>: until the certificate module answers, every reduction is treated as
/// taking cover from a customer who holds a certificate, so it needs the higher approval and a notification reference.
/// Claims (P4) default to none open, which is true while no claim can exist.
/// </para>
/// </summary>
public sealed class InMemoryWarrantyPorts : IItemPolicyPort, ICustomerContractPort, ITimeZonePort, IWarrantyClaimsPort, IWarrantyCertificatePort
{
    private readonly ConcurrentDictionary<Guid, List<ItemWarrantyPolicy>> policies = new();
    private readonly ConcurrentDictionary<Guid, List<(Guid ItemId, CustomerContractTerm Term)>> contracts = new();
    private readonly ConcurrentDictionary<Guid, string> zones = new();
    private readonly ConcurrentDictionary<Guid, bool> openClaims = new();
    private readonly ConcurrentDictionary<Guid, bool> certificateIssued = new();

    public void Register(ItemWarrantyPolicy policy) => policies.AddOrUpdate(policy.ItemId, _ => [policy], (_, list) => { lock (list) { list.RemoveAll(p => p.CoverageFamily == policy.CoverageFamily); list.Add(policy); } return list; });

    public void Register(Guid itemId, CustomerContractTerm term) => contracts.AddOrUpdate(term.PartyRef, _ => [(itemId, term)], (_, list) => { lock (list) list.Add((itemId, term)); return list; });

    public void RegisterZone(Guid locationOrEntityId, string ianaZone) => zones[locationOrEntityId] = ianaZone;

    public void RegisterOpenClaims(Guid entitlementId, bool open) => openClaims[entitlementId] = open;

    public void RegisterCertificateIssued(Guid entitlementId, bool issued) => certificateIssued[entitlementId] = issued;

    public Task<IReadOnlyList<ItemWarrantyPolicy>> PoliciesForAsync(Guid itemId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ItemWarrantyPolicy>>(policies.TryGetValue(itemId, out var list) ? [.. list] : []);

    public Task<IReadOnlyList<CustomerContractTerm>> ContractsForAsync(Guid partyRef, Guid itemId, DateOnly contractDate, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CustomerContractTerm>>(contracts.TryGetValue(partyRef, out var list)
            ? list.Where(c => c.ItemId == itemId && c.Term.ValidFrom <= contractDate && (c.Term.ValidTo is null || contractDate <= c.Term.ValidTo)).Select(c => c.Term).ToList()
            : []);

    public Task<string?> ForLocationAsync(Guid locationId, CancellationToken cancellationToken) => Task.FromResult(zones.TryGetValue(locationId, out var z) ? z : null);

    public Task<string?> ForLegalEntityAsync(Guid legalEntityId, CancellationToken cancellationToken) => Task.FromResult(zones.TryGetValue(legalEntityId, out var z) ? z : null);

    public Task<bool> HasOpenClaimsAsync(Guid unitId, Guid entitlementId, CancellationToken cancellationToken) => Task.FromResult(openClaims.TryGetValue(entitlementId, out var open) && open);

    public Task<bool> CertificateIssuedAsync(Guid entitlementId, CancellationToken cancellationToken) => Task.FromResult(!certificateIssued.TryGetValue(entitlementId, out var issued) || issued);
}
