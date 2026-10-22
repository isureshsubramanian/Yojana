using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Records;

namespace Yojana.Knowledge.Questions;

public sealed class RecordReferenceLookup(IServiceProvider services)
{
    public async Task<RecordReference> OneAsync(string recordKind, Guid recordId, RecordReferenceViewer viewer,
        CancellationToken cancellationToken)
    {
        if (!RecordKind.All.Contains(recordKind, StringComparer.Ordinal))
            throw new QuestionRuleException("record_kind_unknown", $"'{recordKind}' is not a question record kind.");
        var resolver = services.GetKeyedService<IRecordReferenceResolver>(recordKind)
            ?? throw new QuestionRuleException("record_kind_unavailable", $"Questions on '{recordKind}' are not available in this host.");
        var found = await resolver.ResolveAsync([recordId], viewer, cancellationToken);
        return found.TryGetValue(recordId, out var reference)
            ? reference
            : new RecordReference(recordId, false, null, false);
    }

    public async Task<IReadOnlyDictionary<Guid, RecordReference>> ManyAsync(string recordKind,
        IReadOnlyCollection<Guid> recordIds, RecordReferenceViewer viewer, CancellationToken cancellationToken)
    {
        if (!RecordKind.All.Contains(recordKind, StringComparer.Ordinal))
            throw new QuestionRuleException("record_kind_unknown", $"'{recordKind}' is not a question record kind.");
        if (recordIds.Count > IRecordReferenceResolver.MaxRecordIds)
            throw new ArgumentException($"At most {IRecordReferenceResolver.MaxRecordIds} record references resolve at once.",
                nameof(recordIds));
        var resolver = services.GetKeyedService<IRecordReferenceResolver>(recordKind)
            ?? throw new QuestionRuleException("record_kind_unavailable", $"Questions on '{recordKind}' are not available in this host.");
        return await resolver.ResolveAsync(recordIds, viewer, cancellationToken);
    }
}
