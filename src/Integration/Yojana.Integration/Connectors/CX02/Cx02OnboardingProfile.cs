using Yojana.Integration.Gateway;

namespace Yojana.Integration.Connectors.CX02;

public sealed class Cx02OnboardingProfile : IConnectorOnboardingProfile
{
    public string CustomerCostDisclosure =>
        "The customer supplies its own licensed tenant and runs acceptance in one of its included sandbox "
        + "environments before go-live. The connector needs one application registration for this tenant and the "
        + "tenant-installed integration extension at version 1.0 or later; neither paid access nor a shared "
        + "application identity is included in this product.";

    public ConnectorIntegrationUserRequirement IntegrationUser { get; } = new(
        "Register one application identity and assign the automation permission set",
        "Create one service-to-service application registration for this tenant, grant API access and the "
        + "extension's least-privilege permission set, and never assign the full-access role. Install extension "
        + "version 1.0 or later, record the environment and company identifier, and complete the first live "
        + "acceptance run in the customer's sandbox before go-live.",
        "Application client identifier, permission-set evidence, extension version and sandbox acceptance record");
}
