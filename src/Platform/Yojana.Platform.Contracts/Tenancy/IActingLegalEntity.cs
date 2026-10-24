namespace Yojana.Platform.Contracts.Tenancy;

/// <summary>
/// Resolves the legal entity stamped at a trusted screen boundary. Party ownership stays outside Platform; the host
/// supplies this port from the tenant-scoped party-name catalogue.
/// </summary>
public interface IActingLegalEntity
{
    const string DefaultSettingKey = "tenant.legalEntity.default";

    Task<ActingLegalEntityOptions> GetAsync(CancellationToken cancellationToken);

    Task<ActingLegalEntityResolution> ResolveAsync(Guid? requestedId, bool choiceAllowed,
        CancellationToken cancellationToken);

    Task<ActingLegalEntityResolution> SetDefaultAsync(Guid partyId, CancellationToken cancellationToken);
}

public sealed record ActingLegalEntityOption(Guid Id, string Code, string Name);

public sealed record ActingLegalEntityOptions(Guid? DefaultId, IReadOnlyList<ActingLegalEntityOption> Entities);

public sealed record ActingLegalEntityResolution(Guid? LegalEntityId, string? RefusalRule = null,
    string? RefusalMessage = null)
{
    public bool Accepted => LegalEntityId is not null && RefusalRule is null;
}
