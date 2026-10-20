namespace Yojana.Relay;

public static class RelayCommandLine
{
    public static string ConfigurationPath(string[] args)
    {
        var path = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
        if (path is null && args.Contains("--protect-secrets", StringComparer.Ordinal))
            throw new ArgumentException("--protect-secrets requires a configuration path.");
        return Path.GetFullPath(path ?? Path.Combine(AppContext.BaseDirectory, "relay.json"));
    }
}
