namespace Yojana.Bench;

/// <summary>The shared workload contract used to size preparation and drive the run.</summary>
public static class ConcurrencyWorkload
{
    private const int DefaultWeightTotal = 100;
    private const double RmaHeadroom = 1.5;
    private const int MinimumRmaEligibleUnits = 12_000;
    private const int MinimumReplacementCapacity = 12_000;

    public static IReadOnlyDictionary<string, int> Weights(BenchOptions options)
    {
        var defaults = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["unit_lookup"] = 12, ["unit_search"] = 10, ["rma_list"] = 9, ["rma_detail"] = 8,
            ["claim_list"] = 9, ["claim_detail"] = 8, ["rma_create_receive"] = 7,
            ["claim_intake_adjudication"] = 7, ["warranty_replacement_create"] = 5,
            ["refurbishment_append"] = 8, ["document_edit"] = 12, ["background_recheck"] = 5,
        };
        if (string.IsNullOrWhiteSpace(options.Weights)) return defaults;
        var parsed = options.Weights.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .ToDictionary(part => part[0], part => int.Parse(part[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
        if (parsed.Count == 0 || parsed.Values.Any(value => value <= 0))
            throw new ArgumentException("--weights uses operation=positive_integer pairs");
        return parsed;
    }

    public static int RequiredRmaEligibleUnits(BenchOptions options)
    {
        var weightedCapacity = Required(options, "rma_create_receive");
        // A faster server completes substantially more mixed-workload turns than the planning estimate. Keep a
        // bounded floor so short verification runs do not exhaust their pool before they reveal contention.
        return Math.Max(MinimumRmaEligibleUnits, weightedCapacity);
    }

    public static int RequiredReplacementCapacity(BenchOptions options) =>
        Math.Max(MinimumReplacementCapacity, Required(options, "warranty_replacement_create"));

    private static int Required(BenchOptions options, string operation)
    {
        var weights = Weights(options);
        if (!weights.TryGetValue(operation, out var weight)) return 0;
        var normalisedWeight = weight * (double)DefaultWeightTotal / weights.Values.Sum();
        return checked((int)Math.Ceiling(options.Engineers * options.Duration.TotalMinutes
            * normalisedWeight * RmaHeadroom));
    }
}
