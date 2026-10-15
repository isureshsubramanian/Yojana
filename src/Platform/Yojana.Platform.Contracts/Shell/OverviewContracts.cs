using System.Text.Json.Serialization;

namespace Yojana.Platform.Contracts.Shell;

/// <summary>
/// The overview page of the web shell (MOD-02) is assembled from contributions. A module that has figures
/// worth a glance implements <see cref="IOverviewContributor"/> and registers it; the shell orders the
/// contributions and renders them without knowing which module they came from. Values are pre-formatted
/// strings on purpose: formatting rules belong to the module that owns the number.
/// </summary>
public interface IOverviewContributor
{
    /// <summary>Lower runs first and shows first. Platform figures use 1000 so module figures lead.</summary>
    int Order { get; }

    Task<OverviewContribution> ContributeAsync(OverviewContext context, CancellationToken cancellationToken);
}

/// <summary>Who is looking, so a contributor can scope or hide figures (for example approvals waiting on this user's roles).</summary>
public sealed record OverviewContext(Guid UserId, IReadOnlyList<string> Roles, DateTime UtcNow);

public sealed record OverviewContribution(
    IReadOnlyList<OverviewKpi> Kpis,
    IReadOnlyList<OverviewPanel> Panels,
    IReadOnlyList<RailSection> Rail)
{
    public static readonly OverviewContribution Empty = new([], [], []);
}

/// <summary>One tile of the KPI strip. <paramref name="Trend"/> is "up", "down", "flat" or null and colours the detail line only.</summary>
public sealed record OverviewKpi(string Label, string Value, string? Detail = null, string? Trend = null);

/// <summary>A table card. <paramref name="Width"/> is the column's share of the table in percent; widths of a panel add up to 100.</summary>
public sealed record OverviewColumn(string Label, bool Numeric = false, int Width = 0);

/// <summary><paramref name="Badge"/> is "ok", "warn", "bad", "info", "neutral" or null (plain text). <paramref name="Mono"/> renders in the serial face.</summary>
public sealed record OverviewCell(string Text, string? Sub = null, string? Badge = null, bool Mono = false);

public sealed record OverviewLink(string Label, string Href);

public sealed record OverviewPanel(
    string Id,
    string Title,
    string? Meta,
    IReadOnlyList<OverviewColumn> Columns,
    IReadOnlyList<IReadOnlyList<OverviewCell>> Rows,
    OverviewLink? Link = null,
    string? Empty = null);

/// <summary>A row in the right rail: a key/value pair when <paramref name="Badge"/> is null, otherwise a titled row with a status badge.</summary>
public sealed record RailRow(string Label, string? Value = null, string? Badge = null, string? BadgeText = null, string? Detail = null);

public sealed record RailSection(string Title, IReadOnlyList<RailRow> Rows);

/// <summary>
/// Connection health for the header pill. Connector modules (MOD-14) register sources; until one exists the
/// platform's own source answers "none" and the header says so rather than inventing a status.
/// Status is "none", "ok", "warn" or "bad".
/// </summary>
public interface IConnectionHealthSource
{
    Task<ConnectionHealth> GetHealthAsync(CancellationToken cancellationToken);
}

public sealed record ConnectionHealth(string Status, string Label, string? Detail = null)
{
    public static readonly ConnectionHealth None = new("none", "No connections configured");

    [JsonIgnore]
    public int Severity => Status switch { "bad" => 3, "warn" => 2, "ok" => 1, _ => 0 };
}
