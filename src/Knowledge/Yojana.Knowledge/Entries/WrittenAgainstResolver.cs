using Yojana.Knowledge.Contracts;
using Yojana.Knowledge.Questions;
using Yojana.Serialisation.Contracts;

namespace Yojana.Knowledge.Entries;

/// <summary>Canonicalises picker values through narrow owning-module contracts.</summary>
public sealed class WrittenAgainstResolver(IPolicyVersionPickerPort policyVersions, ScreenReferenceCatalogue screens)
{
    public async Task<IReadOnlyList<WrittenAgainstInput>> ResolveAsync(IReadOnlyList<WrittenAgainstInput> requested,
        IReadOnlyList<WrittenAgainstInput> previous, CancellationToken cancellationToken)
    {
        var policyIds = requested.Where(x => x.Kind == WrittenAgainstKind.PolicyVersion)
            .Select(x => Guid.TryParse(x.Reference, out var id) ? id : (Guid?)null).OfType<Guid>()
            .Distinct().ToList();
        var policies = await policyVersions.ResolveAsync(policyIds, cancellationToken);
        var resolved = new List<WrittenAgainstInput>(requested.Count);
        foreach (var item in requested)
        {
            var reference = item.Reference.Trim();
            if (item.Kind == WrittenAgainstKind.PolicyVersion && Guid.TryParse(reference, out var policyId)
                && policies.TryGetValue(policyId, out var policyLabel))
            {
                resolved.Add(new(item.Kind, policyId.ToString("D"), policyLabel));
                continue;
            }
            if (item.Kind == WrittenAgainstKind.Screen && screens.Resolve(reference) is { } screen)
            {
                resolved.Add(new(item.Kind, screen.Reference, screen.Label));
                continue;
            }
            var legacy = previous.FirstOrDefault(x => x.Kind == item.Kind && x.Reference == reference);
            if (legacy is not null)
            {
                resolved.Add(legacy);
                continue;
            }
            throw new QuestionRuleException("entry_written_against_not_found",
                "Choose a policy version or screen from its reference picker.");
        }
        return resolved;
    }
}
