using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yojana.Gates;

/// <summary>Generator input that cannot come from a package: licence text taken from a repository, with its source and date.</summary>
public sealed class RepositoryInput
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("matchRepositoryUrls")]
    public List<string> MatchRepositoryUrls { get; set; } = [];

    [JsonPropertyName("licenceFile")]
    public string LicenceFile { get; set; } = "";

    [JsonPropertyName("noticeFile")]
    public string? NoticeFile { get; set; }

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; set; } = "";

    [JsonPropertyName("noticeSourceUrl")]
    public string? NoticeSourceUrl { get; set; }

    [JsonPropertyName("takenOn")]
    public string TakenOn { get; set; } = "";
}

/// <summary>A component that is not a NuGet package (shared frameworks, vendored files) and must still be credited.</summary>
public sealed class ComponentInput
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("licence")]
    public string Licence { get; set; } = "";

    [JsonPropertyName("copyright")]
    public string? Copyright { get; set; }

    [JsonPropertyName("projectUrl")]
    public string? ProjectUrl { get; set; }

    [JsonPropertyName("licenceFile")]
    public string LicenceFile { get; set; } = "";

    [JsonPropertyName("noticeFile")]
    public string? NoticeFile { get; set; }

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; set; } = "";

    [JsonPropertyName("noticeSourceUrl")]
    public string? NoticeSourceUrl { get; set; }

    [JsonPropertyName("takenOn")]
    public string TakenOn { get; set; } = "";

    [JsonPropertyName("why")]
    public string? Why { get; set; }
}

/// <summary>
/// Answers "did we credit it": writes THIRD-PARTY-NOTICES.txt for everything the artefact ships. Text comes,
/// in order, from the package itself, from a repository input (URL and date recorded), or from the SPDX
/// standard text with its placeholders left unfilled. A package with none of the three fails the gate.
/// </summary>
public static class NoticesGate
{
    public const string OutputRelativePath = "src/Web/Yojana.Web/Notices/THIRD-PARTY-NOTICES.txt";
    public const string InputRelativePath = "tools/notices-input";

    public static GateResult Run(string root, string? depsPath, bool check)
    {
        depsPath ??= FindDeps(root);
        var inputs = Path.Combine(root, InputRelativePath);
        var packages = Packages.FromDepsFile(depsPath);
        var policy = LicencePolicy.Load(root);
        foreach (var lockFile in policy.NpmLockFiles)
        {
            // Only what the browser receives: production dependencies of the client, never its build tooling.
            foreach (var (key, info) in Packages.FromNpmLock(root, lockFile, includeDev: false))
            {
                packages[key] = info;
            }
        }

        var (text, problems) = Generate(packages, inputs, Repo.Relative(root, depsPath) + (policy.NpmLockFiles.Count > 0 ? " and " + string.Join(", ", policy.NpmLockFiles) : ""));
        if (problems.Count > 0)
        {
            return new GateResult("notices", false, $"{problems.Count} component(s) without a usable notice source", problems);
        }

        var output = Path.Combine(root, OutputRelativePath);
        if (check)
        {
            var current = File.Exists(output) ? Repo.Normalise(File.ReadAllText(output)) : "";
            if (current != text)
            {
                return new GateResult("notices", false, "committed notices file differs from the generated one; run `make notices` and commit the result",
                    [Repo.Relative(root, output), $"{packages.Count} packages in {Repo.Relative(root, depsPath)}"]);
            }

            return new GateResult("notices", true, $"{packages.Count} packages, notices file up to date", []);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, text, Repo.Utf8NoBom);
        return new GateResult("notices", true, $"wrote {Repo.Relative(root, output)} for {packages.Count} packages", []);
    }

    public static string FindDeps(string root)
    {
        var candidates = Directory.Exists(Path.Combine(root, "src", "Web", "Yojana.Web", "bin"))
            ? Directory.EnumerateFiles(Path.Combine(root, "src", "Web", "Yojana.Web", "bin"), "Yojana.Web.deps.json", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).ToList()
            : [];
        return candidates.FirstOrDefault() ?? throw new GateException("No Yojana.Web.deps.json under src/Web/Yojana.Web/bin; build the host first or pass --deps.");
    }

    public static (string Text, List<string> Problems) Generate(IReadOnlyDictionary<string, PackageInfo> packages, string inputsFolder, string depsLabel)
    {
        var repositories = LoadAll<RepositoryInput>(Path.Combine(inputsFolder, "repositories"));
        var components = LoadAll<ComponentInput>(Path.Combine(inputsFolder, "components"));
        var problems = new List<string>();
        var sections = new List<(string Title, string Body)>();

        foreach (var component in components.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var body = new StringBuilder();
            body.AppendLine("Licence: " + component.Licence);
            if (component.Copyright is not null)
            {
                body.AppendLine("Copyright: " + component.Copyright);
            }

            if (component.ProjectUrl is not null)
            {
                body.AppendLine("Project: " + component.ProjectUrl);
            }

            if (component.Why is not null)
            {
                body.AppendLine("Included because: " + component.Why);
            }

            body.AppendLine($"Licence text taken from {component.SourceUrl} on {component.TakenOn}.");
            body.AppendLine();
            body.Append(ReadInput(inputsFolder, component.LicenceFile, problems, component.Name));
            if (component.NoticeFile is not null)
            {
                body.AppendLine();
                body.AppendLine($"NOTICE text taken from {component.NoticeSourceUrl} on {component.TakenOn}:");
                body.AppendLine();
                body.Append(ReadInput(inputsFolder, component.NoticeFile, problems, component.Name));
            }

            sections.Add((component.Name, body.ToString()));
        }

        foreach (var package in packages.Values.OrderBy(p => p.Ecosystem, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Version, StringComparer.Ordinal))
        {
            var body = new StringBuilder();
            body.AppendLine("Licence: " + (package.LicenseExpression ?? (package.LicenseFile is not null ? "see licence text below (file " + package.LicenseFile + " in the package)" : "not stated in the package manifest")));
            if (!string.IsNullOrWhiteSpace(package.Copyright))
            {
                body.AppendLine(package.Copyright.StartsWith("Author ", StringComparison.Ordinal) ? package.Copyright : "Copyright (as declared in the package manifest): " + package.Copyright);
            }

            if (!string.IsNullOrWhiteSpace(package.ProjectUrl))
            {
                body.AppendLine("Project: " + package.ProjectUrl);
            }

            if (!string.IsNullOrWhiteSpace(package.RepositoryUrl))
            {
                body.AppendLine("Repository: " + package.RepositoryUrl);
            }

            body.AppendLine();

            if (package.BundledLicenceFiles.Count > 0)
            {
                foreach (var file in package.BundledLicenceFiles)
                {
                    body.AppendLine($"Licence text as shipped in the package ({file}):");
                    body.AppendLine();
                    body.Append(Repo.Normalise(File.ReadAllText(Path.Combine(package.Folder, file))));
                }
            }
            else if (repositories.FirstOrDefault(r => Matches(r, package.RepositoryUrl)) is { } repo)
            {
                body.AppendLine($"The package carries only a licence identifier. Licence text taken from {repo.SourceUrl} on {repo.TakenOn}:");
                body.AppendLine();
                body.Append(ReadInput(inputsFolder, repo.LicenceFile, problems, package.Key));
                if (repo.NoticeFile is not null)
                {
                    body.AppendLine();
                    body.AppendLine($"NOTICE text taken from {repo.NoticeSourceUrl} on {repo.TakenOn}:");
                    body.AppendLine();
                    body.Append(ReadInput(inputsFolder, repo.NoticeFile, problems, package.Key));
                }
            }
            else if (package.LicenseExpression is not null && LicenceGate.SpdxIds(package.LicenseExpression) is { Count: 1 } ids
                     && File.Exists(Path.Combine(inputsFolder, "spdx", ids[0] + ".txt")))
            {
                body.AppendLine($"The package carries only the licence identifier {ids[0]} and its repository publishes no licence text that the generator knows. The SPDX standard text follows with its copyright placeholders left unfilled; the copyright holder is the one declared in the package manifest above.");
                body.AppendLine();
                body.Append(ReadInput(inputsFolder, Path.Combine("spdx", ids[0] + ".txt"), problems, package.Key));
            }
            else
            {
                problems.Add($"{package.Key}: no bundled licence file, no repository input matching '{package.RepositoryUrl ?? "(no repository url)"}', and no SPDX template for '{package.LicenseExpression ?? package.LicenseFile ?? "?"}' in {InputRelativePath}");
                continue;
            }

            foreach (var notice in package.BundledNoticeFiles)
            {
                body.AppendLine();
                body.AppendLine($"NOTICE text as shipped in the package ({notice.Replace('\\', '/')}):");
                body.AppendLine();
                body.Append(Repo.Normalise(File.ReadAllText(Path.Combine(package.Folder, notice))));
            }

            sections.Add((package.Title, body.ToString()));
        }

        var text = new StringBuilder();
        text.AppendLine("THIRD-PARTY NOTICES FOR YOJANA");
        text.AppendLine();
        text.AppendLine("This file is generated by tools/Yojana.Gates (`make notices`). Do not edit it by hand: anything that");
        text.AppendLine("cannot come from a package is a generator input under tools/notices-input with its source and date.");
        text.AppendLine("It lists every third-party component shipped with the Yojana web host and its browser client (from");
        text.AppendLine(depsLabel + ")");
        text.AppendLine("plus the shared frameworks and vendored files it runs on, with each component's licence, the");
        text.AppendLine("copyright line its authors declared, and any NOTICE file its authors ship (Apache-2.0 section 4(d)).");
        text.AppendLine("Listing a licensor here is a licence requirement and is not an endorsement by that licensor.");
        text.AppendLine();
        text.AppendLine("CONTENTS");
        text.AppendLine();
        foreach (var (title, _) in sections)
        {
            text.AppendLine("  - " + title);
        }

        foreach (var (title, body) in sections)
        {
            text.AppendLine();
            text.AppendLine(new string('=', 78));
            text.AppendLine(title);
            text.AppendLine(new string('=', 78));
            text.AppendLine();
            text.Append(body);
        }

        return (Repo.Normalise(text.ToString()), problems);
    }

    private static bool Matches(RepositoryInput input, string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return false;
        }

        var normalised = Canonical(repositoryUrl);
        return input.MatchRepositoryUrls.Any(u => Canonical(u) == normalised);
    }

    private static string Canonical(string url)
    {
        var u = url.Trim().ToLowerInvariant();
        u = u.Replace("http://", "https://", StringComparison.Ordinal);
        if (u.EndsWith(".git", StringComparison.Ordinal))
        {
            u = u[..^4];
        }

        return u.TrimEnd('/');
    }

    private static string ReadInput(string inputsFolder, string relative, List<string> problems, string forWhom)
    {
        var path = Path.Combine(inputsFolder, relative);
        if (!File.Exists(path))
        {
            problems.Add($"{forWhom}: generator input file missing: {Repo.Relative(inputsFolder, path)}");
            return "";
        }

        return Repo.Normalise(File.ReadAllText(path));
    }

    private static List<T> LoadAll<T>(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => JsonSerializer.Deserialize<T>(File.ReadAllText(f)) ?? throw new GateException("Cannot read " + f)).ToList()
            : [];
}
