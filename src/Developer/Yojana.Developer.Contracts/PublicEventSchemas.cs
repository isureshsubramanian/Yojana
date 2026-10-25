using System.Reflection;

namespace Yojana.Developer.Contracts;

/// <summary>Reads the immutable, embedded public-event catalogue schemas.</summary>
public static class PublicEventSchemas
{
    public static string Read(string type, string version = PublicEventTypes.CurrentVersion)
    {
        if (!PublicEventTypes.All.Contains(type, StringComparer.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(type), type, "The public event type is not catalogued.");
        var expected = $"Yojana.Developer.Contracts.Schemas.{version}/{type}.json";
        var assembly = typeof(PublicEventSchemas).Assembly;
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(name =>
            string.Equals(name, expected, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded public event schema '{expected}' was not found.");
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
