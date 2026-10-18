using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Jurisdictions;

/// <summary>Maps catalogue commands to their public summary contracts without exposing persistence entities.</summary>
public sealed class JurisdictionPublishedEventMapper(IClock clock)
{
    public JurisdictionRegionGroupSaved Map(SaveRegionGroup command, RegionGroupSaved result) =>
        new(Header(JurisdictionRegionGroupSaved.Schema, command.ActorRef), result.RegionGroupId,
            command.Code.Trim().ToUpperInvariant(), command.Members.Select(value => value.Trim().ToUpperInvariant()).ToList());

    public JurisdictionRuleSavedPublished Map(SaveJurisdictionRule command, JurisdictionRuleSaved result) =>
        new(Header(JurisdictionRuleSavedPublished.Schema, command.ActorRef), result.RuleId, result.RootRuleId, result.Version,
            command.Kind, command.Scope, command.BuyerClass, command.DateTested, command.EffectiveFrom, command.EffectiveTo);

    public JurisdictionRuleClosedPublished Map(CloseJurisdictionRule command) =>
        new(Header(JurisdictionRuleClosedPublished.Schema, command.ActorRef), command.RuleId, command.EffectiveTo);

    public JurisdictionRulePackImported Map(ImportRulePack command, RulePackImported result) =>
        new(Header(JurisdictionRulePackImported.Schema, command.ActorRef), command.RulePackId,
            result.RegionGroupsAdded, result.RulesAdded);

    private PublishedSummaryHeader Header(string schema, string actorRef)
    {
        var now = clock.UtcNow;
        // Catalogue changes are tenant-wide configuration, so there is no unit or legal entity to put in this header.
        return new(schema, "1.0", TimeOrderedId.New(now), now, now, Guid.Empty, actorRef, null);
    }
}
