using System.Data;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Inbox;

// TIGHT COUPLING (declared, ADR-0002 "Coupling declarations")
// WHAT:  InboundMessageStore.Accept(...) writes the raw ERP payload row and calls
//        IdempotencyIndex.Claim(tenant, connection, entity, externalId, changeMarker)
//        inside the SAME database transaction; the index row references the payload row id.
// WHY:   The receiver's guarantee "a message is processed at most once per change marker and
//        its raw payload is always available for replay" must hold at every commit. If the
//        index were claimed first and the payload written later, a crash between the two would
//        leave a claimed key with no payload (the message is lost and will never be retried,
//        because the ERP believes it was delivered); if the payload were written first and the
//        index claimed later, a concurrent delivery of the same message would be processed twice.
//        An outbox or event hand-off between the two writes would reopen exactly those windows.
// WHAT BREAKS IF SEPARATED: silent message loss on crash (the worst failure mode the market
//        notes document for ERP companions), or duplicate processing of the same ERP document,
//        which would create duplicate custody and entitlement events that the reconciliation
//        sweep could only report, not prevent. Change the Claim signature and this comment
//        together, on both sides.
// Engine: SQL Server / Azure SQL; the pairing relies on a single local transaction, never on a
//        distributed one.
// The TranslateInboundMessage envelope is also persisted through Wolverine's IntegrationDbContext outbox before this
// transaction commits. That durable hand-off extends the atomic acceptance boundary, but the downstream translation
// handler is still deliberately outside this tight coupling and may lag or retry independently.
// HIDDEN COUPLING (connection acceptance lock): AcceptMany and IdempotencyIndex share the SQL application-lock name
// `integration:accept:{tenant}:{connection}`. Absent-key range locks can deadlock two concurrent first deliveries;
// serialising only acceptance for one connection closes that window. Changing the lock scope or name on either side
// can reintroduce duplicate acceptance or deadlock victims at the HTTP boundary.

public sealed record InboundAcceptance(CanonicalEnvelope Envelope, string MessageType,
    string CanonicalMessage, string RawPayload);

public sealed record InboundAcceptanceResult(string Outcome, Guid InboundMessageId, bool Translate)
{
    public const string Accepted = "accepted";
    public const string AlreadyProcessed = "already_processed";
    public const string Superseded = "superseded";
}

public interface IChangeMarkerComparer
{
    int Compare(string left, string right, string markerKind);
}

public sealed class ChangeMarkerComparer : IChangeMarkerComparer
{
    public int Compare(string left, string right, string markerKind)
    {
        if (DateTimeOffset.TryParse(left, out var leftTime) && DateTimeOffset.TryParse(right, out var rightTime))
            return leftTime.CompareTo(rightTime);
        if (decimal.TryParse(left, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var leftNumber)
            && decimal.TryParse(right, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var rightNumber))
            return leftNumber.CompareTo(rightNumber);
        return StringComparer.Ordinal.Compare(left, right);
    }
}

public sealed class InboundMessageStore(IDbContextOutbox<IntegrationDbContext> outbox, IdempotencyIndex index,
    ITenantContext tenant, IClock clock, IChangeMarkerComparer markers)
{
    public async Task<InboundAcceptanceResult> Accept(InboundAcceptance acceptance,
        CancellationToken cancellationToken) =>
        (await AcceptMany([acceptance], cancellationToken))[0];

    public async Task<IReadOnlyList<InboundAcceptanceResult>> AcceptMany(
        IReadOnlyList<InboundAcceptance> acceptances, CancellationToken cancellationToken)
    {
        if (acceptances.Count == 0) return [];
        var db = outbox.DbContext;
        foreach (var acceptance in acceptances)
        {
            if (tenant.TenantId.Value != acceptance.Envelope.TenantId)
                throw new InvalidOperationException("inbound_tenant_mismatch");
            if (!IntegrationContracts.Accepts(acceptance.Envelope.ContractVersion))
                throw new InvalidOperationException($"contract_major_unsupported: {acceptance.Envelope.ContractVersion}");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var tenantId = tenant.TenantId;
        var connections = acceptances.Select(x => x.Envelope.ConnectionId).Distinct().ToList();
        foreach (var connection in connections.Order())
            await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_getapplock @Resource={LockName(tenantId, connection)}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000", cancellationToken);
        var entities = acceptances.Select(x => x.Envelope.ErpEntity).Distinct().ToList();
        var externalIds = acceptances.Select(x => x.Envelope.ErpExternalId).Distinct().ToList();
        var changeMarkers = acceptances.Select(x => x.Envelope.ErpChangeMarker.Value).Distinct().ToList();
        var existingClaims = await db.IdempotencyIndex.Where(x => x.TenantId == tenantId
                && connections.Contains(x.ConnectionId) && entities.Contains(x.ErpEntity)
                && externalIds.Contains(x.ErpExternalId) && changeMarkers.Contains(x.ErpChangeMarker))
            .ToListAsync(cancellationToken);
        var claims = existingClaims.ToDictionary(x => new InboundKey(x.ConnectionId, x.ErpEntity,
            x.ErpExternalId, x.ErpChangeMarker), x => x.InboundMessageId);
        var currentRows = await db.InboundMessages.Where(x => x.TenantId == tenantId && x.IsLatest
                && connections.Contains(x.ConnectionId) && entities.Contains(x.ErpEntity)
                && externalIds.Contains(x.ErpExternalId)).ToListAsync(cancellationToken);
        var current = currentRows.ToDictionary(x => new CurrentKey(x.ConnectionId, x.ErpEntity, x.ErpExternalId));
        var results = new List<InboundAcceptanceResult>(acceptances.Count);
        foreach (var acceptance in acceptances)
        {
            var envelope = acceptance.Envelope;
            var claimKey = new InboundKey(envelope.ConnectionId, envelope.ErpEntity,
                envelope.ErpExternalId, envelope.ErpChangeMarker.Value);
            if (claims.TryGetValue(claimKey, out var existingId))
            {
                results.Add(new(InboundAcceptanceResult.AlreadyProcessed, existingId, false));
                continue;
            }

            var currentKey = new CurrentKey(envelope.ConnectionId, envelope.ErpEntity, envelope.ErpExternalId);
            current.TryGetValue(currentKey, out var currentRow);
            var newer = currentRow is null || markers.Compare(envelope.ErpChangeMarker.Value,
            currentRow.ErpChangeMarker, envelope.ErpChangeMarker.MarkerKind.RawValue
                ?? envelope.ErpChangeMarker.MarkerKind.Value) > 0;
            var now = clock.UtcNow;
            var id = TimeOrderedId.New(now);
            if (newer && currentRow is not null)
            {
                currentRow.IsLatest = false;
                currentRow.TranslationStatus = InboundTranslationStatus.Superseded;
            }

            var row = new InboundMessage
            {
                TenantId = tenantId, InboundMessageId = id, MessageId = envelope.MessageId,
                ConnectionId = envelope.ConnectionId, ConnectorCode = envelope.ConnectorCode,
                ErpEntity = envelope.ErpEntity, ErpExternalId = envelope.ErpExternalId,
                ErpChangeMarker = envelope.ErpChangeMarker.Value,
                MarkerKind = envelope.ErpChangeMarker.MarkerKind.RawValue ?? envelope.ErpChangeMarker.MarkerKind.Value,
                ContractVersion = envelope.ContractVersion, MessageType = acceptance.MessageType,
                RawPayload = acceptance.RawPayload, CanonicalMessage = acceptance.CanonicalMessage,
                OccurredAt = envelope.OccurredAt.UtcDateTime, ObservedAt = envelope.ObservedAt.UtcDateTime,
                AcceptedAt = now, IsLatest = newer,
                SupersedesInboundMessageId = newer ? currentRow?.InboundMessageId : null,
                TranslationStatus = newer ? InboundTranslationStatus.Pending : InboundTranslationStatus.Superseded,
                NextTranslationAttemptAt = newer ? now : null,
            };
            db.InboundMessages.Add(row);
            index.Claim(tenantId, envelope.ConnectionId, envelope.ErpEntity, envelope.ErpExternalId,
                envelope.ErpChangeMarker.Value, id, now);
            claims[claimKey] = id;
            if (newer)
            {
                current[currentKey] = row;
                await outbox.PublishAsync(new Gateway.TranslateInboundMessage(id),
                    new DeliveryOptions { TenantId = tenantId.ToString() });
            }
            results.Add(new(newer ? InboundAcceptanceResult.Accepted : InboundAcceptanceResult.Superseded, id, newer));
        }
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
        if (db.Database.CurrentTransaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return results;
    }

    private readonly record struct InboundKey(Guid ConnectionId, string Entity, string ExternalId, string ChangeMarker);
    private readonly record struct CurrentKey(Guid ConnectionId, string Entity, string ExternalId);
    internal static string LockName(TenantId tenantId, Guid connectionId) =>
        $"integration:accept:{tenantId.Value:N}:{connectionId:N}";
}
