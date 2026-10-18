using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Contracts;
using Xunit;

namespace Yojana.Serialisation.Tests;

public sealed class PdfCertificateTests
{
    [Fact]
    public void Request_mapping_preserves_page_spec_and_adds_page_numbers()
    {
        var document = new CertificateHtmlDocument("<main>TRACK-2026-001</main>",
            new PdfPageSpec("A4", "landscape", 10, 11, 12, 13, "<b>Header</b>", "<i>Footer</i>"));

        var request = CertificatePdfRequestMapper.Map(document);

        Assert.Contains("TRACK-2026-001", request.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Header</b>", request.Html, StringComparison.Ordinal);
        Assert.Equal(("A4", "landscape", 10, 11, 12, 13), (request.Page.Size, request.Page.Orientation,
            request.Page.MarginTopMm, request.Page.MarginRightMm, request.Page.MarginBottomMm, request.Page.MarginLeftMm));
        Assert.Contains("<b>Header</b>", request.Page.HeaderHtml, StringComparison.Ordinal);
        Assert.Contains("Page <span class=\"pageNumber\"></span> of <span class=\"totalPages\"></span>",
            request.Page.FooterHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundle_mapping_is_one_html_document_with_page_breaks_and_per_template_page_rules()
    {
        var first = new CertificateHtmlDocument("<main>FIRST</main>",
            new PdfPageSpec("A4", "portrait", 10, 10, 10, 10, "H1", "F1"));
        var second = new CertificateHtmlDocument("<main>SECOND</main>",
            new PdfPageSpec("Letter", "landscape", 12, 12, 12, 12, "H2", "F2"));

        var request = CertificatePdfRequestMapper.MapBundle([first, second]);

        Assert.Contains("@page certificate0{size:A4 portrait;margin:10mm 10mm 10mm 10mm}", request.Html, StringComparison.Ordinal);
        Assert.Contains("@page certificate1{size:letter landscape;margin:12mm 12mm 12mm 12mm}", request.Html, StringComparison.Ordinal);
        Assert.Contains("break-after:page", request.Html, StringComparison.Ordinal);
        Assert.Contains("FIRST", request.Html, StringComparison.Ordinal);
        Assert.Contains("SECOND", request.Html, StringComparison.Ordinal);
        Assert.Equal(0, request.Page.MarginTopMm);
        Assert.Contains("pageNumber", request.Page.FooterHtml, StringComparison.Ordinal);
    }
}
