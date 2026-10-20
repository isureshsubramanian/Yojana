namespace Yojana.Serialisation.Contracts;

public sealed record WhatsCoveredRequest(string? Type = null, string? Search = null, int? Skip = null, int? Take = null);
public sealed record PolicyCoverItemsRequest(string? Type = null, int? Skip = null, int? Take = null);
public sealed record ClaimCausesRequest(int? Skip = null, int? Take = null);
public sealed record ClaimGuidanceRequest(string Cause);
public sealed record CustomerSheetRequest(string? Language = null);

public sealed record CoverPartsScope(IReadOnlyList<string> PartCategories, IReadOnlyList<string> ItemFamilies)
{
    public static readonly CoverPartsScope All = new([], []);
}

public sealed record PolicyCoverItem(string Code, string Type, string CustomerWording, string? InternalNote,
    string? Category, CoverPartsScope PartsScope, IReadOnlyList<string> CauseCodes, int? SinceVersion = null);
public sealed record PolicyCoverItemPage(IReadOnlyList<PolicyCoverItem> Items, int Total, int Skip, int Take, string Version);
public sealed record UpdatePolicyCoverItems(Guid PolicyVersionId, IReadOnlyList<PolicyCoverItem> Items,
    string ActorRef = "", string? ExpectedVersion = null) : IExpectedDocumentVersion;
public sealed record ClaimCause(string Code, string CustomerWording, bool Active = true);
public sealed record ClaimCausePage(IReadOnlyList<ClaimCause> Items, int Total, int Skip, int Take);
public sealed record UpdateClaimCauses(IReadOnlyList<ClaimCause> Items, string ActorRef = "");

public sealed record CoverUsage(string MeterType, decimal Limit, decimal Used, decimal Remaining, string Period);
public sealed record ItemCover(Guid EntitlementId, string FamilyCode, string FamilyName, string PolicyName, int PolicyVersion,
    DateOnly? From, DateOnly? Until, DateOnly AsOf, bool Future, IReadOnlyList<CoverUsage> Usage, string? PolicyCode = null);
public sealed record CoverItemContribution(string Code, string CustomerWording, string? InternalNote,
    string? Category, CoverPartsScope PartsScope, ItemCover Cover, string PartsScopeDescription = "All parts");
public sealed record UnitCoverItem(string Key, string CustomerWording, string? Category, IReadOnlyList<string> CauseCodes,
    bool Covered, IReadOnlyList<CoverItemContribution> Grants, IReadOnlyList<CoverItemContribution> Excludes, string CanBeCovered);
public sealed record WhatsCoveredPage(Guid UnitId, string Serial, string ItemName, string? CustomerName,
    DateOnly AsOf, IReadOnlyList<ItemCover> Covers, IReadOnlyList<UnitCoverItem> Items,
    int CoveredCount, int ExcludedCount, int Total, int Skip, int Take, string? Notice);
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
