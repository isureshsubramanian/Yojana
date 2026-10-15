namespace Yojana.Integration.Contracts;

/// <summary>
/// Public surface of the Integration module (ADR-0002). The canonical contract, connector catalogue and
/// gateway commands are added from P8 onward. Connector adapters live under <c>Connectors/&lt;code&gt;/</c>
/// in the implementation project; that path is the only place protocol facts of an external system may appear.
/// </summary>
public static class IntegrationContracts
{
    public const string ModuleName = "Integration";
}
