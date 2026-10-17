using System.Reflection;

namespace Yojana.Serialisation.Contracts;

/// <summary>The embedded JSON schemas of published and consumed integration contracts, by schema name and version.</summary>
public static class PublishedEventSchemas
{
    public static string Read(string schema, string version)
    {
        var name = $"Yojana.Serialisation.Contracts.Schemas.{version}/{schema.Replace(".", ".", StringComparison.Ordinal).ToLowerInvariant()}.json";
        var assembly = typeof(PublishedEventSchemas).Assembly;
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No embedded schema for {schema} {version} (looked for {name}).");
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<string> Names() => typeof(PublishedEventSchemas).Assembly.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.Ordinal)).ToList();
}
