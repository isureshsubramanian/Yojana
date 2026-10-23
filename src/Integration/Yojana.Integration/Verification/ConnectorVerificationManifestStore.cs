using System.Reflection;
using System.Text.Json;

namespace Yojana.Integration.Verification;

public sealed class ConnectorVerificationManifestStore
{
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<string, ConnectorVerificationManifest> manifests;

    public ConnectorVerificationManifestStore()
    {
        var assembly = typeof(ConnectorVerificationManifestStore).Assembly;
        manifests = new[] { "CX01", "CX02", "CX04" }.Select(code => Load(assembly, code))
            .ToDictionary(manifest => manifest.ConnectorCode, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> ConnectorCodes => manifests.Keys.OrderBy(code => code).ToList();

    public ConnectorVerificationManifest Get(string connectorCode) =>
        manifests.GetValueOrDefault(connectorCode)
        ?? throw new ConnectorAcceptanceRuleException("verification_manifest_not_found",
            "This connector does not ship a verification manifest.");

    private static ConnectorVerificationManifest Load(Assembly assembly, string code)
    {
        var name = assembly.GetManifestResourceNames().Single(resource =>
            resource.EndsWith($".Connectors.{code}.verification.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded verification manifest {code} was not found.");
        var manifest = JsonSerializer.Deserialize<ConnectorVerificationManifest>(stream, ManifestJson)
            ?? throw new InvalidOperationException($"Embedded verification manifest {code} is empty.");
        Validate(manifest, code);
        return manifest;
    }

    internal static void Validate(ConnectorVerificationManifest manifest, string expectedCode)
    {
        if (!string.Equals(manifest.ConnectorCode, expectedCode, StringComparison.Ordinal))
            throw new InvalidOperationException($"Verification manifest {expectedCode} has the wrong connector code.");
        if (manifest.Facts.Count == 0 || manifest.Facts.Select(fact => fact.Id).Distinct(StringComparer.Ordinal).Count()
            != manifest.Facts.Count)
            throw new InvalidOperationException($"Verification manifest {expectedCode} has no facts or duplicate ids.");
        foreach (var fact in manifest.Facts)
        {
            if (fact.Kind is not (ConnectorVerificationKind.Documented or ConnectorVerificationKind.Inferred
                    or ConnectorVerificationKind.Assumed))
                throw new InvalidOperationException($"Verification fact {fact.Id} has an unsupported kind.");
            if (fact.Kind == ConnectorVerificationKind.Assumed && !fact.AcceptanceItem)
                throw new InvalidOperationException($"Assumed verification fact {fact.Id} is not an acceptance item.");
        }
    }
}
