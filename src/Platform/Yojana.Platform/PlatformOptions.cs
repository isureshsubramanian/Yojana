using System.Net;
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

    /// <summary>Start Quartz after the host is built. Tests may leave the scheduler stopped and invoke jobs explicitly.</summary>
    public bool StartScheduler { get; set; } = true;

    public string WolverineSchema { get; set; } = "wolverine";

    public string QuartzSchema { get; set; } = "quartz";

    public string ServiceName { get; set; } = "yojana";

    /// <summary>Shared SQL pool capacity. 200 covers the supported 120 interactive sessions plus background work.</summary>
    public int MaxPoolSize { get; set; } = 200;
}

/// <summary>
/// Deployment-wide sign-in settings. Identities are global and may belong to several tenants, so these settings
/// cannot be tenant settings without giving the same user contradictory sign-in rules.
/// </summary>
public sealed class SignInOptions
{
    public const string SectionName = "Platform:SignIn";
    public const string AuditHmacKeyVariable = "YOJANA_SECURITY_AUDIT_HMAC_KEY";

    public int AccountFailureThreshold { get; set; } = 5;
    public int AccountWindowMinutes { get; set; } = 15;
    public int InitialLockoutMinutes { get; set; } = 1;
    public int MaximumLockoutMinutes { get; set; } = 30;
    public int AddressFailureThreshold { get; set; } = 50;
    public int AddressWindowMinutes { get; set; } = 15;
    // The account lockout remains the primary credential defence. This higher address allowance accommodates many
    // users signing in together from an office NAT without weakening the per-account limit.
    public int RequestLimitPerMinute { get; set; } = 120;
    public int SecurityEventRetentionDays { get; set; } = 400;
    public int MaintenanceBatchSize { get; set; } = 1_000;
    public int MaximumPasswordLength { get; set; } = 128;
    public TimeSpan StampValidationInterval { get; set; } = TimeSpan.FromMinutes(1);
    public string AuditHmacKey { get; set; } = string.Empty;

    public void Validate()
    {
        if (AccountFailureThreshold < 1 || AccountWindowMinutes < 1 || InitialLockoutMinutes < 1 ||
            MaximumLockoutMinutes < InitialLockoutMinutes || AddressFailureThreshold < 1 ||
            AddressWindowMinutes < 1 || RequestLimitPerMinute < 1 || SecurityEventRetentionDays < 1 ||
            MaintenanceBatchSize is < 1 or > 10_000 ||
            MaximumPasswordLength < Contracts.Security.PasswordRules.LongestMinimum || StampValidationInterval < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName} contains an invalid sign-in setting.");
        }

        if (string.IsNullOrWhiteSpace(AuditHmacKey) || AuditHmacKey.Length < 32)
        {
            throw new InvalidOperationException(
                $"{SectionName}:AuditHmacKey or {AuditHmacKeyVariable} must provide at least 32 characters for unknown-account audit hashing.");
        }
    }
}

/// <summary>
/// Trusted reverse proxies for framework forwarded-header processing. An empty allow-list disables processing, so an
/// internet client can never make X-Forwarded-For authoritative merely by sending it.
/// </summary>
public sealed class PlatformForwardedHeadersOptions
{
    public const string SectionName = "Platform:ForwardedHeaders";

    public string[] KnownProxies { get; set; } = [];
    public string[] KnownNetworks { get; set; } = [];
    public int ForwardLimit { get; set; } = 1;

    public bool Enabled => KnownProxies.Length > 0 || KnownNetworks.Length > 0;

    public void Validate()
    {
        if (ForwardLimit < 1)
            throw new InvalidOperationException($"{SectionName}:ForwardLimit must be at least one.");
        if (KnownProxies.Any(value => !IPAddress.TryParse(value, out _)))
            throw new InvalidOperationException($"{SectionName}:KnownProxies contains an invalid IP address.");
        if (KnownNetworks.Any(value => !IPNetwork.TryParse(value, out _)))
            throw new InvalidOperationException($"{SectionName}:KnownNetworks contains an invalid CIDR network.");
    }
}
