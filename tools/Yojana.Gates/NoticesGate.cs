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

    /// <summary>Optional exact package keys for a licence read at that package's repository commit.</summary>
    [JsonPropertyName("matchPackages")]
    public List<string> MatchPackages { get; set; } = [];

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

    /// <summary>Copyright statement quoted exactly as the project's source files carry it, when the licence text has none.</summary>
    [JsonPropertyName("copyrightStatement")]
    public string? CopyrightStatement { get; set; }

    /// <summary>A file the copyright statement points to (for example "the X authors" listed in AUTHORS); part of the notice.</summary>
    [JsonPropertyName("authorsFile")]
    public string? AuthorsFile { get; set; }

    [JsonPropertyName("authorsSourceUrl")]
    public string? AuthorsSourceUrl { get; set; }
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

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("configFile")]
    public string? ConfigFile { get; set; }

    [JsonPropertyName("archives")]
    public Dictionary<string, ComponentArchiveInput> Archives { get; set; } = [];
}

public sealed class ComponentArchiveInput
{
    [JsonPropertyName("archiveUrl")]
    public string ArchiveUrl { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("licenceFile")]
    public string LicenceFile { get; set; } = "";

    [JsonPropertyName("licenceSha256")]
    public string LicenceSha256 { get; set; } = "";
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

    public static GateResult Run(string root, string? depsPath, bool check, string configuration = "Debug")
    {
        var depsPaths = depsPath is null ? FindDeps(root, configuration) : [Path.GetFullPath(depsPath)];
        var inputs = Path.Combine(root, InputRelativePath);
        var packages = new SortedDictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in depsPaths)
            foreach (var (key, package) in Packages.FromDepsFile(path)) packages[key] = package;
        foreach (var (key, package) in Packages.FromAssetsFiles(root, ["src/Relay"])) packages[key] = package;
        var policy = LicencePolicy.Load(root);
        foreach (var lockFile in policy.NpmLockFiles)
        {
            // Only what the browser receives: production dependencies of the client, never its build tooling.
            foreach (var (key, info) in Packages.FromNpmLock(root, lockFile, includeDev: false))
            {
                packages[key] = info;
            }
        }

        var depsLabel = string.Join(", ", depsPaths.Select(path => Repo.Relative(root, path))) + " and src/Relay/Yojana.Relay/obj/project.assets.json";
        if (policy.NpmLockFiles.Count > 0) depsLabel += " and " + string.Join(", ", policy.NpmLockFiles);
        var (text, problems) = Generate(packages, inputs, depsLabel);
        problems.AddRange(ValidatePinnedComponents(root, inputs));
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
                    [Repo.Relative(root, output), $"{packages.Count} packages in {depsLabel}"]);
            }

            var checkedRelay = RelayNotices(root, check);
            return checkedRelay.Passed ? new GateResult("notices", true, $"{packages.Count} packages; product and relay notices up to date", []) : checkedRelay;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, text, Repo.Utf8NoBom);
        var relayResult = RelayNotices(root, check);
        return relayResult.Passed ? new GateResult("notices", true, $"wrote {Repo.Relative(root, output)} for {packages.Count} packages; relay notices generated", []) : relayResult;
    }

    /// <summary>The shipped dependency graphs of the given build configuration. The configuration is an explicit input,
    /// never "whichever bin folder is newest", so the generated file does not change with what was built last.</summary>
    public static IReadOnlyList<string> FindDeps(string root, string configuration = "Debug")
    {
        var artefacts = new[]
        {
            (Folder: Path.Combine(root, "src", "Web", "Yojana.Web", "bin", configuration), File: "Yojana.Web.deps.json"),
            (Folder: Path.Combine(root, "src", "Workers", "Yojana.RenderWorker", "bin", configuration), File: "Yojana.RenderWorker.deps.json"),
        };
        var result = new List<string>();
        foreach (var artefact in artefacts)
        {
            var candidate = Directory.Exists(artefact.Folder)
                ? Directory.EnumerateFiles(artefact.Folder, artefact.File, SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            if (candidate is null) throw new GateException($"No {artefact.File} under {Repo.Relative(root, artefact.Folder)}; build the solution first or pass --deps.");
            result.Add(candidate);
        }
        return result;
    }

    public static (string Text, List<string> Problems) Generate(IReadOnlyDictionary<string, PackageInfo> packages, string inputsFolder, string depsLabel, bool relayOnly = false)
    {
        var repositories = LoadAll<RepositoryInput>(Path.Combine(inputsFolder, "repositories"));
        var components = LoadAll<ComponentInput>(Path.Combine(inputsFolder, "components"));
        var problems = new List<string>();
        var sections = new List<(string Title, string Body)>();

        foreach (var component in components.Where(component => !relayOnly || component.Name.StartsWith("Microsoft .NET Runtime", StringComparison.Ordinal)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
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

            if (component.Version is not null) body.AppendLine("Pinned version: " + component.Version);
            if (component.Sha256 is not null) body.AppendLine("Pinned archive SHA-256: " + component.Sha256);
            foreach (var (platform, archive) in component.Archives.OrderBy(item => item.Key, StringComparer.Ordinal))
                body.AppendLine($"Pinned archive ({platform}): {archive.ArchiveUrl} (SHA-256 {archive.Sha256})");

            body.AppendLine($"Licence text taken from {component.SourceUrl} on {component.TakenOn}.");
            body.AppendLine();
            body.Append(ReadInput(inputsFolder, component.LicenceFile, problems, component.Name));
            foreach (var archive in component.Archives.OrderBy(item => item.Key, StringComparer.Ordinal)
                         .Where(item => !string.Equals(item.Value.LicenceFile, component.LicenceFile, StringComparison.Ordinal)))
            {
                body.AppendLine();
                body.AppendLine($"LICENSE.headless_shell as shipped in the pinned {archive.Key} archive:");
                body.AppendLine();
                body.Append(ReadInput(inputsFolder, archive.Value.LicenceFile, problems, component.Name + " " + archive.Key));
            }
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
                // Apache-2.0 §4(d): a project may publish a NOTICE in its repository that the package itself omits.
                // An audited repository input supplies it; the bundled licence above still stands.
                if (FindRepositoryInput(repositories, package) is { NoticeFile: not null } noticeOnly)
                {
                    body.AppendLine();
                    body.AppendLine($"NOTICE text taken from {noticeOnly.NoticeSourceUrl} on {noticeOnly.TakenOn}:");
                    body.AppendLine();
                    body.Append(ReadInput(inputsFolder, noticeOnly.NoticeFile, problems, package.Key));
                }
            }
            // Older packages sometimes put their repository only in projectUrl. It is still an explicit manifest
            // source, so allow the same audited repository input to match either field.
            else if (FindRepositoryInput(repositories, package) is { } repo)
            {
                if (repo.CopyrightStatement is not null)
                {
                    body.AppendLine("Copyright (as stated in the project's source files): " + repo.CopyrightStatement);
                    body.AppendLine();
                }
                body.AppendLine($"The package carries only a licence identifier. Licence text taken from {repo.SourceUrl} on {repo.TakenOn}:");
                body.AppendLine();
                body.Append(ReadInput(inputsFolder, repo.LicenceFile, problems, package.Key));
                if (repo.AuthorsFile is not null)
                {
                    body.AppendLine();
                    body.AppendLine($"Authors list the copyright statement refers to, taken from {repo.AuthorsSourceUrl} on {repo.TakenOn}:");
                    body.AppendLine();
                    body.Append(ReadInput(inputsFolder, repo.AuthorsFile, problems, package.Key));
                }
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
                problems.Add($"{package.Key}: no bundled licence file, no repository input matching repository '{package.RepositoryUrl ?? "(none)"}' or project '{package.ProjectUrl ?? "(none)"}', and no SPDX template for '{package.LicenseExpression ?? package.LicenseFile ?? "?"}' in {InputRelativePath}");
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
        text.AppendLine("It lists every third-party component shipped with the artefact dependency graphs (from");
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


    private static GateResult RelayNotices(string root, bool check)
    {
        var packages = Packages.FromAssetsFiles(root, ["src/Relay"]);
        var (text, problems) = Generate(packages, Path.Combine(root, InputRelativePath), "src/Relay/Yojana.Relay/obj/project.assets.json", relayOnly: true);
        if (problems.Count > 0) return new("relay-notices", false, "Relay dependencies lack attribution", problems);
        var output = Path.Combine(root, "src", "Relay", "Yojana.Relay", "Notices", "THIRD-PARTY-NOTICES.txt");
        if (check)
            return new("relay-notices", File.Exists(output) && Repo.Normalise(File.ReadAllText(output)) == text,
                "Relay dependency graph checked against its shipped notices", [Repo.Relative(root, output)]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, text, Repo.Utf8NoBom);
        return new("relay-notices", true, "Relay notices generated", []);
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

    private static RepositoryInput? FindRepositoryInput(IEnumerable<RepositoryInput> repositories,
        PackageInfo package) =>
        repositories.FirstOrDefault(input => input.MatchPackages.Contains(package.Key, StringComparer.OrdinalIgnoreCase))
        ?? repositories.FirstOrDefault(input => input.MatchPackages.Count == 0 &&
            (Matches(input, package.RepositoryUrl) || Matches(input, package.ProjectUrl)));

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

    private static List<string> ValidatePinnedComponents(string root, string inputsFolder)
    {
        var problems = new List<string>();
        foreach (var component in LoadAll<ComponentInput>(Path.Combine(inputsFolder, "components"))
                     .Where(item => item.ConfigFile is not null))
        {
            var configPath = Path.Combine(root, component.ConfigFile!);
            if (!File.Exists(configPath))
            {
                problems.Add($"{component.Name}: pinned-component configuration is missing: {component.ConfigFile}");
                continue;
            }
            using var config = JsonDocument.Parse(File.ReadAllText(configPath));
            var browser = config.RootElement.GetProperty("RenderWorker").GetProperty("Browser");
            var version = browser.GetProperty("Version").GetString();
            if (!string.Equals(version, component.Version, StringComparison.Ordinal))
                problems.Add($"{component.Name}: configured version {version} differs from notices input {component.Version}");
            var platforms = browser.GetProperty("Platforms");
            foreach (var platform in platforms.EnumerateObject())
            {
                if (!component.Archives.TryGetValue(platform.Name, out var notice))
                {
                    problems.Add($"{component.Name}: configured platform {platform.Name} is absent from notices input");
                    continue;
                }
                var url = platform.Value.GetProperty("ArchiveUrl").GetString();
                var hash = platform.Value.GetProperty("Sha256").GetString();
                if (!string.Equals(url, notice.ArchiveUrl, StringComparison.Ordinal)
                    || !string.Equals(hash, notice.Sha256, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{component.Name}: configured URL/hash for {platform.Name} differs from notices input");
                var licencePath = Path.Combine(inputsFolder, notice.LicenceFile);
                if (!File.Exists(licencePath))
                {
                    problems.Add($"{component.Name}: {platform.Name} licence input is missing: {notice.LicenceFile}");
                    continue;
                }
                var licenceHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(licencePath)));
                if (!licenceHash.Equals(notice.LicenceSha256, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{component.Name}: {platform.Name} licence input hash differs from the recorded archive text");
            }
            foreach (var noticePlatform in component.Archives.Keys)
                if (!platforms.TryGetProperty(noticePlatform, out _))
                    problems.Add($"{component.Name}: notices platform {noticePlatform} is absent from configuration");
        }
        return problems;
    }
}
