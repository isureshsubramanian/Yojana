using Microsoft.EntityFrameworkCore;
using Quartz;
using Wolverine;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Currency;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Replacements;

/// <summary>Closes only the link whose original unit is the accepted physical receipt.</summary>
public static class ReplacementReturnReceiptHandler
{
    public static async Task Handle(RmaReceived message, SerialisationDbContext db, ITenantContext tenant, CancellationToken cancellationToken)
    {
        if (message.Outcome != ReturnOutcome.Accepted || message.ReceivedUnitId is not { } receivedUnitId) return;
        var links = await db.ReplacementLinks.Where(row => row.TenantId == tenant.TenantId
            && row.State == ReplacementLinkState.AwaitingReturn && row.OriginalUnitId == receivedUnitId).ToListAsync(cancellationToken);
        foreach (var link in links) link.State = ReplacementLinkState.ClosedReturned;
    }
}

public static class ExtendReturnDueHandler
{
    public static async Task<ExtendReturnDueResult> Handle(ExtendReturnDue command, SerialisationDbContext db, IUnitEventWriter writer,
        ITenantContext tenant, CancellationToken cancellationToken)
    {
        var link = await db.ReplacementLinks.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.ReplacementLinkId == command.ReplacementLinkId, cancellationToken)
            ?? throw new CommandRefusedException("replacement_link_unknown", $"Replacement link {command.ReplacementLinkId} does not exist.");
        var key = IdempotencyKey.ForCommand("serialisation", command.CommandId, link.OriginalUnitId, ReplacementEventType.ReturnDueExtended, command.EventTime);
        if (await db.Events.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.IdempotencyKey == key && row.EventTime == command.EventTime, cancellationToken) is { } replay)
        {
            using var attributes = System.Text.Json.JsonDocument.Parse(replay.Attributes ?? "{}");
            return new(link.ReplacementLinkId, DateOnly.Parse(attributes.RootElement.GetProperty("previous_return_due").GetString()!),
                DateOnly.Parse(attributes.RootElement.GetProperty("new_return_due").GetString()!), replay.EventId, true);
        }
        if (link.State != ReplacementLinkState.AwaitingReturn || link.ReturnDue is not { } previous)
            throw new CommandRefusedException("replacement_not_awaiting_return", "Only an advance replacement awaiting return can have its due date extended.");
        if (string.IsNullOrWhiteSpace(command.Reason)) throw new CommandRefusedException("extension_reason_required", "A due-date extension needs a reason.");
        if (command.NewReturnDue <= previous) throw new CommandRefusedException("return_due_not_extended", "The new return due date must be later than the current date.");

        var eventId = TimeOrderedId.New(command.EventTime);
        await writer.AppendAsync([new UnitEventDraft
        {
            UnitId = link.OriginalUnitId, EventTime = command.EventTime, EventType = ReplacementEventType.ReturnDueExtended, PresetEventId = eventId,
            LegalEntityId = await LegalEntityAsync(db, tenant.TenantId, link.WarrantyReplacementId!.Value, cancellationToken), ActorRef = command.ActorRef,
            DocumentType = "warranty_replacement", DocumentId = link.WarrantyReplacementId, IdempotencyKey = key,
            Attributes = JsonObjectText.Of(new { replacement_link_id = link.ReplacementLinkId, previous_return_due = previous, new_return_due = command.NewReturnDue, reason = command.Reason.Trim() }),
        }], cancellationToken);
        link.ReturnDue = command.NewReturnDue;
        link.ReturnDueCommandId = command.CommandId;
        return new(link.ReplacementLinkId, previous, command.NewReturnDue, eventId, false);
    }

    internal static Task<Guid> LegalEntityAsync(SerialisationDbContext db, TenantId tenantId, Guid documentId, CancellationToken cancellationToken) =>
        db.WarrantyReplacements.Where(row => row.TenantId == tenantId && row.WarrantyReplacementId == documentId).Select(row => row.LegalEntityId).SingleAsync(cancellationToken);
}

public static class BillNonReturnHandler
{
    public static async Task<BillNonReturnResult> Handle(BillNonReturn command, SerialisationDbContext db, IUnitEventWriter writer,
        ITenantCurrency currency, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var link = await db.ReplacementLinks.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.ReplacementLinkId == command.ReplacementLinkId, cancellationToken)
            ?? throw new CommandRefusedException("replacement_link_unknown", $"Replacement link {command.ReplacementLinkId} does not exist.");
        var key = IdempotencyKey.ForCommand("serialisation", command.CommandId, link.OriginalUnitId, ReplacementEventType.NonReturnBilled, command.EventTime);
        if (link.BillCommandId == command.CommandId)
        {
            var replay = await db.Events.AsNoTracking().SingleAsync(row => row.TenantId == tenant.TenantId && row.IdempotencyKey == key && row.EventTime == command.EventTime, cancellationToken);
            return Result(link, replay.EventId, true);
        }
        if (link.State != ReplacementLinkState.AwaitingReturn)
            throw new CommandRefusedException("replacement_not_awaiting_return", "Only an advance replacement awaiting return can be billed for non-return.");
        await HomeCurrencyGuard.RequireAsync(currency, link.Currency, cancellationToken);
        if (string.IsNullOrWhiteSpace(command.Reason))
            throw new CommandRefusedException("non_return_billing_reason_required", "Billing a non-return needs a reason.");
        if (!NonReturnPriceBasis.All.Contains(command.PriceBasis)) throw new CommandRefusedException("non_return_price_basis_unknown", $"'{command.PriceBasis}' is not a non-return price basis.");
        if (command.Amount <= 0) throw new CommandRefusedException("non_return_amount_invalid", "The billed non-return amount must be greater than zero.");
        if (command.BillingDate > DateOnly.FromDateTime(command.EventTime)) throw new CommandRefusedException("billing_date_in_future", "The billing date cannot be after its business event time.");

        var eventId = TimeOrderedId.New(command.EventTime);
        await writer.AppendAsync([new UnitEventDraft
        {
            UnitId = link.OriginalUnitId, EventTime = command.EventTime, EventType = ReplacementEventType.NonReturnBilled, PresetEventId = eventId,
            LegalEntityId = await ExtendReturnDueHandler.LegalEntityAsync(db, tenant.TenantId, link.WarrantyReplacementId!.Value, cancellationToken), ActorRef = command.ActorRef,
            DocumentType = "warranty_replacement", DocumentId = link.WarrantyReplacementId, IdempotencyKey = key,
            Attributes = JsonObjectText.Of(new { replacement_link_id = link.ReplacementLinkId, price_basis = command.PriceBasis, amount = command.Amount, currency = link.Currency, billing_date = command.BillingDate, reason = command.Reason.Trim() }),
        }], cancellationToken);
        link.State = ReplacementLinkState.ClosedBilled;
        link.BillCommandId = command.CommandId;
        link.BilledOn = command.BillingDate;
        link.BilledPriceBasis = command.PriceBasis;
        link.BilledAmount = command.Amount;
        link.BilledAt = clock.UtcNow;
        link.BilledBy = command.ActorRef;
        return Result(link, eventId, false);
    }

    private static BillNonReturnResult Result(ReplacementLink link, Guid eventId, bool replayed) =>
        new(link.ReplacementLinkId, link.State, link.BilledPriceBasis!, link.BilledAmount!.Value, link.Currency, eventId, replayed);
}

public static class LiftReplacementHoldHandler
{
    public static async Task<LiftReplacementHoldResult> Handle(LiftReplacementHold command, SerialisationDbContext db,
        ITenantContext tenant, CancellationToken cancellationToken)
    {
        var replay = await db.PartyReplacementHolds.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.LiftCommandId == command.CommandId, cancellationToken);
        if (replay is not null) return new(replay.PartyReplacementHoldId, replay.PartyRef, replay.LiftedAt!.Value, true);
        if (string.IsNullOrWhiteSpace(command.Reason)) throw new CommandRefusedException("hold_lift_reason_required", "Lifting a replacement hold needs a reason.");
        var hold = await db.PartyReplacementHolds.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.PartyRef == command.PartyRef && row.LiftedAt == null, cancellationToken)
            ?? throw new CommandRefusedException("replacement_hold_not_open", "The party has no open replacement hold.");
        hold.LiftedAt = command.EventTime;
        hold.LiftedBy = command.ActorRef;
        hold.LiftReason = command.Reason.Trim();
        hold.LiftCommandId = command.CommandId;
        return new(hold.PartyReplacementHoldId, hold.PartyRef, hold.LiftedAt.Value, false);
    }
}

public sealed record RunReplacementOverdue(DateOnly AsOf, DateTime EventTime);
public sealed record ReplacementOverdueRunResult(int Published, int HoldsSet);

public static class ReplacementOverdueSweepHandler
{
    public static async Task<ReplacementOverdueRunResult> Handle(RunReplacementOverdue command, SerialisationDbContext db, IUnitEventWriter writer,
        ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var links = await db.ReplacementLinks.Where(row => row.TenantId == tenantId && row.State == ReplacementLinkState.AwaitingReturn
            && row.ReturnDue < command.AsOf && row.OverdueEventId == null).OrderBy(row => row.ReturnDue).ThenBy(row => row.ReplacementLinkId).ToListAsync(cancellationToken);
        if (links.Count == 0) return new(0, 0);
        var documentIds = links.Select(row => row.WarrantyReplacementId!.Value).Distinct().ToList();
        var entities = await db.WarrantyReplacements.AsNoTracking().Where(row => row.TenantId == tenantId && documentIds.Contains(row.WarrantyReplacementId))
            .ToDictionaryAsync(row => row.WarrantyReplacementId, row => row.LegalEntityId, cancellationToken);
        var partyRefs = links.Select(row => row.CounterpartyPartyRef).OfType<Guid>().Distinct().ToList();
        var openHolds = await db.PartyReplacementHolds.Where(row => row.TenantId == tenantId && partyRefs.Contains(row.PartyRef) && row.LiftedAt == null).ToListAsync(cancellationToken);
        var openParties = openHolds.Select(row => row.PartyRef).ToHashSet();
        var drafts = new List<UnitEventDraft>(links.Count);
        var holdsSet = 0;
        foreach (var link in links)
        {
            var partyRef = link.CounterpartyPartyRef ?? throw new CommandRefusedException("replacement_counterparty_missing", $"Replacement link {link.ReplacementLinkId} has no counterparty.");
            var due = link.ReturnDue!.Value;
            // The overdue fact starts at the first instant after the contractual due date; the nightly run time is only its recording time.
            var overdueAt = due.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1);
            var eventId = TimeOrderedId.New(overdueAt);
            drafts.Add(new UnitEventDraft
            {
                UnitId = link.OriginalUnitId, EventTime = overdueAt, EventType = ReplacementEventType.ReturnOverdue, PresetEventId = eventId,
                LegalEntityId = entities[link.WarrantyReplacementId!.Value], ActorRef = "system:replacement-overdue", DocumentType = "warranty_replacement",
                DocumentId = link.WarrantyReplacementId,
                IdempotencyKey = IdempotencyKey.ForCommand("serialisation", link.ReplacementLinkId, link.OriginalUnitId, ReplacementEventType.ReturnOverdue, overdueAt),
                Attributes = JsonObjectText.Of(new { replacement_link_id = link.ReplacementLinkId, original_unit_id = link.OriginalUnitId,
                    replacement_unit_id = link.ReplacementUnitId, party_ref = partyRef, return_due = due,
                    non_return_amount = link.NonReturnAmount, currency = link.Currency, non_return_price_basis = link.NonReturnPriceBasis, days_overdue = command.AsOf.DayNumber - due.DayNumber }),
            });
            link.OverdueEventId = eventId;
            link.OverduePublishedAt = clock.UtcNow;
            if (openParties.Add(partyRef))
            {
                db.PartyReplacementHolds.Add(new PartyReplacementHold
                {
                    TenantId = tenantId, PartyReplacementHoldId = TimeOrderedId.New(command.EventTime), PartyRef = partyRef,
                    Reason = "advance_replacement_overdue", SetAt = clock.UtcNow, SetBy = "system:replacement-overdue", SetCommandId = link.ReplacementLinkId,
                });
                holdsSet++;
            }
        }
        await writer.AppendAsync(drafts, cancellationToken);
        return new(links.Count, holdsSet);
    }
}

[DisallowConcurrentExecution]
public sealed class ReplacementOverdueJob(ITenantContextSetter setter, ITenantContext tenant, IMessageBus bus, IClock clock) : TenantJob(setter)
{
    public const string JobName = "serialisation.replacement_overdue";
    public const string Cron = "0 0 2 * * ?";

    protected override async Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await bus.InvokeAsync<ReplacementOverdueRunResult>(new RunReplacementOverdue(DateOnly.FromDateTime(now), now),
            new DeliveryOptions { TenantId = tenant.TenantId.ToString() }, cancellationToken);
    }
}
