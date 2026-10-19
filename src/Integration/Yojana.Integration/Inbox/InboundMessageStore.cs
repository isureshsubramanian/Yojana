using System.Data;
using Microsoft.EntityFrameworkCore;
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

public sealed class InboundMessageStore(IntegrationDbContext db, IdempotencyIndex index,
    ITenantContext tenant, IClock clock, IChangeMarkerComparer markers)
{
    public async Task<InboundAcceptanceResult> Accept(InboundAcceptance acceptance,
        CancellationToken cancellationToken)
    {
        var envelope = acceptance.Envelope;
        if (tenant.TenantId.Value != envelope.TenantId)
            throw new InvalidOperationException("inbound_tenant_mismatch");
        if (!IntegrationContracts.Accepts(envelope.ContractVersion))
            throw new InvalidOperationException($"contract_major_unsupported: {envelope.ContractVersion}");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var tenantId = tenant.TenantId;
        var existing = await index.FindAsync(tenantId, envelope.ConnectionId, envelope.ErpEntity,
            envelope.ErpExternalId, envelope.ErpChangeMarker.Value, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(InboundAcceptanceResult.AlreadyProcessed, existing.InboundMessageId, false);
        }

        var current = await db.InboundMessages
            .SingleOrDefaultAsync(row => row.TenantId == tenantId && row.ConnectionId == envelope.ConnectionId
                && row.ErpEntity == envelope.ErpEntity && row.ErpExternalId == envelope.ErpExternalId
                && row.IsLatest, cancellationToken);
        var newer = current is null || markers.Compare(envelope.ErpChangeMarker.Value,
            current.ErpChangeMarker, envelope.ErpChangeMarker.MarkerKind.RawValue
                ?? envelope.ErpChangeMarker.MarkerKind.Value) > 0;
        var now = clock.UtcNow;
        var id = TimeOrderedId.New(now);
        if (newer && current is not null)
        {
            current.IsLatest = false;
            current.TranslationStatus = InboundTranslationStatus.Superseded;
        }

        db.InboundMessages.Add(new InboundMessage
        {
            TenantId = tenantId, InboundMessageId = id, MessageId = envelope.MessageId,
            ConnectionId = envelope.ConnectionId, ConnectorCode = envelope.ConnectorCode,
            ErpEntity = envelope.ErpEntity, ErpExternalId = envelope.ErpExternalId,
            ErpChangeMarker = envelope.ErpChangeMarker.Value,
            MarkerKind = envelope.ErpChangeMarker.MarkerKind.RawValue ?? envelope.ErpChangeMarker.MarkerKind.Value,
            ContractVersion = envelope.ContractVersion, MessageType = acceptance.MessageType,
            CaptureMode = envelope.CaptureMode.RawValue ?? envelope.CaptureMode.Value,
            RawPayload = acceptance.RawPayload, CanonicalMessage = acceptance.CanonicalMessage,
            OccurredAt = envelope.OccurredAt.UtcDateTime, ObservedAt = envelope.ObservedAt.UtcDateTime,
            AcceptedAt = now, IsLatest = newer,
            SupersedesInboundMessageId = newer ? current?.InboundMessageId : null,
            TranslationStatus = newer ? InboundTranslationStatus.Pending : InboundTranslationStatus.Superseded,
        });
        index.Claim(tenantId, envelope.ConnectionId, envelope.ErpEntity, envelope.ErpExternalId,
            envelope.ErpChangeMarker.Value, id, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(newer ? InboundAcceptanceResult.Accepted : InboundAcceptanceResult.Superseded, id, newer);
    }
}
