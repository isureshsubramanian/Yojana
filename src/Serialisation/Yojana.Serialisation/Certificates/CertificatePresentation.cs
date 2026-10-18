using Yojana.Serialisation.Contracts;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Data;
using System.Text.Json;

namespace Yojana.Serialisation.Certificates;

public sealed record CertificatePresentation(CertificateIssuerSnapshot Issuer, CertificateBrandingSnapshot Branding,
    CertificatePresentationSnapshot Snapshot);

/// <summary>Resolves immutable issue-time presentation without binding the certificate engine to tenant-setting storage.</summary>
public interface ICertificatePresentationPort
{
    Task<CertificatePresentation> ResolveAsync(Guid issuerEntityId, string documentKind, string formType,
        string language, Guid templateVersionId, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves branding through an append-only tenant asset record. The certificate payload receives only the stable asset
/// id and content hash, so the renderer and versioning engine never reach into asset-store internals.
/// </summary>
public sealed class TenantAssetCertificatePresentationPort(SerialisationDbContext db, ITenantContext tenant,
    IPartyNamePort partyNames, ITimeZonePort timeZones, CertificateDateFormatSettings dateFormats) : ICertificatePresentationPort
{
    public async Task<CertificatePresentation> ResolveAsync(Guid issuerEntityId, string documentKind, string formType,
        string language, Guid templateVersionId, CancellationToken cancellationToken)
    {
        var binding = await db.CertificateTemplateVersions.AsNoTracking().Where(version => version.TenantId == tenant.TenantId
                && version.TemplateVersionId == templateVersionId)
            .Select(version => new { version.BrandingAssetId, version.BrandingAssetRef, version.StaticText }).FirstOrDefaultAsync(cancellationToken);
        // COUPLING (branding-map keys): CertificateTemplateService writes "default" or the issuer Guid string.
        // JSON has no nullable Guid object key; changing this reader without its writer makes certificates lose the
        // intended issuer logo at issue time. The certificate still snapshots only the resolved asset contract.
        var mapped = ParseBindings(binding?.BrandingAssetRef);
        var assetId = mapped.GetValueOrDefault(issuerEntityId.ToString()) is { } exact && exact != Guid.Empty
            ? exact : mapped.GetValueOrDefault("default") is { } fallback && fallback != Guid.Empty
                ? fallback : binding?.BrandingAssetId;
        var asset = assetId is not { } selected ? null : await db.TenantAssets.AsNoTracking()
            .Where(logo => logo.TenantId == tenant.TenantId && logo.AssetId == selected
                && (logo.LegalEntityId == null || logo.LegalEntityId == issuerEntityId))
            .Select(logo => new { logo.AssetId, logo.Sha256, logo.MediaType }).FirstOrDefaultAsync(cancellationToken);
        var labels = await partyNames.NamesAsync([issuerEntityId], cancellationToken);
        var issuer = labels.GetValueOrDefault(issuerEntityId);
        var text = ParseText(binding?.StaticText);
        var configuredZone = await timeZones.ForLegalEntityAsync(issuerEntityId,
            TimeZonePurpose.DocumentPresentation, cancellationToken);
        var timeZone = configuredZone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(configuredZone, out _)
            ? configuredZone : "UTC";
        var dateFormat = await dateFormats.ResolveAsync(language, cancellationToken);
        return new CertificatePresentation(
            new CertificateIssuerSnapshot(text.GetValueOrDefault("issuer_display_name") ?? issuer?.Name ?? issuer?.Code ?? "Issuing entity",
                text.GetValueOrDefault("issuer_registration_code"), text.GetValueOrDefault("issuer_address")),
            asset is null
                ? new CertificateBrandingSnapshot("default", null, null)
                : new CertificateBrandingSnapshot("tenant_logo", null, null, asset.AssetId, asset.Sha256, asset.MediaType),
            new CertificatePresentationSnapshot(timeZone, dateFormat.DatePattern, dateFormat.TimePattern));
    }

    private static Dictionary<string, Guid> ParseBindings(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new Dictionary<string, Guid>();
        try { return JsonSerializer.Deserialize<Dictionary<string, Guid>>(value) ?? []; }
        catch (JsonException) { return new Dictionary<string, Guid>(); }
    }

    private static Dictionary<string, string> ParseText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new Dictionary<string, string>();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(value) ?? []; }
        catch (JsonException) { return new Dictionary<string, string>(); }
    }
}
