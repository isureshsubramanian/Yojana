using Microsoft.Extensions.DependencyInjection;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Labels;
using Xunit;

namespace Yojana.Serialisation.Tests;

public sealed class LabelSheetMathTests
{
    [Fact]
    public void Positions_use_margins_gaps_and_reading_order()
    {
        var profile = new LabelSheetProfile("test", "test", LabelPageSize.A4, 2, 2, 50, 30,
            10, 8, 10, 8, 3, 4);

        var positions = LabelSheetMath.Positions(profile, 1, 1, 4);

        Assert.Equal([
            new LabelSheetPosition(1, 1, 1, 8, 10),
            new LabelSheetPosition(1, 1, 2, 61, 10),
            new LabelSheetPosition(1, 2, 1, 8, 44),
            new LabelSheetPosition(1, 2, 2, 61, 44),
        ], positions);
    }

    [Fact]
    public void Start_offset_uses_the_requested_first_free_cell()
    {
        var profile = LabelSettings.BuiltInProfiles[0];

        var positions = LabelSheetMath.Positions(profile, 2, 2, 2);

        Assert.Equal((1, 2, 2, 70m, 37.5m),
            (positions[0].Page, positions[0].Row, positions[0].Column, positions[0].LeftMm, positions[0].TopMm));
        Assert.Equal((1, 2, 3), (positions[1].Page, positions[1].Row, positions[1].Column));
    }

    [Fact]
    public void Labels_after_the_last_cell_continue_on_the_next_page()
    {
        var profile = LabelSettings.BuiltInProfiles[0];

        var positions = LabelSheetMath.Positions(profile, 8, 3, 3);

        Assert.Equal((1, 8, 3), (positions[0].Page, positions[0].Row, positions[0].Column));
        Assert.Equal((2, 1, 1), (positions[1].Page, positions[1].Row, positions[1].Column));
        Assert.Equal((2, 1, 2), (positions[2].Page, positions[2].Row, positions[2].Column));
    }

    [Fact]
    public void Fit_reports_each_overflow_dimension()
    {
        Assert.True(LabelSheetMath.Fit(LabelSettings.DefaultSheet).Fits);
        var estimate = LabelSettings.DefaultSheet with { MarginTopMm = 4.5m, MarginBottomMm = 4.5m };
        var fit = LabelSheetMath.Fit(estimate);

        Assert.Equal(0, fit.HorizontalOverflowMm);
        Assert.Equal(8, fit.VerticalOverflowMm);
        var refusal = Assert.Throws<CommandRefusedException>(() => LabelSheetMath.EnsureFits(estimate));
        Assert.Equal("label_sheet_grid_overflow", refusal.Rule);
        Assert.Contains("8 mm vertically", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pdf_adapter_refuses_zpl_with_the_public_code_and_message()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var adapter = new PdfDownloadLabelPrinterPort(services);

        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => adapter.SendAsync(
            new LabelPrinterRequest(Guid.NewGuid(), "pdf_download", LabelLayoutKind.Zpl, "^XA^XZ", "hash"),
            CancellationToken.None));

        Assert.Equal(SerialisationUnavailableCodes.LabelLayoutNeedsNetworkPrinter, refusal.Rule);
        Assert.Equal("This label is designed for a network label printer. Use an HTML label template to print as PDF.",
            refusal.Message);
    }
}
