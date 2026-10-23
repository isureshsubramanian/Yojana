using Yojana.Integration.Gateway;

namespace Yojana.Integration.Connectors.CX04;

public sealed class Cx04OnboardingProfile : IConnectorOnboardingProfile
{
    public string CustomerCostDisclosure =>
        "The customer supplies its own licensed environment and runs acceptance in its own sandbox before go-live. "
        + "The connector uses a dedicated API user, a tenant-local connected application and the licence limits "
        + "recorded during onboarding; no paid environment or shared identity is included in this product.";

    public ConnectorIntegrationUserRequirement IntegrationUser { get; } = new(
        "Create a dedicated API user and connected application",
        "Create a least-privilege API user, register the connected application with authorisation-code and PKCE "
        + "support, enable refresh tokens, choose absolute or sliding expiry from 1 to 365 days, record the licence "
        + "tier and complete the first live acceptance run in the customer's sandbox. Never supply a password to Yojana.",
        "Dedicated user evidence, connected-application identifier, refresh mode and lifetime, licence tier and sandbox acceptance record");
}
