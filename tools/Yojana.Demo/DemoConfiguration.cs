using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Yojana.Demo;

/// <summary>
/// Where the demo loader writes. The demo exists to fill the database the development web host shows, so it reads the
/// web host's own development settings and user-secrets instead of keeping a second copy that can drift.
/// </summary>
/// <remarks>
/// COUPLED to src/Web/Yojana.Web (see the note beside its UserSecretsId): appsettings.Development.json supplies the
/// operational database, the import-file database, file root and archive stores, and message-storage auto-build;
/// the "yojana-web" user-secrets supply the local key-encryption key. A separate demo copy was the loose option and
/// it broke in practice: files landed in stores the web host never reads, and credentials sealed with a different key
/// could not be opened by the web host. Renaming that file or the secrets id without updating this class makes the
/// demo fail at start-up or write data the web host cannot see.
/// </remarks>
internal static class DemoConfiguration
{
    public const string WebProjectPath = "src/Web/Yojana.Web";
    public const string WebUserSecretsId = "yojana-web";

    /// <summary>The demo is a development tool; an explicit DOTNET_ENVIRONMENT still wins.</summary>
    public static HostApplicationBuilderSettings Settings(string[] args) => new()
    {
        Args = args,
        EnvironmentName = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"))
            ? Environments.Development
            : null,
    };

    /// <summary>Layers the web host's development storage settings and secrets under environment variables and the command
    /// line, then fixes the values that must differ for a one-shot loader.</summary>
    public static void Apply(IConfigurationManager configuration, string repositoryRoot, string[] args)
    {
        var settingsPath = Path.Combine(repositoryRoot, WebProjectPath, "appsettings.Development.json");
        if (!File.Exists(settingsPath))
            throw new InvalidOperationException(
                $"Demo data not loaded: the web host's development settings were not found at '{settingsPath}'.");

        // Only the storage sections: the web host's logging levels would bury the demo's summary under query logs.
        var web = new ConfigurationBuilder().AddJsonFile(settingsPath, optional: false, reloadOnChange: false).Build();
        configuration.AddInMemoryCollection(new[] { "Platform", "Serialisation" }
            .SelectMany(section => web.GetSection(section).AsEnumerable())
            .Where(entry => entry.Value is not null));
        configuration.AddUserSecrets(WebUserSecretsId, reloadOnChange: false);
        // Re-added so an explicit override still beats the web host's files, as it would in the host itself.
        configuration.AddEnvironmentVariables();
        configuration.AddCommandLine(args);

        // The web host owns schema migration; the demo only needs Wolverine's message tables, which the web
        // development settings already ask to be built.
        configuration["Platform:MigrateOnStartup"] = "false";
        configuration["Platform:ServiceName"] = "yojana-demo";
    }
}
