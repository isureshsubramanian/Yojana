namespace Yojana.Platform.Contracts.Tenancy;

/// <summary>
/// The tenant's working currency (ISO 4217 alphabetic code, upper case), kept as the tenant setting <see cref="Key"/>.
/// Every amount a module stores as tenant configuration (thresholds, policy values) is in this currency; a module that
/// compares amounts refuses to do so while it is not set up, and never assumes a default.
/// </summary>
public interface ITenantCurrency
{
    public const string Key = "tenant.currency";

    /// <summary>Setting key of the other currencies documents may be written in (JSON array of codes).</summary>
    public const string AcceptedKey = "tenant.currencies.accepted";

    /// <summary>The configured code, or null while the tenant has not set one up.</summary>
    Task<string?> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sets the working currency once, at tenant setup. Refuses anything outside the catalogue, and refuses any different
    /// code once one is set (<see cref="TenantCurrencyLockedException"/>): stored amounts are in it and are never converted.
    /// Setting the same code again is a no-op.
    /// </summary>
    Task<string> SetAsync(string code, CancellationToken cancellationToken);

    /// <summary>
    /// The currencies documents (claims, recovery claims, quotes) may be written in: the working currency first, then
    /// the accepted others in code order. Empty while the working currency is not set up.
    /// </summary>
    Task<IReadOnlyList<string>> GetAcceptedAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the accepted other currencies; refuses any code outside the catalogue. The working currency is always accepted.</summary>
    Task<IReadOnlyList<string>> SetAcceptedAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken);

    /// <summary>The normalised code (trimmed, upper case), or null when <paramref name="code"/> is not three letters.</summary>
    static string? Normalise(string? code)
    {
        var trimmed = code?.Trim().ToUpperInvariant();
        return trimmed is { Length: 3 } && trimmed.All(c => c is >= 'A' and <= 'Z') ? trimmed : null;
    }
}

/// <summary>The tenant's working currency is already set and cannot be changed.</summary>
public sealed class TenantCurrencyLockedException(string current, string requested)
    : InvalidOperationException($"The working currency is {current} and cannot be changed to {requested}; every stored amount is in {current}.")
{
    public const string Rule = "working_currency_locked";

    public string Current { get; } = current;
}
