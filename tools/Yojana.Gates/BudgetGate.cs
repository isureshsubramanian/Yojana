using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Yojana.Gates;

public sealed class BudgetConfig
{
    [JsonPropertyName("distDirectory")]
    public string DistDirectory { get; set; } = "web/dist";

    [JsonPropertyName("initialJsGzipBytes")]
    public long InitialJsGzipBytes { get; set; } = 250 * 1024;

    [JsonPropertyName("cssGzipBytes")]
    public long CssGzipBytes { get; set; } = 60 * 1024;

    /// <summary>Client sources the lint rules below scan (.ts and .tsx).</summary>
    [JsonPropertyName("sourceDirectory")]
    public string SourceDirectory { get; set; } = "web/src";

    /// <summary>Module specifiers that bring a CSS-in-JS runtime or theme-specific JavaScript; importing one fails the gate.</summary>
    [JsonPropertyName("forbiddenImports")]
    public List<string> ForbiddenImports { get; set; } = [];

    /// <summary>The only folder whose scripts may read or write the theme and mode attributes.</summary>
    [JsonPropertyName("themeModuleDirectory")]
    public string ThemeModuleDirectory { get; set; } = "web/src/theme/";

    public static BudgetConfig Load(string root)
    {
        var path = Path.Combine(root, "tools", "gates", "budgets.json");
        return JsonSerializer.Deserialize<BudgetConfig>(File.ReadAllText(path)) ?? throw new GateException("Cannot read " + path);
    }
}

/// <summary>
/// MOD-02 performance rules: the gzipped size of what the shell's index.html loads before first paint
/// (stylesheets, the entry script and its module preloads), plus two source rules from MOCKUP-PROCESS §4:
/// no CSS-in-JS runtime and no theme-specific JavaScript outside the theme module.
/// </summary>
public static partial class BudgetGate
{
    public static GateResult Run(string root) => Run(root, BudgetConfig.Load(root));

    public static GateResult Run(string root, BudgetConfig config)
    {
        var details = new List<string>();
        var ruleHits = SourceRules(root, config, details);

        var dist = Path.Combine(root, config.DistDirectory);
        var index = Path.Combine(dist, "index.html");
        if (!File.Exists(index))
        {
            details.Add($"no client build at {config.DistDirectory}/index.html; sizes not measured (run `make web`)");
            return new GateResult("budget", ruleHits == 0, ruleHits == 0 ? "source rules pass; no build to measure" : $"{ruleHits} source rule violation(s)", details);
        }

        var html = File.ReadAllText(index);
        long js = 0, css = 0;
        foreach (Match m in ScriptSrc().Matches(html))
        {
            var size = Gzipped(dist, m.Groups["src"].Value);
            js += size;
            details.Add($"js  {size,9:N0} B gz  {m.Groups["src"].Value}");
        }

        foreach (Match m in ModulePreload().Matches(html))
        {
            var size = Gzipped(dist, m.Groups["href"].Value);
            js += size;
            details.Add($"js  {size,9:N0} B gz  {m.Groups["href"].Value} (modulepreload)");
        }

        foreach (Match m in StylesheetHref().Matches(html))
        {
            var size = Gzipped(dist, m.Groups["href"].Value);
            css += size;
            details.Add($"css {size,9:N0} B gz  {m.Groups["href"].Value}");
        }

        var withinBudget = js <= config.InitialJsGzipBytes && css <= config.CssGzipBytes;
        details.Add($"initial JS {js:N0} B gz (budget {config.InitialJsGzipBytes:N0}); CSS {css:N0} B gz (budget {config.CssGzipBytes:N0})");
        var ok = withinBudget && ruleHits == 0;
        return new GateResult("budget", ok, ok ? "within budget; source rules pass" : (withinBudget ? "" : "over budget") + (ruleHits > 0 ? $" {ruleHits} source rule violation(s)" : ""), details);
    }

    private static int SourceRules(string root, BudgetConfig config, List<string> details)
    {
        var source = Path.Combine(root, config.SourceDirectory);
        if (!Directory.Exists(source))
        {
            return 0;
        }

        var hits = 0;
        var themeDir = config.ThemeModuleDirectory.Replace('\\', '/').TrimEnd('/') + "/";
        foreach (var file in Directory.EnumerateFiles(source, "*.ts*", SearchOption.AllDirectories))
        {
            var relative = Repo.Relative(root, file);
            var inThemeModule = relative.StartsWith(themeDir, StringComparison.OrdinalIgnoreCase);
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                foreach (var forbidden in config.ForbiddenImports)
                {
                    if (line.Contains($"\"{forbidden}\"", StringComparison.Ordinal) || line.Contains($"'{forbidden}'", StringComparison.Ordinal) || line.Contains($"\"{forbidden}/", StringComparison.Ordinal))
                    {
                        hits++;
                        details.Add($"RULE {relative}:{lineNumber}: imports '{forbidden}' (CSS-in-JS or theme runtime is not allowed; theming is tokens only)");
                    }
                }

                if (!inThemeModule && ThemeAttribute().IsMatch(line))
                {
                    hits++;
                    details.Add($"RULE {relative}:{lineNumber}: touches the theme or mode attribute outside {themeDir}");
                }
            }
        }

        return hits;
    }

    private static long Gzipped(string dist, string reference)
    {
        var path = Path.Combine(dist, reference.TrimStart('/').Split('?')[0]);
        if (!File.Exists(path))
        {
            return 0;
        }

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var input = File.OpenRead(path))
        {
            input.CopyTo(gzip);
        }

        return output.Length;
    }

    [GeneratedRegex("<script[^>]*src=\"(?<src>[^\"]+)\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptSrc();

    [GeneratedRegex("<link[^>]*rel=\"modulepreload\"[^>]*href=\"(?<href>[^\"]+)\"[^>]*>|<link[^>]*href=\"(?<href>[^\"]+)\"[^>]*rel=\"modulepreload\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ModulePreload();

    [GeneratedRegex("<link[^>]*rel=\"stylesheet\"[^>]*href=\"(?<href>[^\"]+)\"[^>]*>|<link[^>]*href=\"(?<href>[^\"]+)\"[^>]*rel=\"stylesheet\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex StylesheetHref();

    [GeneratedRegex(@"data-(theme|mode)\b|dataset\.(theme|mode)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ThemeAttribute();
}
