using Microsoft.Extensions.DependencyInjection;

namespace Yojana.Integration.Gateway;

/// <summary>
/// A connector's onboarding facts that are not capabilities: what the customer pays to the remote vendor or its
/// partner, and the dedicated identity the connection must run as. The text seeds tenant-owned catalogue data and
/// checklist items; tenants may replace the seeded disclosure without a code change.
/// </summary>
public interface IConnectorOnboardingProfile
{
    string CustomerCostDisclosure { get; }

    ConnectorIntegrationUserRequirement IntegrationUser { get; }
}

public sealed record ConnectorIntegrationUserRequirement(string Label, string Detail, string EvidencePrompt);

/// <summary>Used for every connector code that has not registered its own profile.</summary>
public sealed class GenericConnectorOnboardingProfile : IConnectorOnboardingProfile
{
    public static readonly GenericConnectorOnboardingProfile Instance = new();

    public string CustomerCostDisclosure =>
        "Confirm licence, API access, hosting, implementation and recurring usage costs with the customer before activation.";

    public ConnectorIntegrationUserRequirement IntegrationUser { get; } = new(
        "Create a dedicated integration identity",
        "Use an identity that belongs to the integration only: never a person's login and never shared with "
        + "another application. Grant only the permissions the declared capabilities need.",
        "Integration identity name as created in the remote system");
}

public interface IConnectorOnboardingProfiles
{
    IConnectorOnboardingProfile For(string connectorCode);
}

/// <summary>Resolves profiles by exact connector code through keyed services, like the connector adapters.</summary>
public sealed class ConnectorOnboardingProfiles(IServiceProvider services) : IConnectorOnboardingProfiles
{
    public IConnectorOnboardingProfile For(string connectorCode) =>
        services.GetKeyedService<IConnectorOnboardingProfile>(connectorCode)
        ?? GenericConnectorOnboardingProfile.Instance;
}
