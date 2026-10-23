using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Security.Audit;

public enum ConnectionAuditEventType
{
    RelayRegistered,
    RelayRotated,
    RelayRevoked,
    RelayFirstSeen,
    RelayVersionChanged,
    RelayPathDenied,
    CredentialSet,
    CredentialRotated,
    ConnectionRegistered,
    ConnectionArchived,
    WebhookSignatureFailed,
    PermissionsChanged,
    ConnectionWrite,
    StreamStopped,
    StreamResumedByWatchdog,
    CallRateSlowedOnBusy,
    OutOfHoursReplayBooked,
    PasswordDue,
    ConnectionPaused,
    HealthStatusChanged,
    RemoteSubscriptionLeft,
    OnboardingItemConfirmed,
    SyncStopped,
    SyncResumed,
    CoexistenceSettingsChanged,
    WarrantyContractMaintenanceChanged,
    CoexistenceConflictResolved,
    ParallelRunStarted,
    ParallelRunItemConfirmed,
    DocumentSettingsChanged,
    MigrationLicenceConfirmed,
    MigrationLicenceWithdrawn,
    ParallelRunHandoverPlanned,
    ParallelRunHandoverConfirmed,
    GoLiveRecordIssued,
    ReconciliationExplained,
    ConsentGranted,
    ConsentDisabled,
    ConsentRequired,
    ConsentExpiry14Days,
    ConsentExpiry3Days,
}

public sealed record ConnectionAuditEvent(
    TenantId TenantId,
    long Sequence,
    Guid EventId,
    Guid ConnectionId,
    ConnectionAuditEventType EventType,
    string ActorRef,
    DateTime OccurredAt,
    string DetailsJson,
    string PreviousRecordHash,
    string RecordHash);

public sealed record NewConnectionAuditEvent(
    TenantId TenantId,
    Guid EventId,
    Guid ConnectionId,
    ConnectionAuditEventType EventType,
    string ActorRef,
    DateTime OccurredAt,
    string DetailsJson);

public interface IConnectionAuditSink
{
    Task<ConnectionAuditEvent> AppendAsync(NewConnectionAuditEvent auditEvent, CancellationToken cancellationToken);
    IAsyncEnumerable<ConnectionAuditEvent> ReadAsync(TenantId tenantId, DateOnly fromDate, DateOnly toDate,
        CancellationToken cancellationToken);
}

public static class ConnectionAuditChain
{
    public const string InitialHash = "";

    public static ConnectionAuditEvent Seal(NewConnectionAuditEvent auditEvent, long sequence,
        string previousRecordHash)
    {
        Validate(auditEvent);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        var hash = Convert.ToHexString(SHA256.HashData(CanonicalBytes(auditEvent, sequence, previousRecordHash))).ToLowerInvariant();
        return new ConnectionAuditEvent(auditEvent.TenantId, sequence, auditEvent.EventId, auditEvent.ConnectionId,
            auditEvent.EventType, auditEvent.ActorRef, auditEvent.OccurredAt, auditEvent.DetailsJson,
            previousRecordHash, hash);
    }

    public static ConnectionAuditVerification Verify(IEnumerable<ConnectionAuditEvent> events)
    {
        var previous = InitialHash;
        var position = 0;
        foreach (var item in events)
        {
            position++;
            var input = new NewConnectionAuditEvent(item.TenantId, item.EventId, item.ConnectionId,
                item.EventType, item.ActorRef, item.OccurredAt, item.DetailsJson);
            var expected = Seal(input, position, previous);
            if (!StringComparer.Ordinal.Equals(item.PreviousRecordHash, previous) ||
                item.Sequence != position ||
                !StringComparer.Ordinal.Equals(item.RecordHash, expected.RecordHash))
                return new ConnectionAuditVerification(false, position, item.EventId);
            previous = item.RecordHash;
        }
        return new ConnectionAuditVerification(true, null, null);
    }

    private static byte[] CanonicalBytes(NewConnectionAuditEvent item, long sequence, string previousRecordHash)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("tenant_id", item.TenantId.ToString());
            writer.WriteNumber("sequence", sequence);
            writer.WriteString("event_id", item.EventId);
            writer.WriteString("connection_id", item.ConnectionId);
            writer.WriteString("event_type", item.EventType.ToString());
            writer.WriteString("actor_ref", item.ActorRef);
            writer.WriteString("occurred_at", item.OccurredAt);
            writer.WriteString("details_json", item.DetailsJson);
            writer.WriteString("previous_record_hash", previousRecordHash);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void Validate(NewConnectionAuditEvent item)
    {
        if (item.TenantId.IsEmpty || item.EventId == Guid.Empty || item.ConnectionId == Guid.Empty)
            throw new ArgumentException("Tenant, event and connection identifiers are required.");
        if (item.OccurredAt.Kind != DateTimeKind.Utc) throw new ArgumentException("OccurredAt must be UTC.");
        if (string.IsNullOrWhiteSpace(item.ActorRef)) throw new ArgumentException("Actor reference is required.");
        using var _ = JsonDocument.Parse(item.DetailsJson);
    }
}

public sealed record ConnectionAuditVerification(bool IsValid, int? InvalidPosition, Guid? InvalidEventId);

public sealed class InMemoryConnectionAuditSink : IConnectionAuditSink
{
    private readonly object sync = new();
    private readonly Dictionary<TenantId, List<ConnectionAuditEvent>> events = [];

    public Task<ConnectionAuditEvent> AppendAsync(NewConnectionAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!events.TryGetValue(auditEvent.TenantId, out var tenantEvents))
                events[auditEvent.TenantId] = tenantEvents = [];
            var sealedEvent = ConnectionAuditChain.Seal(auditEvent, tenantEvents.Count + 1,
                tenantEvents.LastOrDefault()?.RecordHash ?? ConnectionAuditChain.InitialHash);
            tenantEvents.Add(sealedEvent);
            return Task.FromResult(sealedEvent);
        }
    }

    public async IAsyncEnumerable<ConnectionAuditEvent> ReadAsync(TenantId tenantId, DateOnly fromDate,
        DateOnly toDate, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<ConnectionAuditEvent> snapshot;
        lock (sync) snapshot = events.GetValueOrDefault(tenantId, []).ToList();
        foreach (var item in snapshot.Where(x => DateOnly.FromDateTime(x.OccurredAt) >= fromDate &&
                                                 DateOnly.FromDateTime(x.OccurredAt) <= toDate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }
}
