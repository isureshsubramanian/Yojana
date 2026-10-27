using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Identity;

/// <summary>Code defaults and registrations for development, demo data and tests.</summary>
[DevelopmentOnly]
public sealed class InMemorySerialSchemes : ISerialSchemeSource
{
    /// <summary>A plain eight-digit counter, no block.</summary>
    public static readonly SerialSchemeDefinition Default =
        new("internal", IdentifierType.InternalSerial, "", null, "", 8);

    private readonly Dictionary<string, SerialSchemeDefinition> schemes =
        new(StringComparer.OrdinalIgnoreCase) { [Default.Code] = Default };

    public void Register(SerialSchemeDefinition scheme) => schemes[scheme.Code] = scheme;

    public SerialSchemeDefinition? Find(string code) => schemes.TryGetValue(code, out var scheme) ? scheme : null;

    public IReadOnlyList<SerialSchemeDefinition> All() => schemes.Values.OrderBy(scheme => scheme.Code).ToList();

    public SerialSchemeDefinition? Recognise(string normalisedValue)
    {
        var matches = schemes.Values.Where(scheme => scheme.TryParse(normalisedValue) is not null).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
