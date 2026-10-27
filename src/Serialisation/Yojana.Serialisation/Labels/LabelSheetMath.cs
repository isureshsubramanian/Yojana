using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Labels;

public sealed record LabelSheetFit(decimal HorizontalOverflowMm, decimal VerticalOverflowMm)
{
    public bool Fits => HorizontalOverflowMm <= 0 && VerticalOverflowMm <= 0;
}

public sealed record LabelSheetPosition(int Page, int Row, int Column, decimal LeftMm, decimal TopMm);

public static class LabelSheetMath
{
    public static LabelSheetFit Fit(LabelSheetProfile profile)
    {
        var (pageWidth, pageHeight) = PageDimensions(profile.PageSize);
        var usedWidth = profile.MarginLeftMm + profile.MarginRightMm
            + profile.Columns * profile.LabelWidthMm + (profile.Columns - 1) * profile.HorizontalGapMm;
        var usedHeight = profile.MarginTopMm + profile.MarginBottomMm
            + profile.Rows * profile.LabelHeightMm + (profile.Rows - 1) * profile.VerticalGapMm;
        return new(Math.Max(0, usedWidth - pageWidth), Math.Max(0, usedHeight - pageHeight));
    }

    public static void EnsureFits(LabelSheetProfile profile)
    {
        var fit = Fit(profile);
        if (fit.Fits) return;
        var parts = new List<string>();
        if (fit.HorizontalOverflowMm > 0) parts.Add($"{Mm(fit.HorizontalOverflowMm)} mm horizontally");
        if (fit.VerticalOverflowMm > 0) parts.Add($"{Mm(fit.VerticalOverflowMm)} mm vertically");
        throw new CommandRefusedException("label_sheet_grid_overflow",
            $"The sticker grid does not fit the {profile.PageSize} page; it overflows by {string.Join(" and ", parts)}.");
    }

    public static IReadOnlyList<LabelSheetPosition> Positions(LabelSheetProfile profile, int startRow, int startColumn,
        int labelCount)
    {
        EnsureFits(profile);
        if (startRow < 1 || startRow > profile.Rows || startColumn < 1 || startColumn > profile.Columns)
            throw new CommandRefusedException("label_sheet_start_invalid",
                $"Start row must be 1–{profile.Rows} and start column must be 1–{profile.Columns}.");
        ArgumentOutOfRangeException.ThrowIfNegative(labelCount);
        var capacity = profile.Columns * profile.Rows;
        var start = (startRow - 1) * profile.Columns + startColumn - 1;
        return Enumerable.Range(0, labelCount).Select(index =>
        {
            var absolute = start + index;
            var slot = absolute % capacity;
            var row = slot / profile.Columns;
            var column = slot % profile.Columns;
            return new LabelSheetPosition(absolute / capacity + 1, row + 1, column + 1,
                profile.MarginLeftMm + column * (profile.LabelWidthMm + profile.HorizontalGapMm),
                profile.MarginTopMm + row * (profile.LabelHeightMm + profile.VerticalGapMm));
        }).ToList();
    }

    public static (decimal WidthMm, decimal HeightMm) PageDimensions(string pageSize) => pageSize switch
    {
        LabelPageSize.A4 => (210m, 297m),
        LabelPageSize.Letter => (215.9m, 279.4m),
        _ => throw new CommandRefusedException("label_sheet_page_size_invalid", "Sticker-sheet page size must be A4 or Letter."),
    };

    private static string Mm(decimal value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
