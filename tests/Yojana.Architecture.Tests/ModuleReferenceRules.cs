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

    private static string ModuleOf(string project)
    {
        if (project.StartsWith("Yojana.Serialisation.", StringComparison.Ordinal)) return "Yojana.Serialisation";
        return project.Replace(".Contracts", "", StringComparison.Ordinal);
    }

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
    public void Knowledge_reaches_other_modules_only_through_contracts_and_platform_infrastructure()
    {
        var references = Graph()["Yojana.Knowledge"];
        Assert.Contains("Yojana.Knowledge.Contracts", references);
        Assert.Contains("Yojana.Platform", references);
        Assert.DoesNotContain("Yojana.Serialisation", references);
        Assert.DoesNotContain("Yojana.Integration", references);
    }

    [Fact]
    public void Other_modules_reach_knowledge_only_through_its_contracts()
    {
        var violations = Graph().Where(pair => pair.Key is not "Yojana.Knowledge" and not "Yojana.Web")
            .SelectMany(pair => pair.Value.Where(reference => reference == "Yojana.Knowledge")
                .Select(reference => $"{pair.Key} -> {reference}"))
            .ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void Developer_reaches_other_modules_only_through_contracts_and_platform_infrastructure()
    {
        var references = Graph()["Yojana.Developer"];
        Assert.Contains("Yojana.Developer.Contracts", references);
        Assert.Contains("Yojana.Platform", references);
        Assert.DoesNotContain("Yojana.Serialisation", references);
        Assert.DoesNotContain("Yojana.Integration", references);
        Assert.DoesNotContain("Yojana.Knowledge", references);
    }

    [Fact]
    public void Other_modules_reach_developer_only_through_its_contracts()
    {
        var violations = Graph().Where(pair => pair.Key is not "Yojana.Developer" and not "Yojana.Web")
            .SelectMany(pair => pair.Value.Where(reference => reference == "Yojana.Developer")
                .Select(reference => $"{pair.Key} -> {reference}"))
            .ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void Outbound_source_ports_are_contracts_without_infrastructure_dependencies()
    {
        var source = File.ReadAllText(Path.Combine(Root, "src/Serialisation/Yojana.Serialisation.Contracts/OutboundSourcePorts.cs"));
        Assert.Contains("interface IOutboundUnitSourcePort", source);
        Assert.Contains("interface IRenderedOutboundDocumentPort", source);
        Assert.DoesNotContain("EntityFrameworkCore", source);
        Assert.DoesNotContain("Yojana.Integration", source);
        Assert.DoesNotContain("IServiceProvider", source);
    }

    [Fact]
    public void Relay_has_only_wire_contracts_and_the_approved_host_package()
    {
        Assert.Equal(["Yojana.Relay.Contracts"], Graph()["Yojana.Relay"]);
        Assert.Empty(Graph()["Yojana.Relay.Contracts"]);
        var relay = XDocument.Load(Path.Combine(Root, "src/Relay/Yojana.Relay/Yojana.Relay.csproj"));
        Assert.Equal(["Microsoft.Extensions.Hosting.WindowsServices"], relay.Descendants("PackageReference")
            .Select(reference => reference.Attribute("Include")!.Value));
        Assert.Empty(relay.Descendants("FrameworkReference"));
        var contract = XDocument.Load(Path.Combine(Root, "src/Relay/Yojana.Relay.Contracts/Yojana.Relay.Contracts.csproj"));
        Assert.Empty(contract.Descendants("PackageReference"));
        var forbidden = new[] { "Yojana.Integration", "Yojana.Platform", "EntityFrameworkCore", "SqlClient" };
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src/Relay"), "*.cs", SearchOption.AllDirectories)
                     .Where(file => !file.Contains("/obj/", StringComparison.Ordinal)))
            foreach (var text in forbidden) Assert.DoesNotContain(text, File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void Document_mappers_depend_on_contracts_and_never_on_persistence_or_transport()
    {
        var boundary = Path.Combine(Root, "src/Integration/Yojana.Integration/Connectors/CX01");
        foreach (var name in new[] { "Delivery", "Credit", "Return", "Journal", "Stock", "Service" })
        {
            var text = File.ReadAllText(Path.Combine(boundary, "Cx01" + name + "Mapper.cs"));
            Assert.DoesNotContain("DbContext", text, StringComparison.Ordinal);
            Assert.DoesNotContain("HttpClient", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Cx01Client", text, StringComparison.Ordinal);
        }
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

    [Fact]
    public void Integration_sources_do_not_reach_into_serialisation_internals()
    {
        var source = Path.Combine(Root, "src", "Integration");
        var forbidden = new List<string>();
        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
                     .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                         StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text,
                         @"Yojana\.Serialisation\.(?<area>[A-Za-z_][A-Za-z_0-9]*)"))
                if (match.Groups["area"].Value != "Contracts") forbidden.Add(file);
        }
        Assert.Empty(forbidden);
    }
}
