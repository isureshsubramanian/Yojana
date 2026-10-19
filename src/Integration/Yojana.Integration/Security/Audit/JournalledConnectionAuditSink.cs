using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Security.Audit;

/// <summary>Keeps the paged SQL journal available when immutable storage is selected.</summary>
public sealed class JournalledConnectionAuditSink(IConnectionAuditSink journal, IConnectionAuditSink immutable)
    : IConnectionAuditSink
{
    public async Task<ConnectionAuditEvent> AppendAsync(NewConnectionAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        // TIGHT COUPLING (audit journal composition): IntegrationModule pairs the configured immutable sink with
        // EfConnectionAuditSink here. Their storage APIs admit no shared transaction; separating them loses either
        // the immutable record or the paged connection_event feed. Both maintain independent hash chains.
        // Immutable storage and SQL have independent hash chains and no distributed transaction. Write the durable
        // archive first, so a successful SQL journal append never claims an immutable copy that was not written.
        // A SQL failure after archive success can leave an extra archive event; EventId identifies that attempt.
        await immutable.AppendAsync(auditEvent, cancellationToken);
        return await journal.AppendAsync(auditEvent, cancellationToken);
    }

    public IAsyncEnumerable<ConnectionAuditEvent> ReadAsync(TenantId tenantId, DateOnly fromDate, DateOnly toDate,
        CancellationToken cancellationToken) => journal.ReadAsync(tenantId, fromDate, toDate, cancellationToken);
}

internal sealed record ConfiguredConnectionAuditSink(IConnectionAuditSink Sink);
