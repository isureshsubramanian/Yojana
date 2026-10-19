using System.Reflection;

namespace Yojana.Integration.Contracts;

public static class IntegrationSchemaCatalog
{
    // MSBuild escapes numeric path segments in manifest resource names. Keep that packaging detail here so callers
    // continue to address the public contract directory as Schemas/1.0.
    private const string Prefix = "Yojana.Integration.Contracts.Schemas._1._0.";

    public static IReadOnlyList<string> Resources => typeof(IntegrationSchemaCatalog).Assembly
        .GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).ToList();

    public static string Read(string relativeName)
    {
        var resource = Prefix + relativeName.Replace('/', '.');
        using var stream = typeof(IntegrationSchemaCatalog).Assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException($"Embedded integration contract resource '{relativeName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
