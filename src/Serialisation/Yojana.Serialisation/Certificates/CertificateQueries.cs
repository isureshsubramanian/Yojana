using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Paging;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Certificates;

public sealed class CertificateQueries(SerialisationDbContext db, ITenantContext tenant, ICertificateUnitReadPort units,
    IPartyNamePort partyNames, ICertificateRenderer renderer, ICertificateBundleRenderer bundleRenderer,
    CertificateTemplateEngine templateEngine)
{
    private static readonly SortedPage<Data.Entities.CertificateInstance, string> Pages = new("issued",
        row => row.TrackingNumber,
        new SortColumn<Data.Entities.CertificateInstance, string>("tracking_number", row => row.TrackingNumber),
        new SortColumn<Data.Entities.CertificateInstance, DateTime>("issued", row => row.IssuedAt, SortDirection.Descending),
        new SortColumn<Data.Entities.CertificateInstance, string>("status", row => row.Status));

    public async Task<CertificateRenderResult?> RenderAsync(Guid certificateId, CancellationToken cancellationToken)
    {
        var row = await db.CertificateInstances.AsNoTracking().FirstOrDefaultAsync(item => item.TenantId == tenant.TenantId
            && item.CertificateId == certificateId, cancellationToken);
        if (row is null) return null;
        var signature = await db.CertificateSignatures.AsNoTracking().Where(item => item.TenantId == tenant.TenantId
                && item.CertificateId == certificateId).OrderByDescending(item => item.SignedAt).FirstOrDefaultAsync(cancellationToken);
        var supersedes = row.SupersedesCertificateId is not { } predecessorId ? null
            : await db.CertificateInstances.AsNoTracking().Where(item => item.TenantId == tenant.TenantId
                    && item.CertificateId == predecessorId).Select(item => item.TrackingNumber)
                .SingleOrDefaultAsync(cancellationToken);
        return await renderer.RenderAsync(RenderRequest(row, signature, supersedes), cancellationToken);
    }

    /// <summary>Composite print is a rendering concern: both stored certificate chains remain independent.</summary>
    public async Task<CertificateRenderResult?> RenderCompositeAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var current = await db.CertificateInstances.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.UnitId == unitId && row.Status == CertificateStatus.Issued)
            .OrderBy(row => row.DocumentKind).ThenByDescending(row => row.Version).ToListAsync(cancellationToken);
        var selected = new[]
        {
            current.FirstOrDefault(row => row.DocumentKind == CertificateDocumentKind.ReleaseRecord),
            current.FirstOrDefault(row => row.DocumentKind == CertificateDocumentKind.WarrantyCertificate),
        };
        if (selected.Any(row => row is null)) return null;
        var rows = selected.OfType<Data.Entities.CertificateInstance>().ToList();
        var ids = rows.Select(row => row.CertificateId).ToList();
        var signatures = await db.CertificateSignatures.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && ids.Contains(row.CertificateId)).OrderBy(row => row.SignedAt).ToListAsync(cancellationToken);
        var predecessorIds = rows.Select(row => row.SupersedesCertificateId).OfType<Guid>().ToList();
        var predecessors = predecessorIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.CertificateInstances.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                    && predecessorIds.Contains(row.CertificateId))
                .ToDictionaryAsync(row => row.CertificateId, row => row.TrackingNumber, cancellationToken);
        var requests = rows.Select(row => RenderRequest(row,
            signatures.LastOrDefault(signature => signature.CertificateId == row.CertificateId),
            row.SupersedesCertificateId is { } predecessorId ? predecessors.GetValueOrDefault(predecessorId) : null)).ToList();
        return await bundleRenderer.RenderBundleAsync(requests, cancellationToken);
    }

    public async Task<CertificateDetail?> DetailAsync(Guid certificateId, CancellationToken cancellationToken)
    {
        var row = await db.CertificateInstances.AsNoTracking().FirstOrDefaultAsync(item => item.TenantId == tenant.TenantId
            && item.CertificateId == certificateId, cancellationToken);
        if (row is null) return null;
        using var payloadDocument = JsonDocument.Parse(row.Payload);
        var payload = payloadDocument.RootElement.Clone();
        var signatureRows = await db.CertificateSignatures.AsNoTracking().Where(item => item.TenantId == tenant.TenantId && item.CertificateId == certificateId)
            .OrderBy(item => item.SignedAt).Select(item => new
            {
                item.SignatureId, item.PrintedName, item.Meaning, item.SignedAt, item.HashSigned, item.SignatureMethod,
            }).ToListAsync(cancellationToken);
        var signatures = signatureRows.Select(item => new CertificateSignatureRow(item.SignatureId, item.PrintedName,
            item.Meaning, item.SignedAt, DisplayTime(row.Language, item.SignedAt, payload), item.HashSigned,
            item.SignatureMethod)).ToList();
        var reprints = await db.ReprintLogs.AsNoTracking().Where(item => item.TenantId == tenant.TenantId && item.CertificateId == certificateId)
            .OrderByDescending(item => item.At).ToListAsync(cancellationToken);
        var recipientIds = reprints.Where(item => item.RecipientPartyRef != null).Select(item => item.RecipientPartyRef!.Value).Distinct().ToList();
        var recipients = recipientIds.Count == 0 ? new Dictionary<Guid, PartyLabel>() : await partyNames.NamesAsync(recipientIds, cancellationToken);
        var serials = await units.PrimarySerialsAsync([row.UnitId], cancellationToken);
        var itemIds = await units.CurrentItemIdsAsync([row.UnitId], cancellationToken);
        var redaction = await db.CertificateRedactions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == tenant.TenantId && item.CertificateId == certificateId, cancellationToken);
        return new CertificateDetail(row.CertificateId, row.UnitId,
            itemIds.TryGetValue(row.UnitId, out var itemId) ? itemId : null,
            serials.GetValueOrDefault(row.UnitId) ?? "Unlabelled unit",
            row.DocumentKind, row.FormType, row.Version, row.TrackingNumber, row.TemplateVersionId, row.AsBuiltRevisionId,
            payload, row.PayloadHash, row.SupersedesCertificateId, row.SupersedeReason,
            row.TriggeringEventId, Deserialize<CertificateIssuerSnapshot>(row.IssuerSnapshot),
            row.OwnerSnapshot is null ? null : Deserialize<CertificateOwnerSnapshot>(row.OwnerSnapshot),
            Deserialize<CertificateBrandingSnapshot>(row.BrandingSnapshot), row.Language, row.Provenance,
            Deserialize<List<string>>(row.Unknowns), Deserialize<List<string>>(row.Evidence), row.IssuedAt,
            DisplayTime(row.Language, row.IssuedAt, payload), row.Status,
            signatures, reprints.Select(item => new CertificateReprintRow(item.ReprintId, item.Version, item.RequestedBy, item.At,
                DisplayTime(row.Language, item.At, payload),
                (item.RecipientPartyRef is { } recipient && recipients.TryGetValue(recipient, out var name)
                    ? name.Name ?? name.Code : null))).ToList(),
            redaction is null ? null : new CertificateRedactionView(redaction.RedactedAt,
                redaction.OriginalPayloadHash,
                $"Issued and signed on {row.IssuedAt:yyyy-MM-dd}; owner details removed on {redaction.RedactedAt:yyyy-MM-dd} under a personal-data erasure."));
    }

    public async Task<CertificateVersionChain> ChainAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var rows = await Rows(db.CertificateInstances.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && row.UnitId == unitId)
            .OrderBy(row => row.DocumentKind).ThenBy(row => row.Version), cancellationToken);
        return new CertificateVersionChain(rows.Where(row => row.DocumentKind == CertificateDocumentKind.ReleaseRecord).ToList(),
            rows.Where(row => row.DocumentKind == CertificateDocumentKind.WarrantyCertificate).ToList());
    }

    public async Task<Page<CertificateListRow>> PageAsync(string? kind, string? formType, string? status, string? unitSerial,
        DateTime? issuedFrom, DateTime? issuedTo, int skip, int take, CancellationToken cancellationToken,
        string? sort = null, string? direction = null)
    {
        var paging = Pages.Parse(sort, direction, take, skip: skip);
        var source = db.CertificateInstances.AsNoTracking().Where(row => row.TenantId == tenant.TenantId);
        if (!string.IsNullOrWhiteSpace(kind)) source = source.Where(row => row.DocumentKind == kind);
        if (!string.IsNullOrWhiteSpace(formType)) source = source.Where(row => row.FormType == formType);
        if (!string.IsNullOrWhiteSpace(status)) source = source.Where(row => row.Status == status);
        if (issuedFrom is { } from) source = source.Where(row => row.IssuedAt >= from);
        if (issuedTo is { } to) source = source.Where(row => row.IssuedAt <= to);
        if (!string.IsNullOrWhiteSpace(unitSerial))
        {
            var unitIds = await units.FindUnitIdsBySerialAsync(unitSerial.Trim(), cancellationToken);
            source = source.Where(row => unitIds.Contains(row.UnitId));
        }
        var total = await source.CountAsync(cancellationToken);
        var entities = await paging.Apply(source).Take(paging.Take).ToListAsync(cancellationToken);
        var rows = await Rows(entities, cancellationToken);
        return new Page<CertificateListRow>(rows, total, paging.Skip, paging.Take);
    }

    private async Task<List<CertificateListRow>> Rows(IQueryable<Data.Entities.CertificateInstance> source, CancellationToken cancellationToken) =>
        await Rows(await source.ToListAsync(cancellationToken), cancellationToken);

    private async Task<List<CertificateListRow>> Rows(IReadOnlyList<Data.Entities.CertificateInstance> entities, CancellationToken cancellationToken)
    {
        var unitIds = entities.Select(row => row.UnitId).Distinct().ToList();
        var certificateIds = entities.Select(row => row.CertificateId).ToList();
        var serials = await units.PrimarySerialsAsync(unitIds, cancellationToken);
        var itemIds = await units.CurrentItemIdsAsync(unitIds, cancellationToken);
        var issuedBy = certificateIds.Count == 0 ? new Dictionary<Guid, string>() : await db.CertificateSignatures.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && certificateIds.Contains(row.CertificateId)
                && row.SignatureMethod == "authenticated_user")
            .GroupBy(row => row.CertificateId)
            .Select(group => new { CertificateId = group.Key, PrintedName = group.OrderBy(row => row.SignedAt).Select(row => row.PrintedName).First() })
            .ToDictionaryAsync(row => row.CertificateId, row => row.PrintedName, cancellationToken);
        return entities.Select(row =>
        {
            using var document = JsonDocument.Parse(row.Payload);
            return new CertificateListRow(row.CertificateId, row.UnitId,
                itemIds.TryGetValue(row.UnitId, out var itemId) ? itemId : null,
                serials.GetValueOrDefault(row.UnitId) ?? "Unlabelled unit", row.DocumentKind, row.FormType, row.Version,
                row.TrackingNumber, row.Status, row.IssuedAt,
                DisplayTime(row.Language, row.IssuedAt, document.RootElement), row.Provenance,
                issuedBy.GetValueOrDefault(row.CertificateId));
        }).ToList();
    }

    private string DisplayTime(string language, DateTime value, JsonElement payload) =>
        templateEngine.FormatDateTime(language, value, payload);

    internal static CertificateRenderRequest RenderRequest(Data.Entities.CertificateInstance row,
        Data.Entities.CertificateSignature? signature, string? supersedesTrackingNumber) => new(row.Payload,
        row.TemplateVersionId, false,
        signature is null ? null : new CertificateRenderSignature(signature.PrintedName,
            DisplayMeaning(signature.Meaning), signature.SignedAt, signature.SignatureMethod),
        new CertificateRenderMetadata(row.TrackingNumber, row.Version, row.IssuedAt, supersedesTrackingNumber,
            row.SupersedeReason, row.PayloadHash, $"urn:yojana:certificate:{row.TrackingNumber}"),
        row.DocumentKind);

    private static string DisplayMeaning(string value) => value == CertificateSignatureMeaning.Release
        ? "released as conforming" : value;

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, CanonicalJson.Options)
        ?? throw new InvalidOperationException("Stored certificate JSON is invalid.");
}
