using System.Text.RegularExpressions;
using Yojana.Relay.Contracts;

namespace Yojana.Relay;

public sealed class RelayAllowList(RelayDeclaration declaration)
{
    private readonly IReadOnlyList<(string Method, Regex Path)> rules = declaration.Paths.Select(rule =>
        (rule.Method, new Regex("\\A(?:" + rule.Pattern + ")\\z", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100)))).ToList();

    public bool Allows(string method, string path, string query)
    {
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains('\\') || path.Contains('%') || path.Contains('?') || path.Contains('#')
            || path.Split('/').Any(part => part is "." or "..") || path.Any(char.IsControl)
            || query.Length > 16_384 || query.Any(char.IsControl)
            || query.Length > 0 && !query.StartsWith('?')) return false;
        return rules.Any(rule => rule.Method == method && rule.Path.IsMatch(path));
    }
}
