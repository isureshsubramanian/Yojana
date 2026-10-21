namespace Yojana.Integration.LegacyMigration.Templates;

public sealed record TemplateSourceTerms(
    int? DurationValue,
    string DurationUnit,
    bool DurationUnitVerified,
    decimal? PartsPct,
    decimal? LabourPct,
    decimal? TravelPct,
    int? ResponseHours,
    string? ResponseSource,
    string? CoverageHours,
    string? ContractType);

public sealed record TemplateWordingToReview(string Kind, string Text);

public sealed record TemplateBlockView(
    string FamilyCode,
    string FamilyName,
    bool Stacking,
    string Resolution,
    Guid? PolicyId,
    string? PolicyCode,
    int? PolicyVersion,
    string? PolicyName,
    TemplateSourceTerms ProposedTerms);

public sealed record TemplateUsageCounts(
    int Contracts,
    int Serials,
    int ActiveContracts,
    int ActiveSerials);

public sealed record TemplateMappingSummary(
    string TemplateKey,
    string TemplateName,
    string State,
    bool NeedsPerson,
    string? ReviewReason,
    TemplateUsageCounts Usage);

public sealed record TemplatePatternView(
    string PatternKey,
    string PatternLabel,
    string State,
    int TemplateCount,
    TemplateUsageCounts Usage,
    IReadOnlyList<TemplateMappingSummary> Templates);

public sealed record TemplatePatternPage(IReadOnlyList<TemplatePatternView> Items, string? NextCursor);

public sealed record TemplateMappingDetail(
    string TemplateKey,
    string TemplateName,
    string SourceSystem,
    string TemplateSourceKey,
    Guid ExtractionBatchId,
    DateTime ExtractedAt,
    string State,
    bool NeedsPerson,
    string? ReviewReason,
    string PatternKey,
    string PatternLabel,
    TemplateSourceTerms SourceTerms,
    IReadOnlyList<TemplateWordingToReview> WordingToReview,
    TemplateUsageCounts Usage,
    IReadOnlyList<TemplateBlockView> Blocks,
    string? ApprovedBy,
    DateTime? ApprovedAt,
    string Version);

public sealed record ProposeTemplatesRequest(IReadOnlyList<string>? TemplateKeys = null);

public sealed record ProposeTemplatesResponse(int Requested, int Proposed, int Unchanged, int Superseded);

public sealed record EditTemplateBlock(string FamilyCode, Guid? PolicyId = null);

public sealed record UpdateTemplateMappingRequest(IReadOnlyList<EditTemplateBlock> Blocks, string ExpectedVersion);

public sealed record ApprovePatternRequest(string PatternKey);

public sealed record ApprovePatternResponse(string PatternKey, int Approved);

public sealed record TemplateKeywordRule(string Keyword, string FamilyCode);

public sealed record TemplateKeywordRulesView(IReadOnlyList<TemplateKeywordRule> Rules, string Version);

public sealed record UpdateTemplateKeywordRulesRequest(
    IReadOnlyList<TemplateKeywordRule> Rules,
    string ExpectedVersion);
