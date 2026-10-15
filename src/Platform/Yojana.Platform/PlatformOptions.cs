namespace Yojana.Platform;

public sealed class PlatformOptions
{
    public const string SectionName = "Platform";

    /// <summary>SQL Server or Azure SQL connection string shared by every module, Wolverine and Quartz.</summary>
    public required string ConnectionString { get; set; }

    /// <summary>Apply pending migrations at start (development and test only; production runs `--migrate` explicitly).</summary>
    public bool MigrateOnStartup { get; set; }

    /// <summary>Let Wolverine create or update its envelope tables at start (development default; off in production).</summary>
    public bool AutoBuildMessageStorage { get; set; } = true;

    public string WolverineSchema { get; set; } = "wolverine";

    public string QuartzSchema { get; set; } = "quartz";

    public string ServiceName { get; set; } = "yojana";
}
