using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Yojana.Gates;

public sealed class NameAuditConfig
{
    /// <summary>Base64 of a newline-separated list, so the names themselves never appear as literals in the source tree.</summary>
    [JsonPropertyName("denyListFile")]
    public string DenyListFile { get; set; } = "name-denylist.b64";

    [JsonPropertyName("scanRoots")]
    public List<string> ScanRoots { get; set; } = [];

    [JsonPropertyName("excludeDirectories")]
    public List<string> ExcludeDirectories { get; set; } = [];

    [JsonPropertyName("excludeFiles")]
    public List<string> ExcludeFiles { get; set; } = [];

    /// <summary>Repository-relative prefixes where protocol facts of an external system may be named (connector adapters, migration staging).</summary>
    [JsonPropertyName("allowedPathPrefixes")]
    public List<string> AllowedPathPrefixes { get; set; } = [];

    public static NameAuditConfig Load(string root)
    {
        var path = Path.Combine(root, "tools", "gates", "name-audit.json");
        return JsonSerializer.Deserialize<NameAuditConfig>(File.ReadAllText(path)) ?? throw new GateException("Cannot read " + path);
    }

    public IReadOnlyList<string> DenyList(string root)
    {
        var path = Path.Combine(root, "tools", "gates", DenyListFile);
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(path).Trim()));
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => !l.StartsWith('#')).ToList();
    }
}

/// <summary>
/// Competitor and external-product name audit (ADR-0001 AI 16, ADR-0002 AI 13): a case-insensitive,
/// word-bounded search over the source tree. Matches are allowed only under the configured prefixes.
/// </summary>
public static class NameAuditGate
{
    public static GateResult Run(string root) => Run(root, NameAuditConfig.Load(root));

    public static GateResult Run(string root, NameAuditConfig config)
    {
        var patterns = config.DenyList(root).Select(name => new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(name) + @"(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
        var excludeDirs = new HashSet<string>(config.ExcludeDirectories, StringComparer.OrdinalIgnoreCase);
        var excludeFiles = new HashSet<string>(config.ExcludeFiles.Select(f => f.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
        var hits = new List<string>();
        var scanned = 0;
        foreach (var scanRoot in config.ScanRoots)
        {
            var full = Path.Combine(root, scanRoot);
            IEnumerable<string> files = Directory.Exists(full) ? Repo.TextFiles(full, excludeDirs) : File.Exists(full) ? [full] : [];
            foreach (var file in files)
            {
                var relative = Repo.Relative(root, file);
                if (excludeFiles.Contains(relative) || Repo.LooksBinary(file))
                {
                    continue;
                }

                scanned++;
                var allowed = config.AllowedPathPrefixes.Any(p => relative.StartsWith(p.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
                if (allowed)
                {
                    continue;
                }

                var lineNumber = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNumber++;
                    foreach (var pattern in patterns)
                    {
                        var match = pattern.Match(line);
                        if (match.Success)
                        {
                            hits.Add($"{relative}:{lineNumber}: '{Mask(match.Value)}' ({line.Trim().Length} chars on the line)");
                        }
                    }
                }
            }
        }

        return new GateResult("names", hits.Count == 0, $"{scanned} files scanned, {hits.Count} disallowed name(s)", hits);
    }

    /// <summary>The report must not re-emit the name in full (it would land in CI logs and build output).</summary>
    private static string Mask(string value) => value.Length <= 2 ? "**" : value[0] + new string('*', value.Length - 2) + value[^1];
}
