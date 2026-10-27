using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Labels;

/// <summary>Resolves a bounded pasted list in sets, then delegates each resolved unit to the normal print command path.</summary>
public static class LabelBatchPrintHandler
{
    private const int MaximumSerials = 500;

    public static async Task<LabelBatchPrintResult> Handle(BatchPrintLabels command, SerialisationDbContext db,
        LabelPrintService prints, IIdentifierPolicySource policies, ITenantContext tenant, DocumentNumbers numbers,
        CancellationToken cancellationToken)
    {
        if (command.CommandId == Guid.Empty)
            throw new CommandRefusedException("label_batch_command_required", "A command id is required.");
        if (string.IsNullOrWhiteSpace(command.PrinterRef))
            throw new CommandRefusedException("label_printer_required", "A printer reference is required.");
        if (command.Serials.Count is < 1 or > MaximumSerials)
            throw new CommandRefusedException("label_batch_size_invalid", $"A label batch needs between 1 and {MaximumSerials} serials.");

        var tenantId = tenant.TenantId;
        var replay = await db.PrintJobs.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.BatchCommandId == command.CommandId)
            .OrderBy(row => row.BatchSequence)
            .ToListAsync(cancellationToken);
        if (replay.Count > 0)
            return new(command.CommandId, replay.Select(Outcome).ToList(), true);

        var serials = command.Serials.Select(value => value?.Trim() ?? string.Empty).ToList();
        if (serials.Any(value => value.Length == 0))
            throw new CommandRefusedException("label_batch_serial_required", "Every serial-list row must contain a serial.");

        var identifierTypes = IdentifierPolicySet.Defaults.Keys.ToList();
        var candidates = serials.Select(serial => identifierTypes
                .Select(type => IdentifierNormaliser.Normalise(serial, policies.For(type)).Value)
                .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal))
            .ToList();
        var wanted = candidates.SelectMany(values => values).Distinct(StringComparer.Ordinal).ToList();
        var identifiers = new List<ResolvedIdentifier>();
        foreach (var chunk in wanted.Chunk(1_500))
        {
            identifiers.AddRange(await db.Identifiers.AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.ValidTo == null && chunk.Contains(row.NormalisedValue))
                .Select(row => new ResolvedIdentifier(row.UnitId, row.IdentifierId, row.NormalisedValue))
                .ToListAsync(cancellationToken));
        }
        var unitIdsWithHits = identifiers.Select(row => row.UnitId).Distinct().ToList();
        var legalEntities = unitIdsWithHits.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await (from current in db.Current.AsNoTracking()
                     join @event in db.Events.AsNoTracking()
                         on new { current.TenantId, current.UnitId, EventTime = current.LastEventTime }
                         equals new { @event.TenantId, @event.UnitId, @event.EventTime }
                     where current.TenantId == tenantId && unitIdsWithHits.Contains(current.UnitId)
                         && @event.Seq == current.LastEventSeq
                     select new { current.UnitId, @event.LegalEntityId })
                .ToDictionaryAsync(row => row.UnitId, row => row.LegalEntityId, cancellationToken);

        var jobs = new List<PrintJob>(serials.Count);
        var prepared = new List<PreparedLabelPrint>(serials.Count);
        for (var sequence = 0; sequence < serials.Count; sequence++)
        {
            var serial = serials[sequence];
            var matches = identifiers.Where(row => candidates[sequence].Contains(row.NormalisedValue)).ToList();
            var unitIds = matches.Select(row => row.UnitId).Distinct().ToList();
            if (unitIds.Count != 1)
            {
                var rule = unitIds.Count == 0 ? "label_batch_unknown_serial" : "label_batch_ambiguous_serial";
                var reason = unitIds.Count == 0
                    ? $"Serial '{serial}' does not identify an open unit."
                    : $"Serial '{serial}' identifies more than one open unit.";
                var jobNumber = await numbers.NextAsync(DocumentNumbers.PrintJob, command.PrintAt, cancellationToken);
                var rejected = Rejected(command, tenantId, sequence, serial, jobNumber, rule, reason);
                db.PrintJobs.Add(rejected);
                jobs.Add(rejected);
                continue;
            }

            var match = matches.First(row => row.UnitId == unitIds[0]);
            if (!legalEntities.TryGetValue(match.UnitId, out var legalEntityId))
                throw new CommandRefusedException("label_batch_unit_has_no_ledger", $"Serial '{serial}' resolves to a unit with no ledger history.");
            var print = await prints.PrepareAsync(new PrintLabel(ChildCommandId(command.CommandId, sequence),
                match.UnitId, null, match.IdentifierId, command.PrinterRef, command.PrintAt, legalEntityId,
                command.ActorRef, TemplateVersionId: command.TemplateVersionId, BatchCommandId: command.CommandId,
                BatchSequence: sequence, RequestedSerial: serial, PrintLayout: command.PrintLayout), cancellationToken);
            prepared.Add(print);
            jobs.Add(print.Job);
        }

        await db.SaveChangesAsync(cancellationToken);
        var receipt = await prints.SendBatchAsync(prepared, cancellationToken);
        return new(command.CommandId, jobs.OrderBy(row => row.BatchSequence).Select(Outcome).ToList(), false,
            receipt?.Download);
    }

    private static PrintJob Rejected(BatchPrintLabels command, TenantId tenantId, int sequence, string serial,
        string jobNumber, string rule, string reason) => new()
    {
        TenantId = tenantId,
        JobId = ChildCommandId(command.CommandId, sequence),
        CommandId = ChildCommandId(command.CommandId, sequence),
        JobNumber = jobNumber,
        BatchCommandId = command.CommandId,
        BatchSequence = sequence,
        RequestedSerial = serial,
        UnitId = null,
        PrinterRef = command.PrinterRef.Trim(),
        RequestedBy = command.ActorRef,
        RequestedAt = command.PrintAt,
        Status = LabelPrintStatus.Rejected,
        RejectionRule = rule,
        RejectionReason = reason,
    };

    private static LabelBatchPrintOutcome Outcome(PrintJob job) => new(job.RequestedSerial ?? string.Empty,
        job.UnitId, job.UnitId is null ? null : job.JobId, job.Status,
        job.RejectionRule, job.RejectionReason, job.EmptyRequiredField);

    private static Guid ChildCommandId(Guid commandId, int sequence)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"labels:{commandId:N}:{sequence}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private sealed record ResolvedIdentifier(Guid UnitId, Guid IdentifierId, string NormalisedValue);
}
