using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Certificates;

/// <summary>Dependency-free HTML renderer for tests and previews. Production PDF waits on library licence approval.</summary>
public sealed partial class PlainHtmlCertificateRenderer(SerialisationDbContext db, ITenantContext tenant) : ICertificateRenderer
{
    public async Task<CertificateRenderResult> RenderAsync(CertificateRenderRequest request, CancellationToken cancellationToken)
    {
        var template = await db.CertificateTemplateVersions.AsNoTracking().FirstOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.TemplateVersionId == request.TemplateVersionId, cancellationToken)
            ?? throw new InvalidOperationException($"Certificate template version {request.TemplateVersionId} does not exist.");
        using var payload = JsonDocument.Parse(request.Payload);
        var header = Render(template.HeaderBody, payload.RootElement);
        var html = Render(template.LayoutBody, payload.RootElement);
        var footer = Render(template.FooterBody, payload.RootElement);
        var logo = await LogoAsync(payload.RootElement, cancellationToken);
        var copy = request.Copy ? "<div role=\"note\" style=\"font:700 18px sans-serif;letter-spacing:.12em\">COPY</div>" : "";
        var signature = request.Signature is null ? "" : $"<footer><hr><strong>{Encode(request.Signature.PrintedName)}</strong><br>" +
            $"{Encode(request.Signature.Meaning)} · {request.Signature.SignedAt:O}</footer>";
        var document = $"<!doctype html><html><head><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'\"></head><body>{copy}{logo}{header}{html}{footer}{signature}</body></html>";
        return new CertificateRenderResult(Encoding.UTF8.GetBytes(document), "text/html; charset=utf-8");
    }

    private static string Render(string? template, JsonElement payload)
    {
        var html = EachPattern().Replace(template ?? "", match => RenderEach(match, payload));
        return ValuePattern().Replace(html, match => Encode(Read(payload, match.Groups[1].Value)));
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

    private static string RenderEach(Match match, JsonElement root)
    {
        var value = Read(root, match.Groups[1].Value);
        if (value is not { ValueKind: JsonValueKind.Array }) return "";
        var body = match.Groups[2].Value;
        return string.Concat(value.Value.EnumerateArray().Select(item =>
            ValuePattern().Replace(body, token => Encode(Read(item, token.Groups[1].Value)))));
    }

    private static JsonElement? Read(JsonElement root, string path)
    {
        var current = root;
        foreach (var part in path.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries))
            if (!current.TryGetProperty(part, out current)) return null;
        return current;
    }

    private static string Encode(JsonElement? element) => WebUtility.HtmlEncode(element?.ToString() ?? "");
    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    [GeneratedRegex(@"\{\{#each\s+([a-zA-Z0-9_.]+)\s*\}\}([\s\S]*?)\{\{/each\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex EachPattern();
    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_.]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex ValuePattern();
}
