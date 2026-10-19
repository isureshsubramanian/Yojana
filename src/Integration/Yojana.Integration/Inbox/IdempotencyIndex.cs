using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Tenancy;

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

public sealed class IdempotencyIndex(IntegrationDbContext db)
{
    public Task<IdempotencyClaim?> FindAsync(TenantId tenant, Guid connection, string entity,
        string externalId, string changeMarker, CancellationToken cancellationToken) =>
        db.IdempotencyIndex.SingleOrDefaultAsync(row => row.TenantId == tenant
            && row.ConnectionId == connection && row.ErpEntity == entity
            && row.ErpExternalId == externalId && row.ErpChangeMarker == changeMarker, cancellationToken);

    public void Claim(TenantId tenant, Guid connection, string entity, string externalId,
        string changeMarker, Guid inboundMessageId, DateTime claimedAt) =>
        db.IdempotencyIndex.Add(new IdempotencyClaim
        {
            TenantId = tenant, ConnectionId = connection, ErpEntity = entity,
            ErpExternalId = externalId, ErpChangeMarker = changeMarker,
            InboundMessageId = inboundMessageId, ClaimedAt = claimedAt,
        });
}
