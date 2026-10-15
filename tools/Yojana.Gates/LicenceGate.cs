using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yojana.Gates;

public sealed class LicencePolicy
{
    [JsonPropertyName("allowedExpressions")]
    public List<string> AllowedExpressions { get; set; } = [];

    [JsonPropertyName("scanDirectories")]
    public List<string> ScanDirectories { get; set; } = ["src", "tests", "tools"];

    /// <summary>npm lockfiles whose whole graph (development tooling included) is under the policy.</summary>
    [JsonPropertyName("npmLockFiles")]
    public List<string> NpmLockFiles { get; set; } = [];

    [JsonPropertyName("exceptions")]
    public List<LicenceException> Exceptions { get; set; } = [];

    /// <summary>Old packages carry only a licenseUrl. Each URL here was opened and read; the value is the SPDX id of the text found there.</summary>
    [JsonPropertyName("knownLicenceUrls")]
    public Dictionary<string, KnownLicenceUrl> KnownLicenceUrls { get; set; } = [];

    public static LicencePolicy Load(string root)
    {
        var path = Path.Combine(root, "tools", "gates", "licence-policy.json");
        return JsonSerializer.Deserialize<LicencePolicy>(File.ReadAllText(path)) ?? throw new GateException("Cannot read " + path);
    }
}

public sealed class KnownLicenceUrl
{
    [JsonPropertyName("spdx")]
    public string Spdx { get; set; } = "";

    [JsonPropertyName("readOn")]
    public string ReadOn { get; set; } = "";
}

/// <summary>A package that is not covered by the allow-list and whose use the owner has decided on explicitly.</summary>
public sealed class LicenceException
{
    [JsonPropertyName("package")]
    public string Package { get; set; } = "";

    [JsonPropertyName("licence")]
    public string Licence { get; set; } = "";

    [JsonPropertyName("why")]
    public string Why { get; set; } = "";

    /// <summary>pending (fails the gate until decided), approved or rejected.</summary>
    [JsonPropertyName("decision")]
    public string Decision { get; set; } = "pending";

    [JsonPropertyName("decisionRef")]
    public string? DecisionRef { get; set; }

    [JsonPropertyName("approvedBy")]
    public string? ApprovedBy { get; set; }

    [JsonPropertyName("approvedOn")]
    public string? ApprovedOn { get; set; }
}

/// <summary>Answers "may we use this": every restored package carries an allow-listed SPDX expression or an approved exception.</summary>
public static class LicenceGate
{
    public static GateResult Run(string root)
    {
        var policy = LicencePolicy.Load(root);
        var packages = Packages.FromAssetsFiles(root, policy.ScanDirectories);
        foreach (var lockFile in policy.NpmLockFiles)
        {
            foreach (var (key, info) in Packages.FromNpmLock(root, lockFile, includeDev: true))
            {
                packages[key] = info;
            }
        }

        return Evaluate(policy, packages);
    }

    public static GateResult Evaluate(LicencePolicy policy, IReadOnlyDictionary<string, PackageInfo> packages)
    {
        var allowed = new HashSet<string>(policy.AllowedExpressions, StringComparer.OrdinalIgnoreCase);
        var details = new List<string>();
        var failures = 0;
        foreach (var package in packages.Values)
        {
            var exception = policy.Exceptions.FirstOrDefault(e => string.Equals(e.Package, package.Id, StringComparison.OrdinalIgnoreCase));
            if (exception is not null)
            {
                switch (exception.Decision)
                {
                    case "approved" when !string.IsNullOrWhiteSpace(exception.ApprovedBy) && !string.IsNullOrWhiteSpace(exception.ApprovedOn):
                        details.Add($"ok       {package.Key}: exception approved by {exception.ApprovedBy} on {exception.ApprovedOn} ({exception.Licence})");
                        continue;
                    case "approved":
                        failures++;
                        details.Add($"FAIL     {package.Key}: exception marked approved without approvedBy/approvedOn");
                        continue;
                    case "pending":
                        failures++;
                        details.Add($"PENDING  {package.Key}: {exception.Licence}; needs owner decision {exception.DecisionRef ?? "(unreferenced)"}; why: {exception.Why}");
                        continue;
                    default:
                        failures++;
                        details.Add($"FAIL     {package.Key}: exception decision '{exception.Decision}' ({exception.Licence})");
                        continue;
                }
            }

            var expression = package.LicenseExpression;
            if (expression is null && package.LicenseFile is null && package.LicenseUrl is not null
                && policy.KnownLicenceUrls.TryGetValue(package.LicenseUrl, out var known))
            {
                expression = known.Spdx;
            }

            if (expression is null && package.Ecosystem == "npm" && package.LicenseFile is null && package.Folder.Length == 0)
            {
                failures++;
                details.Add($"FAIL     {package.Key}: optional package not installed on this OS and the lockfile records no licence for it");
                continue;
            }

            if (expression is null)
            {
                failures++;
                var what = package.LicenseFile is not null ? $"licence is a file ({package.LicenseFile}), not an SPDX expression" : $"no licence expression in the manifest (licenseUrl: {package.LicenseUrl ?? "none"})";
                details.Add($"FAIL     {package.Key}: {what}; read it and add an exception with the owner's decision");
                continue;
            }

            var ids = SpdxIds(expression);
            var disallowed = ids.Where(id => !allowed.Contains(id)).ToList();
            if (disallowed.Count > 0)
            {
                failures++;
                details.Add($"FAIL     {package.Key}: {expression} (not allowed: {string.Join(", ", disallowed)})");
            }
        }

        var summary = $"{packages.Count} packages checked, {failures} not allowed" + (failures == 0 ? "" : " (see details)");
        return new GateResult("licences", failures == 0, summary, failures == 0 ? [] : details.Where(d => !d.StartsWith("ok", StringComparison.Ordinal)).ToList());
    }

    /// <summary>Every identifier in a compound SPDX expression must be allowed (an OR is not resolved in our favour automatically).</summary>
    public static IReadOnlyList<string> SpdxIds(string expression) =>
        expression.Replace("(", " ").Replace(")", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !t.Equals("OR", StringComparison.OrdinalIgnoreCase) && !t.Equals("AND", StringComparison.OrdinalIgnoreCase) && !t.Equals("WITH", StringComparison.OrdinalIgnoreCase))
            .ToList();
}
