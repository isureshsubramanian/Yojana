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

/// <summary>A vendor's published warranty policy for an item on the day we received it; null means no supplier cover.</summary>
public sealed record VendorWarrantyTerm(Guid VendorPartyRef, Guid ItemId, Guid PolicyVersionId);

public interface IVendorWarrantyTermsPort
{
    Task<VendorWarrantyTerm?> TermsForAsync(Guid vendorPartyRef, Guid itemId, DateOnly receiptDate, CancellationToken cancellationToken);
}

/// <summary>IANA zone of a customer site or of the obligor legal entity, for the reference time-zone rule (case 112); null when unknown.</summary>
public interface ITimeZonePort
{
    Task<string?> ForLocationAsync(Guid locationId, CancellationToken cancellationToken);

    Task<string?> ForLegalEntityAsync(Guid legalEntityId, CancellationToken cancellationToken);
}

/// <summary>
/// Whether a unit's entitlement has claims still open (claims and adjudication, P4). A reduction of term or coverage is
/// blocked while any is open (case 140), so a claim is never adjudicated against a term shortened under it.
/// </summary>
public interface IWarrantyClaimsPort
{
    Task<bool> HasOpenClaimsAsync(Guid unitId, Guid entitlementId, CancellationToken cancellationToken);

    /// <summary>One set-level answer for a unit read model; connectors must not issue one remote query per entitlement.</summary>
    Task<bool> HasOpenClaimsAsync(Guid unitId, IReadOnlyList<Guid> entitlementIds, CancellationToken cancellationToken);

}

/// <summary>Settlement arithmetic stays behind this port so MOD-22 can add measured formulas without changing claims.</summary>
public interface ISettlementFormulaPort
{
    Task<SettlementFormulaResult> CalculateAsync(SettlementFormulaQuestion question, CancellationToken cancellationToken);
}

public sealed record SettlementFormulaQuestion(string Formula, decimal RequestedAmount, decimal CoveragePct, IReadOnlyDictionary<string, decimal> Measurements);

public sealed record SettlementFormulaResult(decimal ApprovedAmount);

/// <summary>Historical inbound cover for supplier recovery, including children installed on the claim date.</summary>
public interface IEntitlementRecoveryQuery
{
    Task<IReadOnlyList<InboundRecoveryCoverage>> FindAsync(Guid rootUnitId, Guid lineUnitId, DateOnly claimDate, CancellationToken cancellationToken);
}

public sealed record InboundRecoveryCoverage(Guid EntitlementId, Guid UnitId, Guid VendorPartyRef);

/// <summary>
/// Whether a warranty certificate carrying this entitlement has been issued to the customer (certificates, P6). A
/// reduction after issue needs the higher approval and a customer notification reference (case 140).
/// </summary>
public interface IWarrantyCertificatePort
{
    Task<bool> CertificateIssuedAsync(Guid entitlementId, CancellationToken cancellationToken);
}
