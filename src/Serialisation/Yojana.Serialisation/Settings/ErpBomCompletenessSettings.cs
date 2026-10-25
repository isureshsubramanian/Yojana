using Yojana.Integration.Contracts;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Settings;

/// <summary>The ERP BOM completeness rule is tenant-owned and remains off until an administrator opts in.</summary>
public sealed class ErpBomCompletenessSettings(ITenantSettings settings, IErpBomCapabilityPort capabilities)
{
    public const string Key = "serialisation.check_erp_bom_completeness";

    public async Task<ErpBomCompletenessSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetVersionedAsync(Key, cancellationToken);
        return new(stored is not null && bool.TryParse(stored.Value, out var enabled) && enabled,
            stored?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<ErpBomCompletenessSettingsView> SetAsync(UpdateErpBomCompletenessSettings update,
        CancellationToken cancellationToken)
    {
        if (update.CheckAgainstErpBom && !await capabilities.HasActiveBomSourceAsync(cancellationToken))
            throw new CommandRefusedException("bom_completeness_needs_erp_bom",
                "The completeness check needs an active ERP connection that supplies bills of materials.");
        var stored = await settings.SetAsync(Key, update.CheckAgainstErpBom.ToString(), update.ExpectedVersion,
            cancellationToken);
        return new(update.CheckAgainstErpBom, stored.Version);
    }

    public async Task<bool> EnabledAsync(CancellationToken cancellationToken) =>
        (await GetAsync(cancellationToken)).CheckAgainstErpBom;
}

public sealed class UnavailableErpBomCapabilityPort : IErpBomCapabilityPort
{
    public Task<bool> HasActiveBomSourceAsync(CancellationToken cancellationToken) => Task.FromResult(false);
}
