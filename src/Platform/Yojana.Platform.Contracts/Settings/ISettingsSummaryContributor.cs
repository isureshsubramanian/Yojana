namespace Yojana.Platform.Contracts.Settings;

public interface ISettingsSummaryContributor
{
    /// <summary>The settings keys this contributor answers, and the roles that may see each one.</summary>
    IReadOnlyList<SettingsSummaryKey> Keys { get; }

    Task<IReadOnlyList<SettingsSummaryLine>> ReadAsync(
        IReadOnlySet<string> keys,
        CancellationToken cancellationToken);
}

public sealed record SettingsSummaryKey(string Key, IReadOnlyList<string> Roles);

public sealed record SettingsSummaryLine(
    string Key,
    IReadOnlyDictionary<string, long> Counts,
    string? Attention);
