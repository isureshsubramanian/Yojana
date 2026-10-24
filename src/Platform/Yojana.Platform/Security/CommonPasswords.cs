using System.Text;

namespace Yojana.Platform.Security;

/// <summary>
/// The most common passwords (99,995 of the top 100,000) (ADR-0008 §6), lower-cased. Source: Mark Burnett's ten-million-password set,
/// released by him to the public domain in 2015, as published top-100,000 in the SecLists collection (MIT); the notice is
/// in THIRD-PARTY-NOTICES.txt via tools/notices-input/components/seclists-common-passwords.json. The file is the
/// download with five lines removed (other vendors' brand names, which the source tree may not contain); the notices
/// input records both checksums so the copy can be checked against its source.
/// </summary>
public static class CommonPasswords
{
    public const string ResourceName = "common-passwords-100k.txt";

    private static readonly Lazy<HashSet<string>> List = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static int Count => List.Value.Count;

    public static bool Contains(string lowered) => List.Value.Contains(lowered);

    // The list is an assembly resource already mapped into memory, so reading it performs no I/O to await.
    private static HashSet<string> Load()
    {
        using var stream = typeof(CommonPasswords).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) set.Add(line.ToLowerInvariant());
        return set;
    }
}
