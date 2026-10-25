namespace Yojana.Gates;

/// <summary>ADR-0021 rollout gate: new paged grids and list queries must use the shared paging seams.</summary>
public static class GridMigrationGate
{
    public const string BaselineRelativePath = "tools/gates/grid-sort-baseline.txt";

    public static GateResult Run(string root)
    {
        var path = Path.Combine(root, BaselineRelativePath);
        if (!File.Exists(path)) throw new GateException($"Missing {BaselineRelativePath}.");
        var web = new HashSet<string>(StringComparer.Ordinal);
        var server = new HashSet<string>(StringComparer.Ordinal);
        var target = web;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line == "[web]") { target = web; continue; }
            if (line == "[server]") { target = server; continue; }
            target.Add(line);
        }
        return Evaluate(root, web, server);
    }

    internal static GateResult Evaluate(string root, IReadOnlySet<string> webBaseline, IReadOnlySet<string> serverBaseline)
    {
        var web = Directory.Exists(Path.Combine(root, "web", "src"))
            ? Directory.EnumerateFiles(Path.Combine(root, "web", "src"), "*.tsx", SearchOption.AllDirectories)
                .Where(path => File.ReadAllText(path).Contains("<DataGrid", StringComparison.Ordinal))
                .Where(path => { var text = File.ReadAllText(path); return !text.Contains("useGridQuery", StringComparison.Ordinal) && !text.Contains("useGridState", StringComparison.Ordinal) && !text.Contains("footer=", StringComparison.Ordinal); })
                .Select(path => Repo.Relative(root, path)).ToHashSet(StringComparer.Ordinal)
            : [];
        var server = Directory.Exists(Path.Combine(root, "src"))
            ? Directory.EnumerateFiles(Path.Combine(root, "src"), "*Endpoints.cs", SearchOption.AllDirectories)
                .Where(path => { var text = File.ReadAllText(path); return (text.Contains("string? sort", StringComparison.Ordinal) || text.Contains("int? take", StringComparison.Ordinal)) && !text.Contains("SortedPage<", StringComparison.Ordinal); })
                .Select(path => Repo.Relative(root, path)).ToHashSet(StringComparer.Ordinal)
            : [];
        var details = new List<string>();
        Compare("paged DataGrid without shared grid state", web, webBaseline, details);
        Compare("sort/take reader without SortedPage", server, serverBaseline, details);
        var passed = details.Count == 0;
        return new GateResult("grid migration", passed, passed ? $"shared paging rules pass; {web.Count + server.Count} baseline file(s) remain" : $"{details.Count} grid migration violation(s)", details);
    }

    private static void Compare(string rule, IReadOnlySet<string> actual, IReadOnlySet<string> baseline, List<string> details)
    {
        foreach (var path in actual.Except(baseline).Order()) details.Add($"RULE {path}: {rule}; use the ADR-0021 shared component (new files cannot enter the baseline)");
        foreach (var path in baseline.Except(actual).Order()) details.Add($"STALE {path}: remove this migrated or deleted file from {BaselineRelativePath}");
    }
}
