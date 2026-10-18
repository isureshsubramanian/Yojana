using System.Runtime.InteropServices;

namespace Yojana.RenderWorker;

public sealed class RenderWorkerOptions
{
    public const string SectionName = "RenderWorker";
    public string ListenUrl { get; set; } = "http://127.0.0.1:5091";
    public int MaximumConcurrentPages { get; set; } = 2;
    public int RecycleAfterRenders { get; set; } = 200;
    public int RenderTimeoutSeconds { get; set; } = 45;
    public BrowserOptions Browser { get; set; } = new();
}

public sealed class BrowserOptions
{
    public string Version { get; set; } = "";
    public string InstallDirectory { get; set; } = ".browser";
    public Dictionary<string, BrowserPlatformOptions> Platforms { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BrowserPlatformOptions
{
    public string ArchiveUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string ExecutableRelativePath { get; set; } = "";
    public string LicenceRelativePath { get; set; } = "";
}

public sealed record BrowserInstallation(string Version, string Platform, string InstallRoot, string ExecutablePath,
    string LicencePath, string ReceiptPath, BrowserPlatformOptions Pin)
{
    public static BrowserInstallation Resolve(BrowserOptions options, string contentRoot)
    {
        var platform = PlatformKey();
        if (!options.Platforms.TryGetValue(platform, out var pin))
            throw new InvalidOperationException($"No pinned headless-shell build is configured for {platform}.");
        var installBase = Path.GetFullPath(options.InstallDirectory, contentRoot);
        var root = Path.Combine(installBase, options.Version, platform);
        var executable = Path.GetFullPath(Path.Combine(root, pin.ExecutableRelativePath));
        var licence = Path.Combine(Path.GetDirectoryName(executable)!, "LICENSE.headless_shell");
        EnsureWithin(root, executable);
        return new BrowserInstallation(options.Version, platform, root, executable, licence,
            Path.Combine(root, "browser-installation.json"), pin);
    }

    private static string PlatformKey()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        if (OperatingSystem.IsMacOS()) return architecture == Architecture.Arm64 ? "macos-arm64" : "macos-x64";
        if (OperatingSystem.IsLinux() && architecture == Architecture.X64) return "linux-x64";
        if (OperatingSystem.IsWindows() && architecture == Architecture.X64) return "windows-x64";
        throw new PlatformNotSupportedException($"No pinned headless-shell archive supports {RuntimeInformation.OSDescription} {architecture}.");
    }

    private static void EnsureWithin(string root, string path)
    {
        var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Browser configuration contains a path outside its install directory.");
    }
}

public sealed record BrowserInstallationReceipt(string Version, string Platform, string ArchiveSha256,
    string ExecutableSha256, DateTime InstalledAtUtc);
