using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Labels;

public sealed class LabelQueries(SerialisationDbContext db, ITenantContext tenant)
{
    public async Task<IReadOnlyList<LabelTemplateCatalogueRow>> TemplatesAsync(string? name, string? appliesTo,
        string? status, int skip, int take, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        take = Math.Clamp(take, 1, 200);
        var templates = db.LabelTemplates.AsNoTracking().Where(row => row.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(name)) templates = templates.Where(row => row.Name.Contains(name.Trim()));
        if (!string.IsNullOrWhiteSpace(appliesTo)) templates = templates.Where(row => row.AppliesTo == appliesTo);
        var rows = await templates.OrderBy(row => row.Name).Skip(Math.Max(0, skip)).Take(take).ToListAsync(cancellationToken);
        var ids = rows.Select(row => row.TemplateId).ToList();
        var versions = await db.LabelTemplateVersions.AsNoTracking().Where(row => row.TenantId == tenantId && ids.Contains(row.TemplateId)
            && (status == null || row.Status == status)).ToListAsync(cancellationToken);
        if (status is not null)
        {
            var matched = versions.Select(row => row.TemplateId).ToHashSet();
            rows = rows.Where(row => matched.Contains(row.TemplateId)).ToList();
        }
        var versionIds = versions.Select(version => version.TemplateVersionId).ToList();
        var printedByVersion = await db.PrintJobs.AsNoTracking().Where(row => row.TenantId == tenantId
                && row.TemplateVersionId != null && versionIds.Contains(row.TemplateVersionId.Value)
                && row.Status != LabelPrintStatus.Rejected)
            .GroupBy(row => row.TemplateVersionId!.Value).Select(group => new { VersionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.VersionId, row => row.Count, cancellationToken);
        return rows.Select(row =>
        {
            var own = versions.Where(version => version.TemplateId == row.TemplateId).ToList();
            var latest = own.OrderByDescending(version => version.Version).FirstOrDefault();
            var active = own.Where(version => version.Status == LabelTemplateStatus.Active)
                .OrderByDescending(version => version.Version).FirstOrDefault();
            return new LabelTemplateCatalogueRow(row.TemplateId, row.Name, row.AppliesTo, row.AppliesToValue,
                own.Count, active?.Version, active?.TemplateVersionId,
                row.CreatedAt, row.CreatedBy, latest?.Version, latest?.Status, latest?.EffectiveFrom, latest?.EffectiveTo,
                latest?.LegalReview ?? false, latest?.LegalReviewedBy, latest?.ApprovedAt,
                own.Sum(version => printedByVersion.GetValueOrDefault(version.TemplateVersionId)));
        }).ToList();
    }

    public async Task<LabelTemplateDetail?> TemplateAsync(Guid templateId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var template = await db.LabelTemplates.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenantId && row.TemplateId == templateId, cancellationToken);
        if (template is null) return null;
        var versions = await db.LabelTemplateVersions.AsNoTracking().Where(row => row.TenantId == tenantId && row.TemplateId == templateId)
            .OrderByDescending(row => row.Version).ToListAsync(cancellationToken);
        return new(template.TemplateId, template.Name, template.AppliesTo, template.AppliesToValue, template.CreatedAt,
            template.CreatedBy, versions.Select(row => new LabelTemplateVersionRow(row.TemplateVersionId, row.TemplateId,
                row.Version, row.EffectiveFrom, row.EffectiveTo, row.LayoutKind, row.Layout,
                LabelTemplateRules.StaticText(row.StaticText), LabelTemplateRules.Bindings(row.Bindings), row.Symbology,
                row.PreviewSampleUnitId, row.LegalReview, row.LegalReviewedBy, row.LegalReviewedAt, row.Regulated,
                row.ApprovedBy, row.ApprovedAt,
                row.PreviewedContentHash == row.ContentHash,
                !row.LegalReview || row.LegalReviewContentHash == row.ContentHash,
                !row.Regulated || row.ApprovalContentHash == row.ContentHash,
                row.Status, row.RecordedAt, row.ActorRef)).ToList());
    }

    public async Task<LabelPrintJobPage> JobsAsync(string? status, Guid? templateId, string? find,
        string? printer, string? kind, DateTime? requestedFrom, DateTime? requestedTo, int skip, int take,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        take = Math.Clamp(take, 1, 200);
        var query = db.PrintJobs.AsNoTracking().Where(row => row.TenantId == tenantId);
        if (string.Equals(status, "waiting", StringComparison.Ordinal))
            query = query.Where(row => row.Status == LabelPrintStatus.Rendered || row.Status == LabelPrintStatus.Sent);
        else if (!string.IsNullOrWhiteSpace(status)) query = query.Where(row => row.Status == status);
        if (templateId is { } wantedTemplate)
        {
            var versionIds = db.LabelTemplateVersions.Where(row => row.TenantId == tenantId && row.TemplateId == wantedTemplate)
                .Select(row => row.TemplateVersionId);
            query = query.Where(row => row.TemplateVersionId != null && versionIds.Contains(row.TemplateVersionId.Value));
        }
        if (!string.IsNullOrWhiteSpace(find))
        {
            var text = find.Trim();
            var unitIds = db.Identifiers.Where(row => row.TenantId == tenantId
                && (row.NormalisedValue.Contains(text) || row.RawValue.Contains(text))).Select(row => row.UnitId);
            var matchingVersions = from version in db.LabelTemplateVersions
                join template in db.LabelTemplates on new { version.TenantId, version.TemplateId }
                    equals new { template.TenantId, template.TemplateId }
                where version.TenantId == tenantId && template.Name.Contains(text)
                select version.TemplateVersionId;
            var isJobId = Guid.TryParse(text, out var jobId);
            query = query.Where(row => row.UnitId != null && unitIds.Contains(row.UnitId.Value)
                || row.JobNumber.Contains(text)
                || (row.RequestedSerial != null && row.RequestedSerial.Contains(text))
                || (row.TemplateVersionId != null && matchingVersions.Contains(row.TemplateVersionId.Value))
                || (isJobId && row.JobId == jobId));
        }
        if (!string.IsNullOrWhiteSpace(printer)) query = query.Where(row => row.PrinterRef == printer.Trim());
        query = kind switch
        {
            LabelPrintJobKind.Manual => query.Where(row => row.OriginalJobId == null && row.SourceEventId == null),
            LabelPrintJobKind.Reprint => query.Where(row => row.OriginalJobId != null),
            LabelPrintJobKind.Event => query.Where(row => row.SourceEventId != null),
            _ => query,
        };
        if (requestedFrom is { } from) query = query.Where(row => row.RequestedAt >= from);
        if (requestedTo is { } to) query = query.Where(row => row.RequestedAt < to);
        var total = await query.CountAsync(cancellationToken);
        var jobs = await query.OrderByDescending(row => row.RequestedAt).ThenByDescending(row => row.JobId)
            .Skip(Math.Max(0, skip)).Take(take).ToListAsync(cancellationToken);
        return new(await RowsAsync(jobs, cancellationToken), total, Math.Max(0, skip), take);
    }

    public async Task<LabelPrintJobSummary> SummaryAsync(DateTime from, DateTime to,
        CancellationToken cancellationToken)
    {
        if (to <= from) throw new CommandRefusedException("label_print_range_invalid", "The summary end must be after its start.");
        var tenantId = tenant.TenantId;
        var aggregate = await db.PrintJobs.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.RequestedAt >= from && row.RequestedAt < to)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Printed = group.Count(),
                Confirmed = group.Count(row => row.Status == LabelPrintStatus.Confirmed),
                Waiting = group.Count(row => row.Status == LabelPrintStatus.Rendered || row.Status == LabelPrintStatus.Sent),
                Rejected = group.Count(row => row.Status == LabelPrintStatus.Rejected),
                Reprints = group.Count(row => row.OriginalJobId != null),
                FromEvents = group.Count(row => row.SourceEventId != null),
            }).SingleOrDefaultAsync(cancellationToken);
        return new(aggregate?.Printed ?? 0, aggregate?.Confirmed ?? 0, aggregate?.Waiting ?? 0,
            aggregate?.Rejected ?? 0, aggregate?.Reprints ?? 0, aggregate?.FromEvents ?? 0, from, to);
    }

    public Task<List<string>> PrintersAsync(DateTime? from, DateTime? to, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var query = db.PrintJobs.AsNoTracking().Where(row => row.TenantId == tenantId && row.PrinterRef != null);
        if (from is { } requestedFrom) query = query.Where(row => row.RequestedAt >= requestedFrom);
        if (to is { } requestedTo) query = query.Where(row => row.RequestedAt < requestedTo);
        return query.Select(row => row.PrinterRef!).Distinct().OrderBy(value => value).ToListAsync(cancellationToken);
    }

    public async Task<LabelPrintJobRow?> JobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.PrintJobs.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.JobId == jobId, cancellationToken);
        return job is null ? null : (await RowsAsync([job], cancellationToken))[0];
    }

    private async Task<IReadOnlyList<LabelPrintJobRow>> RowsAsync(List<PrintJob> jobs,
        CancellationToken cancellationToken)
    {
        if (jobs.Count == 0) return [];
        var tenantId = tenant.TenantId;
        var versionIds = jobs.Where(row => row.TemplateVersionId != null).Select(row => row.TemplateVersionId!.Value).Distinct().ToList();
        var versions = await (from version in db.LabelTemplateVersions.AsNoTracking()
            join template in db.LabelTemplates.AsNoTracking()
                on new { version.TenantId, version.TemplateId } equals new { template.TenantId, template.TemplateId }
            where version.TenantId == tenantId && versionIds.Contains(version.TemplateVersionId)
            select new { version.TemplateVersionId, version.Version, template.Name }).ToDictionaryAsync(row => row.TemplateVersionId, cancellationToken);
        var unitIds = jobs.Where(row => row.UnitId != null).Select(row => row.UnitId!.Value).Distinct().ToList();
        var identifiers = await db.Identifiers.AsNoTracking().Where(row => row.TenantId == tenantId && unitIds.Contains(row.UnitId) && row.ValidTo == null)
            .ToListAsync(cancellationToken);
        return jobs.Select(job =>
        {
            var version = job.TemplateVersionId is { } id && versions.TryGetValue(id, out var found) ? found : null;
            var serialValue = job.RequestedSerial ?? (job.IdentifierId is { } identifierId
                ? identifiers.FirstOrDefault(row => row.IdentifierId == identifierId)?.RawValue
                : identifiers.FirstOrDefault(row => row.UnitId == job.UnitId && row.Type == IdentifierType.InternalSerial)?.RawValue
                    ?? identifiers.FirstOrDefault(row => row.UnitId == job.UnitId && row.Type == IdentifierType.ManufacturerSerial)?.RawValue
                    ?? identifiers.FirstOrDefault(row => row.UnitId == job.UnitId)?.RawValue);
            return new LabelPrintJobRow(job.JobId, job.JobNumber, job.TemplateVersionId, job.UnitId, job.IdentifierId, job.OriginalJobId,
                version?.Name, version?.Version, serialValue, job.RenderedPayload, job.PayloadHash, job.PrinterRef,
                job.RequestedBy, job.RequestedAt, job.Status, job.RejectionRule, job.RejectionReason,
                job.EmptyRequiredField, job.SentAt, job.ConfirmedAt, job.SourceEventId);
        }).ToList();
    }
}
