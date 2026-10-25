using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Yojana.Developer.DatabaseTests;

// COUPLING (signature format): pins the reference vector and the C# sample published in
// web/src/settings/developers/signatureSamples.ts. IsFromYojana below must stay character-for-character the sample's
// text, so the documentation shows code that is compiled and run here.
public sealed class SignatureSampleTests
{
    private const string Secret = "yjw_7Jq2c9VYbX1mT4rK0pLs8dWfE3nHuQaZgRyCvBoMiNk";
    private const long Timestamp = 1794044832;
    private const string Body = "{\"id\":\"0193f6a1-7c2e-7b44-9d10-5e2a8c1f0b37\",\"type\":\"unit.shipped\",\"version\":\"1\"}";
    private const string Signature = "d3efb7f84924fffb323a622ec14c7cdf5ef7ccd9a5311bb05caf981f16f60afc";

    [Fact]
    public void Csharp_sample_accepts_the_reference_vector_and_refuses_tampering()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(Timestamp + 10);
        Assert.True(IsFromYojana($"t={Timestamp},v1={Signature}", Body, Secret, now));
        Assert.True(IsFromYojana($"t={Timestamp},v1={new string('0', 64)},v1={Signature}", Body, Secret, now));
        Assert.False(IsFromYojana($"t={Timestamp},v1={Signature}", Body + " ", Secret, now));
        Assert.False(IsFromYojana($"t={Timestamp},v1={Signature}", Body, Secret,
            DateTimeOffset.FromUnixTimeSeconds(Timestamp + 301)));
        Assert.False(IsFromYojana($"t={Timestamp},v1=zz", Body, Secret, now));
        Assert.False(IsFromYojana($"t={Timestamp},v1={new string('z', 64)}", Body, Secret, now));
    }

    [Fact]
    public void Published_csharp_sample_is_the_tested_method()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Yojana.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var samples = File.ReadAllText(Path.Combine(root!.FullName, "web/src/settings/developers/signatureSamples.ts"));
        var published = Regex.Match(samples, @"csharp: \{\s*label: ""C#"",\s*code: `(?<code>[^`]*)`", RegexOptions.Singleline);
        Assert.True(published.Success);
        var tests = File.ReadAllText(Path.Combine(root.FullName, "tests/Yojana.Developer.DatabaseTests/SignatureSampleTests.cs"));
        var start = tests.LastIndexOf("    static bool IsFromYojana(", StringComparison.Ordinal);
        var end = tests.IndexOf("\n    }\n", start, StringComparison.Ordinal) + "\n    }".Length;
        var tested = string.Join('\n', tests[start..end].Split('\n').Select(line => line.Length >= 4 ? line[4..] : line.TrimStart()));
        Assert.Equal(published.Groups["code"].Value.Replace("\r", ""), tested.Replace("\r", ""));
    }

    static bool IsFromYojana(string header, string body, string secret, DateTimeOffset now)
    {
        var parts = header.Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToList();
        var t = parts.FirstOrDefault(p => p[0] == "t")?[1];
        if (t is null || !long.TryParse(t, out var seconds)) return false;
        if (Math.Abs(now.ToUnixTimeSeconds() - seconds) > 300) return false;
        var expected = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"))));
        return parts.Where(p => p[0] == "v1").Any(p =>
            CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(p[1])));
    }
}
