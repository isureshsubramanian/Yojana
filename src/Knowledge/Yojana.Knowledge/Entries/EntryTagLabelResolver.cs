using System.Globalization;
using Yojana.Knowledge.Contracts;
using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;

namespace Yojana.Knowledge.Entries;

public sealed record ResolvedEntryTag(string Kind, string Value, string Label);

public sealed class EntryTagLabelResolver(IItemFamilyNamePort itemFamilies, IClaimCauseNamePort claimCauses)
{
    public async Task<IReadOnlyList<ResolvedEntryTag>> ResolveAsync(IReadOnlyList<EntryTagInput> tags,
        CancellationToken cancellationToken)
    {
        if (tags.Count > 50) throw Rule("entry_tags_too_many", "An entry can have at most 50 tags.");
        if (tags.Any(x => !KnowledgeTagKind.All.Contains(x.Kind, StringComparer.Ordinal)
                || string.IsNullOrWhiteSpace(x.Value) || x.Value.Trim().Length > 200))
            throw Rule("entry_tag_invalid", "Each tag needs a known kind and a value of at most 200 characters.");
        var normalised = tags.Select(x => new EntryTagInput(x.Kind, x.Value.Trim())).ToList();
        if (normalised.Select(x => (x.Kind, x.Value)).Distinct().Count() != normalised.Count)
            throw Rule("entry_tag_duplicate", "Do not add the same tag twice.");

        var families = (await itemFamilies.ListAsync(cancellationToken)).ToDictionary(x => x.Code,
            StringComparer.OrdinalIgnoreCase);
        var causes = (await claimCauses.ListAsync(cancellationToken)).ToDictionary(x => x.Code,
            StringComparer.OrdinalIgnoreCase);
        var answer = new List<ResolvedEntryTag>(normalised.Count);
        foreach (var tag in normalised)
        {
            var label = tag.Kind switch
            {
                KnowledgeTagKind.Role when YojanaRole.IsKnown(tag.Value) => Humanise(tag.Value),
                KnowledgeTagKind.Role => throw Rule("entry_role_unknown", $"'{tag.Value}' is not a tenant role."),
                KnowledgeTagKind.ItemFamily when families.TryGetValue(tag.Value, out var family) => family.Name,
                KnowledgeTagKind.ItemFamily => throw Rule("entry_item_family_unknown", $"'{tag.Value}' is not a known item family."),
                KnowledgeTagKind.ClaimCause when causes.TryGetValue(tag.Value, out var cause) => cause.Label,
                KnowledgeTagKind.ClaimCause => throw Rule("entry_claim_cause_unknown", $"'{tag.Value}' is not a known claim cause."),
                _ => Humanise(tag.Value),
            };
            answer.Add(new ResolvedEntryTag(tag.Kind, tag.Value, label));
        }
        return answer;
    }

    private static string Humanise(string value) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' '));
    private static Questions.QuestionRuleException Rule(string rule, string message) => new(rule, message);
}
