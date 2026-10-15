namespace Yojana.Serialisation.Contracts;

/// <summary>
/// Public surface of the Serialisation module (ADR-0001 bounded context). Commands and the thin events
/// listed in ADR-0001 "Integration boundaries and coupling" are added here from P1 onward. Other modules
/// reference this project only, never the implementation.
/// </summary>
public static class SerialisationContracts
{
    public const string ModuleName = "Serialisation";
}
