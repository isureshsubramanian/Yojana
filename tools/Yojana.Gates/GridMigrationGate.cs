using System.Text.RegularExpressions;

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
        var literals = new HashSet<string>(StringComparer.Ordinal);
        var target = web;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line == "[web]") { target = web; continue; }
            if (line == "[server]") { target = server; continue; }
            if (line == "[literal-take]") { target = literals; continue; }
            target.Add(line);
        }
        return Evaluate(root, web, server, literals);
    }

    internal static GateResult Evaluate(string root, IReadOnlySet<string> webBaseline, IReadOnlySet<string> serverBaseline,
        IReadOnlySet<string>? literalBaseline = null)
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
        var literalIssues = LiteralTakeViolations(root);
        var literalIds = literalIssues.Select(issue => issue.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var issue in literalIssues.Where(issue => literalBaseline is null || !literalBaseline.Contains(issue.Id))) details.Add(issue.Detail);
        if (literalBaseline is not null)
            foreach (var stale in literalBaseline.Except(literalIds).Order()) details.Add($"STALE {stale}: remove this fixed or deleted literal take from {BaselineRelativePath}");
        var passed = details.Count == 0;
        return new GateResult("grid migration", passed, passed ? $"shared paging rules pass; {web.Count + server.Count} grid baseline file(s) and {literalIds.Count} literal-take baseline call(s) remain" : $"{details.Count} grid migration violation(s)", details);
    }

    internal static IReadOnlyList<(string Id, string Detail)> LiteralTakeViolations(string root)
    {
        var sortableRoutes = SortableRoutes(root);
        if (sortableRoutes.Count == 0 || !Directory.Exists(Path.Combine(root, "web", "src"))) return [];
        var details = new List<(string Id, string Detail)>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "web", "src"), "*.*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".ts", StringComparison.Ordinal) || path.EndsWith(".tsx", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(path);
            var values = TypeScriptTemplates(text);
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (!Regex.IsMatch(line, @"\btake\s*:\s*\d+\b") || line.Contains("// grid-gate: picker", StringComparison.Ordinal)) continue;
                var route = ClientRoute(line, values);
                if (route is null || !sortableRoutes.Contains(route) || HasSortAndDirection(line)) continue;
                var id = $"{Repo.Relative(root, path)}:{index + 1}";
                details.Add((id, $"RULE {id}: literal take for sortable list {route}; send sort, dir and the selected page size, or mark a genuine picker with // grid-gate: picker"));
            }
        }
        return details;
    }

    private static bool HasSortAndDirection(string line) =>
        Regex.IsMatch(line, @"\bsort\s*:") && Regex.IsMatch(line, @"\bdir\s*:");

    private static HashSet<string> SortableRoutes(string root)
    {
        var routes = new HashSet<string>(StringComparer.Ordinal);
        var source = Path.Combine(root, "src");
        if (!Directory.Exists(source)) return routes;
        foreach (var path in Directory.EnumerateFiles(source, "*Endpoints.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            var groups = new Dictionary<string, string>(StringComparer.Ordinal) { ["app"] = "" };
            var pending = Regex.Matches(text, "var\\s+(?<name>\\w+)\\s*=\\s*(?<parent>\\w+)\\.MapGroup\\(\\\"(?<route>[^\\\"]*)\\\"\\)")
                .Cast<Match>().ToList();
            while (pending.Count > 0)
            {
                var added = pending.RemoveAll(match =>
                {
                    if (!groups.TryGetValue(match.Groups["parent"].Value, out var parent)) return false;
                    groups[match.Groups["name"].Value] = NormaliseRoute(parent + match.Groups["route"].Value);
                    return true;
                });
                if (added == 0) break;
            }
            foreach (Match match in Regex.Matches(text,
                         "(?<receiver>\\w+)\\.MapGet\\(\\\"(?<route>[^\\\"]*)\\\"\\s*,\\s*(?:async\\s*)?\\((?<arguments>.*?)\\)\\s*=>",
                         RegexOptions.Singleline))
            {
                var arguments = match.Groups["arguments"].Value;
                if (!arguments.Contains("string? sort", StringComparison.Ordinal)
                    || !arguments.Contains("string? dir", StringComparison.Ordinal)
                    || !arguments.Contains("int? take", StringComparison.Ordinal)
                    || !groups.TryGetValue(match.Groups["receiver"].Value, out var prefix)) continue;
                routes.Add(NormaliseRoute(prefix + match.Groups["route"].Value));
            }
        }
        return routes;
    }

    private static Dictionary<string, string> TypeScriptTemplates(string text)
    {
        var values = Regex.Matches(text, "const\\s+(?<name>\\w+)\\s*=\\s*\\\"(?<value>/[^\\\"]*)\\\"\\s*;")
            .Cast<Match>().ToDictionary(match => match.Groups["name"].Value,
                match => match.Groups["value"].Value, StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text,
                     @"const\s+(?<name>\w+)\s*=\s*\([^)]*\)\s*=>\s*`(?<value>[^`]*)`\s*;"))
            values[match.Groups["name"].Value] = match.Groups["value"].Value;
        return values;
    }

    private static string? ClientRoute(string line, Dictionary<string, string> values)
    {
        var start = line.IndexOf('`');
        if (start < 0) return null;
        var end = line.IndexOf('`', start + 1);
        if (end < 0) return null;
        var template = line[(start + 1)..end];
        var query = Regex.Match(template, @"\$\{(?:query|qs|q)\(");
        if (query.Success) template = template[..query.Index];
        for (var pass = 0; pass < 4; pass++)
        {
            template = Regex.Replace(template, @"\$\{(?<name>\w+)\}", match =>
                values.GetValueOrDefault(match.Groups["name"].Value, "{}"));
            template = Regex.Replace(template, @"\$\{(?<name>\w+)\([^}]*\)\}", match =>
                values.TryGetValue(match.Groups["name"].Value, out var value) ? value : "{}");
        }
        template = Regex.Replace(template, @"\$\{[^}]+\}", "{}");
        return template.StartsWith('/') ? NormaliseRoute(template) : null;
    }

    private static string NormaliseRoute(string route)
    {
        route = Regex.Replace(route, @"\{[^}]+\}", "{}");
        route = Regex.Replace(route, "/+", "/");
        return route.Length > 1 ? route.TrimEnd('/') : route;
    }

    private static void Compare(string rule, IReadOnlySet<string> actual, IReadOnlySet<string> baseline, List<string> details)
    {
        foreach (var path in actual.Except(baseline).Order()) details.Add($"RULE {path}: {rule}; use the ADR-0021 shared component (new files cannot enter the baseline)");
        foreach (var path in baseline.Except(actual).Order()) details.Add($"STALE {path}: remove this migrated or deleted file from {BaselineRelativePath}");
    }
}
