using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Settings;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Settings;

public sealed class LegalEntitySettingsSummaryContributor(IActingLegalEntity legalEntities)
    : ISettingsSummaryContributor
{
    // Hidden coupling: this key must also appear as summaryKey in web/src/settings/registry.ts. The architecture
    // cross-check protects both otherwise-independent sides from silently drifting.
    public const string Key = "company";

    public IReadOnlyList<SettingsSummaryKey> Keys { get; } =
        [new(Key, [YojanaRole.TenantAdministrator])];

    public async Task<IReadOnlyList<SettingsSummaryLine>> ReadAsync(IReadOnlySet<string> keys,
        CancellationToken cancellationToken)
    {
        if (!keys.Contains(Key)) return [];
        var options = await legalEntities.GetAsync(cancellationToken);
        return [new(Key, new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["entities"] = options.Entities.Count,
            ["defaultConfigured"] = options.DefaultId is null ? 0 : 1,
        }, options.DefaultId is null ? "legal_entity_not_configured" : null)];
    }
}
