using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Certificates;

public sealed partial class CertificateTemplateService(SerialisationDbContext db, ITenantContext tenant, IClock clock,
    CertificatePayloadBuilder payloads, ICertificateRenderer renderer, CertificateDatabaseLock databaseLock)
{
    private const string BuiltInActor = "system:certificate-starters";

    public async Task<IReadOnlyList<CertificateTemplateSummary>> ListAsync(CancellationToken cancellationToken)
    {
        await EnsureStartersAsync(cancellationToken);
        var rows = await db.CertificateTemplates.AsNoTracking().Where(row => row.TenantId == tenant.TenantId)
            .OrderBy(row => row.DocumentKind).ThenBy(row => row.Name).ToListAsync(cancellationToken);
        var versions = await db.CertificateTemplateVersions.AsNoTracking().Where(row => row.TenantId == tenant.TenantId)
            .ToListAsync(cancellationToken);
        var versionIds = versions.Select(version => version.TemplateVersionId).ToList();
        var issuedByVersion = await db.CertificateInstances.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && versionIds.Contains(row.TemplateVersionId))
            .GroupBy(row => row.TemplateVersionId).Select(group => new { VersionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.VersionId, row => row.Count, cancellationToken);
        return rows.Select(row =>
        {
            var own = versions.Where(version => version.TemplateId == row.TemplateId).ToList();
            var latest = own.OrderByDescending(version => version.Version).First();
            return new CertificateTemplateSummary(row.TemplateId, row.Code, row.Name, row.DocumentKind, row.FormType,
                row.AppliesToItemClass, row.BuiltIn, latest.Version, latest.Status, latest.EffectiveFrom, latest.EffectiveTo,
                latest.ApprovedBy, latest.ApprovedAt, own.Sum(version => issuedByVersion.GetValueOrDefault(version.TemplateVersionId)));
        }).ToList();
    }

    public async Task<CertificateTemplateDetail?> GetAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await EnsureStartersAsync(cancellationToken);
        var template = await db.CertificateTemplates.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.TemplateId == templateId, cancellationToken);
        if (template is null) return null;
        var versions = await db.CertificateTemplateVersions.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && row.TemplateId == templateId)
            .OrderByDescending(row => row.Version).ToListAsync(cancellationToken);
        return Detail(template, versions);
    }

    public async Task<CertificateTemplateDetail> CreateAsync(CreateCertificateTemplateRequest request, string actorRef, CancellationToken cancellationToken)
    {
        await EnsureStartersAsync(cancellationToken);
        ValidateIdentity(request.DocumentKind, request.FormType);
        if (await db.CertificateTemplates.AnyAsync(row => row.TenantId == tenant.TenantId && row.Code == request.Code, cancellationToken))
            throw new CommandRefusedException("certificate_template_code_exists", $"Certificate template code '{request.Code}' already exists.");
        CertificateTemplateVersion? source = null;
        if (request.CopyFromTemplateId is { } sourceId)
        {
            source = await db.CertificateTemplateVersions.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && row.TemplateId == sourceId)
                .OrderByDescending(row => row.Status == CertificateTemplateStatus.Active).ThenByDescending(row => row.Version)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new CommandRefusedException("certificate_template_copy_source_unknown", $"Certificate template {sourceId} does not exist.");
        }
        var now = clock.UtcNow;
        var template = new CertificateTemplate
        {
            TenantId = tenant.TenantId, TemplateId = TimeOrderedId.New(now), Code = request.Code.Trim(), Name = request.Name.Trim(),
            DocumentKind = request.DocumentKind, FormType = request.FormType, AppliesToItemClass = request.AppliesToItemClass,
            BuiltIn = false, CreatedAt = now, CreatedBy = actorRef,
        };
        var version = CopyOrBlank(template, source, now, actorRef);
        db.CertificateTemplates.Add(template);
        db.CertificateTemplateVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);
        return Detail(template, [version]);
    }

    public async Task<CertificateTemplateSaveResult> SaveDraftAsync(Guid templateId, SaveCertificateTemplateDraftRequest request,
        string actorRef, CancellationToken cancellationToken)
    {
        var template = await EditableAsync(templateId, cancellationToken);
        var validation = Validate(template.DocumentKind, request.LayoutBody, request.HeaderBody, request.FooterBody, request.Fields);
        if (!validation.Valid) return new CertificateTemplateSaveResult(null, validation);
        var referencedAssets = request.BrandingAssets.Values.Append(request.BrandingAssetId ?? Guid.Empty)
            .Where(assetId => assetId != Guid.Empty).Distinct().ToList();
        var assets = await db.TenantAssets.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && referencedAssets.Contains(row.AssetId) && row.Kind == TenantAssetKind.Logo)
            .ToDictionaryAsync(row => row.AssetId, cancellationToken);
        if (assets.Count != referencedAssets.Count)
            throw new CommandRefusedException("certificate_logo_unknown", "One or more logo assets do not exist for this tenant.");
        foreach (var binding in request.BrandingAssets)
        {
            Guid? expectedEntity = binding.Key == "default" ? null
                : Guid.TryParse(binding.Key, out var entityId) ? entityId
                : throw new CommandRefusedException("certificate_logo_issuer_invalid", $"Logo issuer key '{binding.Key}' is invalid.");
            if (assets[binding.Value].LegalEntityId != expectedEntity)
                throw new CommandRefusedException("certificate_logo_issuer_mismatch", "The logo was not uploaded for the issuer it is bound to.");
        }
        var now = clock.UtcNow;
        CertificateTemplateVersion version;
        if (request.TemplateVersionId is { } versionId)
        {
            version = await db.CertificateTemplateVersions.FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId
                && row.TemplateId == templateId && row.TemplateVersionId == versionId, cancellationToken)
                ?? throw new CommandRefusedException("certificate_template_version_unknown", $"Template version {versionId} does not exist.");
            if (version.Status != CertificateTemplateStatus.Draft)
                throw new CommandRefusedException("certificate_template_version_immutable", "An activated or retired template version cannot be edited; create a new draft version.");
        }
        else
        {
            var next = (await db.CertificateTemplateVersions.Where(row => row.TenantId == tenant.TenantId && row.TemplateId == templateId)
                .MaxAsync(row => (int?)row.Version, cancellationToken) ?? 0) + 1;
            version = new CertificateTemplateVersion
            {
                TenantId = tenant.TenantId, TemplateVersionId = TimeOrderedId.New(now), TemplateId = templateId, Version = next,
                Status = CertificateTemplateStatus.Draft, EffectiveFrom = null, LayoutKind = CertificateLayoutKind.Html,
                CreatedAt = now, CreatedBy = actorRef, UpdatedAt = now, UpdatedBy = actorRef,
                Language = request.Language, PayloadSchemaVersion = request.PayloadSchemaVersion, LayoutBody = request.LayoutBody,
                PageSize = request.PageSize, Orientation = request.Orientation,
            };
            db.CertificateTemplateVersions.Add(version);
        }
        ApplyDraft(version, request, now, actorRef);
        await db.SaveChangesAsync(cancellationToken);
        return new CertificateTemplateSaveResult(Version(version), validation);
    }

    public CertificateTemplateValidation Validate(string documentKind, string layoutBody, string? headerBody,
        string? footerBody, IReadOnlyList<string> declaredFields)
    {
        var catalogue = Catalogue(documentKind).Select(field => field.Path).ToHashSet(StringComparer.Ordinal);
        var used = Placeholders(string.Join('\n', new[] { layoutBody, headerBody, footerBody }.Where(value => value is not null)!));
        var all = used.Concat(declaredFields).Distinct(StringComparer.Ordinal).ToList();
        var unknown = all.Where(path => !catalogue.Contains(path)).Order().ToList();
        var party = documentKind == CertificateDocumentKind.ReleaseRecord
            ? all.Where(IsPartyPath).Order().ToList() : [];
        var unused = declaredFields.Where(path => !used.Contains(path, StringComparer.Ordinal)).Distinct().Order().ToList();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(layoutBody)) errors.Add("Layout body is required.");
        if (unknown.Count > 0) errors.Add("The layout contains placeholders outside the fixed payload catalogue.");
        if (party.Count > 0) errors.Add("Release-record layouts cannot bind party fields.");
        return new CertificateTemplateValidation(errors.Count == 0, unknown, party, unused, errors);
    }

    public IReadOnlyList<CertificateFieldCatalogueEntry> Catalogue(string documentKind) => documentKind switch
    {
        CertificateDocumentKind.ReleaseRecord => FieldCatalogue.For<ReleaseRecordPayload>(),
        CertificateDocumentKind.WarrantyCertificate => FieldCatalogue.For<WarrantyCertificatePayload>(),
        _ => throw new ArgumentException($"Unknown document kind '{documentKind}'.", nameof(documentKind)),
    };

    public async Task<CertificateTemplateVersionRow> ActivateAsync(Guid templateId, Guid versionId, string actorRef, CancellationToken cancellationToken)
    {
        var template = await EditableAsync(templateId, cancellationToken);
        var version = await db.CertificateTemplateVersions.FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.TemplateId == templateId && row.TemplateVersionId == versionId, cancellationToken)
            ?? throw new CommandRefusedException("certificate_template_version_unknown", $"Template version {versionId} does not exist.");
        if (version.Status != CertificateTemplateStatus.Draft)
            throw new CommandRefusedException("certificate_template_not_draft", "Only a draft template version can be activated.");
        var fields = JsonSerializer.Deserialize<List<string>>(version.Fields) ?? [];
        var validation = Validate(template.DocumentKind, version.LayoutBody, version.HeaderBody, version.FooterBody, fields);
        if (!validation.Valid) throw new CommandRefusedException("certificate_template_invalid", string.Join(' ', validation.Errors));
        var now = clock.UtcNow;
        var current = await db.CertificateTemplateVersions.Where(row => row.TenantId == tenant.TenantId && row.TemplateId == templateId
            && row.Status == CertificateTemplateStatus.Active).ToListAsync(cancellationToken);
        foreach (var active in current)
        {
            active.Status = CertificateTemplateStatus.Retired;
            active.EffectiveTo = now;
            active.UpdatedAt = now;
            active.UpdatedBy = actorRef;
        }
        version.Status = CertificateTemplateStatus.Active;
        version.EffectiveFrom = now;
        version.ApprovedAt = now;
        version.ApprovedBy = actorRef;
        version.UpdatedAt = now;
        version.UpdatedBy = actorRef;
        await db.SaveChangesAsync(cancellationToken);
        return Version(version);
    }

    public async Task<CertificateTemplateVersionRow> RetireAsync(Guid templateId, Guid versionId, string actorRef, CancellationToken cancellationToken)
    {
        await EditableAsync(templateId, cancellationToken);
        var version = await db.CertificateTemplateVersions.FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.TemplateId == templateId && row.TemplateVersionId == versionId, cancellationToken)
            ?? throw new CommandRefusedException("certificate_template_version_unknown", $"Template version {versionId} does not exist.");
        if (version.Status != CertificateTemplateStatus.Active)
            throw new CommandRefusedException("certificate_template_not_active", "Only an active template version can be retired.");
        version.Status = CertificateTemplateStatus.Retired;
        version.EffectiveTo = clock.UtcNow;
        version.UpdatedAt = clock.UtcNow;
        version.UpdatedBy = actorRef;
        await db.SaveChangesAsync(cancellationToken);
        return Version(version);
    }

    public async Task<CertificateTemplatePreview> PreviewAsync(Guid templateId, Guid versionId, Guid sampleUnitId,
        string actorRef, CancellationToken cancellationToken)
    {
        var template = await db.CertificateTemplates.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.TemplateId == templateId, cancellationToken)
            ?? throw new CommandRefusedException("certificate_template_unknown", $"Certificate template {templateId} does not exist.");
        var version = await db.CertificateTemplateVersions.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.TemplateId == templateId && row.TemplateVersionId == versionId, cancellationToken)
            ?? throw new CommandRefusedException("certificate_template_version_unknown", $"Template version {versionId} does not exist.");
        var fields = JsonSerializer.Deserialize<List<string>>(version.Fields) ?? [];
        var validation = Validate(template.DocumentKind, version.LayoutBody, version.HeaderBody, version.FooterBody, fields);
        if (!validation.Valid) return new CertificateTemplatePreview([], "text/html; charset=utf-8", validation);
        var built = await payloads.BuildAsync(new IssueCertificateVersion(Guid.CreateVersion7(), sampleUnitId, template.DocumentKind,
            template.FormType, null, clock.UtcNow, actorRef, Guid.Empty), versionId, cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Sample unit {sampleUnitId} does not exist.");
        var rendered = await renderer.RenderAsync(new CertificateRenderRequest(built.CanonicalPayload, versionId), cancellationToken);
        return new CertificateTemplatePreview(rendered.Bytes, rendered.MediaType, validation);
    }

    public async Task<Guid?> ActiveVersionAsync(string documentKind, string formType, string? itemClass,
        CancellationToken cancellationToken)
    {
        var versions = await ActiveVersionsAsync(documentKind, formType, [itemClass], cancellationToken);
        return versions.GetValueOrDefault(itemClass ?? "");
    }

    /// <summary>Resolves exact item-class templates with the generic fallback in one catalogue query.</summary>
    public async Task<IReadOnlyDictionary<string, Guid?>> ActiveVersionsAsync(string documentKind, string formType,
        IReadOnlyCollection<string?> itemClasses, CancellationToken cancellationToken)
    {
        await EnsureStartersAsync(cancellationToken);
        var keys = itemClasses.Select(itemClass => itemClass ?? "").Distinct(StringComparer.Ordinal).ToList();
        var namedClasses = keys.Where(key => key.Length > 0).ToList();
        var candidates = await (from template in db.CertificateTemplates.AsNoTracking()
                                join version in db.CertificateTemplateVersions.AsNoTracking()
                                    on new { template.TenantId, template.TemplateId } equals new { version.TenantId, version.TemplateId }
                                where template.TenantId == tenant.TenantId && template.DocumentKind == documentKind
                                    && template.FormType == formType && version.Status == CertificateTemplateStatus.Active
                                    && (template.AppliesToItemClass == null || namedClasses.Contains(template.AppliesToItemClass))
                                select new
                                {
                                    template.AppliesToItemClass,
                                    template.BuiltIn,
                                    version.Version,
                                    version.TemplateVersionId,
                                }).ToListAsync(cancellationToken);
        return keys.ToDictionary(key => key, key => candidates
            .Where(candidate => candidate.AppliesToItemClass == null || candidate.AppliesToItemClass == key)
            .OrderByDescending(candidate => key.Length > 0 && candidate.AppliesToItemClass == key)
            .ThenBy(candidate => candidate.BuiltIn)
            .ThenByDescending(candidate => candidate.Version)
            .Select(candidate => (Guid?)candidate.TemplateVersionId)
            .FirstOrDefault(), StringComparer.Ordinal);
    }

    public async Task EnsureStartersAsync(CancellationToken cancellationToken)
    {
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            // The lock and tenant/code unique key form one insert-if-absent boundary. Without both, two first GETs can
            // observe an empty catalogue and race three identical starter inserts.
            await databaseLock.AcquireStartersAsync(cancellationToken);
            var starters = Starters();
            var existing = await db.CertificateTemplates.AsNoTracking()
                .Where(row => row.TenantId == tenant.TenantId && row.BuiltIn)
                .Select(row => row.Code).ToListAsync(cancellationToken);
            var missing = starters.Where(starter => !existing.Contains(starter.Code, StringComparer.Ordinal)).ToList();
            if (missing.Count == 0)
            {
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                return;
            }
            var now = clock.UtcNow;
            foreach (var starter in missing)
            {
                var template = new CertificateTemplate
                {
                    TenantId = tenant.TenantId, TemplateId = TimeOrderedId.New(now), Code = starter.Code, Name = starter.Name,
                    DocumentKind = starter.Kind, FormType = starter.Form, BuiltIn = true, CreatedAt = now, CreatedBy = BuiltInActor,
                };
                db.CertificateTemplates.Add(template);
                db.CertificateTemplateVersions.Add(new CertificateTemplateVersion
                {
                    TenantId = tenant.TenantId, TemplateVersionId = TimeOrderedId.New(now), TemplateId = template.TemplateId,
                    Version = 1, Language = "en", PayloadSchemaVersion = "1.0", Status = CertificateTemplateStatus.Active,
                    EffectiveFrom = now, LayoutKind = CertificateLayoutKind.Html, LayoutBody = starter.Body, PageSize = "A4",
                    Orientation = "portrait", MarginTopMm = 15, MarginRightMm = 15, MarginBottomMm = 15, MarginLeftMm = 15,
                    StaticText = "{}", Fields = JsonSerializer.Serialize(Placeholders(starter.Body)), ApprovedBy = BuiltInActor,
                    ApprovedAt = now, CreatedAt = now, CreatedBy = BuiltInActor, UpdatedAt = now, UpdatedBy = BuiltInActor,
                });
            }
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<CertificateTemplate> EditableAsync(Guid templateId, CancellationToken cancellationToken)
    {
        var template = await db.CertificateTemplates.FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.TemplateId == templateId, cancellationToken)
            ?? throw new CommandRefusedException("certificate_template_unknown", $"Certificate template {templateId} does not exist.");
        if (template.BuiltIn) throw new CommandRefusedException("certificate_template_builtin_immutable", "Built-in starter templates must be copied before editing.");
        return template;
    }

    private static void ApplyDraft(CertificateTemplateVersion version, SaveCertificateTemplateDraftRequest request, DateTime now, string actorRef)
    {
        version.Language = request.Language;
        version.PayloadSchemaVersion = request.PayloadSchemaVersion;
        version.LayoutKind = CertificateLayoutKind.Html;
        version.LayoutBody = request.LayoutBody;
        version.PageSize = request.PageSize;
        version.Orientation = request.Orientation;
        version.MarginTopMm = request.MarginTopMm;
        version.MarginRightMm = request.MarginRightMm;
        version.MarginBottomMm = request.MarginBottomMm;
        version.MarginLeftMm = request.MarginLeftMm;
        version.HeaderBody = request.HeaderBody;
        version.FooterBody = request.FooterBody;
        version.BrandingAssetId = request.BrandingAssetId;
        // COUPLING (branding-map keys): TenantAssetCertificatePresentationPort reads "default" or the issuer Guid
        // string written here. A typed JSON object cannot use a nullable Guid key; changing either format alone makes
        // an issuer silently fall back to neutral branding, so keep the key contract in step on both sides.
        version.BrandingAssetRef = JsonSerializer.Serialize(request.BrandingAssets);
        version.StaticText = JsonSerializer.Serialize(request.StaticText);
        version.Fields = JsonSerializer.Serialize(request.Fields.Distinct(StringComparer.Ordinal).Order().ToList());
        version.UpdatedAt = now;
        version.UpdatedBy = actorRef;
    }

    private static CertificateTemplateVersion CopyOrBlank(CertificateTemplate template, CertificateTemplateVersion? source, DateTime now, string actorRef) => new()
    {
        TenantId = template.TenantId, TemplateVersionId = TimeOrderedId.New(now), TemplateId = template.TemplateId, Version = 1,
        Language = source?.Language ?? "en", PayloadSchemaVersion = source?.PayloadSchemaVersion ?? "1.0",
        Status = CertificateTemplateStatus.Draft, LayoutKind = CertificateLayoutKind.Html,
        LayoutBody = source?.LayoutBody ?? "<article><h1>Certificate</h1></article>", PageSize = source?.PageSize ?? "A4",
        Orientation = source?.Orientation ?? "portrait", MarginTopMm = source?.MarginTopMm ?? 15,
        MarginRightMm = source?.MarginRightMm ?? 15, MarginBottomMm = source?.MarginBottomMm ?? 15,
        MarginLeftMm = source?.MarginLeftMm ?? 15, HeaderBody = source?.HeaderBody, FooterBody = source?.FooterBody,
        BrandingAssetId = source?.BrandingAssetId, StaticText = source?.StaticText ?? "{}", Fields = source?.Fields ?? "[]",
        BrandingAssetRef = source?.BrandingAssetRef,
        CreatedAt = now, CreatedBy = actorRef, UpdatedAt = now, UpdatedBy = actorRef,
    };

    private static CertificateTemplateDetail Detail(CertificateTemplate template, IReadOnlyList<CertificateTemplateVersion> versions) =>
        new(template.TemplateId, template.Code, template.Name, template.DocumentKind, template.FormType, template.AppliesToItemClass,
            template.BuiltIn, versions.Select(Version).ToList());

    private static CertificateTemplateVersionRow Version(CertificateTemplateVersion row) => new(row.TemplateVersionId,
        row.Version, row.Status, row.Language, row.PayloadSchemaVersion, row.EffectiveFrom, row.EffectiveTo, row.LayoutKind,
        row.LayoutBody, row.PageSize, row.Orientation, row.MarginTopMm, row.MarginRightMm, row.MarginBottomMm, row.MarginLeftMm,
        row.HeaderBody, row.FooterBody, row.BrandingAssetId,
        BrandingAssets(row.BrandingAssetRef),
        JsonSerializer.Deserialize<Dictionary<string, string>>(row.StaticText) ?? [], JsonSerializer.Deserialize<List<string>>(row.Fields) ?? [],
        row.ApprovedBy, row.ApprovedAt, row.CreatedAt, row.CreatedBy, row.UpdatedAt, row.UpdatedBy);

    private static Dictionary<string, Guid> BrandingAssets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, Guid>();
        try { return JsonSerializer.Deserialize<Dictionary<string, Guid>>(json) ?? []; }
        catch (JsonException) { return new Dictionary<string, Guid>(); }
    }

    private static List<string> Placeholders(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var withoutEach = EachPattern().Replace(text, match =>
        {
            var collection = match.Groups[1].Value;
            result.Add(collection);
            foreach (Match token in ValuePattern().Matches(match.Groups[2].Value)) result.Add(collection + "." + token.Groups[1].Value);
            return "";
        });
        foreach (Match token in ValuePattern().Matches(withoutEach)) result.Add(token.Groups[1].Value);
        return result.Order().ToList();
    }

    private static bool IsPartyPath(string path) => path.Split('.').Any(part => part.Contains("party", StringComparison.OrdinalIgnoreCase)
        || part.Contains("owner", StringComparison.OrdinalIgnoreCase) || part.Contains("customer", StringComparison.OrdinalIgnoreCase)
        || part.Contains("address", StringComparison.OrdinalIgnoreCase));

    private static void ValidateIdentity(string kind, string form)
    {
        if (!CertificateDocumentKind.All.Contains(kind)) throw new CommandRefusedException("certificate_document_kind_unknown", $"Unknown document kind '{kind}'.");
        if (!CertificateFormType.All.Contains(form)) throw new CommandRefusedException("certificate_form_type_unknown", $"Unknown certificate form type '{form}'.");
    }

    private static IReadOnlyList<(string Code, string Name, string Kind, string Form, string Body)> Starters() =>
    [
        ("builtin-generic-release", "Generic release record", CertificateDocumentKind.ReleaseRecord, CertificateFormType.GenericRelease,
            "<article><h1>Release record</h1><p>{{model.code}} · {{condition}}</p><ul>{{#each identifiers}}<li>{{type}}: {{value}}</li>{{/each}}</ul></article>"),
        ("builtin-coc", "Certificate of conformity", CertificateDocumentKind.ReleaseRecord, CertificateFormType.CertificateOfConformity,
            "<article><h1>Certificate of conformity</h1><p>{{model.code}} · {{build_date}}</p><p>{{issuer.name}}</p></article>"),
        ("builtin-warranty", "Warranty certificate", CertificateDocumentKind.WarrantyCertificate, CertificateFormType.Custom,
            "<article><h1>Warranty certificate</h1><p>{{owner.name}}</p><ul>{{#each entitlements}}<li>{{coverage_family}}: {{start_date}} – {{end_date}}</li>{{/each}}</ul></article>"),
    ];

    [GeneratedRegex(@"\{\{#each\s+([a-zA-Z0-9_.]+)\s*\}\}([\s\S]*?)\{\{/each\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex EachPattern();
    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_.]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex ValuePattern();
}

public sealed record CertificateTemplateSaveResult(CertificateTemplateVersionRow? Version, CertificateTemplateValidation Validation);

internal static class FieldCatalogue
{
    public static IReadOnlyList<CertificateFieldCatalogueEntry> For<T>()
    {
        var result = new List<CertificateFieldCatalogueEntry>();
        Walk(typeof(T), "", false, result, new HashSet<Type>());
        return result.OrderBy(row => row.Path).ToList();
    }

    private static void Walk(Type type, string prefix, bool repeating, List<CertificateFieldCatalogueEntry> result, HashSet<Type> stack)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (IsScalar(type)) return;
        if (!stack.Add(type)) return;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);
            var path = string.IsNullOrEmpty(prefix) ? name : prefix + "." + name;
            var element = CollectionElement(property.PropertyType);
            if (element is not null)
            {
                result.Add(new CertificateFieldCatalogueEntry(path, "array", true));
                if (!IsScalar(element)) Walk(element, path, true, result, stack);
            }
            else if (IsScalar(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType))
            {
                result.Add(new CertificateFieldCatalogueEntry(path, ScalarName(property.PropertyType), repeating));
            }
            else Walk(property.PropertyType, path, repeating, result, stack);
        }
        stack.Remove(type);
    }

    private static Type? CollectionElement(Type type) => type.IsGenericType
        && typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string)
            ? type.GetGenericArguments()[0] : null;
    private static bool IsScalar(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Guid)
        || type == typeof(DateTime) || type == typeof(DateOnly) || type == typeof(decimal) || type == typeof(JsonElement);
    private static string ScalarName(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(bool) ? "boolean" : type == typeof(int) || type == typeof(decimal) ? "number" : "string";
    }
}
