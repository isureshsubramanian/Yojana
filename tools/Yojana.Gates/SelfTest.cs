using System.Security.Cryptography;
using System.Text;

namespace Yojana.Gates;

/// <summary>
/// Exit criterion of P0: every gate has been seen to fail on a planted violation. Each check builds its
/// violation in a temporary directory (a copyleft package, a missing notice, a denied name, an oversized
/// bundle) and asserts that the gate reports FAIL, then that the clean counterpart reports PASS.
/// </summary>
public static class SelfTest
{
    public static IReadOnlyList<GateResult> Run(string root)
    {
        var results = new List<GateResult>();
        var temp = Directory.CreateTempSubdirectory("yojana-gates-selftest-");
        try
        {
            results.Add(LicencePlanted(temp.FullName));
            results.Add(NoticesPlanted(root, temp.FullName));
            results.Add(NamePlanted(root, temp.FullName));
            results.Add(BudgetPlanted(temp.FullName));
            results.Add(NpmPlanted(temp.FullName));
            results.Add(ClientRulesPlanted(temp.FullName));
            results.Add(GridRulesPlanted(temp.FullName));
        }
        finally
        {
            temp.Delete(recursive: true);
        }

        return results;
    }

    private static GateResult GridRulesPlanted(string temp)
    {
        var root = Directory.CreateDirectory(Path.Combine(temp, "grid")).FullName;
        var web = Directory.CreateDirectory(Path.Combine(root, "web", "src")).FullName;
        var plantedPath = Path.Combine(web, "Planted.tsx");
        File.WriteAllText(plantedPath, "export const x = <DataGrid columns={[]} page={p} />;\n");
        var planted = GridMigrationGate.Evaluate(root, new HashSet<string>(), new HashSet<string>());
        var baseline = GridMigrationGate.Evaluate(root, new HashSet<string> { "web/src/Planted.tsx" }, new HashSet<string>());
        File.WriteAllText(plantedPath, "useGridQuery(path, schema, options); export const x = <DataGrid columns={[]} page={p} />;\n");
        var stale = GridMigrationGate.Evaluate(root, new HashSet<string> { "web/src/Planted.tsx" }, new HashSet<string>());
        var clean = GridMigrationGate.Evaluate(root, new HashSet<string>(), new HashSet<string>());
        var ok = !planted.Passed && baseline.Passed && !stale.Passed && clean.Passed;
        return new GateResult("self-test grid migration", ok, ok ? "a new local paging copy fails, a baseline entry passes, and a migrated entry must leave the baseline" : "unexpected outcome",
            [$"planted: {planted.Summary}", $"baseline: {baseline.Summary}", $"stale: {stale.Summary}", $"clean: {clean.Summary}"]);
    }

    private static GateResult LicencePlanted(string temp)
    {
        var policy = new LicencePolicy { AllowedExpressions = ["MIT", "Apache-2.0"] };
        var copyleft = FakePackage(temp, "Planted.Copyleft", "1.0.0", "<license type=\"expression\">GPL-3.0-only</license>");
        var fileOnly = FakePackage(temp, "Planted.FileLicence", "1.0.0", "<license type=\"file\">LICENSE.txt</license>");
        var fine = FakePackage(temp, "Planted.Fine", "1.0.0", "<license type=\"expression\">MIT</license>");
        var planted = LicenceGate.Evaluate(policy, new Dictionary<string, PackageInfo> { [copyleft.Key] = copyleft, [fileOnly.Key] = fileOnly });
        var clean = LicenceGate.Evaluate(policy, new Dictionary<string, PackageInfo> { [fine.Key] = fine });
        var pendingBlocks = LicenceGate.Evaluate(new LicencePolicy
        {
            AllowedExpressions = ["MIT"],
            Exceptions = [new LicenceException { Package = "Planted.FileLicence", Decision = "pending", Licence = "vendor terms" }],
        }, new Dictionary<string, PackageInfo> { [fileOnly.Key] = fileOnly });
        var ok = !planted.Passed && clean.Passed && !pendingBlocks.Passed;
        return new GateResult("self-test licences", ok, ok ? "copyleft and file-only licences fail, allowed passes, pending exception fails" : "unexpected outcome",
            [$"planted: {planted.Summary}", $"clean: {clean.Summary}", $"pending exception: {pendingBlocks.Summary}"]);
    }

    private static GateResult NoticesPlanted(string root, string temp)
    {
        var inputs = Path.Combine(root, NoticesGate.InputRelativePath);
        var orphan = FakePackage(temp, "Planted.NoText", "1.0.0", "<license type=\"expression\">MIT-0</license>", repository: "https://example.invalid/none");
        var (_, problems) = NoticesGate.Generate(new Dictionary<string, PackageInfo> { [orphan.Key] = orphan }, inputs, "self-test");
        var credited = FakePackage(temp, "Planted.Credited", "1.0.0", "<license type=\"expression\">MIT</license>", licenceText: "MIT License\n\nCopyright (c) planted\n");
        var (text, cleanProblems) = NoticesGate.Generate(new Dictionary<string, PackageInfo> { [credited.Key] = credited }, inputs, "self-test");
        var ok = problems.Count == 1 && cleanProblems.Count == 0 && text.Contains("Copyright (c) planted", StringComparison.Ordinal);
        return new GateResult("self-test notices", ok, ok ? "a package without any notice source fails; a package with bundled text is reproduced" : "unexpected outcome",
            [$"planted: {problems.Count} problem(s)", $"clean: {cleanProblems.Count} problem(s)"]);
    }

    private static GateResult NamePlanted(string root, string temp)
    {
        var config = NameAuditConfig.Load(root);
        var name = config.DenyList(root)[0];
        var scan = Directory.CreateDirectory(Path.Combine(temp, "names"));
        var planted = Path.Combine(scan.FullName, "src", "Planted.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
        File.WriteAllText(planted, "// imported from " + name + " last year\n");
        var allowed = Path.Combine(scan.FullName, config.AllowedPathPrefixes[0].Replace('/', Path.DirectorySeparatorChar), "Planted.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(allowed)!);
        File.WriteAllText(allowed, "// protocol fact: " + name + "\n");
        var gates = Directory.CreateDirectory(Path.Combine(scan.FullName, "tools", "gates"));
        File.Copy(Path.Combine(root, "tools", "gates", config.DenyListFile), Path.Combine(gates.FullName, config.DenyListFile));
        var plantedConfig = new NameAuditConfig { DenyListFile = config.DenyListFile, ScanRoots = ["src"], AllowedPathPrefixes = config.AllowedPathPrefixes };
        var result = NameAuditGate.Run(scan.FullName, plantedConfig);
        File.Delete(planted);
        var clean = NameAuditGate.Run(scan.FullName, plantedConfig);
        var ok = !result.Passed && result.Details.Count == 1 && clean.Passed;
        return new GateResult("self-test names", ok, ok ? "a denied name fails outside the connector boundary and is allowed inside it" : "unexpected outcome",
            [$"planted: {result.Summary}", $"clean: {clean.Summary}"]);
    }

    private static GateResult BudgetPlanted(string temp)
    {
        var dist = Directory.CreateDirectory(Path.Combine(temp, "web", "dist"));
        File.WriteAllText(Path.Combine(dist.FullName, "index.html"), "<html><head><link rel=\"stylesheet\" href=\"app.css\"><script src=\"app.js\"></script></head></html>");
        File.WriteAllBytes(Path.Combine(dist.FullName, "app.js"), RandomNumberGenerator.GetBytes(300 * 1024));
        File.WriteAllText(Path.Combine(dist.FullName, "app.css"), "body{margin:0}");
        var config = new BudgetConfig { DistDirectory = "web/dist" };
        var planted = BudgetGate.Run(temp, config);
        File.WriteAllText(Path.Combine(dist.FullName, "app.js"), "console.log(1)");
        var clean = BudgetGate.Run(temp, config);
        var ok = !planted.Passed && clean.Passed;
        return new GateResult("self-test budget", ok, ok ? "an oversized bundle fails, a small one passes" : "unexpected outcome", [$"planted: {planted.Summary}", $"clean: {clean.Summary}"]);
    }

    private static GateResult NpmPlanted(string temp)
    {
        var web = Directory.CreateDirectory(Path.Combine(temp, "npm", "web")).FullName;
        static string Lock(string name, string licence, bool dev) =>
            "{\"name\":\"planted\",\"lockfileVersion\":3,\"packages\":{\"\":{\"name\":\"planted\"},\"node_modules/" + name + "\":{\"version\":\"1.0.0\",\"license\":\"" + licence + "\"" + (dev ? ",\"dev\":true" : "") + "}}}";
        static void Install(string web, string name, string licence)
        {
            var folder = Directory.CreateDirectory(Path.Combine(web, "node_modules", name)).FullName;
            File.WriteAllText(Path.Combine(folder, "package.json"), "{\"name\":\"" + name + "\",\"version\":\"1.0.0\",\"license\":\"" + licence + "\"}");
            File.WriteAllText(Path.Combine(folder, "LICENSE"), licence + " text\n\nCopyright (c) planted npm\n");
        }

        var policy = new LicencePolicy { AllowedExpressions = ["MIT"] };
        File.WriteAllText(Path.Combine(web, "package-lock.json"), Lock("planted-copyleft", "GPL-3.0-only", dev: true));
        Install(web, "planted-copyleft", "GPL-3.0-only");
        var planted = LicenceGate.Evaluate(policy, Packages.FromNpmLock(Path.Combine(temp, "npm"), "web/package-lock.json", includeDev: true));

        File.WriteAllText(Path.Combine(web, "package-lock.json"), Lock("planted-fine", "MIT", dev: false));
        Install(web, "planted-fine", "MIT");
        var fine = Packages.FromNpmLock(Path.Combine(temp, "npm"), "web/package-lock.json", includeDev: true);
        var clean = LicenceGate.Evaluate(policy, fine);
        var (text, problems) = NoticesGate.Generate(fine, Path.Combine(temp, "npm", "no-inputs"), "self-test");

        File.WriteAllText(Path.Combine(web, "package-lock.json"), Lock("planted-missing", "MIT", dev: false));
        var missingThrows = false;
        try
        {
            Packages.FromNpmLock(Path.Combine(temp, "npm"), "web/package-lock.json", includeDev: true);
        }
        catch (GateException)
        {
            missingThrows = true;
        }

        var ok = !planted.Passed && clean.Passed && problems.Count == 0 && text.Contains("Copyright (c) planted npm", StringComparison.Ordinal) && text.Contains("planted-fine 1.0.0 (npm)", StringComparison.Ordinal) && missingThrows;
        return new GateResult("self-test npm", ok, ok ? "a copyleft dev package fails the policy, an MIT package passes and is credited from its bundled text, an uninstalled package stops the gate" : "unexpected outcome",
            [$"planted: {planted.Summary}", $"clean: {clean.Summary}", $"notices problems: {problems.Count}", $"missing install throws: {missingThrows}"]);
    }

    private static GateResult ClientRulesPlanted(string temp)
    {
        var root = Directory.CreateDirectory(Path.Combine(temp, "client")).FullName;
        var src = Directory.CreateDirectory(Path.Combine(root, "web", "src")).FullName;
        Directory.CreateDirectory(Path.Combine(src, "theme"));
        var config = new BudgetConfig { DistDirectory = "web/dist", SourceDirectory = "web/src", ThemeModuleDirectory = "web/src/theme/", ForbiddenImports = ["styled-components"] };
        File.WriteAllText(Path.Combine(src, "Planted.tsx"), "import styled from \"styled-components\";\nexport const x = () => { document.documentElement.dataset.mode = \"dark\"; };\n");
        File.WriteAllText(Path.Combine(src, "theme", "mode.ts"), "export const set = () => { document.documentElement.dataset.mode = \"dark\"; };\n");
        Directory.CreateDirectory(Path.Combine(src, "api"));
        File.WriteAllText(Path.Combine(src, "api", "client.ts"), "export async function api<T>(path: string): Promise<T> { return (await fetch(path)).json() as T; }\nexport const apiParsed = (p: string) => api<unknown>(p);\n");
        File.WriteAllText(Path.Combine(src, "PlantedApi.ts"), "export const a = () => api<Thing>(\"/api/things\");\nexport const b = () => api(\"/api/other\");\nexport const c = () => apiParsed(\"/api/ok\", thing);\nexport const d = () => client.api(\"/x\");\n");
        var planted = BudgetGate.Run(root, config);
        File.Delete(Path.Combine(src, "Planted.tsx"));
        File.Delete(Path.Combine(src, "PlantedApi.ts"));
        var clean = BudgetGate.Run(root, config);
        var ok = !planted.Passed && planted.Details.Count(d => d.StartsWith("RULE", StringComparison.Ordinal)) == 4
            && planted.Details.Count(d => d.Contains("calls api() unchecked", StringComparison.Ordinal)) == 2 && clean.Passed;
        return new GateResult("self-test client rules", ok, ok ? "a CSS-in-JS import, a theme attribute outside the theme module and two unchecked api calls fail; the theme module, the API client, apiParsed and x.api pass" : "unexpected outcome",
            [$"planted: {planted.Summary}", $"clean: {clean.Summary}"]);
    }

    private static PackageInfo FakePackage(string temp, string id, string version, string licenseElement, string? repository = null, string? licenceText = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(temp, "packages", id.ToLowerInvariant(), version)).FullName;
        var nuspec = new StringBuilder("<?xml version=\"1.0\"?><package><metadata><id>" + id + "</id><version>" + version + "</version>" + licenseElement + "<copyright>planted</copyright>");
        if (repository is not null)
        {
            nuspec.Append("<repository type=\"git\" url=\"" + repository + "\" />");
        }

        nuspec.Append("</metadata></package>");
        File.WriteAllText(Path.Combine(folder, id.ToLowerInvariant() + ".nuspec"), nuspec.ToString());
        if (licenceText is not null)
        {
            File.WriteAllText(Path.Combine(folder, "LICENSE"), licenceText);
        }

        return Packages.Load(id, version, [Path.Combine(temp, "packages")]);
    }
}
