using System.Text.Json;

namespace Yojana.Serialisation.Contracts;

public static class JurisdictionScope
{
    public const string Any = "any";
    public const string Country = "country";
    public const string Subdivision = "subdivision";
    public const string RegionGroup = "region_group";
    public static readonly IReadOnlyList<string> All = [Any, Country, Subdivision, RegionGroup];
}

public static class JurisdictionBuyerClass
{
    public const string Any = "any";
    public const string Consumer = "consumer";
    public const string Business = "business";
    public static readonly IReadOnlyList<string> All = [Any, Consumer, Business];
}

public static class JurisdictionDateTested
{
    public const string Contract = "contract";
    public const string Purchase = "purchase";
    public const string Delivery = "delivery";
    public const string FirstSale = "first_sale";
    public static readonly IReadOnlyList<string> All = [Contract, Purchase, Delivery, FirstSale];
}

public static class JurisdictionValueType
{
    public const string Months = "months";
    public const string Days = "days";
    public const string WholeNumber = "integer";
    public const string Boolean = "boolean";
    public const string TextLanguage = "text_language";
    public const string PhraseList = "phrase_list";
    public const string StringList = "string_list";
    public const string Enum = "enum";
    public const string Money = "money";
    public const string AssetReference = "asset_reference";
    public const string Json = "json";
}

public static class JurisdictionRuleKind
{
    public const string StatutoryConformityTerm = "statutory_conformity_term";
    public const string PresumptionPeriod = "presumption_period";
    public const string UsedGoodsMinTerm = "used_goods_min_term";
    public const string ImpliedWarrantyDurationBounds = "implied_warranty_duration_bounds";
    public const string RemedyOrder = "remedy_order";
    public const string ShortTermRejectWindow = "short_term_reject_window";
    public const string RepairExtensionMonths = "repair_extension_months";
    public const string RepairExtensionCount = "repair_extension_count";
    public const string RefurbishedReplacementRequiresConsent = "refurbished_replacement_requires_consent";
    public const string RefurbishedLoanAllowed = "refurbished_loan_allowed";
    public const string RepairChoiceNoticeRequired = "repair_choice_notice_required";
    public const string ThirdPartyRepairPreservesCover = "third_party_repair_preserves_cover";
    public const string ProhibitedWording = "prohibited_wording";
    public const string MandatoryStatement = "mandatory_statement";
    public const string GuaranteeDocumentFields = "guarantee_document_fields";
    public const string RequiredLanguages = "required_languages";
    public const string WarrantyDesignation = "warranty_designation";
    public const string DisclosurePriceThreshold = "disclosure_price_threshold";
    public const string PrintedExpiryPolicy = "printed_expiry_policy";
    public const string ManufactureDateOnPackage = "manufacture_date_on_package";
    public const string HarmonisedLabelAsset = "harmonised_label_asset";
    public const string PartsAndRepairAvailability = "parts_and_repair_availability";
    public const string SecureDeletionCapabilityRequired = "secure_deletion_capability_required";
    public const string SanitisationBeforeResale = "sanitisation_before_resale";
    public const string TakebackDuty = "takeback_duty";
    public const string HandlerRegistrationRequired = "handler_registration_required";
    public const string EwasteStorageMaxDays = "ewaste_storage_max_days";
    public const string BatteryPassportRequired = "battery_passport_required";
    public const string RecordRetention = "record_retention";
    public const string ClaimLimitationPeriod = "claim_limitation_period";
    public const string BuyerClassScope = "buyer_class_scope";
}

public sealed record JurisdictionRuleKindRow(string Kind, string ValueType, string? Unit, string Description);

public sealed record JurisdictionRuleContext(
    string Country,
    string? Subdivision,
    string BuyerClass,
    string? ItemClass,
    decimal? Price,
    string? Currency,
    DateOnly? ContractDate,
    DateOnly? PurchaseDate,
    DateOnly? DeliveryDate,
    DateOnly? FirstSaleDate);

public sealed record JurisdictionRuleResolution(
    Guid RuleId,
    string Kind,
    JsonElement Value,
    string Scope,
    string ScopeLabel,
    string BuyerClass,
    string? ItemClass,
    string DateTested,
    DateOnly TestedDate,
    int Precedence,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string? SourceCitation,
    string? SourceNote);

public sealed record RegionGroupRow(
    Guid RegionGroupId,
    string Code,
    string Name,
    IReadOnlyList<string> Members,
    string Version,
    DateTime CreatedAt,
    string CreatedBy,
    DateTime LastModifiedAt,
    string LastModifiedBy);

public sealed record SaveRegionGroup(
    Guid CommandId,
    Guid? RegionGroupId,
    string Code,
    string Name,
    IReadOnlyList<string> Members,
    string? ExpectedVersion = null,
    string ActorRef = "") : IExpectedDocumentVersion;

public sealed record RegionGroupSaved(Guid RegionGroupId, string Version, bool Replayed);

public sealed record JurisdictionRuleRow(
    Guid RuleId,
    Guid RootRuleId,
    Guid? SupersedesRuleId,
    int Version,
    string Kind,
    string Scope,
    string? CountryCode,
    string? SubdivisionCode,
    Guid? RegionGroupId,
    string? RegionGroupName,
    string BuyerClass,
    string? ItemClass,
    decimal? PriceMin,
    decimal? PriceMax,
    string? Currency,
    string DateTested,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    JsonElement Value,
    string? SourceCitation,
    string? SourceNote,
    bool ConfirmedByCounsel,
    DateOnly? CounselConfirmedOn,
    string CreatedBy,
    string ApprovedBy,
    DateTime CreatedAt,
    string VersionToken,
    bool Current);

public sealed record SaveJurisdictionRule(
    Guid CommandId,
    Guid? RuleId,
    string Kind,
    string Scope,
    string? CountryCode,
    string? SubdivisionCode,
    Guid? RegionGroupId,
    string BuyerClass,
    string? ItemClass,
    decimal? PriceMin,
    decimal? PriceMax,
    string? Currency,
    string DateTested,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    JsonElement Value,
    string? SourceCitation,
    string? SourceNote,
    bool ConfirmedByCounsel,
    DateOnly? CounselConfirmedOn,
    string? ExpectedVersion = null,
    string ActorRef = "") : IExpectedDocumentVersion;

public sealed record JurisdictionRuleSaved(Guid RuleId, Guid RootRuleId, int Version, string VersionToken, bool Replayed);

public sealed record CloseJurisdictionRule(
    Guid CommandId,
    Guid RuleId,
    DateOnly EffectiveTo,
    string? ExpectedVersion = null,
    string ActorRef = "") : IExpectedDocumentVersion;

public sealed record JurisdictionRuleClosed(Guid RuleId, DateOnly EffectiveTo, string VersionToken, bool Replayed);

public sealed record RulePackRow(Guid RulePackId, string Code, string Name, string Warning, int RuleCount,
    bool Imported, DateTime? ImportedAt, string? ImportedBy);

public sealed record RulePackPreview(Guid RulePackId, string Name, string Warning,
    IReadOnlyList<RegionGroupPreviewRow> RegionGroups, IReadOnlyList<RulePackRulePreviewRow> Rules);
public sealed record RegionGroupPreviewRow(string Code, string Name, IReadOnlyList<string> Members);
public sealed record RulePackRulePreviewRow(string Kind, string Scope, string ScopeLabel, string BuyerClass,
    string DateTested, DateOnly EffectiveFrom, JsonElement Value, string? SourceCitation, string? SourceNote);

public sealed record ImportRulePack(Guid CommandId, Guid RulePackId, string ActorRef = "");
public sealed record RulePackImported(Guid RulePackId, int RegionGroupsAdded, int RulesAdded, bool Replayed);

public sealed record JurisdictionCatalogue(
    IReadOnlyList<JurisdictionRuleKindRow> Kinds,
    IReadOnlyList<RegionGroupRow> RegionGroups,
    IReadOnlyList<JurisdictionRuleRow> Rules,
    IReadOnlyList<RulePackRow> RulePacks);

public sealed record JurisdictionWhatAppliesRequest(string? Kind, JurisdictionRuleContext Context);
public sealed record JurisdictionWhatAppliesResult(IReadOnlyList<JurisdictionRuleResolution> Rules);

public sealed record JurisdictionMandatoryText(string Text, string Language, string Placement, bool Verbatim, Guid RuleId);

public interface IJurisdictionRuleResolver
{
    Task<IReadOnlyList<JurisdictionRuleResolution>> ResolveAsync(string kind, JurisdictionRuleContext context,
        CancellationToken cancellationToken);
}

public interface IJurisdictionRegionPort
{
    Task<bool> IsKnownScopeAsync(string regionOrCountry, CancellationToken cancellationToken);
    Task<bool> MatchesAsync(string regionOrCountry, string country, string? subdivision,
        CancellationToken cancellationToken);
    Task<IReadOnlySet<string>> ApplicableScopesAsync(string country, string? subdivision,
        CancellationToken cancellationToken);
}

public interface IJurisdictionLabelRulesPort
{
    Task<IReadOnlyList<string>> ForbiddenPhrasesAsync(CancellationToken cancellationToken);
}

public interface IJurisdictionCertificateTextPort
{
    IReadOnlyList<string> PlaceholderPaths { get; }
    Task<IReadOnlyList<JurisdictionMandatoryText>> MandatoryTextAsync(JurisdictionRuleContext context,
        CancellationToken cancellationToken);
}
