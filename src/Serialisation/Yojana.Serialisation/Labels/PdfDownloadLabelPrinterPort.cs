using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.Labels;

/// <summary>Creates an inert, self-contained print document; the browser or PDF viewer owns the physical print.</summary>
public sealed class PdfDownloadLabelPrinterPort(IServiceProvider services) : ILabelPrinterPort
{
    public Task<LabelPrinterReceipt> SendAsync(LabelPrinterRequest request, CancellationToken cancellationToken) =>
        SendBatchAsync(new LabelPrinterBatchRequest([request]), cancellationToken);

    public async Task<LabelPrinterReceipt> SendBatchAsync(LabelPrinterBatchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Labels.Count == 0)
            throw new CommandRefusedException("label_print_empty", "At least one rendered label is required.");
        if (request.Labels.Any(label => label.LayoutKind == LabelLayoutKind.Zpl))
            throw new CommandRefusedException(SerialisationUnavailableCodes.LabelLayoutNeedsNetworkPrinter,
                "This label is designed for a network label printer. Use an HTML label template to print as PDF.");
        if (request.Labels.Any(label => label.LayoutKind != LabelLayoutKind.Html))
            throw new CommandRefusedException("label_layout_kind_invalid", "Only HTML label templates can be downloaded as PDF.");
        foreach (var label in request.Labels)
            if (label.WidthMm is < 10 or > 200 || label.HeightMm is < 10 or > 200)
                throw new CommandRefusedException("label_template_size_invalid",
                    "HTML label width and height must each be between 10 and 200 mm.");

        var layout = request.Labels[0].PrintLayout ?? new LabelPrintLayout(LabelOutputFormat.OnePerPage);
        if (!LabelOutputFormat.All.Contains(layout.Format))
            throw new CommandRefusedException("label_output_format_invalid", "Label format must be sheet or one_per_page.");
        if (layout.Copies is < 1 or > 100)
            throw new CommandRefusedException("label_copies_invalid", "Label copies must be between 1 and 100.");
        var expanded = request.Labels.SelectMany(label => Enumerable.Repeat(label, layout.Copies)).ToList();
        var document = layout.Format == LabelOutputFormat.Sheet
            ? Sheet(expanded, layout, request.Labels[0].SheetProfile)
            : OnePerPage(expanded);
        var renderer = services.GetService<IPdfRenderer>();
        var first = request.Labels[0];
        if (renderer is null)
        {
            return new(false, $"html:{first.JobId:N}", Download: new LabelDownload(
                Encoding.UTF8.GetBytes(document.Html), "text/html; charset=utf-8",
                $"labels-{first.JobId:N}.html", true));
        }
        var bytes = await renderer.RenderAsync(document, cancellationToken);
        return new(false, $"pdf:{first.JobId:N}", Download: new LabelDownload(bytes, "application/pdf",
            $"labels-{first.JobId:N}.pdf"));
    }

    private static PdfRenderRequest Sheet(List<LabelPrinterRequest> labels, LabelPrintLayout layout,
        LabelSheetProfile? profile)
    {
        if (profile is null)
            throw new CommandRefusedException("label_sheet_profile_required", "Choose a sticker-sheet profile.");
        LabelSheetMath.EnsureFits(profile);
        if (labels.Any(label => label.WidthMm != profile.LabelWidthMm || label.HeightMm != profile.LabelHeightMm))
            throw new CommandRefusedException("label_sheet_size_mismatch",
                $"The selected sheet takes {Mm(profile.LabelWidthMm)} × {Mm(profile.LabelHeightMm)} mm labels. Choose a matching template or sheet profile.");
        var positions = LabelSheetMath.Positions(profile, layout.StartRow, layout.StartColumn, labels.Count);
        var pages = positions.Count == 0 ? 0 : positions.Max(position => position.Page);
        var sections = new StringBuilder();
        for (var page = 1; page <= pages; page++)
        {
            sections.Append("<section class=\"label-sheet\" style=\"break-after:")
                .Append(page == pages ? "auto" : "page").Append("\">");
            foreach (var item in positions.Select((position, index) => new { position, index })
                         .Where(item => item.position.Page == page))
            {
                var position = item.position;
                sections.Append("<div class=\"label-cell\" style=\"left:").Append(Mm(position.LeftMm))
                    .Append("mm;top:").Append(Mm(position.TopMm)).Append("mm;width:")
                    .Append(Mm(profile.LabelWidthMm)).Append("mm;height:").Append(Mm(profile.LabelHeightMm))
                    .Append("mm\">").Append(labels[item.index].Payload).Append("</div>");
            }
            sections.Append("</section>");
        }
        var dimensions = LabelSheetMath.PageDimensions(profile.PageSize);
        var css = $"@page{{size:{profile.PageSize};margin:0}}html,body{{margin:0;padding:0}}*{{box-sizing:border-box}}" +
            $".label-sheet{{position:relative;width:{Mm(dimensions.WidthMm)}mm;height:{Mm(dimensions.HeightMm)}mm}}" +
            ".label-cell{position:absolute;overflow:hidden}";
        return Document(css, sections.ToString(), profile.PageSize);
    }

    private static PdfRenderRequest OnePerPage(List<LabelPrinterRequest> labels)
    {
        var css = new StringBuilder("html,body{margin:0;padding:0}*{box-sizing:border-box}");
        var sections = new StringBuilder();
        for (var index = 0; index < labels.Count; index++)
        {
            var label = labels[index];
            var pageName = $"label{index}";
            css.Append("@page ").Append(pageName).Append("{size:").Append(Mm(label.WidthMm!.Value)).Append("mm ")
                .Append(Mm(label.HeightMm!.Value)).Append("mm;margin:0}");
            sections.Append("<section class=\"label-page\" style=\"page:").Append(pageName).Append(";width:")
                .Append(Mm(label.WidthMm.Value)).Append("mm;height:").Append(Mm(label.HeightMm.Value))
                .Append("mm;break-after:").Append(index + 1 == labels.Count ? "auto" : "page")
                .Append("\">").Append(label.Payload).Append("</section>");
        }
        css.Append(".label-page{overflow:hidden}");
        return Document(css.ToString(), sections.ToString(), "A4");
    }

    private static PdfRenderRequest Document(string css, string body, string pageSize) => new(
        "<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" " +
        "content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:\"><style>" + css +
        "</style></head><body>" + body + "</body></html>",
        new PdfPageSpec(pageSize, "portrait", 0, 0, 0, 0, null, null, false));

    private static string Mm(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>The tenant setting selects an honest unavailable answer or the real download adapter.</summary>
public sealed class TenantConfiguredLabelPrinterPort(
    LabelSettings settings,
    PdfDownloadLabelPrinterPort pdf,
    UnavailableLabelPrinterPort unavailable) : ILabelPrinterPort
{
    public async Task<LabelPrinterReceipt> SendAsync(LabelPrinterRequest request, CancellationToken cancellationToken) =>
        await (await SelectedAsync(cancellationToken)).SendAsync(request, cancellationToken);

    public async Task<LabelPrinterReceipt> SendBatchAsync(LabelPrinterBatchRequest request,
        CancellationToken cancellationToken) =>
        await (await SelectedAsync(cancellationToken)).SendBatchAsync(request, cancellationToken);

    private async Task<ILabelPrinterPort> SelectedAsync(CancellationToken cancellationToken) =>
        (await settings.GetAsync(cancellationToken)).PrinterAdapter switch
        {
            LabelPrinterAdapter.PdfDownload => pdf,
            LabelPrinterAdapter.Unavailable => unavailable,
            _ => throw new CommandRefusedException("label_printer_adapter_invalid",
                "The configured label printer adapter is not supported."),
        };
}
