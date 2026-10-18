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
        var html = EachPattern().Replace(template.LayoutBody, match => RenderEach(match, payload.RootElement));
        html = ValuePattern().Replace(html, match => Encode(Read(payload.RootElement, match.Groups[1].Value)));
        var copy = request.Copy ? "<div role=\"note\" style=\"font:700 18px sans-serif;letter-spacing:.12em\">COPY</div>" : "";
        var signature = request.Signature is null ? "" : $"<footer><hr><strong>{Encode(request.Signature.PrintedName)}</strong><br>" +
            $"{Encode(request.Signature.Meaning)} · {request.Signature.SignedAt:O}</footer>";
        var document = $"<!doctype html><html><body>{copy}{template.HeaderBody}{html}{template.FooterBody}{signature}</body></html>";
        return new CertificateRenderResult(Encoding.UTF8.GetBytes(document), "text/html; charset=utf-8");
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
