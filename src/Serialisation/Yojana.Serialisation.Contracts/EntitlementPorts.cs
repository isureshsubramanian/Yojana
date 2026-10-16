namespace Yojana.Serialisation.Contracts;

// What the entitlement engine asks the host (MOD-06 ports, P2-T04). Implemented by connectors (P8) or in-house modules
// (P12); in-memory defaults stand in until then. The module never reads an item master, a contract or a location table.

/// <summary>An item's warranty policy in one coverage family, by policy code; the version is chosen by contract date.</summary>
public sealed record ItemWarrantyPolicy(Guid ItemId, string CoverageFamily, string PolicyCode, bool EntitlementRequiredOnSale);

public interface IItemPolicyPort
{
    Task<IReadOnlyList<ItemWarrantyPolicy>> PoliciesForAsync(Guid itemId, CancellationToken cancellationToken);
}

/// <summary>
/// A customer contract that grants warranty terms for an item (policy stack level 3). <see cref="Priority"/> is the
/// administrator's tie-break when several contracts match; the later start wins a tie on priority.
/// </summary>
public sealed record CustomerContractTerm(string ContractRef, Guid PartyRef, string CoverageFamily, string PolicyCode, int Priority, DateOnly ValidFrom, DateOnly? ValidTo);

public interface ICustomerContractPort
{
    Task<IReadOnlyList<CustomerContractTerm>> ContractsForAsync(Guid partyRef, Guid itemId, DateOnly contractDate, CancellationToken cancellationToken);
}

/// <summary>IANA zone of a customer site or of the obligor legal entity, for the reference time-zone rule (case 112); null when unknown.</summary>
public interface ITimeZonePort
{
    Task<string?> ForLocationAsync(Guid locationId, CancellationToken cancellationToken);

    Task<string?> ForLegalEntityAsync(Guid legalEntityId, CancellationToken cancellationToken);
}
