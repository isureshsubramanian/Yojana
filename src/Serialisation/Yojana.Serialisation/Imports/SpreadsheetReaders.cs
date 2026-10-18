using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ExcelDataReader;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Imports;

public sealed class OpenXmlSpreadsheetReader : ISpreadsheetReader
{
    public bool CanRead(string format) => format == ImportFormat.Xlsx;

    public Task<SpreadsheetReadResult> ReadAsync(Stream input, CancellationToken cancellationToken)
    {
        var rows = new List<SpreadsheetRow>();
        using var document = SpreadsheetDocument.Open(input, false);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("spreadsheet_workbook_missing");
        var sharedTable = workbook.SharedStringTablePart?.SharedStringTable;
        var shared = sharedTable is null ? [] : sharedTable.Elements<SharedStringItem>().Select(item => item.InnerText).ToArray();
        var styles = workbook.WorkbookStylesPart?.Stylesheet;
        var workbookRoot = workbook.Workbook ?? throw new InvalidDataException("spreadsheet_workbook_missing");
        foreach (var sheet in workbookRoot.Sheets?.Elements<Sheet>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sheet.State is not null && (sheet.State.Value == SheetStateValues.Hidden || sheet.State.Value == SheetStateValues.VeryHidden)) continue;
            if (sheet.Id?.Value is not { } relationshipId) continue;
            var part = (WorksheetPart)workbook.GetPartById(relationshipId);
            var sheetName = sheet.Name?.Value ?? "Sheet";
            var sheetRows = new List<SpreadsheetRow>();
            var inferredRowNumber = 0;
            var mergedRanges = MergedRanges(part);
            using var reader = OpenXmlReader.Create(part);
            while (reader.Read())
            {
                if (!reader.IsStartElement || reader.ElementType != typeof(Row)) continue;
                if (reader.LoadCurrentElement() is not Row row) continue;
                var cells = new List<SpreadsheetCell>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var index = ColumnIndex(cell.CellReference?.Value);
                    while (cells.Count < index) cells.Add(new(null));
                    cells.Add(ReadCell(cell, shared, styles));
                }

                var rowNumber = row.RowIndex?.Value is { } explicitRow ? checked((int)explicitRow) : inferredRowNumber + 1;
                inferredRowNumber = Math.Max(inferredRowNumber, rowNumber);
                if (cells.Any(cell => !string.IsNullOrEmpty(cell.Text)))
                    sheetRows.Add(new(sheetName, rowNumber, cells));
            }
            ApplyMergedCells(mergedRanges, sheetRows);
            rows.AddRange(sheetRows);
        }

        return Task.FromResult(new SpreadsheetReadResult(rows));
    }

    private static List<string> MergedRanges(WorksheetPart part)
    {
        var ranges = new List<string>();
        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (!reader.IsStartElement || reader.ElementType != typeof(MergeCell)) continue;
            if (reader.LoadCurrentElement() is MergeCell { Reference.Value: { } reference }) ranges.Add(reference);
        }
        return ranges;
    }

    private static void ApplyMergedCells(IReadOnlyList<string> ranges, List<SpreadsheetRow> rows)
    {
        var byNumber = rows.ToDictionary(row => row.RowNumber);
        foreach (var range in ranges)
        {
            var reference = range.Split(':', 2);
            if (reference is not { Length: 2 }) continue;
            var (firstColumn, firstRow) = CellPosition(reference[0]);
            var (lastColumn, lastRow) = CellPosition(reference[1]);
            if (!byNumber.TryGetValue(firstRow, out var first) || firstColumn >= first.Cells.Count) continue;
            var value = first.Cells[firstColumn];
            for (var rowNumber = firstRow; rowNumber <= lastRow; rowNumber++)
            {
                if (!byNumber.TryGetValue(rowNumber, out var row) || row.Cells is not List<SpreadsheetCell> cells) continue;
                while (cells.Count <= lastColumn) cells.Add(new(null));
                for (var column = firstColumn; column <= lastColumn; column++)
                    if (string.IsNullOrEmpty(cells[column].Text)) cells[column] = value;
            }
        }
    }

    private static SpreadsheetCell ReadCell(Cell cell, string[] shared, Stylesheet? styles)
    {
        var formula = cell.CellFormula is not null;
        var text = cell.CellValue?.InnerText ?? cell.InlineString?.InnerText;
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(text, out var index) && index >= 0 && index < shared.Length)
            return new(shared[index], false, formula);
        if (cell.DataType?.Value == CellValues.String || cell.DataType?.Value == CellValues.InlineString)
            return new(text, false, formula);
        if (cell.DataType?.Value == CellValues.Boolean)
            return new(text == "1" ? "TRUE" : "FALSE", false, formula);
        if (text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            if (IsDate(cell, styles)) return new(DateTime.FromOADate(number).ToString("O", CultureInfo.InvariantCulture), false, formula);
            return new(text, true, formula);
        }
        return new(text, false, formula);
    }

    private static bool IsDate(Cell cell, Stylesheet? styles)
    {
        if (styles?.CellFormats is null || cell.StyleIndex?.Value is not { } styleIndex || styleIndex >= styles.CellFormats.Count?.Value) return false;
        var format = (CellFormat)styles.CellFormats.ChildElements[(int)styleIndex];
        var id = format.NumberFormatId?.Value ?? 0;
        if (id is >= 14 and <= 22 or >= 45 and <= 47) return true;
        var code = styles.NumberingFormats?.Elements<NumberingFormat>().FirstOrDefault(row => row.NumberFormatId?.Value == id)?.FormatCode?.Value;
        return code is not null && (code.Contains('d', StringComparison.OrdinalIgnoreCase) || code.Contains('y', StringComparison.OrdinalIgnoreCase));
    }

    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return 0;
        var index = 0;
        foreach (var c in reference.TakeWhile(char.IsLetter)) index = checked(index * 26 + char.ToUpperInvariant(c) - 'A' + 1);
        return Math.Max(0, index - 1);
    }

    private static (int Column, int Row) CellPosition(string reference)
    {
        var letters = reference.TakeWhile(char.IsLetter).ToArray();
        var digits = reference.Skip(letters.Length).ToArray();
        return (ColumnIndex(new string(letters)), int.TryParse(digits, CultureInfo.InvariantCulture, out var row) ? row : 0);
    }
}

public sealed class LegacyXlsSpreadsheetReader : ISpreadsheetReader
{
    static LegacyXlsSpreadsheetReader() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    public bool CanRead(string format) => format == ImportFormat.Xls;

    public Task<SpreadsheetReadResult> ReadAsync(Stream input, CancellationToken cancellationToken)
    {
        var rows = new List<SpreadsheetRow>();
        using var reader = ExcelReaderFactory.CreateBinaryReader(input);
        do
        {
            if (reader.VisibleState != "visible") continue;
            var rowNumber = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rowNumber++;
                var cells = new List<SpreadsheetCell>(reader.FieldCount);
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    var value = reader.GetValue(column);
                    cells.Add(value switch
                    {
                        null => new(null),
                        DateTime date => new(date.ToString("O", CultureInfo.InvariantCulture)),
                        double number => new(number.ToString("R", CultureInfo.InvariantCulture), true),
                        float number => new(number.ToString("R", CultureInfo.InvariantCulture), true),
                        decimal number => new(number.ToString(CultureInfo.InvariantCulture), true),
                        _ => new(Convert.ToString(value, CultureInfo.InvariantCulture)),
                    });
                }
                if (cells.Any(cell => !string.IsNullOrEmpty(cell.Text))) rows.Add(new(reader.Name, rowNumber, cells));
            }
        } while (reader.NextResult());

        using var converted = new MemoryStream();
        OpenXmlTableWriter.WriteWorkbook(converted, rows.GroupBy(row => row.Sheet).Select(sheet =>
            (sheet.Key, (IEnumerable<IReadOnlyList<string?>>)sheet.Select(row => row.Cells.Select(cell => cell.Text).ToList()))));
        return Task.FromResult(new SpreadsheetReadResult(rows, converted.ToArray()));
    }
}
