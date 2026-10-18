using Microsoft.Data.SqlClient;

namespace Yojana.Platform;

public sealed class PlatformOptions
{
    public const string SectionName = "Platform";

    /// <summary>Environment variable that supplies the SQL login password when the configured connection string carries none.</summary>
    public const string PasswordVariable = "YOJANA_SQL_PASSWORD";

    /// <summary>
    /// SQL Server or Azure SQL connection string shared by every module, Wolverine and Quartz. Configuration
    /// files never carry the password: a string with a user id and no password is completed from
    /// <see cref="PasswordVariable"/> by <see cref="Resolve"/>; integrated or token authentication needs neither.
    /// </summary>
    public required string ConnectionString { get; set; }

    /// <summary>
    /// Applies the environment password to a password-less SQL login string. Fails loudly when a login needs a
    /// password and none is available, so a misconfigured host never falls back to a built-in default.
    /// </summary>
    public static string Resolve(string connectionString, Func<string, string?> environment, int maxPoolSize = 200)
    {
        if (maxPoolSize <= 0) throw new ArgumentOutOfRangeException(nameof(maxPoolSize), "Max pool size must be positive.");
        var builder = new SqlConnectionStringBuilder(connectionString);
        var usesLogin = !string.IsNullOrEmpty(builder.UserID) && !builder.IntegratedSecurity && builder.Authentication == SqlAuthenticationMethod.NotSpecified;
        if (usesLogin && string.IsNullOrEmpty(builder.Password))
        {
            var password = environment(PasswordVariable);
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException($"The connection string uses SQL login '{builder.UserID}' without a password and {PasswordVariable} is not set. Run `make env` (development) or set the variable.");
            }

            builder.Password = password;
        }

        // A 120-engineer deployment needs room for the active HTTP set plus Wolverine, Quartz and health reads.
        // Keeping this explicit also prevents a provider-default change from silently resizing the production pool.
        builder.MaxPoolSize = maxPoolSize;
        return builder.ConnectionString;
    }

    /// <summary>Apply pending migrations at start (development and test only; production runs `--migrate` explicitly).</summary>
    public bool MigrateOnStartup { get; set; }

    /// <summary>Let Wolverine create or update its envelope tables at start (development default; off in production).</summary>
    public bool AutoBuildMessageStorage { get; set; } = true;

    public string WolverineSchema { get; set; } = "wolverine";

    public string QuartzSchema { get; set; } = "quartz";

    public string ServiceName { get; set; } = "yojana";

    /// <summary>Shared SQL pool capacity. 200 covers the supported 120 interactive sessions plus background work.</summary>
    public int MaxPoolSize { get; set; } = 200;
}
