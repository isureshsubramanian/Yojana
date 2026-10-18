using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Labels;

/// <summary>OD-10/OD-11 tenant policy. No country rule is embedded: tenants choose phrases and expiry use.</summary>
public sealed class LabelSettings(ITenantSettings settings)
{
    public const string Key = "labels.policy";
    public static readonly LabelPolicy Default = new(["void", "voids warranty", "warranty void"], false, "default");

    // Coupled to the factory registration in SerialisationModule: exposing ITenantSettings directly to label handlers
    // adds PlatformDbContext beside SerialisationDbContext and prevents Wolverine transaction discovery.
    public async Task<LabelPolicy> GetAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } text
            ? JsonSerializer.Deserialize<LabelPolicy>(text, LedgerJson.Options) ?? Default
            : Default;

    public async Task<LabelPolicy> SetAsync(LabelPolicy policy, CancellationToken cancellationToken)
    {
        var phrases = policy.LegalReviewPhrases.Select(value => value.Trim()).Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (phrases.Count == 0)
            throw new CommandRefusedException("label_legal_phrases_empty", "At least one legal-review phrase is required.");
        if (phrases.Any(value => value.Length > 100))
            throw new CommandRefusedException("label_legal_phrase_too_long", "A legal-review phrase cannot exceed 100 characters.");
        var printer = policy.DefaultPrinterRef.Trim();
        if (printer.Length is 0 or > 200)
            throw new CommandRefusedException("label_default_printer_invalid", "A default printer reference is required and cannot exceed 200 characters.");
        var clean = new LabelPolicy(phrases, policy.PrintExpiryDates, printer);
        await settings.SetAsync(Key, JsonSerializer.Serialize(clean, LedgerJson.Options), cancellationToken);
        return clean;
    }
}
