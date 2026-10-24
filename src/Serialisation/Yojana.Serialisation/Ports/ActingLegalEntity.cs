using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Ports;

/// <summary>
/// Adapts the party owner's existing name port to Platform's acting-entity contract. A party returned as
/// <c>legal_entity</c> is considered active because the current party label contract exposes no separate active flag.
/// </summary>
public sealed class ActingLegalEntity(IPartyNamePort parties, ITenantSettings settings) : IActingLegalEntity
{
    private const int MaximumLegalEntities = 500;
    private Task<IReadOnlyList<ActingLegalEntityOption>>? entities;

    public async Task<ActingLegalEntityOptions> GetAsync(CancellationToken cancellationToken)
    {
        var available = await EntitiesAsync(cancellationToken);
        var configuredText = await settings.GetAsync(IActingLegalEntity.DefaultSettingKey, cancellationToken);
        Guid? defaultId = string.IsNullOrWhiteSpace(configuredText)
            ? available.Count == 1 ? available[0].Id : null
            : Guid.TryParse(configuredText, out var configured)
                && available.Any(entity => entity.Id == configured) ? configured : null;
        return new(defaultId, available);
    }

    public async Task<ActingLegalEntityResolution> ResolveAsync(Guid? requestedId, bool choiceAllowed,
        CancellationToken cancellationToken)
    {
        var available = await EntitiesAsync(cancellationToken);
        if (requestedId is { } requested && requested != Guid.Empty)
        {
            if (available.All(entity => entity.Id != requested))
                return Unknown();
            if (choiceAllowed) return new(requested);
        }

        var configuredText = await settings.GetAsync(IActingLegalEntity.DefaultSettingKey, cancellationToken);
        if (!string.IsNullOrWhiteSpace(configuredText))
        {
            if (!Guid.TryParse(configuredText, out var configured)
                || available.All(entity => entity.Id != configured))
                return Unknown();
            return new(configured);
        }

        if (available.Count == 1) return new(available[0].Id);
        return new(null, "legal_entity_not_configured",
            available.Count == 0
                ? "No active legal entity is available for this tenant."
                : "Choose a default legal entity in Company settings before recording this command.");
    }

    public async Task<ActingLegalEntityResolution> SetDefaultAsync(Guid partyId,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(partyId, choiceAllowed: true, cancellationToken);
        if (!resolved.Accepted) return resolved;
        await settings.SetAsync(IActingLegalEntity.DefaultSettingKey,
            resolved.LegalEntityId!.Value.ToString(), cancellationToken);
        return resolved;
    }

    private Task<IReadOnlyList<ActingLegalEntityOption>> EntitiesAsync(CancellationToken cancellationToken) =>
        entities ??= LoadEntitiesAsync(cancellationToken);

    private async Task<IReadOnlyList<ActingLegalEntityOption>> LoadEntitiesAsync(CancellationToken cancellationToken) =>
        (await parties.SearchAsync(string.Empty, MaximumLegalEntities, cancellationToken))
        .Where(party => string.Equals(party.PartyType, "legal_entity", StringComparison.OrdinalIgnoreCase))
        .OrderBy(party => party.Code, StringComparer.OrdinalIgnoreCase)
        .Select(party => new ActingLegalEntityOption(party.PartyRef, party.Code, party.Name ?? party.Code))
        .ToList();

    private static ActingLegalEntityResolution Unknown() => new(null, "legal_entity_unknown",
        "The selected legal entity is not an active legal entity of this tenant.");
}
