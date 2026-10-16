using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Identity;

/// <summary>Case handling of a normaliser (Identity rule 3).</summary>
public enum CasePolicy
{
    /// <summary>Upper-case the value; most OEM serials.</summary>
    Upper,

    /// <summary>Keep the value as scanned; GS1 serials are case-sensitive.</summary>
    Preserve,
}

/// <summary>Leading-zero handling (case 9).</summary>
public enum LeadingZeroPolicy
{
    /// <summary>Zeros are part of the serial.</summary>
    Keep,

    /// <summary>Strip leading zeros from an all-digit value before comparing.</summary>
    Strip,
}

/// <summary>Which barcode symbology prints an identifier type; it decides the character set the validator accepts (case 139).</summary>
public enum Symbology
{
    /// <summary>Full ASCII; the recommended default for serial labels.</summary>
    Code128,

    /// <summary>Full ASCII, 2D; the recommended default for small labels.</summary>
    DataMatrix,

    /// <summary>Full ASCII, 2D.</summary>
    QrCode,

    /// <summary>Digits, A–Z, space and - . $ / + %; '*' is the start and stop character and cannot be data.</summary>
    Code39,

    /// <summary>Digits only (GTIN carriers).</summary>
    EanUpc,

    /// <summary>Human entry or an unknown carrier: no symbology restriction.</summary>
    None,
}

/// <summary>Which check applies at entry, scan and import (Identity rule 4).</summary>
public enum Validator
{
    None,
    Vin,
    ImeiLuhn,
    Gtin,
    Sscc,
    Gs1ElementString,
    UdiDeviceIdentifier,
    Uii,
    MacAddress,
}

/// <summary>What a failed check does: block the capture, or warn and raise an exception task (Identity rule 4: block for regulated types).</summary>
public enum FailureSeverity
{
    Block,
    Warn,
}

/// <summary>
/// The rules for one identifier type: scheme, normalisation, check, symbology and uniqueness scope (Identity rules 1–4).
/// Defaults live in <see cref="IdentifierPolicySet.Defaults"/>; a tenant may override a type's policy as tenant data
/// (<see cref="IIdentifierPolicySource"/>), but only when no open duplicate exists for the new scope rule.
/// </summary>
public sealed record IdentifierPolicy(
    string Type,
    string Scheme,
    CasePolicy Case,
    IReadOnlySet<char> StripCharacters,
    LeadingZeroPolicy LeadingZeros,
    Validator Validator,
    FailureSeverity OnFailure,
    Symbology Symbology,
    bool GlobalUniqueness)
{
    /// <summary>Scope key for this type on a given item and owner (Identity rule 1; asset tags scope to the owner party).</summary>
    public string ScopeKeyFor(Guid? itemId, Guid? ownerPartyRef)
    {
        if (GlobalUniqueness)
        {
            return ScopeKeys.Tenant;
        }

        if (Type == IdentifierType.CustomerAssetTag)
        {
            return ownerPartyRef is { } owner ? "owner:" + owner.ToString("N") : ScopeKeys.Tenant;
        }

        return itemId is { } item ? item.ToString("N") : ScopeKeys.Tenant;
    }
}

/// <summary>Source of the active policy per identifier type for the current tenant.</summary>
public interface IIdentifierPolicySource
{
    IdentifierPolicy For(string identifierType);
}

/// <summary>The code defaults; a tenant-data override arrives with the settings screen in a later phase.</summary>
public sealed class IdentifierPolicySet : IIdentifierPolicySource
{
    private static readonly IReadOnlySet<char> None = new HashSet<char>();
    private static readonly IReadOnlySet<char> HyphenSpace = new HashSet<char> { '-', ' ' };
    private static readonly IReadOnlySet<char> Separators = new HashSet<char> { '-', ' ', ':', '.' };

    public static readonly IReadOnlyDictionary<string, IdentifierPolicy> Defaults = new Dictionary<string, IdentifierPolicy>(StringComparer.Ordinal)
    {
        [IdentifierType.InternalSerial] = new(IdentifierType.InternalSerial, IdentifierScheme.Internal, CasePolicy.Upper, HyphenSpace, LeadingZeroPolicy.Keep, Validator.None, FailureSeverity.Block, Symbology.Code128, false),
        [IdentifierType.ManufacturerSerial] = new(IdentifierType.ManufacturerSerial, IdentifierScheme.Oem, CasePolicy.Upper, HyphenSpace, LeadingZeroPolicy.Keep, Validator.None, FailureSeverity.Warn, Symbology.Code128, false),
        [IdentifierType.VendorSerial] = new(IdentifierType.VendorSerial, IdentifierScheme.Oem, CasePolicy.Upper, HyphenSpace, LeadingZeroPolicy.Keep, Validator.None, FailureSeverity.Warn, Symbology.Code128, false),
        [IdentifierType.CustomerSerial] = new(IdentifierType.CustomerSerial, IdentifierScheme.Oem, CasePolicy.Upper, HyphenSpace, LeadingZeroPolicy.Keep, Validator.None, FailureSeverity.Warn, Symbology.None, false),
        [IdentifierType.Imei] = new(IdentifierType.Imei, IdentifierScheme.Oem, CasePolicy.Upper, Separators, LeadingZeroPolicy.Keep, Validator.ImeiLuhn, FailureSeverity.Block, Symbology.Code128, true),
        [IdentifierType.Mac] = new(IdentifierType.Mac, IdentifierScheme.Oem, CasePolicy.Upper, Separators, LeadingZeroPolicy.Keep, Validator.MacAddress, FailureSeverity.Block, Symbology.Code128, true),
        [IdentifierType.UdiDi] = new(IdentifierType.UdiDi, IdentifierScheme.Udi, CasePolicy.Preserve, None, LeadingZeroPolicy.Keep, Validator.UdiDeviceIdentifier, FailureSeverity.Block, Symbology.DataMatrix, false),
        [IdentifierType.UdiPi] = new(IdentifierType.UdiPi, IdentifierScheme.Udi, CasePolicy.Preserve, None, LeadingZeroPolicy.Keep, Validator.Gs1ElementString, FailureSeverity.Block, Symbology.DataMatrix, false),
        [IdentifierType.Uii] = new(IdentifierType.Uii, IdentifierScheme.DoD, CasePolicy.Upper, None, LeadingZeroPolicy.Keep, Validator.Uii, FailureSeverity.Block, Symbology.DataMatrix, true),
        [IdentifierType.Vin] = new(IdentifierType.Vin, IdentifierScheme.Iso15459, CasePolicy.Upper, HyphenSpace, LeadingZeroPolicy.Keep, Validator.Vin, FailureSeverity.Block, Symbology.Code39, true),
        [IdentifierType.Sgtin] = new(IdentifierType.Sgtin, IdentifierScheme.Gs1, CasePolicy.Preserve, None, LeadingZeroPolicy.Keep, Validator.Gs1ElementString, FailureSeverity.Block, Symbology.DataMatrix, true),
        [IdentifierType.Giai] = new(IdentifierType.Giai, IdentifierScheme.Gs1, CasePolicy.Preserve, None, LeadingZeroPolicy.Keep, Validator.Gs1ElementString, FailureSeverity.Block, Symbology.DataMatrix, true),
        [IdentifierType.ChannelOrderRef] = new(IdentifierType.ChannelOrderRef, IdentifierScheme.Oem, CasePolicy.Preserve, None, LeadingZeroPolicy.Keep, Validator.None, FailureSeverity.Warn, Symbology.None, false),
        [IdentifierType.CustomerAssetTag] = new(IdentifierType.CustomerAssetTag, IdentifierScheme.Oem, CasePolicy.Upper, None, LeadingZeroPolicy.Keep, Validator.None, FailureSeverity.Warn, Symbology.Code128, false),
        [IdentifierType.SoftwareLicense] = new(IdentifierType.SoftwareLicense, IdentifierScheme.Oem, CasePolicy.Preserve, None, LeadingZeroPolicy.Keep, Validator.None, FailureSeverity.Warn, Symbology.None, false),
        [IdentifierType.Provisional] = new(IdentifierType.Provisional, IdentifierScheme.Internal, CasePolicy.Upper, None, LeadingZeroPolicy.Keep, Validator.None, FailureSeverity.Warn, Symbology.None, false),
    };

    private readonly IReadOnlyDictionary<string, IdentifierPolicy> policies;

    public IdentifierPolicySet() : this(Defaults) { }

    public IdentifierPolicySet(IReadOnlyDictionary<string, IdentifierPolicy> policies) => this.policies = policies;

    public IdentifierPolicy For(string identifierType) =>
        policies.TryGetValue(identifierType, out var policy) ? policy : throw new ArgumentException($"Unknown identifier type '{identifierType}'.", nameof(identifierType));
}
