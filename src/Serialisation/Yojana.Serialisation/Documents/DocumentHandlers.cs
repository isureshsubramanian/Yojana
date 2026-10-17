using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;
using Wolverine;

namespace Yojana.Serialisation.Documents;

// Consumed-event handlers (MOD-05, P1-T15). Each one: fetch the document through the publisher's port, resolve the
// serials to units (or commission them where the document creates units), prepare one event per unit per line with a
// deterministic idempotency key (source, document, line, unit, event type, business time), append once, and report what
// happened per line. A replayed message produces duplicates only, never a second effect. Lines the rules stop are
// reported as exceptions and skipped: a document never fails as a whole because one serial is wrong (ADR-0002 degradation).
// Reads are set-based (P1-T24): a document's serials resolve in one query per item, its idempotency keys are checked in
// one, and the open episodes and current rows of its units load in one each before the per-unit rules run in memory;
// the rules themselves are the per-unit ones of EpisodeService, unchanged. 500 serials cost about 1,500 statements before.

internal static class Documents
{
    public static string Key(ThinEnvelope env, DocumentRef doc, int? line, Guid unitId, string eventType, DateTime at) => IdempotencyKey.For(env.Source, doc.Type, doc.Id, line, unitId, eventType, at);

    public static DocumentRef Line(DocumentRef doc, int line) => new(doc.Type, doc.Id, line);

    /// <summary>
    /// Resolves every serial of every line, one query per item; unknown serials become exceptions, in line order. Only
    /// for documents that do not commission units part-way: the map is the database as it was before the first line.
    /// </summary>
    public static async Task<List<(DocumentLine Line, DocumentSerial Serial, ResolvedUnit Unit)>> ResolveAllAsync(DocumentContext ctx, IReadOnlyList<DocumentLine> lines, DocumentRef doc, List<string> exceptions, CancellationToken ct)
    {
        var maps = new Dictionary<Guid, Dictionary<DocumentSerial, ResolvedUnit?>>();
        foreach (var item in lines.Where(l => l.Serials.Count > 0).GroupBy(l => l.ItemId))
        {
            maps[item.Key] = await ctx.Resolver.ResolveManyAsync(item.SelectMany(l => l.Serials).ToList(), item.Key, ct);
        }

        var resolved = new List<(DocumentLine, DocumentSerial, ResolvedUnit)>();
        foreach (var line in lines)
        {
            CheckQuantity(line, doc, exceptions);
            foreach (var serial in line.Serials)
            {
                if (maps[line.ItemId][serial] is not { } unit)
                {
                    exceptions.Add($"{doc.Type} {doc.Id:N} line {line.LineNo}: unknown serial '{serial.Value}'");
                    continue;
                }

                resolved.Add((line, serial, unit));
            }
        }

        return resolved;
    }

    /// <summary>
    /// The second half of the set-based read: which of these keys the ledger holds already, and the open episodes of
    /// the units still to move, loaded once. Returns the keys seen; the caller skips those units as before.
    /// </summary>
    public static async Task<HashSet<(string Key, DateTime At)>> PrefetchAsync(DocumentContext ctx, IReadOnlyCollection<(Guid UnitId, string Key, DateTime At)> moves, CancellationToken ct)
    {
        var seen = await ctx.SeenManyAsync(moves.Select(m => (m.Key, m.At)).ToList(), ct);
        await ctx.Episodes.PreloadOpenAsync(moves.Where(m => !seen.Contains((m.Key, m.At))).Select(m => m.UnitId).Distinct().ToList(), ct);
        return seen;
    }

    /// <summary>Invariant 5 (case 12): a unit is one base unit, so a line's quantity is its serial count; conversion is the document's concern and never reaches here.</summary>
    public static void CheckQuantity(DocumentLine line, DocumentRef doc, List<string> exceptions)
    {
        if (line.Serials.Count > 0 && line.Quantity != line.Serials.Count)
        {
            exceptions.Add($"{doc.Type} {doc.Id:N} line {line.LineNo}: quantity_serial_mismatch (quantity {line.Quantity}, {line.Serials.Count} serials); one serial per base unit");
        }
    }

    /// <summary>Evaluates the tenant scan rule once per serial-bearing line; absent markers mean scanned for compatibility.</summary>
    public static Dictionary<int, SerialConfirmationDecision> SerialConfirmations(IReadOnlyList<DocumentLine> lines, DocumentRef doc, string mode, List<string> exceptions)
    {
        var decisions = new Dictionary<int, SerialConfirmationDecision>();
        foreach (var line in lines.Where(l => l.Serials.Count > 0))
        {
            var decision = SerialConfirmationRules.Evaluate(mode, line.SerialCaptureMethod);
            decisions[line.LineNo] = decision;
            if (decision.Refusal is { } refusal)
            {
                exceptions.Add($"{doc.Type} {doc.Id:N} line {line.LineNo}: {refusal.Rule}: {refusal.Message}; unblocking step: {refusal.UnblockingStep}");
            }
        }

        return decisions;
    }

    /// <summary>The old document-wide flag remains a compatibility input; an explicit line marker always wins.</summary>
    public static IReadOnlyList<DocumentLine> ShipmentLines(ShipmentDocument document) =>
        document.SerialsCopiedWithoutScan
            ? document.Lines.Select(l => l.SerialCaptureMethod is null ? l with { SerialCaptureMethod = SerialCaptureMethodValue.Copied } : l).ToList()
            : document.Lines;

    public static CommissionUnits CommissionFor(ThinEnvelope env, DocumentRef doc, DocumentLine line, string origin, DateTime at, Guid? locationId, string? locationKind, string? source, IEnumerable<DocumentSerial> serials) =>
        new(DeterministicCommandId(env, doc, line.LineNo), line.ItemId, origin,
            serials.Select(s => new CommissionLine([new IdentifierInput(s.IdentifierType ?? IdentifierType.InternalSerial, s.Value, s.IssuerPartyRef)], s.Value, line.LotId, line.ExpiryDate)).ToList(),
            at, env.Source, env.LegalEntityId, Document: Line(doc, line.LineNo), LocationId: locationId, LocationKind: locationKind, Source: source, SkipInvalidLines: true);

    /// <summary>Commands derived from a document are replayable: their id is a function of the document, not of the run.</summary>
    public static Guid DeterministicCommandId(ThinEnvelope env, DocumentRef doc, int line)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{env.Source}|{doc.Type}|{doc.Id:N}|{line}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>A large document's rows go through the bulk write path (P1-T26); a small one is cheaper through SaveChanges.</summary>
    public static async Task FlushIfLargeAsync(DocumentContext ctx, AppendResult appended, CancellationToken ct)
    {
        if (appended.Written.Count >= ctx.Options.BulkWriteThreshold) await ctx.Bulk.FlushTrackedAsync(ct);
    }

    public static async Task<ConsumedEventResult> ReportAsync(DocumentContext ctx, string eventType, DocumentRef? doc, List<UnitEventDraft> drafts, List<string> exceptions, int pending, CancellationToken ct)
    {
        var appended = drafts.Count == 0 ? new AppendResult([], []) : await ctx.Writer.AppendAsync(drafts, ct);
        await FlushIfLargeAsync(ctx, appended, ct);
        return new ConsumedEventResult(eventType, doc, appended.Written.Select(e => e.UnitId).Distinct().Count(), pending, exceptions, drafts.Count > 0 && appended.Written.Count == 0);
    }
}

public static class PurchasingReceiptPostedHandler
{
    public static Task<ConsumedEventResult> Handle(PurchasingReceiptPosted message, DocumentContext ctx, IMessageBus bus, CancellationToken ct) => ctx.InTransactionAsync(async () =>
    {
        var doc = await ctx.Purchasing.GetReceiptAsync(message.Document, ct) ?? throw new CommandRefusedException("document_unavailable", $"Receipt {message.Document.Id:N} could not be read through the purchasing port.");
        var exceptions = new List<string>();
        var drafts = new List<UnitEventDraft>();
        var affected = 0;
        foreach (var line in doc.Lines)
        {
            if (line.Serials.Count == 0)
            {
                exceptions.Add($"line {line.LineNo}: no serials ({message.Envelope.AttributionCompleteness})");
                continue;
            }

            // Pre-announced units (ASN) exist already: they move from in transit to the warehouse. New serials are commissioned purchased, released.
            Documents.CheckQuantity(line, doc.Document, exceptions);
            var fresh = new List<DocumentSerial>();
            // Per line, not per document: a line commissions its new serials before the next line resolves its own.
            var units = await ctx.Resolver.ResolveManyAsync(line.Serials, line.ItemId, ct);
            var applied = units.Values.Any(u => u is not null) ? await ctx.UnitsAppliedAsync(doc.Document, line.LineNo, ct) : [];
            var seen = await Documents.PrefetchAsync(ctx, units.Values.OfType<ResolvedUnit>().Where(u => !applied.Contains(u.UnitId))
                .Select(u => (u.UnitId, Documents.Key(message.Envelope, doc.Document, line.LineNo, u.UnitId, UnitEventType.CustodyChanged, doc.PostedAt), doc.PostedAt)).ToList(), ct);
            foreach (var serial in line.Serials)
            {
                var unit = units[serial];
                if (unit is null)
                {
                    fresh.Add(serial);
                    continue;
                }

                if (unit.LifecycleStatus is LifecycleStatus.Scrapped or LifecycleStatus.ScrappedPreRelease or LifecycleStatus.Void or LifecycleStatus.Decommissioned)
                {
                    exceptions.Add($"line {line.LineNo}: serial '{serial.Value}' belongs to a {unit.LifecycleStatus} unit");
                    continue;
                }

                if (applied.Contains(unit.UnitId))
                {
                    continue; // this receipt line commissioned the unit on an earlier delivery of the same message
                }

                var key = Documents.Key(message.Envelope, doc.Document, line.LineNo, unit.UnitId, UnitEventType.CustodyChanged, doc.PostedAt);
                if (seen.Contains((key, doc.PostedAt))) { continue; }
                var move = await ctx.Episodes.PrepareCustodyMoveAsync(unit.UnitId, new CustodyTarget(line.LocationKind ?? LocationKind.Warehouse, line.LocationId, null, null, EpisodeReason.Receipt, EpisodeSource.Receipt), UnitEventType.CustodyChanged, key, doc.PostedAt, message.Envelope.Source, message.Envelope.LegalEntityId, Documents.Line(doc.Document, line.LineNo),
                    new Dictionary<string, object?> { ["message_id"] = message.Envelope.MessageId, ["vendor_party_ref"] = doc.VendorPartyRef }, null, false, ct);
                drafts.Add(move.Draft);
                affected++;
            }

            if (fresh.Count > 0)
            {
                var result = await ctx.CommissionAsync(Documents.CommissionFor(message.Envelope, doc.Document, line, UnitOrigin.Purchased, doc.PostedAt, line.LocationId, line.LocationKind ?? LocationKind.Warehouse, EpisodeSource.Receipt, fresh), ct);
                affected += result.Commissioned;
                exceptions.AddRange(result.Lines.Where(l => l.Outcome == CommissionLineResult.Refused).Select(l => $"line {line.LineNo}: serial '{l.ClientReference}' refused: {string.Join(", ", l.Reasons)}"));
            }
        }

        var report = await Documents.ReportAsync(ctx, "Purchasing.ReceiptPosted", doc.Document, drafts, exceptions, 0, ct);
        // Published while the receipt transaction is open: Wolverine holds this follow-on in the transactional outbox,
        // so supplier-term work cannot lengthen the receipt's 5 s path and cannot be lost after a successful commit.
        await bus.PublishAsync(new CreateInboundEntitlements(message.Envelope, doc.Document), new DeliveryOptions { TenantId = ctx.Tenant.TenantId.ToString() });
        return report with { UnitsAffected = affected, Duplicate = affected == 0 && exceptions.Count == 0 };
    }, ct);
}

public static class PurchasingAsnReceivedHandler
{
    public static Task<ConsumedEventResult> Handle(PurchasingAsnReceived message, DocumentContext ctx, CancellationToken ct) => ctx.InTransactionAsync(async () =>
    {
        var doc = await ctx.Purchasing.GetReceiptAsync(message.Document, ct) ?? throw new CommandRefusedException("document_unavailable", $"ASN {message.Document.Id:N} could not be read through the purchasing port.");
        var exceptions = new List<string>();
        var affected = 0;
        var dropShip = doc.VendorShipment is { } shipmentRef ? await ctx.Purchasing.GetVendorShipmentAsync(shipmentRef, ct) : null;
        var resolvedDropShipLines = new HashSet<int>();
        foreach (var line in doc.Lines)
        {
            var known = await ctx.Resolver.ResolveManyAsync(line.Serials, line.ItemId, ct);
            var fresh = line.Serials.Where(s => known[s] is null).ToList();
            if (fresh.Count > 0)
            {
                // Units the vendor announced: they exist from now with custody in transit under the vendor (case 26 starts here).
                var result = await ctx.CommissionAsync(Documents.CommissionFor(message.Envelope, doc.Document, line, UnitOrigin.VendorAsn, doc.PostedAt, null, LocationKind.InTransit, EpisodeSource.Receipt, fresh), ct);
                affected += result.Commissioned;
                exceptions.AddRange(result.Lines.Where(l => l.Outcome == CommissionLineResult.Refused).Select(l => $"line {line.LineNo}: serial '{l.ClientReference}' refused: {string.Join(", ", l.Reasons)}"));
            }

            if (dropShip is not null)
            {
                await ctx.Db.SaveChangesAsync(ct);
                var announced = await ctx.Resolver.ResolveManyAsync(line.Serials, line.ItemId, ct);
                var sourceLine = dropShip.Lines.FirstOrDefault(l => l.LineNo == line.LineNo && l.ItemId == line.ItemId)
                    ?? dropShip.Lines.FirstOrDefault(l => l.ItemId == line.ItemId);
                if (sourceLine is null) continue;
                var accepted = announced.Values.OfType<ResolvedUnit>().ToList();
                if (accepted.Count > 0) resolvedDropShipLines.Add(sourceLine.LineNo);
                foreach (var unit in accepted)
                {
                    var key = Documents.Key(message.Envelope, dropShip.Document, sourceLine.LineNo, unit.UnitId, UnitEventType.OwnershipOpened, dropShip.ShippedAt);
                    if (await ctx.SeenAsync(key, dropShip.ShippedAt, ct)) continue;
                    var owner = await ctx.Episodes.PrepareOwnershipOpenAsync(unit.UnitId,
                        new OwnershipTarget(dropShip.CustomerPartyRef, PartyType.Customer, EpisodeReason.Sale, EpisodeSource.CarrierPod), key, dropShip.ShippedAt,
                        message.Envelope.Source, message.Envelope.LegalEntityId, Documents.Line(dropShip.Document, sourceLine.LineNo), null, ct);
                    if (owner.CloseDraft is not null) await ctx.Writer.AppendAsync([owner.CloseDraft], ct);
                    await ctx.Writer.AppendAsync([owner.Draft], ct);
                }
            }
        }

        // ASN is the second half of the drop-ship entitlement contract: requirements held for this source document
        // are retried only after its serial announcement has been accepted in the same transaction.
        var retry = await ctx.Db.EntitlementRequirements.AsNoTracking()
            .Where(r => r.SourceDocumentType == message.Document.Type && r.SourceDocumentId == message.Document.Id && r.Status == EntitlementRequirementStatus.Exception && r.MissingInput == EntitlementMissingInput.SerialsPendingAsn)
            .Select(r => r.RequirementId).ToListAsync(ct);
        if (retry.Count > 0) await ctx.Bus.PublishAsync(new ResolveEntitlementRequirements(retry), new Wolverine.DeliveryOptions { TenantId = ctx.Tenant.TenantId.ToString() });

        if (dropShip is not null)
        {
            // The ownership opens above raised its P2-T13 rows in this unit of work. The source-document form lets the
            // outbox resolver find those rows after commit without depending on their generated identifiers here.
            await ctx.Bus.PublishAsync(new ResolveEntitlementRequirements([], dropShip.Document), new Wolverine.DeliveryOptions { TenantId = ctx.Tenant.TenantId.ToString() });
            // HIDDEN COUPLING with WarrantyLineException.TargetDocument*: the vendor-shipment handler persists the
            // line blocker against that document, and this ASN closes it only after its serials were accepted.
            var resolved = await ctx.Db.WarrantyLineExceptions.Where(e => e.TargetDocumentType == dropShip.Document.Type && e.TargetDocumentId == dropShip.Document.Id
                && resolvedDropShipLines.Contains(e.DocumentLine)
                && e.Status == WarrantyLineExceptionStatus.Open && e.MissingInput == WarrantyLineMissingInput.SerialsPendingAsn).ToListAsync(ct);
            foreach (var row in resolved)
            {
                row.Status = WarrantyLineExceptionStatus.Resolved;
                row.ResolvedAt = ctx.Clock.UtcNow;
                row.ResolvedBy = message.Envelope.Source;
                row.Detail = null;
            }
        }

        return new ConsumedEventResult("Purchasing.AsnReceived", doc.Document, affected, 0, exceptions, affected == 0 && exceptions.Count == 0);
    }, ct);
}

public static class PurchasingVendorShipConfirmedHandler
{
    public static Task<ConsumedEventResult> Handle(PurchasingVendorShipConfirmed message, DocumentContext ctx, CancellationToken ct) => ctx.InTransactionAsync(async () =>
    {
        var doc = await ctx.Purchasing.GetVendorShipmentAsync(message.Document, ct) ?? throw new CommandRefusedException("document_unavailable", $"Vendor shipment {message.Document.Id:N} could not be read through the purchasing port.");
        var exceptions = new List<string>();
        var drafts = new List<UnitEventDraft>();
        var affected = 0;
        var delivered = doc.DeliveredAt is not null;
        foreach (var line in doc.Lines)
        {
            if (line.Serials.Count == 0 && (await ctx.ItemPolicies.PoliciesForAsync(line.ItemId, ct)).Any(p => p.CoverageFamily == CoverageFamilyCode.Base && p.EntitlementRequiredOnSale))
            {
                // HIDDEN COUPLING with PurchasingAsnReceivedHandler: TargetDocument* is the durable join by which the
                // later ASN closes this blocker. Changing it to the ASN document would leave the vendor line open.
                var existing = await ctx.Db.WarrantyLineExceptions.SingleOrDefaultAsync(e => e.DocumentType == doc.Document.Type && e.DocumentId == doc.Document.Id && e.DocumentLine == line.LineNo, ct);
                if (existing is null)
                {
                    ctx.Db.WarrantyLineExceptions.Add(new WarrantyLineException
                    {
                        TenantId = ctx.Tenant.TenantId,
                        ExceptionId = Yojana.Platform.Contracts.Identifiers.TimeOrderedId.New(doc.ShippedAt),
                        DocumentType = doc.Document.Type,
                        DocumentId = doc.Document.Id,
                        DocumentLine = line.LineNo,
                        TargetDocumentType = doc.Document.Type,
                        TargetDocumentId = doc.Document.Id,
                        ItemId = line.ItemId,
                        Status = WarrantyLineExceptionStatus.Open,
                        MissingInput = WarrantyLineMissingInput.SerialsPendingAsn,
                        Detail = "The entitlement-required drop-ship line is waiting for vendor ASN serials.",
                        TriggerAt = doc.ShippedAt,
                        RaisedAt = ctx.Clock.UtcNow,
                    });
                }
                continue;
            }
            var fresh = line.Serials.ToList();
            var units = new List<ResolvedUnit>();
            var known = await ctx.Resolver.ResolveManyAsync(line.Serials, line.ItemId, ct);
            foreach (var serial in line.Serials)
            {
                if (known[serial] is { } unit) { units.Add(unit); fresh.Remove(serial); }
            }

            // only the units that existed before this line: the ones commissioned below are read the per-unit way
            await ctx.Episodes.PreloadOpenAsync(units.Select(u => u.UnitId).ToList(), ct);

            if (fresh.Count > 0)
            {
                var result = await ctx.CommissionAsync(Documents.CommissionFor(message.Envelope, doc.Document, line, UnitOrigin.VendorAsn, doc.ShippedAt, null, LocationKind.InTransit, EpisodeSource.CarrierPod, fresh), ct);
                await ctx.Db.SaveChangesAsync(ct);
                foreach (var r in result.Lines.Where(l => l.UnitId is not null)) units.Add(new ResolvedUnit(r.UnitId!.Value, Guid.Empty, LifecycleStatus.Released, r.ClientReference ?? "", IdentifierType.InternalSerial));
                exceptions.AddRange(result.Lines.Where(l => l.Outcome == CommissionLineResult.Refused).Select(l => $"line {line.LineNo}: serial '{l.ClientReference}' refused: {string.Join(", ", l.Reasons)}"));
            }

            foreach (var unit in units)
            {
                // Drop-ship: the carrier evidence dates the delivery and the ownership; our posting date never does (case 26).
                var at = delivered ? doc.DeliveredAt!.Value : doc.ShippedAt;
                var eventType = delivered ? UnitEventType.Delivered : UnitEventType.Shipped;
                var key = Documents.Key(message.Envelope, doc.Document, line.LineNo, unit.UnitId, eventType, at);
                if (await ctx.SeenAsync(key, at, ct)) continue;
                var target = delivered ? new CustodyTarget(LocationKind.CustomerSite, doc.ShipToLocationId, doc.CustomerPartyRef, PartyType.Customer, EpisodeReason.Sale, EpisodeSource.CarrierPod)
                                       : new CustodyTarget(LocationKind.InTransit, null, null, PartyType.ThreePl, EpisodeReason.Sale, EpisodeSource.CarrierPod);
                var move = await ctx.Episodes.PrepareCustodyMoveAsync(unit.UnitId, target, eventType, key, at, message.Envelope.Source, message.Envelope.LegalEntityId, Documents.Line(doc.Document, line.LineNo),
                    new Dictionary<string, object?> { ["message_id"] = message.Envelope.MessageId, ["drop_ship"] = true, ["carrier_ref"] = doc.CarrierRef, ["vendor_party_ref"] = doc.VendorPartyRef, ["serial_confirmation_mode"] = "vendor_asn" }, null, false, ct);
                drafts.Add(move.Draft);
                var ownershipKey = Documents.Key(message.Envelope, doc.Document, line.LineNo, unit.UnitId, UnitEventType.OwnershipOpened, at);
                var owner = await ctx.Episodes.PrepareOwnershipOpenAsync(unit.UnitId, new OwnershipTarget(doc.CustomerPartyRef, PartyType.Customer, EpisodeReason.Sale, EpisodeSource.CarrierPod), ownershipKey, at, message.Envelope.Source, message.Envelope.LegalEntityId, Documents.Line(doc.Document, line.LineNo), null, ct);
                if (owner.CloseDraft is not null) drafts.Add(owner.CloseDraft);
                drafts.Add(owner.Draft);
                affected++;
            }
        }

        var report = await Documents.ReportAsync(ctx, "Purchasing.VendorShipConfirmed", doc.Document, drafts, exceptions, 0, ct);
        return report with { UnitsAffected = affected };
    }, ct);
}

public static class ManufacturingProductionReceiptPostedHandler
{
    public static Task<ConsumedEventResult> Handle(ManufacturingProductionReceiptPosted message, DocumentContext ctx, CancellationToken ct) => ctx.InTransactionAsync(async () =>
    {
        var doc = await ctx.Manufacturing.GetProductionReceiptAsync(message.Document, ct) ?? throw new CommandRefusedException("document_unavailable", $"Production receipt {message.Document.Id:N} could not be read through the manufacturing port.");
        var exceptions = new List<string>();
        var affected = 0;
        foreach (var line in doc.Produced)
        {
            var known = await ctx.Resolver.ResolveManyAsync(line.Serials, line.ItemId, ct);
            var fresh = line.Serials.Where(s => known[s] is null).ToList();
            if (fresh.Count == 0) continue;
            var result = await ctx.CommissionAsync(Documents.CommissionFor(message.Envelope, doc.Document, line, UnitOrigin.Manufactured, doc.PostedAt, line.LocationId ?? doc.FacilityId, line.LocationKind ?? LocationKind.Warehouse, EpisodeSource.Receipt, fresh) with { Document = doc.ProductionOrder with { Line = line.LineNo } }, ct);
            affected += result.Commissioned;
            exceptions.AddRange(result.Lines.Where(l => l.Outcome == CommissionLineResult.Refused).Select(l => $"line {line.LineNo}: serial '{l.ClientReference}' refused: {string.Join(", ", l.Reasons)}"));
        }

        await ctx.Db.SaveChangesAsync(ct);
        exceptions.AddRange(await Components.AssembleAsync(ctx, message.Envelope, doc.Document, doc.ProductionOrder, doc.Components, doc.PostedAt, ct));
        return new ConsumedEventResult("Manufacturing.ProductionReceiptPosted", doc.Document, affected, 0, exceptions, affected == 0 && exceptions.Count == 0);
    }, ct);
}

public static class ManufacturingComponentIssuedHandler
{
    public static Task<ConsumedEventResult> Handle(ManufacturingComponentIssued message, DocumentContext ctx, CancellationToken ct) => ctx.InTransactionAsync(async () =>
    {
        var doc = await ctx.Manufacturing.GetComponentIssueAsync(message.Document, ct) ?? throw new CommandRefusedException("document_unavailable", $"Component issue {message.Document.Id:N} could not be read through the manufacturing port.");
        var exceptions = await Components.AssembleAsync(ctx, message.Envelope, doc.Document, doc.ProductionOrder, doc.Components, doc.PostedAt, ct);
        return new ConsumedEventResult("Manufacturing.ComponentIssued", doc.Document, doc.Components.Count(c => c.ParentSerial is not null), 0, exceptions);
    }, ct);
}

internal static class Components
{
    /// <summary>Component lines with a parent serial become AssemblyLinks of that parent (case 35); lines without one are the order's loose stock and are reported, not guessed.</summary>
    public static async Task<List<string>> AssembleAsync(DocumentContext ctx, ThinEnvelope env, DocumentRef doc, DocumentRef order, IReadOnlyList<ComponentIssue> components, DateTime at, CancellationToken ct)
    {
        var exceptions = new List<string>();
        foreach (var group in components.Where(c => c.ParentSerial is not null).GroupBy(c => c.ParentSerial!))
        {
            var parent = await ctx.Resolver.ResolveAsync(new DocumentSerial(group.Key), null, ct);
            if (parent is null)
            {
                exceptions.Add($"{doc.Type} {doc.Id:N}: parent serial '{group.Key}' is unknown; components not linked");
                continue;
            }

            var installs = new List<ChildInput>();
            foreach (var component in group)
            {
                if (component.Serials.Count == 0)
                {
                    installs.Add(new ChildInput(null, component.ComponentItemId, component.LotId, component.Quantity, Slot: component.Slot, ThroughPhantomItemId: component.ThroughPhantomItemId));
                    continue;
                }

                foreach (var serial in component.Serials)
                {
                    var child = await ctx.Resolver.ResolveAsync(serial, component.ComponentItemId, ct);
                    if (child is null)
                    {
                        exceptions.Add($"{doc.Type} {doc.Id:N} line {component.LineNo}: component serial '{serial.Value}' is unknown");
                        continue;
                    }

                    installs.Add(new ChildInput(child.UnitId, Slot: component.Slot, ThroughPhantomItemId: component.ThroughPhantomItemId));
                }
            }

            if (installs.Count == 0) continue;
            try
            {
                await ctx.AssembleAsync(new RecordAssembly(Documents.DeterministicCommandId(env, doc, group.First().LineNo), parent.UnitId, installs, at, env.Source, env.LegalEntityId, Document: order), ct);
                await ctx.Db.SaveChangesAsync(ct);
            }
            catch (CommandRefusedException ex)
            {
                exceptions.Add($"{doc.Type} {doc.Id:N}: parent '{group.Key}': {ex.Rule}: {ex.Message}");
            }
        }

        foreach (var loose in components.Where(c => c.ParentSerial is null && c.Serials.Count > 0))
        {
            exceptions.Add($"{doc.Type} {doc.Id:N} line {loose.LineNo}: {loose.Serials.Count} component serial(s) issued without a parent serial; not linked");
        }

        return exceptions;
    }
}

public static class InventoryTransferPostedHandler
{
    public static Task<ConsumedEventResult> Handle(InventoryTransferPosted message, DocumentContext ctx, CancellationToken ct) => ctx.InTransactionAsync(async () =>
    {
        var doc = await ctx.Inventory.GetTransferAsync(message.Document, ct) ?? throw new CommandRefusedException("document_unavailable", $"Transfer {message.Document.Id:N} could not be read through the inventory port.");
        var exceptions = new List<string>();
        var drafts = new List<UnitEventDraft>();
        var confirmationMode = await ctx.SerialConfirmation.GetAsync(ct);
        var confirmations = Documents.SerialConfirmations(doc.Lines, doc.Document, confirmationMode, exceptions);
        var acceptedLines = doc.Lines.Where(l => l.Serials.Count == 0 || confirmations[l.LineNo].Accepted).ToList();
        var resolved = await Documents.ResolveAllAsync(ctx, acceptedLines, doc.Document, exceptions, ct);
        var seen = await Documents.PrefetchAsync(ctx, resolved.Select(r => (r.Unit.UnitId, Documents.Key(message.Envelope, doc.Document, r.Line.LineNo, r.Unit.UnitId, UnitEventType.CustodyChanged, doc.PostedAt), doc.PostedAt)).ToList(), ct);
        foreach (var (line, serial, unit) in resolved)
        {
            var kind = doc.TwoStep ? LocationKind.InTransit : doc.ToLocationKind;
            var key = Documents.Key(message.Envelope, doc.Document, line.LineNo, unit.UnitId, UnitEventType.CustodyChanged, doc.PostedAt);
            if (seen.Contains((key, doc.PostedAt))) continue;
            try
            {
                var move = await ctx.Episodes.PrepareCustodyMoveAsync(unit.UnitId, new CustodyTarget(kind, doc.TwoStep ? null : doc.ToLocationId, null, null, EpisodeReason.Transfer, EpisodeSource.Delivery), UnitEventType.CustodyChanged, key, doc.PostedAt, message.Envelope.Source, message.Envelope.LegalEntityId, Documents.Line(doc.Document, line.LineNo),
                    new Dictionary<string, object?> { ["message_id"] = message.Envelope.MessageId, ["from_location_id"] = doc.FromLocationId, ["to_location_id"] = doc.ToLocationId, ["two_step"] = doc.TwoStep,
                        ["serial_confirmation_mode"] = confirmationMode, ["serial_capture_method"] = confirmations[line.LineNo].CaptureMethod,
                        ["serials_copied_without_scan"] = confirmations[line.LineNo].SerialsCopiedWithoutScan }, null, true, ct);
                drafts.Add(move.Draft);
            }
            catch (CommandRefusedException ex)
            {
                exceptions.Add($"line {line.LineNo}: serial '{serial.Value}': {ex.Rule}: {ex.Message}");
            }
        }

        return await Documents.ReportAsync(ctx, "Inventory.TransferPosted", doc.Document, drafts, exceptions, 0, ct);
    }, ct);
}

public static class InventoryAdjustmentPostedHandler
{
    public static Task<ConsumedEventResult> Handle(InventoryAdjustmentPosted message, DocumentContext ctx, CancellationToken ct) => ctx.InTransactionAsync(async () =>
    {
        var doc = await ctx.Inventory.GetAdjustmentAsync(message.Document, ct) ?? throw new CommandRefusedException("document_unavailable", $"Adjustment {message.Document.Id:N} could not be read through the inventory port.");
        var exceptions = new List<string>();
        var drafts = new List<UnitEventDraft>();
        var affected = 0;
        foreach (var line in doc.Lines)
        {
            var fresh = new List<DocumentSerial>();
            var known = await ctx.Resolver.ResolveManyAsync(line.Serials, line.ItemId, ct);
            foreach (var serial in line.Serials)
            {
                var unit = known[serial];
                switch (doc.AdjustmentKind)
                {
                    case AdjustmentDocument.Kinds.In when unit is null:
                        fresh.Add(serial);
                        break;
                    case AdjustmentDocument.Kinds.In or AdjustmentDocument.Kinds.StatusChange when unit is not null:
                    {
                        var key = Documents.Key(message.Envelope, doc.Document, line.LineNo, unit.UnitId, UnitEventType.CustodyChanged, doc.PostedAt);
                        if (await ctx.SeenAsync(key, doc.PostedAt, ct)) break;
                        var move = await ctx.Episodes.PrepareCustodyMoveAsync(unit.UnitId, new CustodyTarget(line.LocationKind ?? LocationKind.Warehouse, line.LocationId, null, null, EpisodeReason.Transfer, EpisodeSource.Delivery), UnitEventType.CustodyChanged, key, doc.PostedAt, message.Envelope.Source, message.Envelope.LegalEntityId, Documents.Line(doc.Document, line.LineNo),
                            new Dictionary<string, object?> { ["message_id"] = message.Envelope.MessageId, ["adjustment_kind"] = doc.AdjustmentKind, ["reason_code"] = doc.ReasonCode }, null, false, ct);
                        drafts.Add(move.Draft);
                        affected++;
                        break;
                    }

                    case AdjustmentDocument.Kinds.Out when unit is not null:
                    {
                        // An adjustment out of a serialised unit is a scrap: the unit keeps every row and its identifier (case 13 decides re-use).
                        var row = await ctx.Db.Units.FirstAsync(u => u.UnitId == unit.UnitId, ct);
                        var next = UnitLifecycle.Next(row.LifecycleStatus, UnitEventType.Scrapped, row.Origin);
                        if (next is null) { exceptions.Add($"line {line.LineNo}: serial '{serial.Value}' is {row.LifecycleStatus}; cannot scrap"); break; }
                        var key = Documents.Key(message.Envelope, doc.Document, line.LineNo, unit.UnitId, UnitEventType.Scrapped, doc.PostedAt);
                        if (await ctx.SeenAsync(key, doc.PostedAt, ct)) break;
                        drafts.Add(new UnitEventDraft
                        {
                            UnitId = unit.UnitId, EventTime = doc.PostedAt, EventType = UnitEventType.Scrapped, BizStep = "destroying", Disposition = UnitDisposition.Destroyed, LocationId = line.LocationId,
                            LegalEntityId = message.Envelope.LegalEntityId, ActorRef = message.Envelope.Source, DocumentType = doc.Document.Type, DocumentId = doc.Document.Id, DocumentLine = line.LineNo, IdempotencyKey = key,
                            Attributes = JsonObjectText.Of(new { message_id = message.Envelope.MessageId, adjustment_kind = doc.AdjustmentKind, reason_code = doc.ReasonCode }),
                            Delta = new CurrentStateDelta { LifecycleStatus = next, Disposition = UnitDisposition.Destroyed },
                        });
                        row.LifecycleStatus = next;
                        affected++;
                        break;
                    }

                    case AdjustmentDocument.Kinds.Count:
                        if (unit is null) exceptions.Add($"line {line.LineNo}: count found serial '{serial.Value}' that the module does not know");
                        break;
                    default:
                        if (unit is null) exceptions.Add($"line {line.LineNo}: unknown serial '{serial.Value}' for adjustment {doc.AdjustmentKind}");
                        break;
                }
            }

            if (fresh.Count > 0)
            {
                // Stock found that the module never saw: a unit of unknown provenance, so the history says so (origin legacy_unknown).
                var result = await ctx.CommissionAsync(Documents.CommissionFor(message.Envelope, doc.Document, line, UnitOrigin.LegacyUnknown, doc.PostedAt, line.LocationId, line.LocationKind ?? LocationKind.Warehouse, EpisodeSource.Delivery, fresh), ct);
                affected += result.Commissioned;
                exceptions.AddRange(result.Lines.Where(l => l.Outcome == CommissionLineResult.Refused).Select(l => $"line {line.LineNo}: serial '{l.ClientReference}' refused: {string.Join(", ", l.Reasons)}"));
            }
        }

        var report = await Documents.ReportAsync(ctx, "Inventory.AdjustmentPosted", doc.Document, drafts, exceptions, 0, ct);
        return report with { UnitsAffected = affected };
    }, ct);
}

public static class SalesShipmentPostedHandler
{
    public static Task<ConsumedEventResult> Handle(SalesShipmentPosted message, DocumentContext ctx, CancellationToken ct) => ctx.InTransactionAsync(async () =>
    {
        var doc = await ctx.Sales.GetShipmentAsync(message.Document, ct) ?? throw new CommandRefusedException("document_unavailable", $"Shipment {message.Document.Id:N} could not be read through the sales port.");
        var exceptions = new List<string>();
        var drafts = new List<UnitEventDraft>();
        var pending = 0;
        var lines = Documents.ShipmentLines(doc);
        var confirmationMode = await ctx.SerialConfirmation.GetAsync(ct);
        var confirmations = Documents.SerialConfirmations(lines, doc.Document, confirmationMode, exceptions);
        foreach (var line in lines)
        {
            if (line.Serials.Count == 0)
            {
                // A 3PL ships now and confirms serials later (case 24): the line waits for ConfirmShipmentSerials.
                var tenantId = ctx.Tenant.TenantId;
                if (!await ctx.Db.PendingShipmentLines.AnyAsync(p => p.TenantId == tenantId && p.DocumentType == doc.Document.Type && p.DocumentId == doc.Document.Id && p.DocumentLine == line.LineNo, ct))
                {
                    ctx.Db.PendingShipmentLines.Add(new PendingShipmentLine { TenantId = tenantId, PendingId = TimeOrderedId.New(doc.PostedAt), DocumentType = doc.Document.Type, DocumentId = doc.Document.Id, DocumentLine = line.LineNo, ItemId = line.ItemId, Quantity = line.Quantity, CustomerPartyRef = doc.CustomerPartyRef, ShipToLocationId = doc.ShipToLocationId, CarrierPartyRef = doc.CarrierPartyRef, PostedAt = doc.PostedAt, LegalEntityId = message.Envelope.LegalEntityId, Source = message.Envelope.Source });
                }

                pending++;
            }
        }

        var acceptedLines = lines.Where(l => l.Serials.Count == 0 || confirmations[l.LineNo].Accepted).ToList();
        var resolved = await Documents.ResolveAllAsync(ctx, acceptedLines, doc.Document, exceptions, ct);
        var known = await Shipments.PrefetchAsync(ctx, message.Envelope, doc, resolved.Select(r => (r.Line.LineNo, r.Line.ItemId, r.Unit.UnitId)).ToList(), doc.PostedAt, ct);
        foreach (var (line, serial, unit) in resolved)
        {
            exceptions.AddRange(await Shipments.ShipAsync(ctx, message.Envelope, doc, line.LineNo, line.ItemId, unit, serial, doc.PostedAt,
                confirmationMode, confirmations[line.LineNo], drafts, ct, known));
        }

        return await Documents.ReportAsync(ctx, "Sales.ShipmentPosted", doc.Document, drafts, exceptions, pending, ct);
    }, ct);
}

internal static class Shipments
{
    /// <summary>What <see cref="ShipAsync"/> would otherwise read once per unit, read for the whole shipment.</summary>
    public sealed record Prefetched(HashSet<(string Key, DateTime At)> Seen, Dictionary<Guid, UnitCompleteness> Completeness,
        IReadOnlyDictionary<Guid, UnitShipmentGuardDecision> GuardDecisions);

    public static async Task<Prefetched> PrefetchAsync(DocumentContext ctx, ThinEnvelope env, ShipmentDocument doc, IReadOnlyList<(int LineNo, Guid ItemId, Guid UnitId)> units, DateTime at, CancellationToken ct)
    {
        var seen = await Documents.PrefetchAsync(ctx, units.Select(u => (u.UnitId, Documents.Key(env, doc.Document, u.LineNo, u.UnitId, UnitEventType.Shipped, at), at)).ToList(), ct);
        var toCheck = units.Where(u => !seen.Contains((Documents.Key(env, doc.Document, u.LineNo, u.UnitId, UnitEventType.Shipped, at), at))).Select(u => (u.UnitId, u.ItemId)).Distinct().ToList();
        var completeness = new Dictionary<Guid, UnitCompleteness>();
        foreach (var chunk in toCheck.Chunk(2000))
        {
            foreach (var c in await ctx.Completeness.QueryAsync(chunk, DateOnly.FromDateTime(at), ct)) completeness[c.UnitId] = c;
        }

        var guards = await ctx.SaleShipmentGuard.CheckAsync(toCheck.Select(u => new UnitShipmentCheck(u.UnitId, u.ItemId, at)).ToList(), ct);
        return new Prefetched(seen, completeness, guards);
    }

    /// <summary>One unit leaving on a shipment line: completeness at sale (rule 12), custody to in transit or the site, ownership when the trigger is ship.</summary>
    public static async Task<List<string>> ShipAsync(DocumentContext ctx, ThinEnvelope env, ShipmentDocument doc, int lineNo, Guid itemId, ResolvedUnit unit, DocumentSerial serial, DateTime at,
        string confirmationMode, SerialConfirmationDecision confirmation, List<UnitEventDraft> drafts, CancellationToken ct, Prefetched? known = null)
    {
        var exceptions = new List<string>();
        var key = Documents.Key(env, doc.Document, lineNo, unit.UnitId, UnitEventType.Shipped, at);
        if (known is not null ? known.Seen.Contains((key, at)) : await ctx.SeenAsync(key, at, ct)) return exceptions;
        var guard = known?.GuardDecisions.GetValueOrDefault(unit.UnitId)
            ?? (await ctx.SaleShipmentGuard.CheckAsync([new UnitShipmentCheck(unit.UnitId, itemId, at)], ct)).GetValueOrDefault(unit.UnitId);
        if (guard is { Allowed: false })
        {
            exceptions.Add($"line {lineNo}: serial '{serial.Value}': {guard.Rule}: {guard.Detail}");
            return exceptions;
        }
        var completeness = known?.Completeness.GetValueOrDefault(unit.UnitId) is { ItemId: var checkedItem } c && checkedItem == itemId ? c : await ctx.Completeness.CheckAsync(unit.UnitId, itemId, DateOnly.FromDateTime(at), ct);
        if (!completeness.IsComplete && completeness.Policy == ItemSerialisationProfile.Completeness.Block)
        {
            exceptions.Add($"line {lineNo}: serial '{serial.Value}': incomplete_asbuilt ({completeness.MissingRequiredComponentSerials} missing); not shipped in the module until the component serials are recorded");
            return exceptions;
        }

        var direct = !ctx.Options.ConfirmDeliverySeparately || doc.IsDropShip;
        var target = direct ? new CustodyTarget(LocationKind.CustomerSite, doc.ShipToLocationId, doc.CustomerPartyRef, PartyType.Customer, EpisodeReason.Sale, EpisodeSource.Delivery)
                            : new CustodyTarget(LocationKind.InTransit, null, doc.CarrierPartyRef, doc.CarrierPartyRef is null ? null : PartyType.ThreePl, EpisodeReason.Sale, EpisodeSource.Delivery);
        try
        {
            var period = await ctx.Periods.ResolveAsync(at, ct);
            var move = await ctx.Episodes.PrepareCustodyMoveAsync(unit.UnitId, target, UnitEventType.Shipped, key, at, env.Source, env.LegalEntityId, Documents.Line(doc.Document, lineNo),
                new Dictionary<string, object?> { ["message_id"] = env.MessageId, ["customer_party_ref"] = doc.CustomerPartyRef, ["serial_confirmation_mode"] = confirmationMode,
                    ["serial_capture_method"] = confirmation.CaptureMethod, ["serials_copied_without_scan"] = confirmation.SerialsCopiedWithoutScan,
                    ["base_order"] = doc.BaseOrder, ["attribution_completeness"] = env.AttributionCompleteness }, null, false, ct);
            drafts.Add(move.Draft with { PostingPeriod = period });
            if (ctx.Options.OwnershipTrigger == "ship" || direct)
            {
                var ownerKey = Documents.Key(env, doc.Document, lineNo, unit.UnitId, UnitEventType.OwnershipOpened, at);
                var owner = await ctx.Episodes.PrepareOwnershipOpenAsync(unit.UnitId, new OwnershipTarget(doc.CustomerPartyRef, PartyType.Customer, EpisodeReason.Sale, EpisodeSource.Delivery, CustomerPoRef: doc.CustomerPoRef), ownerKey, at, env.Source, env.LegalEntityId, Documents.Line(doc.Document, lineNo), null, ct);
                if (owner.CloseDraft is not null) drafts.Add(owner.CloseDraft);
                drafts.Add(owner.Draft);
            }
        }
        catch (CommandRefusedException ex)
        {
            exceptions.Add($"line {lineNo}: serial '{serial.Value}': {ex.Rule}: {ex.Message}");
        }

        return exceptions;
    }
}

public static class ConfirmShipmentSerialsHandler
{
    public static Task<ConfirmShipmentSerialsResult> Handle(ConfirmShipmentSerials command, DocumentContext ctx, CancellationToken ct) => ctx.InTransactionAsync(async () =>
    {
        var tenantId = ctx.Tenant.TenantId;
        var pending = await ctx.Db.PendingShipmentLines.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.DocumentType == command.Document.Type && p.DocumentId == command.Document.Id && p.DocumentLine == command.Line, ct)
            ?? throw new CommandRefusedException("no_pending_line", $"{command.Document.Type} {command.Document.Id:N} line {command.Line} has no serials pending.", "the line was posted with serials, or the document is not known; check the document reference");
        var env = new ThinEnvelope(command.CommandId, command.LegalEntityId, command.PhysicalShipTime, command.Source);
        var doc = new ShipmentDocument(command.Document, pending.CustomerPartyRef, pending.PostedAt, [], pending.ShipToLocationId, pending.CarrierPartyRef);
        var drafts = new List<UnitEventDraft>();
        var exceptions = new List<string>();
        var shipped = 0;
        var confirmationMode = await ctx.SerialConfirmation.GetAsync(ct);
        var confirmation = SerialConfirmationRules.Evaluate(confirmationMode, command.SerialCaptureMethod);
        if (confirmation.Refusal is { } refusal)
        {
            exceptions.Add($"{command.Document.Type} {command.Document.Id:N} line {command.Line}: {refusal.Rule}: {refusal.Message}; unblocking step: {refusal.UnblockingStep}");
            return new ConfirmShipmentSerialsResult(pending.PendingId, 0, (int)Math.Max(0, pending.Quantity - pending.ResolvedCount), exceptions);
        }
        var units = await ctx.Resolver.ResolveManyAsync(command.Serials, pending.ItemId, ct);
        var known = await Shipments.PrefetchAsync(ctx, env, doc, units.Values.OfType<ResolvedUnit>().Select(u => (command.Line, pending.ItemId, u.UnitId)).ToList(), command.PhysicalShipTime, ct);
        foreach (var serial in command.Serials)
        {
            var unit = units[serial];
            if (unit is null) { exceptions.Add($"serial '{serial.Value}' is unknown"); continue; }
            var before = drafts.Count;
            exceptions.AddRange(await Shipments.ShipAsync(ctx, env, doc, command.Line, pending.ItemId, unit, serial, command.PhysicalShipTime,
                confirmationMode, confirmation, drafts, ct, known));
            if (drafts.Count > before) shipped++;
        }

        if (drafts.Count > 0) await Documents.FlushIfLargeAsync(ctx, await ctx.Writer.AppendAsync(drafts, ct), ct);
        pending.ResolvedCount += shipped;
        if (pending.ResolvedCount >= pending.Quantity)
        {
            pending.ResolvedAt = ctx.Clock.UtcNow;
            pending.ResolvedCommandId = command.CommandId;
        }

        return new ConfirmShipmentSerialsResult(pending.PendingId, shipped, (int)Math.Max(0, pending.Quantity - pending.ResolvedCount), exceptions);
    }, ct);
}

public static class SalesDeliveryConfirmedHandler
{
    public static Task<ConsumedEventResult> Handle(SalesDeliveryConfirmed message, DocumentContext ctx, CancellationToken ct) => ctx.InTransactionAsync(async () =>
    {
        var confirmation = await ctx.Sales.GetDeliveryConfirmationAsync(message.Document, ct) ?? throw new CommandRefusedException("document_unavailable", $"Delivery confirmation {message.Document.Id:N} could not be read through the sales port.");
        var shipment = await ctx.Sales.GetShipmentAsync(confirmation.Shipment, ct) ?? throw new CommandRefusedException("document_unavailable", $"Shipment {confirmation.Shipment.Id:N} behind the confirmation could not be read.");
        var exceptions = new List<string>();
        var drafts = new List<UnitEventDraft>();
        var shipmentLines = Documents.ShipmentLines(shipment);
        var lines = confirmation.Serials is { Count: > 0 } s ? shipmentLines.Select(l => l with { Serials = l.Serials.Where(x => s.Any(y => y.Value == x.Value)).ToList() }).ToList() : shipmentLines.ToList();
        var confirmationMode = await ctx.SerialConfirmation.GetAsync(ct);
        var confirmations = Documents.SerialConfirmations(lines, shipment.Document, confirmationMode, exceptions);
        var acceptedLines = lines.Where(l => l.Serials.Count == 0 || confirmations[l.LineNo].Accepted).ToList();
        var resolved = await Documents.ResolveAllAsync(ctx, acceptedLines, shipment.Document, exceptions, ct);
        var seen = await Documents.PrefetchAsync(ctx, resolved.Select(r => (r.Unit.UnitId, Documents.Key(message.Envelope, confirmation.Document, r.Line.LineNo, r.Unit.UnitId, UnitEventType.Delivered, confirmation.DeliveredAt), confirmation.DeliveredAt)).ToList(), ct);
        foreach (var (line, serial, unit) in resolved)
        {
            var key = Documents.Key(message.Envelope, confirmation.Document, line.LineNo, unit.UnitId, UnitEventType.Delivered, confirmation.DeliveredAt);
            if (seen.Contains((key, confirmation.DeliveredAt))) continue;
            try
            {
                var move = await ctx.Episodes.PrepareCustodyMoveAsync(unit.UnitId, new CustodyTarget(LocationKind.CustomerSite, confirmation.ShipToLocationId ?? shipment.ShipToLocationId, shipment.CustomerPartyRef, PartyType.Customer, EpisodeReason.Sale, EpisodeSource.CarrierPod), UnitEventType.Delivered, key, confirmation.DeliveredAt, message.Envelope.Source, message.Envelope.LegalEntityId, Documents.Line(shipment.Document, line.LineNo),
                    new Dictionary<string, object?> { ["message_id"] = message.Envelope.MessageId, ["evidence"] = confirmation.Evidence, ["confirmation_document"] = confirmation.Document,
                        ["serial_confirmation_mode"] = confirmationMode, ["serial_capture_method"] = confirmations[line.LineNo].CaptureMethod,
                        ["serials_copied_without_scan"] = confirmations[line.LineNo].SerialsCopiedWithoutScan }, null, false, ct);
                drafts.Add(move.Draft);
                var currentOwner = await ctx.Episodes.OpenAsync(unit.UnitId, EpisodeKind.Ownership, ct);
                if (ctx.Options.OwnershipTrigger == "deliver" && currentOwner?.PartyRef != shipment.CustomerPartyRef)
                {
                    var ownerKey = Documents.Key(message.Envelope, confirmation.Document, line.LineNo, unit.UnitId, UnitEventType.OwnershipOpened, confirmation.DeliveredAt);
                    var open = await ctx.Episodes.PrepareOwnershipOpenAsync(unit.UnitId, new OwnershipTarget(shipment.CustomerPartyRef, PartyType.Customer, EpisodeReason.Sale, EpisodeSource.CarrierPod, CustomerPoRef: shipment.CustomerPoRef), ownerKey, confirmation.DeliveredAt, message.Envelope.Source, message.Envelope.LegalEntityId, Documents.Line(shipment.Document, line.LineNo), null, ct);
                    if (open.CloseDraft is not null) drafts.Add(open.CloseDraft);
                    drafts.Add(open.Draft);
                }
            }
            catch (CommandRefusedException ex)
            {
                exceptions.Add($"line {line.LineNo}: serial '{serial.Value}': {ex.Rule}: {ex.Message}");
            }
        }

        var report = await Documents.ReportAsync(ctx, "Sales.DeliveryConfirmed", confirmation.Document, drafts, exceptions, 0, ct);
        if (report.UnitsAffected == 0) return report;
        // The exception row is the narrow contract between delivery posting and warranty-line retry. It stores the
        // referenced document explicitly; relying on handler order would leave invoice cover unresolved after replay.
        var retries = await ctx.Db.WarrantyLineExceptions.AsNoTracking()
            .Where(e => e.Status == WarrantyLineExceptionStatus.Open &&
                ((e.TargetDocumentType == confirmation.Document.Type && e.TargetDocumentId == confirmation.Document.Id)
                 || (e.TargetDocumentType == shipment.Document.Type && e.TargetDocumentId == shipment.Document.Id)))
            .Select(e => e.ExceptionId).ToListAsync(ct);
        foreach (var retry in retries)
            await ctx.Bus.PublishAsync(new ResolveWarrantyLineException(retry, [], "delivery-posting"), new DeliveryOptions { TenantId = ctx.Tenant.TenantId.ToString() });
        return report;
    }, ct);
}

public static class SalesReturnPostedHandler
{
    public static Task<ConsumedEventResult> Handle(SalesReturnPosted message, DocumentContext ctx, CancellationToken ct) => ctx.InTransactionAsync(async () =>
    {
        var doc = await ctx.Sales.GetReturnAsync(message.Document, ct) ?? throw new CommandRefusedException("document_unavailable", $"Return {message.Document.Id:N} could not be read through the sales port.");
        var exceptions = new List<string>();
        var drafts = new List<UnitEventDraft>();
        var resolved = await Documents.ResolveAllAsync(ctx, doc.Lines, doc.Document, exceptions, ct);
        var seen = await Documents.PrefetchAsync(ctx, resolved.Select(r => (r.Unit.UnitId, Documents.Key(message.Envelope, doc.Document, r.Line.LineNo, r.Unit.UnitId, UnitEventType.CustodyChanged, doc.ReceivedAt), doc.ReceivedAt)).ToList(), ct);
        foreach (var (line, serial, unit) in resolved)
        {
            // Custody to quarantine only (P1); the RMA stages, grading and the ownership effects arrive with P5.
            var key = Documents.Key(message.Envelope, doc.Document, line.LineNo, unit.UnitId, UnitEventType.CustodyChanged, doc.ReceivedAt);
            if (seen.Contains((key, doc.ReceivedAt))) continue;
            var move = await ctx.Episodes.PrepareCustodyMoveAsync(unit.UnitId, new CustodyTarget(LocationKind.Quarantine, doc.ReceivingLocationId, null, null, EpisodeReason.Return, EpisodeSource.Receipt), UnitEventType.CustodyChanged, key, doc.ReceivedAt, message.Envelope.Source, message.Envelope.LegalEntityId, Documents.Line(doc.Document, line.LineNo),
                new Dictionary<string, object?> { ["message_id"] = message.Envelope.MessageId, ["return_reason_code"] = doc.ReturnReasonCode, ["rma_external_ref"] = doc.RmaExternalRef, ["base_delivery"] = doc.BaseDelivery, ["customer_party_ref"] = doc.CustomerPartyRef }, new CurrentStateDelta { Disposition = UnitDisposition.Returned }, false, ct);
            drafts.Add(move.Draft);
        }

        return await Documents.ReportAsync(ctx, "Sales.ReturnPosted", doc.Document, drafts, exceptions, 0, ct);
    }, ct);
}

public static class ServiceLoanerIssuedHandler
{
    public static Task<ConsumedEventResult> Handle(ServiceLoanerIssued message, DocumentContext ctx, CancellationToken ct) => ctx.InTransactionAsync(async () =>
    {
        var doc = await ctx.Service.GetLoanerIssueAsync(message.Document, ct) ?? throw new CommandRefusedException("document_unavailable", $"Loaner issue {message.Document.Id:N} could not be read through the service port.");
        var exceptions = new List<string>();
        var drafts = new List<UnitEventDraft>();
        var resolved = await Documents.ResolveAllAsync(ctx, doc.Lines, doc.Document, exceptions, ct);
        var seen = await Documents.PrefetchAsync(ctx, resolved.Select(r => (r.Unit.UnitId, Documents.Key(message.Envelope, doc.Document, r.Line.LineNo, r.Unit.UnitId, UnitEventType.CustodyChanged, doc.IssuedAt), doc.IssuedAt)).ToList(), ct);
        foreach (var (line, serial, unit) in resolved)
        {
            var key = Documents.Key(message.Envelope, doc.Document, line.LineNo, unit.UnitId, UnitEventType.CustodyChanged, doc.IssuedAt);
            if (seen.Contains((key, doc.IssuedAt))) continue;
            // A loaner is a normal unit at the customer with a due date (case 21); ownership stays with us.
            var move = await ctx.Episodes.PrepareCustodyMoveAsync(unit.UnitId, new CustodyTarget(LocationKind.CustomerSite, doc.SiteLocationId, doc.CustomerPartyRef, PartyType.Customer, doc.Reason, EpisodeSource.Delivery, DueBack: doc.DueBack), UnitEventType.CustodyChanged, key, doc.IssuedAt, message.Envelope.Source, message.Envelope.LegalEntityId, Documents.Line(doc.Document, line.LineNo),
                new Dictionary<string, object?> { ["message_id"] = message.Envelope.MessageId, ["loan"] = true }, null, false, ct);
            drafts.Add(move.Draft);
        }

        return await Documents.ReportAsync(ctx, "Service.LoanerIssued", doc.Document, drafts, exceptions, 0, ct);
    }, ct);
}

public static class FinancePeriodClosedHandler
{
    public static async Task<ConsumedEventResult> Handle(FinancePeriodClosed message, DocumentContext ctx, CancellationToken ct)
    {
        await ctx.Periods.CloseAsync(message.Period, message.Envelope.OccurredAt, message.Envelope.Source, ct);
        return new ConsumedEventResult("Finance.PeriodClosed", null, 0, 0, []);
    }
}
