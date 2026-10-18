using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Certificates;

// TIGHT COUPLING (sale-fact transactional hand-off)
// WHAT:  this certificate participant is invoked by UnitEventWriter (and direct BulkIngestion) after fresh ledger
//        facts are accepted but before their transaction commits. It returns only outbox messages through the narrow
//        IUnitEventAppendObserver contract; neither ledger writer knows certificate commands or policy.
// WHY:   the release-record trigger must commit atomically with every customer-sale path, including set-based document
//        posting, drop shipment and corrective shipment facts.
// WHAT BREAKS IF SEPARATED: a committed sale can have no durable version-one trigger, or a rolled-back sale can issue
//        one. Keep this comment and the UnitEventWriter comment together if the transaction boundary changes.
public sealed class CustomerSaleCertificateObserver(
    SerialisationDbContext db,
    IMessageBus bus,
    ITenantContext tenant,
    LedgerPublication publication) : IUnitEventAppendObserver
{
    public async Task AppendedAsync(IReadOnlyList<UnitEvent> written, CancellationToken cancellationToken)
    {
        if (publication.Suppressed || written.Count == 0) return;

        var saleFacts = written.Where(IsCustomerSaleFact).ToList();
        var salePostings = saleFacts.Select(PostingKey.For).ToHashSet();
        var ownership = written.Where(IsCustomerOwnershipSale)
            .Where(fact => !salePostings.Contains(PostingKey.For(fact)))
            .ToList();

        if (ownership.Count > 0)
        {
            var unitIds = ownership.Select(fact => fact.UnitId).Distinct().ToList();
            var times = ownership.Select(fact => fact.EventTime).Distinct().ToList();
            var stored = await db.Episodes.AsNoTracking()
                .Where(row => unitIds.Contains(row.UnitId) && times.Contains(row.ValidFrom)
                    && row.Kind == EpisodeKind.Custody && row.LocationKind == LocationKind.CustomerSite
                    && row.PartyType == PartyType.Customer && row.Reason == EpisodeReason.Sale)
                .Select(row => new
                {
                    row.UnitId,
                    row.ValidFrom,
                    row.OpeningDocumentType,
                    row.OpeningDocumentId,
                    row.OpeningDocumentLine,
                })
                .ToListAsync(cancellationToken);
            var storedPostings = stored.Select(row => new PostingKey(row.UnitId, row.ValidFrom,
                row.OpeningDocumentType, row.OpeningDocumentId, row.OpeningDocumentLine)).ToHashSet();
            ownership.RemoveAll(fact => storedPostings.Contains(PostingKey.For(fact)));
        }

        foreach (var fact in saleFacts.Concat(ownership).DistinctBy(fact => fact.EventId))
        {
            var schema = fact.EventType is UnitEventType.OwnershipOpened or UnitEventType.OwnershipTransferred
                ? OwnershipOpened.Schema
                : CustodyChanged.Schema;
            var command = new IssueCertificateVersion(fact.EventId, fact.UnitId,
                CertificateDocumentKind.ReleaseRecord, CertificateFormType.GenericRelease,
                CertificateSupersedeReason.Reassembly, fact.EventTime, "system:certificates",
                fact.LegalEntityId, TriggerSchema: schema);
            await bus.PublishAsync(new CertificateTriggerWork(command),
                new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        }

        foreach (var fact in written.Where(IsCorrectiveShipmentReversal))
        {
            await bus.PublishAsync(new CertificateSaleReversalWork(fact.UnitId, fact.EventId,
                    fact.ReversesEventId!.Value, fact.EventTime, fact.LegalEntityId,
                    fact.DocumentType, fact.DocumentId, fact.DocumentLine),
                new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        }
    }

    private static bool IsCustomerSaleFact(UnitEvent fact) =>
        fact.EventType == UnitEventType.Shipped
            ? HasSaleShipment(fact.Attributes)
            : fact.EventType is UnitEventType.CustodyChanged or UnitEventType.Delivered
                && HasSaleTarget(fact.Attributes, LocationKind.CustomerSite);

    private static bool IsCustomerOwnershipSale(UnitEvent fact) =>
        fact.EventType is UnitEventType.OwnershipOpened or UnitEventType.OwnershipTransferred
        && HasSaleTarget(fact.Attributes, null);

    private static bool IsCorrectiveShipmentReversal(UnitEvent fact) =>
        fact.EventType == UnitEventType.ShipmentReversed && fact.ReversesEventId is not null
        && HasProperty(fact.Attributes, "correction_of_document_id");

    private static bool HasSaleTarget(string? attributes, string? locationKind)
    {
        if (attributes is null) return false;
        using var document = JsonDocument.Parse(attributes);
        var root = document.RootElement;
        if (!root.TryGetProperty("reason", out var reason)
            || reason.GetString() != EpisodeReason.Sale)
            return false;

        if (locationKind is null)
            return root.TryGetProperty("party_type", out var partyType)
                && partyType.GetString() == PartyType.Customer;

        return root.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.Object
            && to.TryGetProperty("location_kind", out var kind) && kind.GetString() == locationKind
            && to.TryGetProperty("party_type", out var targetPartyType)
            && targetPartyType.GetString() == PartyType.Customer;
    }

    private static bool HasSaleShipment(string? attributes)
    {
        if (attributes is null) return false;
        using var document = JsonDocument.Parse(attributes);
        var root = document.RootElement;
        return root.TryGetProperty("reason", out var reason) && reason.GetString() == EpisodeReason.Sale;
    }

    private static bool HasProperty(string? attributes, string property)
    {
        if (attributes is null) return false;
        using var document = JsonDocument.Parse(attributes);
        return document.RootElement.TryGetProperty(property, out _);
    }

    private readonly record struct PostingKey(Guid UnitId, DateTime At, string? DocumentType,
        Guid? DocumentId, int? DocumentLine)
    {
        public static PostingKey For(UnitEvent fact) => new(fact.UnitId, fact.EventTime,
            fact.DocumentType, fact.DocumentId, fact.DocumentLine);
    }
}
