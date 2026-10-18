using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Yojana.RenderWorker;

public static class BrowserHashVerifier
{
    public static async Task<bool> MatchesAsync(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || expectedSha256.Length != 64) return false;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}

public static class BrowserInstaller
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<BrowserInstallation> InstallAsync(BrowserOptions options, string contentRoot,
        HttpClient client, CancellationToken cancellationToken)
    {
        var installation = BrowserInstallation.Resolve(options, contentRoot);
        if (await BrowserInstallationVerifier.IsValidAsync(installation, cancellationToken)) return installation;

        var parent = Directory.GetParent(installation.InstallRoot)?.FullName
            ?? throw new InvalidOperationException("Browser install directory has no parent.");
        Directory.CreateDirectory(parent);
        var archive = Path.Combine(parent, $".{installation.Platform}-{Guid.NewGuid():N}.download.zip");
        var staging = installation.InstallRoot + $".staging-{Guid.NewGuid():N}";
        try
        {
            using (var response = await client.GetAsync(installation.Pin.ArchiveUrl,
                       HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var destination = File.Create(archive);
                await source.CopyToAsync(destination, cancellationToken);
            }
            if (!await BrowserHashVerifier.MatchesAsync(archive, installation.Pin.Sha256, cancellationToken))
                throw new InvalidOperationException($"Downloaded headless-shell archive failed SHA-256 verification for {installation.Platform}.");

            ZipFile.ExtractToDirectory(archive, staging);
            var stagedExecutable = Path.Combine(staging, installation.Pin.ExecutableRelativePath);
            var stagedLicence = Path.Combine(staging, installation.Pin.LicenceRelativePath);
            if (!File.Exists(stagedExecutable) || !File.Exists(stagedLicence))
                throw new InvalidOperationException("Pinned archive does not contain the configured executable and LICENSE.headless_shell.");
            var licenceBesideExecutable = Path.Combine(Path.GetDirectoryName(stagedExecutable)!, "LICENSE.headless_shell");
            if (!Path.GetFullPath(stagedLicence).Equals(Path.GetFullPath(licenceBesideExecutable), StringComparison.Ordinal))
                File.Copy(stagedLicence, licenceBesideExecutable, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stagedExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var executableHash = await BrowserHashVerifier.HashAsync(stagedExecutable, cancellationToken);
            var receipt = new BrowserInstallationReceipt(installation.Version, installation.Platform,
                installation.Pin.Sha256.ToLowerInvariant(), executableHash, DateTime.UtcNow);
            await File.WriteAllTextAsync(Path.Combine(staging, "browser-installation.json"),
                JsonSerializer.Serialize(receipt, JsonOptions), cancellationToken);

            if (Directory.Exists(installation.InstallRoot)) Directory.Delete(installation.InstallRoot, recursive: true);
            Directory.Move(staging, installation.InstallRoot);
            if (!await BrowserInstallationVerifier.IsValidAsync(installation, cancellationToken))
                throw new InvalidOperationException("Installed headless shell failed its post-install integrity check.");
            return installation;
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}

public static class BrowserInstallationVerifier
{
    private const string InstallCommand = "make browser";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task EnsureValidAsync(BrowserInstallation installation, CancellationToken cancellationToken)
    {
        if (!await IsValidAsync(installation, cancellationToken))
            throw new InvalidOperationException($"Pinned headless shell {installation.Version} is missing or failed its integrity check. Run `{InstallCommand}`.");
    }

    public static async Task<bool> IsValidAsync(BrowserInstallation installation, CancellationToken cancellationToken)
    {
        if (!File.Exists(installation.ExecutablePath) || !File.Exists(installation.LicencePath)
            || !File.Exists(installation.ReceiptPath)) return false;
        try
        {
            var receipt = JsonSerializer.Deserialize<BrowserInstallationReceipt>(
                await File.ReadAllTextAsync(installation.ReceiptPath, cancellationToken), JsonOptions);
            return receipt is not null && receipt.Version == installation.Version && receipt.Platform == installation.Platform
                && receipt.ArchiveSha256.Equals(installation.Pin.Sha256, StringComparison.OrdinalIgnoreCase)
                && await BrowserHashVerifier.MatchesAsync(installation.ExecutablePath, receipt.ExecutableSha256, cancellationToken);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
