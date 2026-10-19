using System.Xml.Linq;
using Xunit;

namespace Yojana.Architecture.Tests;

/// <summary>
/// MOD-01 solution layout rule: a module may reference only the Contracts project of another module.
/// Enforced on the .csproj graph itself (no package needed), so a wrong ProjectReference fails the build gate.
/// </summary>
public sealed class ModuleReferenceRules
{
    private static readonly string Root = FindRoot();
    private static readonly string[] InfrastructureProjects = ["Yojana.Platform", "Yojana.Platform.Contracts"];

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static Dictionary<string, IReadOnlyList<string>> Graph()
    {
        var graph = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var csproj in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            var refs = XDocument.Load(csproj).Descendants("ProjectReference")
                .Select(r => Path.GetFileNameWithoutExtension(r.Attribute("Include")!.Value.Replace('\\', '/')))
                .ToList();
            graph[name] = refs;
        }

        return graph;
    }

    private static bool IsContracts(string project) => project.EndsWith(".Contracts", StringComparison.Ordinal);

    private static string ModuleOf(string project) => project.Replace(".Contracts", "", StringComparison.Ordinal);

    [Fact]
    public void Every_module_references_only_contracts_of_other_modules()
    {
        var graph = Graph();
        var violations = new List<string>();
        foreach (var (project, refs) in graph.Where(kv => !InfrastructureProjects.Contains(kv.Key) && kv.Key != "Yojana.Web" && !IsContracts(kv.Key)))
        {
            foreach (var reference in refs)
            {
                var sameModule = ModuleOf(reference) == ModuleOf(project);
                var allowed = sameModule || InfrastructureProjects.Contains(reference) || IsContracts(reference);
                if (!allowed)
                {
                    violations.Add($"{project} -> {reference}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Contracts_projects_reference_only_platform_contracts()
    {
        var graph = Graph();
        var violations = graph.Where(kv => IsContracts(kv.Key) && kv.Key != "Yojana.Platform.Contracts")
            .SelectMany(kv => kv.Value.Where(r => r != "Yojana.Platform.Contracts").Select(r => $"{kv.Key} -> {r}"))
            .ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void Platform_contracts_has_no_references_and_no_packages()
    {
        var csproj = Directory.EnumerateFiles(Path.Combine(Root, "src"), "Yojana.Platform.Contracts.csproj", SearchOption.AllDirectories).Single();
        var doc = XDocument.Load(csproj);
        Assert.Empty(doc.Descendants("ProjectReference"));
        Assert.Empty(doc.Descendants("PackageReference"));
    }

    [Fact]
    public void Platform_references_no_module()
    {
        var graph = Graph();
        Assert.Equal(["Yojana.Platform.Contracts"], graph["Yojana.Platform"]);
    }

    [Fact]
    public void Only_the_web_host_references_module_implementations()
    {
        var graph = Graph();
        var implementations = graph.Keys.Where(k => !IsContracts(k) && !InfrastructureProjects.Contains(k) && k != "Yojana.Web").ToHashSet();
        var violations = graph.Where(kv => kv.Key != "Yojana.Web")
            .SelectMany(kv => kv.Value.Where(r => implementations.Contains(r) && ModuleOf(r) != ModuleOf(kv.Key)).Select(r => $"{kv.Key} -> {r}"))
            .ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void Integration_reaches_serialisation_only_through_its_contracts()
    {
        var references = Graph()["Yojana.Integration"];
        Assert.Contains("Yojana.Serialisation.Contracts", references);
        Assert.DoesNotContain("Yojana.Serialisation", references);
    }

    [Fact]
    public void Every_source_project_is_in_the_solution()
    {
        var solution = File.ReadAllText(Path.Combine(Root, "Yojana.slnx"));
        var missing = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Where(f => !solution.Contains(f!, StringComparison.Ordinal))
            .ToList();
        Assert.Empty(missing);
    }
}
