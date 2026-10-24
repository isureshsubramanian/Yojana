namespace Yojana.Platform.Contracts.Tenancy;

/// <summary>
/// The tenant's home currency (ISO 4217 alphabetic code, upper case), kept as the tenant setting <see cref="Key"/>.
/// Every amount a module stores is in this currency; a module that compares amounts refuses to do so while it is not
/// set up, and never assumes a default.
/// </summary>
public interface ITenantCurrency
{
    public const string Key = "tenant.currency";

    /// <summary>The configured code, or null while the tenant has not set one up.</summary>
    Task<string?> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sets or changes the home currency while no module has recorded an amount. Refuses anything outside the catalogue,
    /// and refuses a different code after the first amount (<see cref="TenantCurrencyLockedException"/>). Setting the same
    /// code again is a no-op.
    /// </summary>
    Task<string> SetAsync(string code, CancellationToken cancellationToken);

    /// <summary>The currency together with the records that make it changeable or locked.</summary>
    Task<TenantCurrencyStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>The normalised code (trimmed, upper case), or null when <paramref name="code"/> is not three letters.</summary>
    static string? Normalise(string? code)
    {
        var trimmed = code?.Trim().ToUpperInvariant();
        return trimmed is { Length: 3 } && trimmed.All(c => c is >= 'A' and <= 'Z') ? trimmed : null;
    }
}

/// <summary>
/// A module-owned, cheap count of records whose stored amount gives the home currency meaning. This contract is the
/// Platform side of a deliberate hidden coupling: every new money-storing entity must be added to its owning module's
/// implementation, or a currency change could relabel that amount. CurrencyUsageArchitectureTests guards that seam.
/// </summary>
public interface ICurrencyUsage
{
    Task<IReadOnlyList<CurrencyUsage>> CountAsync(CancellationToken cancellationToken);
}

/// <summary>A module-owned read of legacy rows that do not match the home currency.</summary>
public interface ICurrencyUsageReport
{
    Task<IReadOnlyList<OtherCurrencyUsage>> CountOtherAsync(string homeCurrency, CancellationToken cancellationToken);
}

public sealed record CurrencyUsage(string Kind, long Count);

public sealed record OtherCurrencyUsage(string Kind, string Currency, long Count);

public sealed record TenantCurrencyStatus(string? Currency, bool Changeable, IReadOnlyList<CurrencyUsage> Usage);

/// <summary>The tenant's home currency has recorded amounts and cannot be changed.</summary>
public sealed class TenantCurrencyLockedException(string current, string requested, IReadOnlyList<CurrencyUsage> usage)
    : InvalidOperationException($"The home currency is {current} and cannot be changed to {requested}; {Summary(usage)} are recorded in {current}. Amounts are never converted.")
{
    public const string Rule = "working_currency_locked";

    public string Current { get; } = current;

    public IReadOnlyList<CurrencyUsage> Usage { get; } = usage;

    private static string Summary(IReadOnlyList<CurrencyUsage> values) => values.Count == 0
        ? "amounts"
        : string.Join(" and ", values.Where(value => value.Count > 0).Select(value => $"{value.Count:N0} {value.Kind.Replace('_', ' ')}"));
}
