using System.Globalization;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Settings;

public sealed record ScanningPolicy(int EndPauseMs, int RepeatWindowMs);

/// <summary>Tenant-tunable keyboard-wedge timing. Missing or malformed values use the accepted scanner defaults.</summary>
public sealed class ScanningSettings(ITenantSettings settings)
{
    public const string EndPauseKey = "scan.endPauseMs";
    public const string RepeatWindowKey = "scan.repeatWindowMs";
    public const int DefaultEndPauseMs = 300;
    public const int DefaultRepeatWindowMs = 1500;
    public const int MinimumEndPauseMs = 150;
    public const int MaximumEndPauseMs = 1000;
    public const int MinimumRepeatWindowMs = 0;
    public const int MaximumRepeatWindowMs = 10000;

    // Coupled to the factory registration in SerialisationModule so future handler use cannot expose PlatformDbContext
    // beside SerialisationDbContext in Wolverine's generated dependency graph.

    public async Task<ScanningPolicy> GetAsync(CancellationToken cancellationToken)
    {
        var endPause = await settings.GetAsync(EndPauseKey, cancellationToken);
        var repeatWindow = await settings.GetAsync(RepeatWindowKey, cancellationToken);
        return new(Parse(endPause, DefaultEndPauseMs, MinimumEndPauseMs, MaximumEndPauseMs),
            Parse(repeatWindow, DefaultRepeatWindowMs, MinimumRepeatWindowMs, MaximumRepeatWindowMs));
    }

    public async Task<ScanningPolicy> SetAsync(ScanningPolicy policy, CancellationToken cancellationToken)
    {
        if (policy.EndPauseMs is < MinimumEndPauseMs or > MaximumEndPauseMs)
            throw new CommandRefusedException("scan_end_pause_out_of_range",
                $"Scan end pause must be between {MinimumEndPauseMs} and {MaximumEndPauseMs} milliseconds.");
        if (policy.RepeatWindowMs is < MinimumRepeatWindowMs or > MaximumRepeatWindowMs)
            throw new CommandRefusedException("scan_repeat_window_out_of_range",
                $"Scan repeat window must be between {MinimumRepeatWindowMs} and {MaximumRepeatWindowMs} milliseconds.");

        await settings.SetAsync(EndPauseKey, policy.EndPauseMs.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await settings.SetAsync(RepeatWindowKey, policy.RepeatWindowMs.ToString(CultureInfo.InvariantCulture), cancellationToken);
        return policy;
    }

    private static int Parse(string? value, int fallback, int minimum, int maximum) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= minimum && parsed <= maximum ? parsed : fallback;
}
