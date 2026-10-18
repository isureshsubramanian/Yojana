using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Certificates;

public sealed record CertificateHtmlDocument(string BodyHtml, PdfPageSpec Page)
{
    public string AsStandaloneHtml(bool pageChrome) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" " +
        "content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:\">" +
        "<style>html{font-family:Arial,Helvetica,sans-serif;color:#172033}*{box-sizing:border-box}body{margin:0}" +
        "table{width:100%;border-collapse:collapse}thead{display:table-header-group}tr{break-inside:avoid}" +
        ".certificate-copy{position:fixed;right:4mm;top:2mm;z-index:2;border:2px solid #991b1b;color:#991b1b;" +
        "padding:2mm 4mm;font-weight:800;font-size:18px;letter-spacing:.12em;transform:rotate(-4deg)}" +
        ".certificate-signature{break-inside:avoid}.certificate-logo{position:absolute;left:0;top:0}" +
        ".certificate-footer{font-size:7.5pt;line-height:1.25;color:#64748b}" +
        ".certificate-logo img{max-width:48mm;max-height:20mm}</style></head><body>" +
        (pageChrome ? $"<header>{Page.HeaderHtml}</header>" : "") + BodyHtml +
        (pageChrome ? $"<footer>{Page.FooterHtml}</footer>" : "") + "</body></html>";
}

/// <summary>Creates inert HTML from an immutable payload and template; browser transport is deliberately outside this class.</summary>
public sealed class CertificateHtmlComposer(SerialisationDbContext db, ITenantContext tenant,
    CertificateTemplateEngine engine)
{
    public async Task<CertificateHtmlDocument> ComposeAsync(CertificateRenderRequest request, CancellationToken cancellationToken)
    {
        var template = await db.CertificateTemplateVersions.AsNoTracking().FirstOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.TemplateVersionId == request.TemplateVersionId, cancellationToken)
            ?? throw new InvalidOperationException($"Certificate template version {request.TemplateVersionId} does not exist.");
        using var payload = JsonDocument.Parse(request.Payload);
        var root = payload.RootElement;
        var header = engine.Render(template.HeaderBody, root, request, template.Language);
        var body = engine.Render(template.LayoutBody, root, request, template.Language);
        var footer = engine.Render(template.FooterBody, root, request, template.Language);
        var logo = await LogoAsync(root, cancellationToken);
        var copy = request.Copy ? "<div class=\"certificate-copy\" role=\"note\">COPY</div>" : "";
        var signature = request.Signature is null ? "" :
            $"<section class=\"certificate-signature\"><hr><div><strong>Released by</strong><br>" +
            $"{Encode(request.Signature.PrintedName)} · {Encode(request.Signature.Meaning)}</div>" +
            $"<div><strong>Signed</strong><br>{Encode(engine.FormatDateTime(template.Language, request.Signature.SignedAt, root))} · " +
            $"{Encode(request.Signature.Method.Replace('_', ' '))}</div></section>";
        var articleEnd = body.LastIndexOf("</article>", StringComparison.OrdinalIgnoreCase);
        var signedBody = signature.Length > 0 && articleEnd >= 0
            ? body.Insert(articleEnd, signature)
            : body + signature;
        return new CertificateHtmlDocument(copy + logo + signedBody,
            new PdfPageSpec(template.PageSize, template.Orientation, template.MarginTopMm, template.MarginRightMm,
                template.MarginBottomMm, template.MarginLeftMm, header, footer));
    }

    private async Task<string> LogoAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var branding = Read(payload, "branding");
        if (branding is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty("logo_asset_id", out var idElement)
            || !Guid.TryParse(idElement.GetString(), out var assetId)
            || !value.TryGetProperty("logo_sha256", out var hashElement)) return "";
        var expectedHash = hashElement.GetString();
        var asset = await db.TenantAssets.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.AssetId == assetId && row.Sha256 == expectedHash)
            .Select(row => new { row.MediaType, row.Bytes }).FirstOrDefaultAsync(cancellationToken);
        if (asset is null) return "";
        var source = $"data:{asset.MediaType};base64,{Convert.ToBase64String(asset.Bytes)}";
        return $"<div class=\"certificate-logo\"><img alt=\"\" src=\"{source}\"></div>";
    }

    private static JsonElement? Read(JsonElement root, string path)
    {
        var current = root;
        foreach (var part in path.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries))
            if (!current.TryGetProperty(part, out current)) return null;
        return current;
    }

    private static string Encode(JsonElement? element) => WebUtility.HtmlEncode(element?.ToString() ?? "");
    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");
}
