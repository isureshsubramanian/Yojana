using System.Globalization;
using System.Text;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Certificates;

/// <summary>Dependency-free HTML renderer for tests, previews and installations without a configured PDF worker.</summary>
public sealed class PlainHtmlCertificateRenderer(CertificateHtmlComposer composer) : ICertificateRenderer, ICertificateBundleRenderer
{
    public string ContentType => "text/html; charset=utf-8";

    public async Task<CertificateRenderResult> RenderAsync(CertificateRenderRequest request, CancellationToken cancellationToken)
    {
        var document = await composer.ComposeAsync(request, cancellationToken);
        return new CertificateRenderResult(Encoding.UTF8.GetBytes(document.AsStandaloneHtml(pageChrome: true)),
            "text/html; charset=utf-8");
    }

    public async Task<CertificateRenderResult> RenderBundleAsync(IReadOnlyList<CertificateRenderRequest> requests,
        CancellationToken cancellationToken)
    {
        var documents = new List<CertificateHtmlDocument>(requests.Count);
        foreach (var request in requests) documents.Add(await composer.ComposeAsync(request, cancellationToken));
        var sections = string.Concat(documents.Select((document, index) =>
            $"<section class=\"certificate-bundle-page\" style=\"--certificate-page-left:{Mm(document.Page.MarginLeftMm)};--certificate-page-top:{Mm(document.Page.MarginTopMm)};" +
            $"padding:{Mm(document.Page.MarginTopMm)} {Mm(document.Page.MarginRightMm)} {Mm(document.Page.MarginBottomMm)} {Mm(document.Page.MarginLeftMm)};break-after:{(index + 1 == documents.Count ? "auto" : "page")}\">" +
            $"<header>{document.Page.HeaderHtml}</header>{document.BodyHtml}<footer>{document.Page.FooterHtml}</footer></section>"));
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><style>" + CertificateDocumentStyle.Css +
            "*{box-sizing:border-box}body{margin:0}.certificate-bundle-page{position:relative}.certificate-logo{position:absolute;left:var(--certificate-page-left);top:var(--certificate-page-top)}.certificate-logo img{max-width:48mm;max-height:20mm}</style></head><body>" +
            sections + "</body></html>";
        return new CertificateRenderResult(Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
    }

    private static string Mm(decimal value) => Math.Max(0, value).ToString("0.##", CultureInfo.InvariantCulture) + "mm";
}
