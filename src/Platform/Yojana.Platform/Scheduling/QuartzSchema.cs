using System.Reflection;

namespace Yojana.Platform.Scheduling;

/// <summary>Reads the vendored Quartz.NET scripts (embedded resources) and splits them into batches on GO lines.</summary>
public static class QuartzSchema
{
    /// <summary>
    /// Quartz.NET 4.6 required migrations, in the order Quartz lists them. Each statement checks before it alters, so a
    /// database whose tables were already created at 4.6 runs them as no-ops.
    /// </summary>
    public static readonly IReadOnlyList<string> Upgrade46Scripts =
    [
        "quartz_4.6_add_scheduler_version_sqlServer.sql",
        "quartz_4.6_add_fired_trigger_interrupt_sqlServer.sql",
        "quartz_4.6_add_trigger_misfire_threshold_sqlServer.sql",
    ];

    /// <summary>The job-store tables, as the platform Initial migration applies them.</summary>
    public static IReadOnlyList<string> Batches() => Batches("quartz_tables_sqlServer.sql");

    public static IReadOnlyList<string> Upgrade46Batches() => Upgrade46Scripts.SelectMany(Batches).ToList();

    public static IReadOnlyList<string> Batches(string resourceName)
    {
        using var stream = typeof(QuartzSchema).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource {resourceName} is missing.");
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
