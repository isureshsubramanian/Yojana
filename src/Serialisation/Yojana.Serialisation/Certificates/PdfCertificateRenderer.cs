using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Certificates;

public static class CertificatePdfRequestMapper
{
    public static PdfRenderRequest Map(CertificateHtmlDocument document) => new(
        document.AsStandaloneHtml(pageChrome: false), document.Page with
        {
            HeaderHtml = ChromeHeader(document.Page.HeaderHtml),
            FooterHtml = ChromeFooter(document.Page.FooterHtml, document.Page.ShowPageNumbers),
        });

    public static PdfRenderRequest MapBundle(IReadOnlyList<CertificateHtmlDocument> documents)
    {
        if (documents.Count == 0) throw new ArgumentException("At least one certificate is required.", nameof(documents));
        var rules = new StringBuilder();
        var sections = new StringBuilder();
        for (var index = 0; index < documents.Count; index++)
        {
            var document = documents[index];
            var name = $"certificate{index}";
            rules.Append("@page ").Append(name).Append("{size:").Append(CssPageSize(document.Page.Size))
                .Append(' ').Append(CssOrientation(document.Page.Orientation)).Append(";margin:")
                .Append(Mm(document.Page.MarginTopMm)).Append(' ').Append(Mm(document.Page.MarginRightMm)).Append(' ')
                .Append(Mm(document.Page.MarginBottomMm)).Append(' ').Append(Mm(document.Page.MarginLeftMm)).Append('}');
            sections.Append("<section class=\"certificate-bundle-page\" style=\"page:").Append(name)
                .Append(";break-after:").Append(index + 1 == documents.Count ? "auto" : "page").Append("\">")
                .Append("<header>").Append(document.Page.HeaderHtml).Append("</header>")
                .Append(document.BodyHtml).Append("<footer>").Append(document.Page.FooterHtml).Append("</footer></section>");
        }
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" " +
            "content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:\"><style>" +
            CertificateDocumentStyle.Css + rules +
            ".certificate-bundle-page{position:relative;break-inside:avoid}.certificate-logo{position:absolute;left:0;top:0}.certificate-logo img{max-width:48mm;max-height:20mm}</style></head><body>" +
            sections + "</body></html>";
        var first = documents[0].Page;
        return new PdfRenderRequest(html, first with
        {
            MarginTopMm = 0, MarginRightMm = 0, MarginBottomMm = 10, MarginLeftMm = 0,
            HeaderHtml = null, FooterHtml = ChromeFooter(null, showPageNumbers: true),
        });
    }

    private static string? ChromeHeader(string? html) => string.IsNullOrWhiteSpace(html) ? null :
        $"{CertificateDocumentStyle.StyleTag}<div class=\"certificate-page-chrome\" style=\"width:100%;padding:0 8mm\">{html}</div>";

    private static string? ChromeFooter(string? html, bool showPageNumbers)
    {
        if (string.IsNullOrWhiteSpace(html) && !showPageNumbers) return null;
        var pages = showPageNumbers
            ? "<span style=\"margin-left:auto\">Page <span class=\"pageNumber\"></span> of <span class=\"totalPages\"></span></span>"
            : "";
        return $"{CertificateDocumentStyle.StyleTag}<div class=\"certificate-page-chrome\" style=\"width:100%;padding:0 8mm;display:flex\">{html}{pages}</div>";
    }

    private static string CssPageSize(string value) => WebUtility.HtmlEncode(value.Trim().ToUpperInvariant() switch
    {
        "A3" => "A3", "A4" => "A4", "A5" => "A5", "LETTER" => "letter", "LEGAL" => "legal", _ => "A4",
    });
    private static string CssOrientation(string value) => value.Equals("landscape", StringComparison.OrdinalIgnoreCase)
        ? "landscape" : "portrait";
    private static string Mm(decimal value) => Math.Max(0, value).ToString("0.##", CultureInfo.InvariantCulture) + "mm";
}

public sealed class HttpPdfRenderer(HttpClient client) : IPdfRenderer
{
    public async Task<byte[]> RenderAsync(PdfRenderRequest request, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("render", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"PDF render worker returned {(int)response.StatusCode}: {detail}");
        }
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            throw new InvalidOperationException("PDF render worker returned a response that is not a PDF.");
        return bytes;
    }
}

public sealed class PdfCertificateRenderer(CertificateHtmlComposer composer, IPdfRenderer pdf)
    : ICertificateRenderer, ICertificateBundleRenderer
{
    public async Task<CertificateRenderResult> RenderAsync(CertificateRenderRequest request, CancellationToken cancellationToken)
    {
        var document = await composer.ComposeAsync(request, cancellationToken);
        return new CertificateRenderResult(await pdf.RenderAsync(CertificatePdfRequestMapper.Map(document), cancellationToken),
            "application/pdf");
    }

    public async Task<CertificateRenderResult> RenderBundleAsync(IReadOnlyList<CertificateRenderRequest> requests,
        CancellationToken cancellationToken)
    {
        var documents = new List<CertificateHtmlDocument>(requests.Count);
        foreach (var request in requests) documents.Add(await composer.ComposeAsync(request, cancellationToken));
        return new CertificateRenderResult(await pdf.RenderAsync(CertificatePdfRequestMapper.MapBundle(documents), cancellationToken),
            "application/pdf");
    }
}
