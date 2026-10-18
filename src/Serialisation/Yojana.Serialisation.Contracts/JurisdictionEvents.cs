namespace Yojana.Serialisation.Contracts;

public sealed record JurisdictionRegionGroupSaved(PublishedSummaryHeader Header, Guid RegionGroupId, string Code,
    IReadOnlyList<string> Members) : IPublishedSummaryEvent
{
    public const string Schema = "Jurisdiction.RegionGroupSaved";
    public const string Version = "1.0";
}

public sealed record JurisdictionRuleSavedPublished(PublishedSummaryHeader Header, Guid RuleId, Guid RootRuleId, int RuleVersion,
    string Kind, string Scope, string BuyerClass, string DateTested, DateOnly EffectiveFrom, DateOnly? EffectiveTo)
    : IPublishedSummaryEvent
{
    public const string Schema = "Jurisdiction.RuleSaved";
    public const string Version = "1.0";
}

public sealed record JurisdictionRuleClosedPublished(PublishedSummaryHeader Header, Guid RuleId, DateOnly EffectiveTo)
    : IPublishedSummaryEvent
{
    public const string Schema = "Jurisdiction.RuleClosed";
    public const string Version = "1.0";
}

public sealed record JurisdictionRulePackImported(PublishedSummaryHeader Header, Guid RulePackId,
    int RegionGroupsAdded, int RulesAdded) : IPublishedSummaryEvent
{
    public const string Schema = "Jurisdiction.RulePackImported";
    public const string Version = "1.0";
}
