using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Identity;

/// <summary>The per-identifier-type capture exceptions layered over the code-owned identifier policies.</summary>
public sealed class IdentifierCaptureSettings(ITenantSettings settings)
{
    public const string Key = "identity.identifierPolicyOverrides";

    // Coupled to the factory registration in SerialisationModule: exposing ITenantSettings through IdentifierService
    // gives Wolverine both PlatformDbContext and SerialisationDbContext and prevents handler transaction discovery.

    public async Task<IReadOnlySet<string>> CodabarTypesAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetAsync(Key, cancellationToken);
        if (string.IsNullOrWhiteSpace(stored)) return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var overrides = JsonSerializer.Deserialize<IReadOnlyDictionary<string, IdentifierPolicyOverride>>(stored,
                LedgerJson.Options);
            return overrides?.Where(row => row.Value.RemoveCodabarStartStopLetters)
                .Select(row => row.Key).ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public sealed record IdentifierPolicyOverride(bool RemoveCodabarStartStopLetters = false);
}
