using System.Text;
using Yojana.Gates;

// Local and CI gates (MOD-01): licence policy, notices generation, name audit, bundle budget.
// Usage: Yojana.Gates <licences|notices|names|budget|self-test|all> [--root <repo>] [--check] [--deps <deps.json>] [--configuration <Debug|Release>]
var arguments = new Arguments(args);
var root = Path.GetFullPath(arguments.Option("--root") ?? Repo.FindRoot(Directory.GetCurrentDirectory()));
var command = arguments.Command;
var results = new List<GateResult>();

try
{
    switch (command)
    {
        case "licences":
            results.Add(LicenceGate.Run(root));
            break;
        case "notices":
            results.Add(NoticesGate.Run(root, arguments.Option("--deps"), check: arguments.Has("--check"), configuration: arguments.Option("--configuration") ?? "Debug"));
            break;
        case "names":
            results.Add(NameAuditGate.Run(root));
            break;
        case "budget":
            results.Add(BudgetGate.Run(root));
            break;
        case "self-test":
            results.AddRange(SelfTest.Run(root));
            break;
        case "all":
            results.Add(LicenceGate.Run(root));
            results.Add(NoticesGate.Run(root, arguments.Option("--deps"), check: true, configuration: arguments.Option("--configuration") ?? "Debug"));
            results.Add(NameAuditGate.Run(root));
            results.Add(BudgetGate.Run(root));
            break;
        default:
            Console.Error.WriteLine("Usage: Yojana.Gates <licences|notices|names|budget|self-test|all> [--root <repo>] [--check] [--deps <deps.json>] [--configuration <Debug|Release>]");
            return 2;
    }
}
catch (GateException ex)
{
    Console.Error.WriteLine("GATE ERROR: " + ex.Message);
    return 2;
}

var failed = false;
foreach (var result in results)
{
    Console.WriteLine($"[{(result.Passed ? "PASS" : "FAIL")}] {result.Gate}: {result.Summary}");
    foreach (var line in result.Details)
    {
        Console.WriteLine("    " + line);
    }

    failed |= !result.Passed;
}

return failed ? 1 : 0;

namespace Yojana.Gates
{
    public sealed record GateResult(string Gate, bool Passed, string Summary, IReadOnlyList<string> Details);

    public sealed class GateException(string message) : Exception(message);

    public sealed class Arguments(string[] args)
    {
        public string Command => args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "all";

        public bool Has(string flag) => args.Contains(flag, StringComparer.Ordinal);

        public string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }

    public static class Repo
    {
        public static string FindRoot(string start)
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new GateException("Repository root (Directory.Packages.props) not found above " + start + "; pass --root.");
        }

        public static string PackagesFolder()
        {
            var env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            return !string.IsNullOrEmpty(env) ? env : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        }

        public static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

        public static IEnumerable<string> TextFiles(string directory, IReadOnlySet<string> excludeDirectories)
        {
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                foreach (var sub in Directory.EnumerateDirectories(current))
                {
                    if (!excludeDirectories.Contains(Path.GetFileName(sub)))
                    {
                        pending.Push(sub);
                    }
                }

                foreach (var file in Directory.EnumerateFiles(current))
                {
                    yield return file;
                }
            }
        }

        public static bool LooksBinary(string path)
        {
            using var stream = File.OpenRead(path);
            Span<byte> buffer = stackalloc byte[512];
            var read = stream.Read(buffer);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                {
                    return true;
                }
            }

            return false;
        }

        public static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";

        public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    }
}
