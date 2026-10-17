namespace Yojana.Platform.Contracts.Tenancy;

/// <summary>One ISO 4217 currency as the catalogue knows it.</summary>
public sealed record CurrencyInfo(string Code, string Name, string Symbol, int MinorDigits);

/// <summary>
/// The currencies a tenant may choose from when it is set up. The platform's implementation reads the runtime's own
/// globalisation data; a host ERP connector may supply its currency table instead through this same contract.
/// </summary>
public interface ICurrencyCatalogue
{
    IReadOnlyList<CurrencyInfo> All { get; }

    CurrencyInfo? Find(string? code);
}
