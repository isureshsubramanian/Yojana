using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yojana.Gates;

/// <summary>A NuGet package as found in the local package cache, with what its manifest says about its licence.</summary>
public sealed record PackageInfo(
    string Id,
    string Version,
    string Folder,
    string? LicenseExpression,
    string? LicenseFile,
    string? LicenseUrl,
    string? Copyright,
    string? ProjectUrl,
    string? RepositoryUrl,
    IReadOnlyList<string> BundledLicenceFiles,
    IReadOnlyList<string> BundledNoticeFiles)
{
    /// <summary>"NuGet" or "npm". Both ecosystems answer the same two questions (may we use it, did we credit it).</summary>
    public string Ecosystem { get; init; } = "NuGet";

    public string Key => (Ecosystem == "npm" ? "npm:" : "") + Id + "/" + Version;

    /// <summary>Heading in the notices file.</summary>
    public string Title => Id + " " + Version + (Ecosystem == "npm" ? " (npm)" : "");
}

public static partial class Packages
{
    /// <summary>Every package restored for any project under the given roots (policy scope: everything we build with).</summary>
    public static SortedDictionary<string, PackageInfo> FromAssetsFiles(string root, IEnumerable<string> scanDirectories)
    {
        var found = new SortedDictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in scanDirectories.Select(d => Path.Combine(root, d)).Where(Directory.Exists))
        {
            foreach (var assets in Directory.EnumerateFiles(dir, "project.assets.json", SearchOption.AllDirectories))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(assets));
                var folders = doc.RootElement.TryGetProperty("packageFolders", out var pf)
                    ? pf.EnumerateObject().Select(p => p.Name).ToList()
                    : [Repo.PackagesFolder()];
                foreach (var library in doc.RootElement.GetProperty("libraries").EnumerateObject())
                {
                    if (library.Value.GetProperty("type").GetString() != "package")
                    {
                        continue;
                    }

                    var parts = library.Name.Split('/');
                    if (!found.ContainsKey(library.Name))
                    {
                        found[library.Name] = Load(parts[0], parts[1], folders);
                    }
                }
            }
        }

        return found;
    }

    /// <summary>Packages actually shipped with a build, from its .deps.json (notices scope: what the artefact contains).</summary>
    public static SortedDictionary<string, PackageInfo> FromDepsFile(string depsPath)
    {
        var found = new SortedDictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(File.ReadAllText(depsPath));
        var folders = new[] { Repo.PackagesFolder() };
        foreach (var library in doc.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() != "package")
            {
                continue;
            }

            var parts = library.Name.Split('/');
            found[library.Name] = Load(parts[0], parts[1], folders);
        }

        return found;
    }

    /// <summary>
    /// Every package in an npm lockfile (v2/v3 "packages" map), read from the installed node_modules so the
    /// licence text comes from the package itself. Dev-only packages are tooling: in scope for the licence
    /// policy (everything we build with), out of scope for the notices (they never reach the artefact).
    /// Optional platform binaries that npm did not install for this OS are read from the lockfile alone.
    /// </summary>
    public static SortedDictionary<string, PackageInfo> FromNpmLock(string root, string lockRelativePath, bool includeDev)
    {
        var found = new SortedDictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);
        var lockPath = Path.Combine(root, lockRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(lockPath))
        {
            return found;
        }

        var webRoot = Path.GetDirectoryName(lockPath)!;
        using var doc = JsonDocument.Parse(File.ReadAllText(lockPath));
        if (!doc.RootElement.TryGetProperty("packages", out var packages))
        {
            throw new GateException($"{lockRelativePath} has no 'packages' map; lockfileVersion 2 or 3 is required.");
        }

        foreach (var entry in packages.EnumerateObject())
        {
            if (entry.Name.Length == 0)
            {
                continue;
            }

            var isDev = entry.Value.TryGetProperty("dev", out var dev) && dev.GetBoolean();
            var isOptional = entry.Value.TryGetProperty("optional", out var opt) && opt.GetBoolean();
            if (isDev && !includeDev)
            {
                continue;
            }

            var name = entry.Name[(entry.Name.LastIndexOf("node_modules/", StringComparison.Ordinal) + "node_modules/".Length)..];
            var version = entry.Value.GetProperty("version").GetString() ?? "?";
            var folder = Path.Combine(webRoot, entry.Name.Replace('/', Path.DirectorySeparatorChar));
            PackageInfo info;
            if (Directory.Exists(folder) && File.Exists(Path.Combine(folder, "package.json")))
            {
                info = LoadNpm(name, version, folder);
            }
            else if (isOptional)
            {
                var lockLicence = entry.Value.TryGetProperty("license", out var l) ? l.GetString() : null;
                info = new PackageInfo(name, version, "", lockLicence, null, null, null, null, null, [], []) { Ecosystem = "npm" };
            }
            else
            {
                throw new GateException($"npm package {name}@{version} is in {lockRelativePath} but not installed under {Repo.Relative(root, webRoot)}/node_modules; run `make web-install` first.");
            }

            found[info.Key] = info;
        }

        return found;
    }

    private static PackageInfo LoadNpm(string name, string version, string folder)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "package.json")));
        var rootEl = manifest.RootElement;
        string? expression = null, licenceFile = null;
        if (rootEl.TryGetProperty("license", out var lic))
        {
            var text = lic.ValueKind == JsonValueKind.Object && lic.TryGetProperty("type", out var t) ? t.GetString() : lic.ValueKind == JsonValueKind.String ? lic.GetString() : null;
            if (text is not null && text.StartsWith("SEE LICENSE IN ", StringComparison.OrdinalIgnoreCase))
            {
                licenceFile = text["SEE LICENSE IN ".Length..].Trim();
            }
            else
            {
                expression = text;
            }
        }
        else if (rootEl.TryGetProperty("licenses", out var lics) && lics.ValueKind == JsonValueKind.Array)
        {
            // Legacy array form: every listed licence must be acceptable, so it is joined with AND.
            expression = string.Join(" AND ", lics.EnumerateArray().Select(e => e.TryGetProperty("type", out var tt) ? tt.GetString() : null).Where(x => x is not null));
        }

        string? repository = null;
        if (rootEl.TryGetProperty("repository", out var repo))
        {
            repository = repo.ValueKind == JsonValueKind.String ? repo.GetString() : repo.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (repository is not null)
            {
                if (repository.StartsWith("git+", StringComparison.Ordinal)) repository = repository[4..];
                if (repository.StartsWith("git://", StringComparison.Ordinal)) repository = "https://" + repository[6..];
                if (!repository.Contains("://", StringComparison.Ordinal) && !repository.StartsWith("github:", StringComparison.Ordinal)) repository = "https://github.com/" + repository;
                if (repository.StartsWith("github:", StringComparison.Ordinal)) repository = "https://github.com/" + repository[7..];
                if (repository.EndsWith(".git", StringComparison.Ordinal)) repository = repository[..^4];
            }
        }

        string? author = null;
        if (rootEl.TryGetProperty("author", out var au))
        {
            author = au.ValueKind == JsonValueKind.String ? au.GetString() : au.TryGetProperty("name", out var n) ? n.GetString() : null;
        }

        var homepage = rootEl.TryGetProperty("homepage", out var hp) ? hp.GetString() : null;
        var files = Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(f => f is not null).Select(f => f!).ToList();
        var bundledLicences = files.Where(f => LicenceFileName().IsMatch(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var bundledNotices = files.Where(f => NoticeFileName().IsMatch(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        return new PackageInfo(name, version, folder, expression, licenceFile, null, author is null ? null : "Author (as declared in the package manifest): " + author, homepage, repository, bundledLicences, bundledNotices) { Ecosystem = "npm" };
    }

    public static PackageInfo Load(string id, string version, IEnumerable<string> folders)
    {
        var folder = folders.Select(f => Path.Combine(f, id.ToLowerInvariant(), version.ToLowerInvariant())).FirstOrDefault(Directory.Exists)
            ?? throw new GateException($"Package {id} {version} is not in the local package cache; run `dotnet restore` first.");
        var nuspec = Directory.EnumerateFiles(folder, "*.nuspec").FirstOrDefault()
            ?? throw new GateException($"Package {id} {version} has no .nuspec in {folder}.");
        var text = File.ReadAllText(nuspec);
        var license = LicenseElement().Match(text);
        var files = Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(f => f is not null).Select(f => f!).ToList();
        var bundledLicences = files.Where(f => LicenceFileName().IsMatch(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var bundledNotices = files.Where(f => NoticeFileName().IsMatch(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        foreach (var sub in new[] { "notices", "NOTICES" }.Select(s => Path.Combine(folder, s)).Where(Directory.Exists))
        {
            bundledNotices.AddRange(Directory.EnumerateFiles(sub).Select(f => Path.GetRelativePath(folder, f)));
        }

        return new PackageInfo(
            id,
            version,
            folder,
            license.Success && license.Groups["type"].Value == "expression" ? license.Groups["value"].Value.Trim() : null,
            license.Success && license.Groups["type"].Value == "file" ? license.Groups["value"].Value.Trim() : null,
            Element(text, "licenseUrl"),
            Element(text, "copyright"),
            Element(text, "projectUrl"),
            RepositoryUrl().Match(text) is { Success: true } r ? r.Groups["url"].Value : null,
            bundledLicences,
            bundledNotices);
    }

    private static string? Element(string text, string name)
    {
        var m = Regex.Match(text, "<" + name + @"[^>]*>(?<v>[^<]*)</" + name + ">", RegexOptions.Singleline);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups["v"].Value.Trim()) : null;
    }

    [GeneratedRegex("<license type=\"(?<type>expression|file)\"[^>]*>(?<value>[^<]*)</license>")]
    private static partial Regex LicenseElement();

    [GeneratedRegex("<repository[^>]*url=\"(?<url>[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex RepositoryUrl();

    [GeneratedRegex(@"^(licen[cs]e|copying)(\.[a-z]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex LicenceFileName();

    [GeneratedRegex(@"^(notice|third[-_]?party[-_]?notices?)(\.[a-z]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex NoticeFileName();
}
