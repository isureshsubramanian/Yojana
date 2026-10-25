namespace Yojana.Serialisation.Contracts;

public static class EntitlementGapTrigger
{
    public const string MigrationLoad = "migration_load";
    public const string OnDemand = "on_demand";
    public const string DailyJob = "daily_job";
}

public static class EntitlementGapDropShip
{
    public const string Include = "include";
    public const string Only = "only";
    public const string Exclude = "exclude";
}

public sealed record EntitlementGapFilters(
    IReadOnlyList<Guid>? ItemIds = null,
    DateOnly? SaleFrom = null,
    DateOnly? SaleTo = null,
    IReadOnlyList<string>? Channels = null,
    string DropShip = EntitlementGapDropShip.Include,
    IReadOnlyList<string>? RequirementStatuses = null,
    string Sort = "sale_date",
    string Direction = "desc");

public sealed record EntitlementGapRow(Guid UnitId, string? PrimarySerial, Guid ItemId, DateTime SaleDate,
    Guid CustomerPartyRef, string Channel, bool DropShip, string? RequirementStatus, string? MissingInput);

public sealed record EntitlementGapPage(IReadOnlyList<EntitlementGapRow> Rows, int Total, int Skip, int Take);

public sealed record RunEntitlementGapReport(Guid CommandId, string Trigger, EntitlementGapFilters Filters);

public sealed record EntitlementGapCount(Guid ItemId, string Channel, bool DropShip, int Count);

public sealed record EntitlementGapRunResult(Guid RunId, DateTime RunAt, string Trigger, int Total,
    IReadOnlyList<EntitlementGapCount> Counts);
