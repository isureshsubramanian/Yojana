using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Labels;

public sealed record PreparedLabelPrint(
    PrintJob Job,
    LabelTemplateVersion? Version,
    PrintLabel Command,
    LabelPrinterRequest? PrinterRequest,
    bool Replayed);

/// <summary>Shared command/consumer path: resolve, render, persist exact bytes, deliver through the printer port, ledger.</summary>
public sealed class LabelPrintService(SerialisationDbContext db, ITenantContext tenant, LabelRenderer renderer,
    ILabelPrinterPort printer, IUnitEventWriter events, IItemQueryPort itemProfiles, DocumentNumbers numbers,
    LabelSettings settings)
{
    public async Task<LabelPrintResult> PrintAsync(PrintLabel command, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(command, cancellationToken);
        if (prepared.PrinterRequest is null) return Result(prepared.Job, prepared.Replayed);
        var receipt = await SendAsync(prepared, cancellationToken);
        return Result(prepared.Job, prepared.Replayed, receipt.Download);
    }

    public async Task<PreparedLabelPrint> PrepareAsync(PrintLabel command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var replay = await db.PrintJobs.FirstOrDefaultAsync(row => row.TenantId == tenantId
            && (row.CommandId == command.CommandId || (command.SourceEventId != null && row.SourceEventId == command.SourceEventId)), cancellationToken);
        if (replay is not null)
        {
            var replayVersion = replay.TemplateVersionId is { } replayVersionId
                ? await db.LabelTemplateVersions.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId
                    && row.TemplateVersionId == replayVersionId, cancellationToken)
                : null;
            return new(replay, replayVersion, command,
                replayVersion is null || replay.RenderedPayload is null || replay.PayloadHash is null
                    ? null : await PrinterRequestAsync(replay, replayVersion, StoredLayout(replay), cancellationToken), true);
        }
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
            return new(rejected, null, command, null, false);
        }
        var (template, version) = selected.Value;
        var render = await renderer.RenderAsync(template, version, command.UnitId, command.IdentifierId,
            command.PrintAt, cancellationToken, legalEntityId: command.LegalEntityId);
        if (render.RejectionReason is not null || render.Payload is null)
        {
            var rejected = Rejected(command, jobNumber,
                render.EmptyRequiredField is null ? "label_render_rejected" : "label_required_field_empty",
                render.RejectionReason ?? "label rendering failed", version.TemplateVersionId, render.EmptyRequiredField);
            db.PrintJobs.Add(rejected);
            await db.SaveChangesAsync(cancellationToken);
            return new(rejected, version, command, null, false);
        }

        var layout = await LayoutAsync(command.PrintLayout, cancellationToken);
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
            OutputFormat = layout.Format, SheetProfileId = layout.SheetProfileId,
            StartRow = layout.Format == LabelOutputFormat.Sheet ? layout.StartRow : null,
            StartColumn = layout.Format == LabelOutputFormat.Sheet ? layout.StartColumn : null,
            Copies = layout.Copies,
        };
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return new(job, version, command, await PrinterRequestAsync(job, version, layout, cancellationToken), false);
    }

    public async Task<LabelPrinterReceipt?> SendBatchAsync(IReadOnlyList<PreparedLabelPrint> prepared,
        CancellationToken cancellationToken)
    {
        var printable = prepared.Where(item => item.PrinterRequest is not null).ToList();
        if (printable.Count == 0) return null;
        try
        {
            var receipt = await printer.SendBatchAsync(new LabelPrinterBatchRequest(
                printable.Select(item => item.PrinterRequest!).ToList()), cancellationToken);
            foreach (var item in printable) await ApplyReceiptAsync(item, receipt, save: false, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return receipt;
        }
        catch (CommandRefusedException exception)
        {
            foreach (var item in printable) RejectPrinter(item.Job, exception);
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
    }

    public async Task<LabelPrintResult> ReprintAsync(ReprintLabel command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.PrintJobs.FirstOrDefaultAsync(row => row.TenantId == tenantId && row.CommandId == command.CommandId,
                cancellationToken) is { } replay)
            return Result(replay, true);
        var reason = command.Reason.Trim();
        if (reason.Length is 0 or > 500)
            throw new CommandRefusedException("label_reprint_reason_required",
                "A reprint reason is required and cannot exceed 500 characters.");
        var original = await db.PrintJobs.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId
            && row.JobId == command.JobId, cancellationToken)
            ?? throw new CommandRefusedException("label_print_job_not_found", "The print job does not exist.");
        if (original.RenderedPayload is null || original.PayloadHash is null || original.TemplateVersionId is null
            || original.UnitId is null)
            throw new CommandRefusedException("label_reprint_has_no_payload", "A rejected job without a rendered payload cannot be reprinted.");
        var printerRef = string.IsNullOrWhiteSpace(command.PrinterRef) ? original.PrinterRef : command.PrinterRef.Trim();
        if (string.IsNullOrWhiteSpace(printerRef))
            throw new CommandRefusedException("label_printer_required", "A printer reference is required.");
        var jobNumber = await numbers.NextAsync(DocumentNumbers.PrintJob, command.ReprintedAt, cancellationToken);
        var version = await db.LabelTemplateVersions.AsNoTracking().FirstAsync(row => row.TenantId == tenantId
            && row.TemplateVersionId == original.TemplateVersionId, cancellationToken);
        var layout = await LayoutAsync(command.PrintLayout ?? StoredLayout(original), cancellationToken);
        var job = new PrintJob
        {
            TenantId = tenantId, JobId = command.CommandId, CommandId = command.CommandId,
            JobNumber = jobNumber,
            TemplateVersionId = original.TemplateVersionId, UnitId = original.UnitId, IdentifierId = original.IdentifierId,
            OriginalJobId = original.JobId, RenderedPayload = original.RenderedPayload, PayloadHash = original.PayloadHash,
            PrinterRef = printerRef, RequestedBy = command.ActorRef, RequestedAt = command.ReprintedAt,
            Status = LabelPrintStatus.Rendered, RenderedAt = command.ReprintedAt, ReprintReason = reason,
            OutputFormat = layout.Format, SheetProfileId = layout.SheetProfileId,
            StartRow = layout.Format == LabelOutputFormat.Sheet ? layout.StartRow : null,
            StartColumn = layout.Format == LabelOutputFormat.Sheet ? layout.StartColumn : null,
            Copies = layout.Copies,
        };
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        var printCommand = new PrintLabel(command.CommandId, job.UnitId.Value, null, job.IdentifierId, printerRef,
            command.ReprintedAt, command.LegalEntityId, command.ActorRef, PrintLayout: layout);
        var prepared = new PreparedLabelPrint(job, version, printCommand,
            await PrinterRequestAsync(job, version, layout, cancellationToken), false);
        var receipt = await SendAsync(prepared, cancellationToken);
        return Result(job, false, receipt.Download);
    }

    private async Task<LabelPrinterReceipt> SendAsync(PreparedLabelPrint prepared, CancellationToken cancellationToken)
    {
        try
        {
            var receipt = await printer.SendAsync(prepared.PrinterRequest!, cancellationToken);
            await ApplyReceiptAsync(prepared, receipt, save: true, cancellationToken);
            return receipt;
        }
        catch (CommandRefusedException exception)
        {
            RejectPrinter(prepared.Job, exception);
            await db.SaveChangesAsync(cancellationToken);
            return new(false, RejectionReason: exception.Message);
        }
    }

    private async Task ApplyReceiptAsync(PreparedLabelPrint prepared, LabelPrinterReceipt receipt,
        bool save, CancellationToken cancellationToken)
    {
        var job = prepared.Job;
        if (receipt.RejectionReason is not null)
        {
            job.Status = LabelPrintStatus.Rejected;
            job.RejectionRule = "label_printer_rejected";
            job.RejectionReason = receipt.RejectionReason;
        }
        else if (receipt.Download is not null)
        {
            job.Status = LabelPrintStatus.Downloaded;
            job.DownloadedAt = prepared.Command.PrintAt;
        }
        else
        {
            job.SentAt = prepared.Command.PrintAt;
            job.Status = receipt.Confirmed ? LabelPrintStatus.Confirmed : LabelPrintStatus.Sent;
            if (receipt.Confirmed && prepared.Version is not null)
            {
                job.ConfirmedAt = prepared.Command.PrintAt;
                var eventType = job.OriginalJobId is null ? LabelEventType.Printed : LabelEventType.Reprinted;
                var appended = await events.AppendAsync([Draft(prepared.Command, job, prepared.Version, eventType)],
                    cancellationToken);
                job.LedgerEventId = appended.Written.SingleOrDefault()?.EventId;
            }
        }
        if (save) await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<LabelPrinterRequest> PrinterRequestAsync(PrintJob job, LabelTemplateVersion version,
        LabelPrintLayout layout, CancellationToken cancellationToken)
    {
        var profile = layout.Format == LabelOutputFormat.Sheet
            ? await settings.SheetProfileAsync(layout.SheetProfileId, cancellationToken)
            : null;
        return new(job.JobId, job.PrinterRef!, version.LayoutKind, job.RenderedPayload!, job.PayloadHash!,
            version.WidthMm, version.HeightMm, layout, profile);
    }

    private async Task<LabelPrintLayout> LayoutAsync(LabelPrintLayout? requested, CancellationToken cancellationToken)
    {
        var options = await settings.PrintOptionsAsync(cancellationToken);
        var layout = requested ?? new LabelPrintLayout(options.DefaultFormat, options.DefaultSheetProfileId);
        if (!LabelOutputFormat.All.Contains(layout.Format))
            throw new CommandRefusedException("label_output_format_invalid", "Label format must be sheet or one_per_page.");
        if (layout.Copies is < 1 or > 100)
            throw new CommandRefusedException("label_copies_invalid", "Label copies must be between 1 and 100.");
        if (layout.Format == LabelOutputFormat.OnePerPage)
            return layout with { SheetProfileId = null, StartRow = 1, StartColumn = 1 };
        var profile = await settings.SheetProfileAsync(layout.SheetProfileId, cancellationToken);
        if (layout.StartRow < 1 || layout.StartRow > profile.Rows || layout.StartColumn < 1 || layout.StartColumn > profile.Columns)
            throw new CommandRefusedException("label_sheet_start_invalid",
                $"Start row must be 1–{profile.Rows} and start column must be 1–{profile.Columns}.");
        return layout with { SheetProfileId = profile.ProfileId };
    }

    private static LabelPrintLayout StoredLayout(PrintJob job) => new(
        job.OutputFormat ?? LabelOutputFormat.OnePerPage, job.SheetProfileId, job.StartRow ?? 1,
        job.StartColumn ?? 1, job.Copies);

    private static void RejectPrinter(PrintJob job, CommandRefusedException exception)
    {
        job.Status = LabelPrintStatus.Rejected;
        job.RejectionRule = exception.Rule;
        job.RejectionReason = exception.Message;
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
        var current = await db.Current.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId
            && row.UnitId == command.UnitId, cancellationToken);
        if (current is null) return null;
        var openIdentifiers = await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenantId
                && row.UnitId == command.UnitId && row.ValidFrom <= command.PrintAt
                && (row.ValidTo == null || row.ValidTo > command.PrintAt))
            .Select(row => new { row.IdentifierId, row.Type }).ToListAsync(cancellationToken);
        var identifierType = command.IdentifierId is { } identifierId
            ? openIdentifiers.FirstOrDefault(row => row.IdentifierId == identifierId)?.Type
            : null;
        var item = await db.ItemAssignments.AsNoTracking().Where(row => row.TenantId == tenantId
                && row.UnitId == command.UnitId && row.ValidFrom <= command.PrintAt
                && (row.ValidTo == null || row.ValidTo > command.PrintAt))
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
        if (identifierType is not null && versions.FirstOrDefault(row =>
                row.Template.AppliesTo == LabelTemplateAppliesTo.IdentifierType
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
        RejectionReason = reason, EmptyRequiredField = emptyRequiredField, Copies = command.PrintLayout?.Copies ?? 1,
    };

    private static UnitEventDraft Draft(PrintLabel command, PrintJob job, LabelTemplateVersion version, string eventType) => new()
    {
        UnitId = job.UnitId!.Value, EventTime = command.PrintAt, EventType = eventType,
        LegalEntityId = command.LegalEntityId,
        ActorRef = command.ActorRef, IdempotencyKey = IdempotencyKey.ForCommand("labels", command.CommandId,
            job.UnitId!.Value, eventType, command.PrintAt),
        Attributes = JsonObjectText.Of(new
        {
            job_id = job.JobId, job_number = job.JobNumber, original_job_id = job.OriginalJobId,
            template_version_id = version.TemplateVersionId, layout_kind = version.LayoutKind,
            symbology = version.Symbology, payload_hash = job.PayloadHash, printer_ref = job.PrinterRef,
        }),
        ProjectCurrentState = false,
    };

    private static LabelPrintResult Result(PrintJob job, bool replayed, LabelDownload? download = null) =>
        new(job.JobId, job.JobNumber, job.Status, job.RejectionReason, job.PayloadHash, replayed, job.RejectionRule,
            job.EmptyRequiredField, download);
}

public static class LabelPrintHandler
{
    public static Task<LabelPrintResult> Handle(PrintLabel command, LabelPrintService service,
        CancellationToken cancellationToken) => service.PrintAsync(command, cancellationToken);

    public static Task<LabelPrintResult> Handle(ReprintLabel command, LabelPrintService service,
        CancellationToken cancellationToken) => service.ReprintAsync(command, cancellationToken);
}
