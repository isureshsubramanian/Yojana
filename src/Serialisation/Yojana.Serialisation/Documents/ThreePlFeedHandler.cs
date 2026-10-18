using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Imports;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Documents;

/// <summary>
/// MOD-12's 3PL boundary deliberately reuses the pending-shipment service. It does not reproduce shipment state
/// transitions: this adapter only validates feed-only fields, records the message, and adds asset-tag identifiers.
/// </summary>
public static class ReceiveThreePlFeedHandler
{
    public static Task<ReceiveThreePlFeedResult> Handle(ReceiveThreePlFeed command, DocumentContext ctx,
        IItemQueryPort items, IdentifierAssignmentService identifierAssignments, ITenantContext tenant, IClock clock,
        CancellationToken cancellationToken) => ctx.InTransactionAsync<ReceiveThreePlFeedResult>(async () =>
    {
        var tenantId = tenant.TenantId;
        var replay = await ctx.Db.ThreePlFeedMessages.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId
            && (row.CommandId == command.CommandId || row.MessageReference == command.MessageReference), cancellationToken);
        if (replay is not null)
            return new(replay.FeedMessageId, replay.ImportJobId, replay.ShippedCount, replay.AssetTagCount, replay.RejectedCount, [], true);

        var pending = await ctx.Db.PendingShipmentLines.FirstOrDefaultAsync(row => row.TenantId == tenantId
            && row.DocumentType == command.Shipment.Type && row.DocumentId == command.Shipment.Id && row.DocumentLine == command.Line, cancellationToken)
            ?? throw new CommandRefusedException("no_pending_line", $"{command.Shipment.Type} line {command.Line} has no serials pending.");
        var profile = await items.GetAsync(pending.ItemId, cancellationToken);
        var lotRequired = profile?.TrackingMode == ItemSerialisationProfile.TrackingModes.Lot;
        var jobId = TimeOrderedId.New(command.PhysicalShipTime);
        var payload = JsonSerializer.Serialize(command.Units, LedgerJson.Options);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var job = new ImportJob
        {
            TenantId = tenantId, JobId = jobId, CommandId = jobId, Source = ImportSource.ThreePlFeed,
            FileName = command.MessageReference + ".json", OriginalHash = hash, Format = "feed", Mode = ImportMode.Apply,
            TargetType = command.Shipment.Type, TargetId = command.Shipment.Id, TargetLine = command.Line,
            Status = "intake", ActorRef = command.ActorRef, RecordedTime = clock.UtcNow,
        };
        ctx.Db.ImportJobs.Add(job);
        await ctx.Db.SaveChangesAsync(cancellationToken);

        var rows = command.Units.Select((unit, index) => new ImportRowResult
        {
            TenantId = tenantId, RowResultId = TimeOrderedId.New(clock.UtcNow), JobId = jobId, RowNumber = index + 1,
            Sheet = "feed", RawValue = unit.Serial, Lot = unit.Lot, CustomerAssetTag = unit.CustomerAssetTag,
            Outcome = string.IsNullOrWhiteSpace(unit.Serial) || (lotRequired && string.IsNullOrWhiteSpace(unit.Lot)) ? ImportOutcome.Rejected : ImportOutcome.Skipped,
            Rule = string.IsNullOrWhiteSpace(unit.Serial) ? "serial_required" : lotRequired && string.IsNullOrWhiteSpace(unit.Lot) ? "lot_required" : null,
            RecordedTime = clock.UtcNow,
        }).ToList();
        ctx.Db.ImportRowResults.AddRange(rows);

        var accepted = command.Units.Select((unit, index) => (unit, index)).Where(pair => rows[pair.index].Rule is null).ToList();
        var serials = accepted.Select(pair => new DocumentSerial(pair.unit.Serial)).ToList();
        var units = await ctx.Resolver.ResolveManyAsync(serials, pending.ItemId, cancellationToken);
        var env = new ThinEnvelope(command.CommandId, command.LegalEntityId, command.PhysicalShipTime, ImportSource.ThreePlFeed);
        var shipment = new ShipmentDocument(command.Shipment, pending.CustomerPartyRef, pending.PostedAt, [], pending.ShipToLocationId, pending.CarrierPartyRef);
        var drafts = new List<UnitEventDraft>();
        var exceptions = new List<string>();
        var confirmationMode = await ctx.SerialConfirmation.GetAsync(cancellationToken);
        var confirmation = SerialConfirmationRules.Evaluate(confirmationMode, SerialCaptureMethodValue.Scanned);
        var known = await Shipments.PrefetchAsync(ctx, env, shipment, units.Values.OfType<ResolvedUnit>()
            .Select(unit => (command.Line, pending.ItemId, unit.UnitId)).ToList(), command.PhysicalShipTime, cancellationToken);
        var shipped = 0;
        var tags = 0;
        foreach (var pair in accepted)
        {
            var serial = new DocumentSerial(pair.unit.Serial);
            var resolved = units[serial];
            var row = rows[pair.index];
            if (resolved is null)
            {
                row.Outcome = ImportOutcome.Rejected;
                row.Rule = "serial_unknown";
                exceptions.Add($"serial '{pair.unit.Serial}' is unknown");
                continue;
            }
            row.UnitId = resolved.UnitId;
            row.NormalisedValue = resolved.NormalisedValue;
            var before = drafts.Count;
            var problems = await Shipments.ShipAsync(ctx, env, shipment, command.Line, pending.ItemId, resolved, serial,
                command.PhysicalShipTime, confirmationMode, confirmation, drafts, cancellationToken, known);
            exceptions.AddRange(problems);
            if (drafts.Count == before)
            {
                row.Outcome = ImportOutcome.Rejected;
                row.Rule = problems.Count == 0 ? "shipment_not_applied" : Rule(problems[0]);
                continue;
            }
            row.Outcome = ImportOutcome.Applied;
            shipped++;
            if (string.IsNullOrWhiteSpace(pair.unit.CustomerAssetTag)) continue;
            try
            {
                await identifierAssignments.ExecuteAsync(new AssignIdentifier(TimeOrderedId.New(command.PhysicalShipTime.AddTicks(pair.index + 1)),
                    resolved.UnitId, new IdentifierInput(IdentifierType.CustomerAssetTag, pair.unit.CustomerAssetTag), command.PhysicalShipTime,
                    command.ActorRef, command.LegalEntityId, command.Shipment), cancellationToken);
                tags++;
            }
            catch (CommandRefusedException ex)
            {
                row.Outcome = ImportOutcome.Rejected;
                row.Rule = ex.Rule;
                exceptions.Add($"serial '{pair.unit.Serial}' asset tag: {ex.Rule}: {ex.Message}");
            }
        }
        if (drafts.Count > 0) await ctx.Writer.AppendAsync(drafts, cancellationToken);
        pending.ResolvedCount += shipped;
        if (pending.ResolvedCount >= pending.Quantity) { pending.ResolvedAt = clock.UtcNow; pending.ResolvedCommandId = command.CommandId; }

        job.AppliedCount = rows.Count(row => row.Outcome == ImportOutcome.Applied);
        job.RejectedCount = rows.Count(row => row.Outcome == ImportOutcome.Rejected);
        job.SkippedCount = rows.Count(row => row.Outcome == ImportOutcome.Skipped);
        job.Status = job.RejectedCount == 0 ? "applied" : job.AppliedCount > 0 ? "partial" : "rejected";
        job.CompletedTime = clock.UtcNow;
        var receipt = new ThreePlFeedMessage
        {
            TenantId = tenantId, FeedMessageId = command.CommandId, CommandId = command.CommandId,
            MessageReference = command.MessageReference, DocumentType = command.Shipment.Type, DocumentId = command.Shipment.Id,
            DocumentLine = command.Line, ImportJobId = jobId, PayloadHash = hash, Status = job.Status,
            ShippedCount = shipped, AssetTagCount = tags, RejectedCount = job.RejectedCount,
            ActorRef = command.ActorRef, RecordedTime = clock.UtcNow,
        };
        ctx.Db.ThreePlFeedMessages.Add(receipt);
        var recorded = clock.UtcNow;
        await ctx.Bus.PublishAsync(new ImportCompleted(new PublishedSummaryHeader(ImportCompleted.Schema, ImportCompleted.Version,
                TimeOrderedId.New(recorded), command.PhysicalShipTime, recorded, command.LegalEntityId, command.ActorRef, command.Shipment),
            jobId, job.Source, job.OriginalHash, null, job.Status, job.AppliedCount, job.SkippedCount, job.RejectedCount),
            new Wolverine.DeliveryOptions { TenantId = tenantId.ToString() });
        return new(receipt.FeedMessageId, jobId, shipped, tags, job.RejectedCount, exceptions);
    }, cancellationToken);

    private static string Rule(string message)
    {
        var marker = message.IndexOf(": ", StringComparison.Ordinal);
        return marker < 0 ? "shipment_rejected" : message[(marker + 2)..].Split(':', 2)[0];
    }
}
