using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Commands;

public sealed record CustodyTarget(string LocationKind, Guid? LocationId = null, Guid? PartyRef = null, string? PartyType = null, string Reason = "transfer", string Source = "delivery", string Confidence = "high", DateOnly? DueBack = null, string? ExternalOrderRef = null);

public sealed record OwnershipTarget(Guid PartyRef, string PartyType, string Reason, string Source = "invoice", string Confidence = "high", string? CustomerPoRef = null, string? CustomerItemRef = null, bool PartyIsGroupMember = false, string? ExternalOrderRef = null);

public sealed record PreparedCustodyMove(UnitEventDraft Draft, CustodyEpisode Episode, CustodyEpisode? Closed);

public sealed record PreparedOwnershipOpen(UnitEventDraft Draft, UnitEventDraft? CloseDraft, CustodyEpisode Episode, CustodyEpisode? Closed);

/// <summary>
/// The episode mechanics shared by the custody and ownership commands and by the document handlers: close the open row
/// of the kind, open the next, and prepare the event that carries both (the caller appends, possibly many at once).
/// Enforces the one-open-row invariant and the transition tables; the caller chooses the event type (<c>custody.changed</c>,
/// <c>unit.shipped</c>, <c>unit.delivered</c> …) and may add attributes and delta fields.
/// </summary>
public sealed class EpisodeService(SerialisationDbContext db, ITenantContext tenant)
{
    public async Task<PreparedCustodyMove> PrepareCustodyMoveAsync(Guid unitId, CustodyTarget target, string eventType, string idempotencyKey, DateTime at, string actorRef, Guid legalEntityId,
        DocumentRef? document, IDictionary<string, object?>? extraAttributes, CurrentStateDelta? extraDelta, bool enforceTransitions, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var open = await OpenAsync(unitId, EpisodeKind.Custody, cancellationToken);
        if (enforceTransitions && !CustodyTransitions.CustodyAllowed(open?.LocationKind, target.LocationKind))
        {
            throw new CommandRefusedException("custody_transition", $"Custody cannot move from '{open?.LocationKind ?? "(none)"}' to '{target.LocationKind}'.", "see the custody transitions table; a return received at a warehouse goes through quarantine");
        }

        if (open is not null && at < open.ValidFrom)
        {
            throw new CommandRefusedException("effective_before_current", $"The move is dated {at:O}, before the current custody started ({open.ValidFrom:O}).");
        }

        // Invariant 3 (case 37): a child still linked into a parent cannot come back into stock while the parent is out with a customer or in transit.
        if (target.LocationKind is LocationKind.Warehouse or LocationKind.Bin)
        {
            var shippedParent = await (from link in db.AssemblyLinks.AsNoTracking()
                                       join parent in db.Current.AsNoTracking() on link.ParentUnitId equals parent.UnitId
                                       where link.TenantId == tenantId && parent.TenantId == tenantId && link.ChildUnitId == unitId && link.RemovedAt == null
                                             && (parent.LocationKind == LocationKind.CustomerSite || parent.LocationKind == LocationKind.InTransit)
                                       select new { link.ParentUnitId, parent.LocationKind }).FirstOrDefaultAsync(cancellationToken);
            if (shippedParent is not null)
            {
                throw new CommandRefusedException("mismatch_instance", $"Unit {unitId} is still installed in {shippedParent.ParentUnitId}, which is {shippedParent.LocationKind}; it cannot be in stock on its own.", "record the removal (RecordDisassembly or a rework swap) before receiving the child, or receive the parent");
            }
        }

        var eventId = TimeOrderedId.New(at);
        var episode = new CustodyEpisode
        {
            TenantId = tenantId, EpisodeId = TimeOrderedId.New(at), UnitId = unitId, Kind = EpisodeKind.Custody, PartyRef = target.PartyRef, PartyType = target.PartyType ?? (target.PartyRef is null ? PartyType.Us : null),
            LegalEntityId = legalEntityId, LocationId = target.LocationId, LocationKind = target.LocationKind, Reason = target.Reason, DueBack = target.DueBack, Source = target.Source, Confidence = target.Confidence, ValidFrom = at,
            OpeningDocumentType = document?.Type, OpeningDocumentId = document?.Id, OpeningDocumentLine = document?.Line, OpeningEventId = eventId, ExternalOrderRef = target.ExternalOrderRef,
        };
        if (open is not null)
        {
            open.ValidTo = at;
            open.ClosingEventId = eventId;
            open.ClosingDocumentType = document?.Type;
            open.ClosingDocumentId = document?.Id;
            open.ClosingDocumentLine = document?.Line;
        }

        db.Episodes.Add(episode);
        var disposition = DispositionFor(target.LocationKind);
        var attributes = new Dictionary<string, object?>
        {
            ["episode_id"] = episode.EpisodeId,
            ["from"] = open is null ? null : new { location_kind = open.LocationKind, location_id = open.LocationId, party_ref = open.PartyRef },
            ["to"] = new { location_kind = target.LocationKind, location_id = target.LocationId, party_ref = target.PartyRef, party_type = episode.PartyType },
            ["reason"] = target.Reason,
            ["due_back"] = target.DueBack,
            ["previous_episode_id"] = open?.EpisodeId,
        };
        if (extraAttributes is not null)
        {
            foreach (var (key, value) in extraAttributes) attributes[key] = value;
        }

        var delta = (extraDelta ?? new CurrentStateDelta()) with
        {
            Disposition = extraDelta?.Disposition ?? disposition, LocationId = target.LocationId, LocationKind = target.LocationKind, CustodyPartyRef = target.PartyRef, CustodyKind = EpisodeKind.Custody, OpenCustodyEpisodeId = (Guid?)episode.EpisodeId,
        };
        var draft = new UnitEventDraft
        {
            UnitId = unitId, EventTime = at, EventType = eventType, PresetEventId = eventId, BizStep = BizStepFor(open?.LocationKind, target.LocationKind), Disposition = delta.Disposition,
            LocationId = target.LocationId, LegalEntityId = legalEntityId, ActorRef = actorRef, DocumentType = document?.Type, DocumentId = document?.Id, DocumentLine = document?.Line, IdempotencyKey = idempotencyKey,
            Attributes = JsonObjectText.Of(attributes), Delta = delta,
        };
        return new PreparedCustodyMove(draft, episode, open);
    }

    public async Task<PreparedOwnershipOpen> PrepareOwnershipOpenAsync(Guid unitId, OwnershipTarget target, string idempotencyKey, DateTime at, string actorRef, Guid legalEntityId, DocumentRef? document, string? closingReason, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var open = await OpenAsync(unitId, EpisodeKind.Ownership, cancellationToken);
        if (!CustodyTransitions.OwnershipAllowed(open?.PartyType, target.PartyType))
        {
            throw new CommandRefusedException("ownership_transition", $"Ownership cannot pass from '{open?.PartyType ?? "(none)"}' to '{target.PartyType}'.", "see the ownership transitions table; a return goes through us first");
        }

        if (open is not null && at < open.ValidFrom)
        {
            throw new CommandRefusedException("effective_before_current", $"The new ownership is dated {at:O}, before the current one started ({open.ValidFrom:O}).", "date it after the current episode, or use CorrectSourceDocument (P3)");
        }

        var eventId = TimeOrderedId.New(at);
        var episode = new CustodyEpisode
        {
            TenantId = tenantId, EpisodeId = TimeOrderedId.New(at), UnitId = unitId, Kind = EpisodeKind.Ownership, PartyRef = target.PartyRef, PartyType = target.PartyType, LegalEntityId = legalEntityId,
            Reason = target.Reason, Source = target.Source, Confidence = target.Confidence, ValidFrom = at,
            OpeningDocumentType = document?.Type, OpeningDocumentId = document?.Id, OpeningDocumentLine = document?.Line, OpeningEventId = eventId,
            PartyIsGroupMember = target.PartyIsGroupMember, ExternalOrderRef = target.ExternalOrderRef, CustomerPoRef = target.CustomerPoRef, CustomerItemRef = target.CustomerItemRef,
        };
        UnitEventDraft? closeDraft = null;
        if (open is not null)
        {
            open.ValidTo = at;
            open.ClosingEventId = eventId;
            open.ClosingDocumentType = document?.Type;
            open.ClosingDocumentId = document?.Id;
            open.ClosingDocumentLine = document?.Line;
            closeDraft = new UnitEventDraft
            {
                UnitId = unitId, EventTime = at, EventType = UnitEventType.OwnershipClosed, LegalEntityId = legalEntityId, ActorRef = actorRef,
                DocumentType = document?.Type, DocumentId = document?.Id, DocumentLine = document?.Line, IdempotencyKey = IdempotencyKey.Derive(idempotencyKey, "close"),
                Attributes = JsonObjectText.Of(new { episode_id = open.EpisodeId, party_ref = open.PartyRef, party_type = open.PartyType, reason = closingReason ?? target.Reason, replaced_by_episode_id = episode.EpisodeId }),
                Delta = null,
            };
        }

        db.Episodes.Add(episode);
        var draft = new UnitEventDraft
        {
            UnitId = unitId, EventTime = at, EventType = open is null ? UnitEventType.OwnershipOpened : UnitEventType.OwnershipTransferred, PresetEventId = eventId, LegalEntityId = legalEntityId, ActorRef = actorRef,
            DocumentType = document?.Type, DocumentId = document?.Id, DocumentLine = document?.Line, IdempotencyKey = idempotencyKey,
            Attributes = JsonObjectText.Of(new { episode_id = episode.EpisodeId, party_ref = target.PartyRef, party_type = target.PartyType, reason = target.Reason, source = target.Source, confidence = target.Confidence, customer_po_ref = target.CustomerPoRef, customer_item_ref = target.CustomerItemRef, previous_episode_id = open?.EpisodeId }),
            Delta = new CurrentStateDelta { OwnerPartyRef = (Guid?)target.PartyRef, OpenOwnershipEpisodeId = (Guid?)episode.EpisodeId },
        };
        return new PreparedOwnershipOpen(draft, closeDraft, episode, open);
    }

    public async Task<(UnitEventDraft Draft, CustodyEpisode Closed)> PrepareOwnershipCloseAsync(Guid unitId, string reason, string idempotencyKey, DateTime at, string actorRef, Guid legalEntityId, DocumentRef? document, CancellationToken cancellationToken)
    {
        var open = await OpenAsync(unitId, EpisodeKind.Ownership, cancellationToken)
            ?? throw new CommandRefusedException("no_open_ownership", $"Unit {unitId} has no open ownership to close.");
        if (at < open.ValidFrom)
        {
            throw new CommandRefusedException("effective_before_current", $"The close is dated before the episode started ({open.ValidFrom:O}).");
        }

        var eventId = TimeOrderedId.New(at);
        open.ValidTo = at;
        open.ClosingEventId = eventId;
        open.ClosingDocumentType = document?.Type;
        open.ClosingDocumentId = document?.Id;
        open.ClosingDocumentLine = document?.Line;
        var draft = new UnitEventDraft
        {
            UnitId = unitId, EventTime = at, EventType = UnitEventType.OwnershipClosed, PresetEventId = eventId, LegalEntityId = legalEntityId, ActorRef = actorRef,
            DocumentType = document?.Type, DocumentId = document?.Id, DocumentLine = document?.Line, IdempotencyKey = idempotencyKey,
            Attributes = JsonObjectText.Of(new { episode_id = open.EpisodeId, party_ref = open.PartyRef, party_type = open.PartyType, reason }),
            Delta = new CurrentStateDelta { OwnerPartyRef = (Guid?)null, OpenOwnershipEpisodeId = (Guid?)null },
        };
        return (draft, open);
    }

    public Task<CustodyEpisode?> OpenAsync(Guid unitId, string kind, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        // Rows added in this unit of work (an earlier line of the same document) count as open too.
        var local = db.ChangeTracker.Entries<CustodyEpisode>().Select(e => e.Entity).FirstOrDefault(e => e.TenantId == tenantId && e.UnitId == unitId && e.Kind == kind && e.ValidTo == null);
        return local is not null ? Task.FromResult<CustodyEpisode?>(local) : db.Episodes.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.UnitId == unitId && e.Kind == kind && e.ValidTo == null, cancellationToken);
    }

    public static string DispositionFor(string locationKind) => locationKind switch
    {
        LocationKind.InTransit or LocationKind.Returning => UnitDisposition.InTransit,
        LocationKind.Quarantine => UnitDisposition.Returned,
        LocationKind.InRepair => UnitDisposition.InProgress,
        LocationKind.CustomerSite => UnitDisposition.Active,
        _ => UnitDisposition.Available,
    };

    private static string BizStepFor(string? from, string to) => (from, to) switch
    {
        (_, LocationKind.InTransit) => "shipping",
        (LocationKind.InTransit, LocationKind.CustomerSite) => "receiving",
        (_, LocationKind.Returning) => "shipping",
        (_, LocationKind.Quarantine) => "receiving",
        (_, LocationKind.InRepair) => "repairing",
        _ => "storing",
    };
}

/// <summary>Finds the unit behind a document serial (any identifier type, scope by item, then tenant-wide).</summary>
public sealed record ResolvedUnit(Guid UnitId, Guid IdentifierId, string LifecycleStatus, string NormalisedValue, string IdentifierType);

public sealed class UnitResolver(IdentifierService identifiers)
{
    private static readonly string[] FallbackTypes = [IdentifierType.InternalSerial, IdentifierType.ManufacturerSerial, IdentifierType.VendorSerial, IdentifierType.CustomerSerial, IdentifierType.Sgtin, IdentifierType.Imei];

    public async Task<ResolvedUnit?> ResolveAsync(DocumentSerial serial, Guid? itemId, CancellationToken cancellationToken)
    {
        var types = serial.IdentifierType is null ? FallbackTypes : [serial.IdentifierType];
        foreach (var type in types)
        {
            var prepared = identifiers.Prepare(new IdentifierInput(type, serial.Value, serial.IssuerPartyRef), itemId, null);
            if (itemId is null)
            {
                // No item to scope by (a parent serial on a component line): the value must be unique across scopes to resolve.
                var holders = await identifiers.HoldersOfValueAsync(prepared.Policy.Scheme, prepared.Normalised, cancellationToken);
                if (holders.Count == 1)
                {
                    return new ResolvedUnit(holders[0].UnitId, holders[0].IdentifierId, holders[0].LifecycleStatus, prepared.Normalised, type);
                }

                if (holders.Count > 1)
                {
                    throw new CommandRefusedException("serial_ambiguous", $"'{serial.Value}' is open on {holders.Count} units of different items; name the item.", "send the item with the serial, or use the item-qualified lookup");
                }

                continue;
            }

            foreach (var scope in new[] { prepared.ScopeKey, ScopeKeys.Tenant }.Distinct())
            {
                if (await identifiers.HolderOfAsync(prepared.Policy.Scheme, scope, prepared.Normalised, cancellationToken) is { } holder)
                {
                    return new ResolvedUnit(holder.UnitId, holder.IdentifierId, holder.LifecycleStatus, prepared.Normalised, type);
                }
            }
        }

        return null;
    }
}

/// <summary>Case 33: a fact dated inside a closed Finance period posts to the first open period; the business time stays as it was.</summary>
public sealed class PostingPeriods(SerialisationDbContext db, ITenantContext tenant)
{
    public async Task<string> ResolveAsync(DateTime eventTime, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var period = eventTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var lastClosed = await db.FinancePeriods.AsNoTracking().Where(p => p.TenantId == tenantId).OrderByDescending(p => p.Period).Select(p => p.Period).FirstOrDefaultAsync(cancellationToken);
        if (lastClosed is null || string.CompareOrdinal(period, lastClosed) > 0)
        {
            return period;
        }

        var next = DateTime.ParseExact(lastClosed + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).AddMonths(1);
        return next.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task CloseAsync(string period, DateTime closedAt, string source, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (period.Length != 7 || period[4] != '-' || !int.TryParse(period[..4], out _) || !int.TryParse(period[5..], out var month) || month is < 1 or > 12)
        {
            throw new CommandRefusedException("period_invalid", $"'{period}' is not a yyyy-MM period.");
        }

        if (await db.FinancePeriods.AnyAsync(p => p.TenantId == tenantId && p.Period == period, cancellationToken))
        {
            return;
        }

        db.FinancePeriods.Add(new FinancePeriod { TenantId = tenantId, Period = period, ClosedAt = closedAt, Source = source });
    }
}
