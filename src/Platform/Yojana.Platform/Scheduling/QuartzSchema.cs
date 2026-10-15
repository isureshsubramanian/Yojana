using System.Reflection;

namespace Yojana.Platform.Scheduling;

/// <summary>Reads the vendored Quartz.NET table script (embedded resource) and splits it into batches on GO lines.</summary>
public static class QuartzSchema
{
    public static IReadOnlyList<string> Batches()
    {
        using var stream = typeof(QuartzSchema).Assembly.GetManifestResourceStream("quartz_tables_sqlServer.sql")
            ?? throw new InvalidOperationException("Embedded resource quartz_tables_sqlServer.sql is missing.");
        using var reader = new StreamReader(stream);
        var batches = new List<string>();
        var current = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                continue;
            }

            if (line.TrimStart().StartsWith("USE ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            current.Add(line);
        }

        Flush();
        return batches;

        void Flush()
        {
            var text = string.Join('\n', current).Trim();
            if (text.Length > 0 && !text.Split('\n').All(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal) || l.Trim().Length == 0))
            {
                batches.Add(text);
            }

            current.Clear();
        }
    }
}
