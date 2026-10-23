using System.Text;
using System.Text.RegularExpressions;
using Yojana.Integration.Verification;

namespace Yojana.Integration.Tests;

public sealed class ConnectorVerificationManifestTests
{
    private readonly ConnectorVerificationManifestStore store = new();

    [Fact]
    public void Manifests_have_unique_ids_neutral_wording_and_acceptance_for_every_assumption()
    {
        var denyList = Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(Path.Combine(
                RepositoryRoot(), "tools", "gates", "name-denylist.b64")).Trim()))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#')).ToList();
        foreach (var code in store.ConnectorCodes)
        {
            var manifest = store.Get(code);
            Assert.False(manifest.LiveVerification.Verified);
            Assert.Equal(manifest.Facts.Count, manifest.Facts.Select(fact => fact.Id)
                .Distinct(StringComparer.Ordinal).Count());
            Assert.All(manifest.Facts.Where(fact => fact.Kind == ConnectorVerificationKind.Assumed),
                fact => Assert.True(fact.AcceptanceItem, fact.Id));
            var text = string.Join('\n', manifest.Facts.Select(fact =>
                $"{fact.Fact}\n{fact.ValueUsed}\n{fact.SourceLabel}\n{fact.UsedAt}"));
            Assert.All(denyList, name => Assert.DoesNotMatch(new Regex(
                @"(?<![\p{L}\p{N}_])" + Regex.Escape(name) + @"(?![\p{L}\p{N}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), text));
            Assert.DoesNotContain("http://", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https://", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("CX01")]
    [InlineData("CX02")]
    [InlineData("CX04")]
    public void Protocol_ledger_assumptions_match_the_manifest_both_ways(string connectorCode)
    {
        var manifestIds = store.Get(connectorCode).Facts
            .Where(fact => fact.Kind == ConnectorVerificationKind.Assumed)
            .Select(fact => fact.Id).ToHashSet(StringComparer.Ordinal);
        var ledger = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Integration",
            "Yojana.Integration", "Connectors", connectorCode, "Testing", "PROTOCOL-SOURCES.md"));
        HashSet<string> ledgerIds;
        if (connectorCode == "CX01")
            ledgerIds = Regex.Matches(ledger, @"`((?:NW|OPEN)-\d+)`")
                .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        else
            ledgerIds = ledger.Split('\n').Where(line => line.StartsWith('|'))
                .Select(line => line.Trim('|').Split('|', StringSplitOptions.TrimEntries))
                .Where(cells => cells.Length >= 3 && cells[2].Contains("ASSUMED", StringComparison.Ordinal))
                .Select(cells => cells[0]).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(ledgerIds.OrderBy(value => value), manifestIds.OrderBy(value => value));
    }

    [Theory]
    [MemberData(nameof(ResearchAcceptanceFacts))]
    public void Every_research_acceptance_fact_is_an_acceptance_item(string connectorCode, string factId)
    {
        var fact = Assert.Single(store.Get(connectorCode).Facts, row => row.Id == factId);
        Assert.True(fact.AcceptanceItem, factId);
    }

    public static TheoryData<string, string> ResearchAcceptanceFacts()
    {
        var data = new TheoryData<string, string>();
        foreach (var id in new[]
                 {
                     "P-02", "A-11", "A-14", "A-15", "P-12", "P-17", "P-22", "P-25", "P-26",
                     "R-11", "R-12", "W-04", "W-07", "W-10", "W-14", "W-17", "W-18", "W-25",
                     "T-09", "T-11", "T-10", "D-04", "D-05", "D-06", "S-05", "B-03", "B-11",
                     "B-12", "M-09", "L-06",
                 })
            data.Add("CX02", id);
        foreach (var id in new[]
                 {
                     "F65", "F66", "F67", "F62", "F63", "F68", "F129", "F69", "F50", "F51", "F30",
                     "F31", "F81", "F83", "F57", "F84", "F10", "F13", "F94", "F88", "F93", "F104",
                     "F110", "F113", "F114", "F115", "F119", "F122", "F77", "F131", "F132", "F133",
                     "F15", "F47", "F59", "F38", "F130", "F124", "F140", "F142", "F143",
                 })
            data.Add("CX04", id);
        return data;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Yojana.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Repository root was not found.");
    }
}
