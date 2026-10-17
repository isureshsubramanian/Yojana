namespace Yojana.Serialisation.Contracts;

/// <summary>A target for one of ADR-0001's seven correction root causes, expressed per 1,000 shipped units.</summary>
public sealed record CorrectionKpiTarget(int RootCauseCategory, decimal MaximumPerThousand);

/// <summary>Tenant-owned KPI targets. They are reported only once the tenant has 90 days of correction history.</summary>
public sealed record CorrectionKpiTargetPolicy(IReadOnlyList<CorrectionKpiTarget> RootCauses);

public sealed record CorrectionKpiBaseline(
    string Status,
    int HistoryDays,
    int DaysRemaining,
    DateOnly? StartedOn,
    DateOnly? TargetsEffectiveOn);

/// <summary>One monthly bucket and dimension value from the correction-volume KPI.</summary>
public sealed record CorrectionKpiRow(
    string Bucket,
    string Dimension,
    int Corrections,
    int UnitsCorrected,
    int UnitsShipped,
    decimal? PerThousand,
    int ReversedLater,
    decimal? TargetPerThousand);

public sealed record CorrectionKpiReport(
    DateOnly From,
    DateOnly To,
    string Breakdown,
    Guid? ItemId,
    CorrectionKpiBaseline Baseline,
    IReadOnlyList<CorrectionKpiRow> Rows,
    UnclassifiedRequestAlert? UnclassifiedAlert = null);

public sealed record UnclassifiedRequestAlert(
    decimal ThresholdPercent,
    decimal RatePercent,
    int UnclassifiedRequests,
    int Corrections,
    bool Alert,
    IReadOnlyList<UnclassifiedRequestRate>? Months = null);

public sealed record UnclassifiedRequestRate(
    string Bucket,
    decimal RatePercent,
    int UnclassifiedRequests,
    int Corrections,
    bool Alert);

public static class CorrectionKpiStatus
{
    public const string BaselineBuilding = "baseline_building";
    public const string Ready = "ready";
}

public static class CorrectionKpiBreakdown
{
    public const string RootCause = "root_cause";
    public const string InitiatingRole = "initiating_role";
    public const string ReversedLater = "reversed_later";
    public const string InitiatedVia = "initiated_via";
    public const string Item = "item";

    public static readonly IReadOnlyList<string> All = [RootCause, InitiatingRole, ReversedLater, InitiatedVia, Item];
}
