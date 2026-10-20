namespace Yojana.Serialisation.Contracts;

public sealed record WhatsCoveredRequest(string? Type = null, string? Search = null, int? Skip = null, int? Take = null, bool SectionPages = false, int CoveredSkip = 0, int CoveredTake = 7, int ExcludedSkip = 0, int ExcludedTake = 7);
public sealed record PolicyCoverItemsRequest(string? Type = null, int? Skip = null, int? Take = null);
public sealed record ClaimCausesRequest(int? Skip = null, int? Take = null);
public sealed record ClaimGuidanceRequest(string Cause);
public sealed record CustomerSheetRequest(string? Language = null);

public sealed record CoverPartsScope(IReadOnlyList<string> PartCategories, IReadOnlyList<string> ItemFamilies)
{
    public static readonly CoverPartsScope All = new([], []);
}

public sealed record PolicyCoverItem(string Code, string Type, string CustomerWording, string? InternalNote,
    string? Category, CoverPartsScope PartsScope, IReadOnlyList<string> CauseCodes, int? SinceVersion = null)
{
    public bool TooLongForCustomerSheet => CustomerWording.Length > 500;
}
public sealed record PolicyCoverItemPage(IReadOnlyList<PolicyCoverItem> Items, int Total, int Skip, int Take, string Version);
public sealed record UpdatePolicyCoverItems(Guid PolicyVersionId, IReadOnlyList<PolicyCoverItem> Items,
    string ActorRef = "", string? ExpectedVersion = null) : IExpectedDocumentVersion;
public sealed record ClaimCause(string Code, string CustomerWording, bool Active = true);
public sealed record ClaimCausePage(IReadOnlyList<ClaimCause> Items, int Total, int Skip, int Take, string Version = "");
public sealed record UpdateClaimCauses(IReadOnlyList<ClaimCause> Items, string ActorRef = "", string? ExpectedVersion = null) : IExpectedDocumentVersion;

public sealed record CoverUsage(string MeterType, decimal Limit, decimal Used, decimal Remaining, string Period);
public sealed record ItemCover(Guid EntitlementId, string FamilyCode, string FamilyName, string PolicyName, int PolicyVersion,
    DateOnly? From, DateOnly? Until, DateOnly AsOf, bool Future, IReadOnlyList<CoverUsage> Usage, string? PolicyCode = null);
public sealed record CoverItemContribution(string Code, string CustomerWording, string? InternalNote,
    string? Category, CoverPartsScope PartsScope, ItemCover Cover, string PartsScopeDescription = "All parts", IReadOnlyList<string>? CauseCodes = null);
public sealed record UnitCoverItem(string Key, string CustomerWording, string? Category, IReadOnlyList<string> CauseCodes,
    bool Covered, IReadOnlyList<CoverItemContribution> Grants, IReadOnlyList<CoverItemContribution> Excludes, string CanBeCovered, IReadOnlyList<string>? CauseNames = null);
public sealed record WhatsCoveredPage(Guid UnitId, string Serial, string ItemName, string? CustomerName,
    DateOnly AsOf, IReadOnlyList<ItemCover> Covers, IReadOnlyList<UnitCoverItem> Items,
    int CoveredCount, int ExcludedCount, int Total, int Skip, int Take, string? Notice, bool CustomerEmailEnabled = false, int CoveredTotal = 0, int ExcludedTotal = 0);
public sealed record GuidanceUsage(string MeterType, string Period, decimal Now, decimal? AfterThisClaim);
public sealed record GuidedCover(ItemCover Cover, IReadOnlyList<GuidanceUsage> Usage, IReadOnlyList<CoverItemContribution> Items);
public sealed record ClaimGuidance(string CauseCode, bool Covered, IReadOnlyList<GuidedCover> Grants,
    IReadOnlyList<CoverItemContribution> Excludes, string SayToCustomer, string Notice);

/// <summary>Customer document boundary for attachment consumers: no policy entities or internal notes cross it.</summary>
public interface IUnitCustomerDocument
{
    Task<CustomerDocument?> CreateAsync(Guid unitId, string language, CancellationToken cancellationToken);
}
public sealed record CustomerDocument(string Title, string Html, byte[]? PdfBytes,
    IReadOnlyDictionary<string, string> MergeValues);
