using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Integration.Ports;

/// <summary>Question-record boundary for inbound messages shown on the integration exception screens.</summary>
public sealed class IntegrationMessageRecordReferenceResolver(IntegrationDbContext db) : IRecordReferenceResolver
{
    public async Task<IReadOnlyDictionary<Guid, RecordReference>> ResolveAsync(IReadOnlyCollection<Guid> recordIds,
        RecordReferenceViewer viewer, CancellationToken cancellationToken)
    {
        if (recordIds.Count > IRecordReferenceResolver.MaxRecordIds)
            throw new ArgumentException($"At most {IRecordReferenceResolver.MaxRecordIds} record ids resolve in one call.", nameof(recordIds));
        var ids = recordIds.Distinct().ToList();
        var labels = await db.InboundMessages.AsNoTracking().Where(x => ids.Contains(x.InboundMessageId))
            .Select(x => new { Id = x.InboundMessageId, Label = x.MessageType + " · " + x.ErpExternalId })
            .ToDictionaryAsync(x => x.Id, x => x.Label, cancellationToken);
        var mayView = viewer.Roles.Contains(YojanaRole.TenantAdministrator, StringComparer.Ordinal)
            || viewer.Roles.Contains(YojanaRole.IntegrationOperator, StringComparer.Ordinal);
        return ids.ToDictionary(id => id, id => labels.TryGetValue(id, out var label)
            ? new RecordReference(id, true, label, mayView)
            : new RecordReference(id, false, null, false));
    }
}
