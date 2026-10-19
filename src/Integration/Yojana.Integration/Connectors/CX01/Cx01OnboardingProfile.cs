using Yojana.Integration.Gateway;

namespace Yojana.Integration.Connectors.CX01;

/// <summary>
/// The remote vendor licenses automated access separately from interactive use: one indirect-access licence for the
/// installation and one named user licence for the integration user, quoted by the customer's partner. The product
/// does not price these in, so they are disclosed at sale and acknowledged in onboarding (ADR-0002 "Security").
/// </summary>
public sealed class Cx01OnboardingProfile : IConnectorOnboardingProfile
{
    public string CustomerCostDisclosure =>
        "Automated access to this ERP is licensed separately from its interactive users. The customer needs an "
        + "indirect-access licence for the ERP installation and one named user licence for a dedicated integration "
        + "user. Both are quoted by the customer's ERP partner, paid by the customer and not included in this "
        + "product's price; the partner's current price list applies. The connector reads and writes only through "
        + "the ERP's service interface and governed stored queries, never directly from the ERP database, so no "
        + "further database access licence is needed for it. Webhook notifications need the ERP's notification "
        + "service installed and enabled at a release that supports it; without it the connector polls.";

    public ConnectorIntegrationUserRequirement IntegrationUser { get; } = new(
        "Create the dedicated integration user and assign its licence",
        "Create one ERP user for this connection only: never a person's login, never shared with another add-on or "
        + "interface. Assign it the named user licence covered by the indirect-access agreement, grant only the "
        + "authorisations for the documents the connection reads and writes, and keep its password out of "
        + "interactive expiry so that rotation follows the credential replace-by date instead. The connector holds "
        + "one long-lived session for it; a person logging in as this user ends that session.",
        "Integration user code as created in the ERP");
}
