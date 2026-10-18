using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Labels;

/// <summary>Shared command/consumer path: resolve, render, persist exact bytes, deliver through the printer port, ledger.</summary>
public sealed class LabelPrintService(SerialisationDbContext db, ITenantContext tenant, LabelRenderer renderer,
    ILabelPrinterPort printer, IUnitEventWriter events, IItemQueryPort itemProfiles, DocumentNumbers numbers)
{
    public async Task<LabelPrintResult> PrintAsync(PrintLabel command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var replay = await db.PrintJobs.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId
            && (row.CommandId == command.CommandId || (command.SourceEventId != null && row.SourceEventId == command.SourceEventId)), cancellationToken);
        if (replay is not null) return Result(replay, true);
        if (string.IsNullOrWhiteSpace(command.PrinterRef))
            throw new CommandRefusedException("label_printer_required", "A printer reference is required.");
        var jobNumber = await numbers.NextAsync(DocumentNumbers.PrintJob, command.PrintAt, cancellationToken);

        var selected = await SelectVersionAsync(command, cancellationToken);
        if (selected is null)
        {
            var rejected = Rejected(command, jobNumber, "label_template_not_found",
                $"no active label template applies to unit {command.UnitId} at {command.PrintAt:O}");
            db.PrintJobs.Add(rejected);
            await db.SaveChangesAsync(cancellationToken);
            return Result(rejected, false);
        }
        var (template, version) = selected.Value;
        var render = await renderer.RenderAsync(template, version, command.UnitId, command.IdentifierId, command.PrintAt, cancellationToken);
        if (render.RejectionReason is not null || render.Payload is null)
        {
            var rejected = Rejected(command, jobNumber,
                render.EmptyRequiredField is null ? "label_render_rejected" : "label_required_field_empty",
                render.RejectionReason ?? "label rendering failed", version.TemplateVersionId, render.EmptyRequiredField);
            db.PrintJobs.Add(rejected);
            await db.SaveChangesAsync(cancellationToken);
            return Result(rejected, false);
        }

        var hash = LabelTemplateRules.Hash(render.Payload);
        var job = new PrintJob
        {
            TenantId = tenantId, JobId = command.CommandId, CommandId = command.CommandId,
            JobNumber = jobNumber,
            BatchCommandId = command.BatchCommandId, BatchSequence = command.BatchSequence,
            RequestedSerial = command.RequestedSerial,
            SourceEventId = command.SourceEventId, TemplateVersionId = version.TemplateVersionId, UnitId = command.UnitId,
            IdentifierId = command.IdentifierId, RenderedPayload = render.Payload, PayloadHash = hash,
            PrinterRef = command.PrinterRef.Trim(), RequestedBy = command.ActorRef, RequestedAt = command.PrintAt,
            Status = LabelPrintStatus.Rendered, RenderedAt = command.PrintAt,
        };
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        var receipt = await printer.SendAsync(new LabelPrinterRequest(job.JobId, job.PrinterRef, version.LayoutKind, job.RenderedPayload, hash), cancellationToken);
        if (receipt.RejectionReason is not null)
        {
            job.Status = LabelPrintStatus.Rejected;
            job.RejectionRule = "label_printer_rejected";
            job.RejectionReason = receipt.RejectionReason;
            await db.SaveChangesAsync(cancellationToken);
            return Result(job, false);
        }
        job.SentAt = command.PrintAt;
        job.Status = receipt.Confirmed ? LabelPrintStatus.Confirmed : LabelPrintStatus.Sent;
        if (receipt.Confirmed)
        {
            job.ConfirmedAt = command.PrintAt;
            var appended = await events.AppendAsync([Draft(command, job, version, LabelEventType.Printed)], cancellationToken);
            job.LedgerEventId = appended.Written.SingleOrDefault()?.EventId;
        }
        await db.SaveChangesAsync(cancellationToken);
        return Result(job, false);
    }

    public async Task<LabelPrintResult> ReprintAsync(ReprintLabel command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.PrintJobs.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId && row.CommandId == command.CommandId, cancellationToken) is { } replay)
            return Result(replay, true);
        var original = await db.PrintJobs.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId && row.JobId == command.JobId, cancellationToken)
            ?? throw new CommandRefusedException("label_print_job_not_found", "The print job does not exist.");
        if (original.RenderedPayload is null || original.PayloadHash is null || original.TemplateVersionId is null
            || original.UnitId is null)
            throw new CommandRefusedException("label_reprint_has_no_payload", "A rejected job without a rendered payload cannot be reprinted.");
        var printerRef = string.IsNullOrWhiteSpace(command.PrinterRef) ? original.PrinterRef : command.PrinterRef.Trim();
        if (string.IsNullOrWhiteSpace(printerRef)) throw new CommandRefusedException("label_printer_required", "A printer reference is required.");
        var jobNumber = await numbers.NextAsync(DocumentNumbers.PrintJob, command.ReprintedAt, cancellationToken);
        var version = await db.LabelTemplateVersions.AsNoTracking().FirstAsync(row => row.TenantId == tenantId
            && row.TemplateVersionId == original.TemplateVersionId, cancellationToken);
        var job = new PrintJob
        {
            TenantId = tenantId, JobId = command.CommandId, CommandId = command.CommandId,
            JobNumber = jobNumber,
            TemplateVersionId = original.TemplateVersionId, UnitId = original.UnitId, IdentifierId = original.IdentifierId,
            OriginalJobId = original.JobId, RenderedPayload = original.RenderedPayload, PayloadHash = original.PayloadHash,
            PrinterRef = printerRef, RequestedBy = command.ActorRef, RequestedAt = command.ReprintedAt,
            Status = LabelPrintStatus.Rendered, RenderedAt = command.ReprintedAt,
        };
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        var receipt = await printer.SendAsync(new LabelPrinterRequest(job.JobId, printerRef, version.LayoutKind,
            job.RenderedPayload, job.PayloadHash), cancellationToken);
        if (receipt.RejectionReason is not null)
        {
            job.Status = LabelPrintStatus.Rejected;
            job.RejectionReason = receipt.RejectionReason;
            await db.SaveChangesAsync(cancellationToken);
            return Result(job, false);
        }
        job.SentAt = command.ReprintedAt;
        job.Status = receipt.Confirmed ? LabelPrintStatus.Confirmed : LabelPrintStatus.Sent;
        if (receipt.Confirmed)
        {
            job.ConfirmedAt = command.ReprintedAt;
            var printCommand = new PrintLabel(command.CommandId, job.UnitId.Value, null, job.IdentifierId, printerRef,
                command.ReprintedAt, command.LegalEntityId, command.ActorRef);
            var appended = await events.AppendAsync([Draft(printCommand, job, version, LabelEventType.Reprinted)], cancellationToken);
            job.LedgerEventId = appended.Written.SingleOrDefault()?.EventId;
        }
        await db.SaveChangesAsync(cancellationToken);
        return Result(job, false);
    }

    private async Task<(LabelTemplate Template, LabelTemplateVersion Version)?> SelectVersionAsync(PrintLabel command,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var versions = await (from version in db.LabelTemplateVersions.AsNoTracking()
            join template in db.LabelTemplates.AsNoTracking()
                on new { version.TenantId, version.TemplateId } equals new { template.TenantId, template.TemplateId }
            where version.TenantId == tenantId && version.Status != LabelTemplateStatus.Draft
                && version.EffectiveFrom <= command.PrintAt && (version.EffectiveTo == null || version.EffectiveTo > command.PrintAt)
                && (command.TemplateId == null || template.TemplateId == command.TemplateId)
                && (command.TemplateVersionId == null || version.TemplateVersionId == command.TemplateVersionId)
            orderby version.Version descending
            select new { Template = template, Version = version }).ToListAsync(cancellationToken);
        var current = await db.Current.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId && row.UnitId == command.UnitId, cancellationToken);
        if (current is null) return null;
        var openIdentifiers = await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenantId
                && row.UnitId == command.UnitId && row.ValidFrom <= command.PrintAt && (row.ValidTo == null || row.ValidTo > command.PrintAt))
            .Select(row => new { row.IdentifierId, row.Type }).ToListAsync(cancellationToken);
        var identifierType = command.IdentifierId is { } identifierId
            ? openIdentifiers.FirstOrDefault(row => row.IdentifierId == identifierId)?.Type
            : null;
        var item = await db.ItemAssignments.AsNoTracking().Where(row => row.TenantId == tenantId && row.UnitId == command.UnitId
                && row.ValidFrom <= command.PrintAt && (row.ValidTo == null || row.ValidTo > command.PrintAt))
            .Select(row => row.ItemId).FirstOrDefaultAsync(cancellationToken);
        var profile = item == Guid.Empty ? null : await itemProfiles.GetAsync(item, cancellationToken);

        if (command.TemplateId is not null)
        {
            if (versions.Count == 0) return null;
            var chosen = versions[0];
            var applies = chosen.Template.AppliesTo == LabelTemplateAppliesTo.ItemFamily
                ? profile?.ItemClass == chosen.Template.AppliesToValue
                : identifierType is not null
                    ? identifierType == chosen.Template.AppliesToValue
                    : openIdentifiers.Any(row => row.Type == chosen.Template.AppliesToValue);
            return applies ? (chosen.Template, chosen.Version) : null;
        }
        if (identifierType is not null && versions.FirstOrDefault(row => row.Template.AppliesTo == LabelTemplateAppliesTo.IdentifierType
            && row.Template.AppliesToValue == identifierType) is { } identifierMatch)
            return (identifierMatch.Template, identifierMatch.Version);

        // Item family is supplied by the item boundary. Item code is not a family; a missing class deliberately finds no template.
        if (profile?.ItemClass is null) return null;
        var match = versions.FirstOrDefault(row => row.Template.AppliesTo == LabelTemplateAppliesTo.ItemFamily
            && row.Template.AppliesToValue == profile.ItemClass);
        return match is null ? null : (match.Template, match.Version);
    }

    private PrintJob Rejected(PrintLabel command, string jobNumber, string rule, string reason, Guid? versionId = null,
        string? emptyRequiredField = null) => new()
    {
        TenantId = tenant.TenantId, JobId = command.CommandId, CommandId = command.CommandId,
        JobNumber = jobNumber,
        BatchCommandId = command.BatchCommandId, BatchSequence = command.BatchSequence,
        RequestedSerial = command.RequestedSerial,
        SourceEventId = command.SourceEventId, TemplateVersionId = versionId, UnitId = command.UnitId,
        IdentifierId = command.IdentifierId, PrinterRef = command.PrinterRef, RequestedBy = command.ActorRef,
        RequestedAt = command.PrintAt, Status = LabelPrintStatus.Rejected, RejectionRule = rule,
        RejectionReason = reason, EmptyRequiredField = emptyRequiredField,
    };

    private static UnitEventDraft Draft(PrintLabel command, PrintJob job, LabelTemplateVersion version, string eventType) => new()
    {
        UnitId = job.UnitId!.Value, EventTime = command.PrintAt, EventType = eventType, LegalEntityId = command.LegalEntityId,
        ActorRef = command.ActorRef, IdempotencyKey = IdempotencyKey.ForCommand("labels", command.CommandId,
            job.UnitId!.Value, eventType, command.PrintAt),
        Attributes = JsonObjectText.Of(new
        {
            job_id = job.JobId, job_number = job.JobNumber, original_job_id = job.OriginalJobId, template_version_id = version.TemplateVersionId,
            layout_kind = version.LayoutKind, symbology = version.Symbology, payload_hash = job.PayloadHash, printer_ref = job.PrinterRef,
        }),
        ProjectCurrentState = false,
    };

    private static LabelPrintResult Result(PrintJob job, bool replayed) =>
        new(job.JobId, job.JobNumber, job.Status, job.RejectionReason, job.PayloadHash, replayed, job.RejectionRule,
            job.EmptyRequiredField);
}

public static class LabelPrintHandler
{
    public static Task<LabelPrintResult> Handle(PrintLabel command, LabelPrintService service,
        CancellationToken cancellationToken) => service.PrintAsync(command, cancellationToken);

    public static Task<LabelPrintResult> Handle(ReprintLabel command, LabelPrintService service,
        CancellationToken cancellationToken) => service.ReprintAsync(command, cancellationToken);
}
