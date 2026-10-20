namespace Yojana.Serialisation.Contracts;

public sealed record CoverMoney(string Currency, decimal Amount);
public sealed record CoverTerm(string PolicyName, DateOnly? StartDate, DateOnly? EndDate, string Status, bool Future);
public sealed record CoverFieldChange(string Field, string? Before, string? After);
public sealed record CoverChange(Guid EventId, string Kind, string FamilyName, string PolicyName, DateOnly BusinessDate,
    int MonthsAfterSale, string? DocumentNumber, int? DocumentLine, decimal? Amount, string? Currency,
    string? PayerName, string ActorName, string? ApproverName, string? Reason, bool Counted, IReadOnlyList<CoverFieldChange> Fields);
public sealed record CoverFamilyComparison(string FamilyName, CoverTerm? AtSale, CoverTerm? Today, string Change,
    string? ClosedReason, IReadOnlyList<CoverChange> Changes);
public sealed record CoverSinceSale(Guid UnitId, string Serial, string ItemName, string? CustomerName, DateOnly? SaleDate,
    string? SaleDocumentNumber, int ChangeCount, IReadOnlyList<CoverMoney> Value, DateOnly? CoveredTo,
    IReadOnlyList<CoverFamilyComparison> Families, IReadOnlyList<CoverChange> Changes, string? Notice);
public sealed record CoverChangeFilters(DateOnly? SoldFrom = null, DateOnly? SoldTo = null, Guid? Customer = null,
    string? ItemFamily = null, string? ChangeKind = null, string? Serial = null);
public sealed record CoverChangeUnit(Guid UnitId, string Serial, string ItemName, string? CustomerName, string? ItemFamilyName,
    DateOnly SaleDate, string? SaleDocumentNumber, int ChangeCount, IReadOnlyList<string> Kinds,
    IReadOnlyList<CoverMoney> Value, DateTime LastChangeAt, IReadOnlyList<CoverChangeLabel> Changes);
public sealed record CoverChangeLabel(string Kind, string FamilyName, string PolicyName);
public sealed record CoverChangePage(IReadOnlyList<CoverChangeUnit> Rows, string? NextCursor);
public sealed record CoverChangeKindSummary(string Kind, int Units, decimal? MedianMonthsAfterSale, IReadOnlyList<CoverMoney> Value);
public sealed record CoverChangeSummary(int UnitsSold, int UnitsChanged, int Changes, IReadOnlyList<CoverMoney> Value,
    IReadOnlyList<CoverChangeKindSummary> ByKind, decimal? MedianMonthsToFirstPurchase);
