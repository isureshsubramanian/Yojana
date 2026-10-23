using System.Text.RegularExpressions;
using Xunit;

namespace Yojana.Architecture.Tests;

public sealed class SettingsSummaryKeyRules
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Contributor_keys_and_web_registry_summary_keys_match()
    {
        var contributorKeys = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => File.ReadAllText(path))
            .Where(text => text.Contains(": ISettingsSummaryContributor", StringComparison.Ordinal)
                || text.Contains(", ISettingsSummaryContributor", StringComparison.Ordinal))
            .SelectMany(text => Regex.Matches(text, "public const string Key = \"(?<key>[^\"]+)\";")
                .Select(match => match.Groups["key"].Value))
            .Order(StringComparer.Ordinal)
            .ToList();

        var registry = File.ReadAllText(Path.Combine(Root, "web/src/settings/registry.ts"));
        var registryKeys = Regex.Matches(registry, "summaryKey: \"(?<key>[^\"]+)\"")
            .Select(match => match.Groups["key"].Value)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(contributorKeys, registryKeys);
        Assert.Equal(contributorKeys.Count, contributorKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(registryKeys.Count, registryKeys.Distinct(StringComparer.Ordinal).Count());
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
