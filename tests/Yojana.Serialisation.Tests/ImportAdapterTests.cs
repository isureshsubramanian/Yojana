using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Imports;

namespace Yojana.Serialisation.Tests;

public sealed class ImportAdapterTests
{
    [Fact]
    public async Task Xlsx_reader_skips_hidden_and_trailing_rows_preserves_text_and_reports_numeric_serials()
    {
        await using var stream = Workbook();
        var result = await new OpenXmlSpreadsheetReader().ReadAsync(stream, CancellationToken.None);

        Assert.Equal(3, result.Rows.Count);
        Assert.DoesNotContain(result.Rows, row => row.Sheet == "Hidden");
        Assert.Equal("00123", result.Rows[1].Cells[0].Text);
        Assert.False(result.Rows[1].Cells[0].IsNumeric);
        Assert.Contains("T00:00:00", result.Rows[1].Cells[1].Text);
        Assert.True(result.Rows[1].Cells[2].IsFormula);
        Assert.Equal("2", result.Rows[1].Cells[2].Text);
        Assert.Equal("Merged", result.Rows[0].Cells[3].Text);
        Assert.Equal("Merged", result.Rows[0].Cells[4].Text);
        Assert.True(result.Rows[2].Cells[0].IsNumeric);
    }

    [Fact]
    public async Task Shared_exporter_writes_streamed_xlsx_without_losing_leading_zeroes()
    {
        var exporter = new TabularExporter();
        await using var stream = new MemoryStream();
        await exporter.WriteAsync("xlsx", new TabularExport(["serial"], Rows(["0000123"])), stream, CancellationToken.None);
        stream.Position = 0;
        var read = await new OpenXmlSpreadsheetReader().ReadAsync(stream, CancellationToken.None);
        Assert.Equal("0000123", read.Rows[1].Cells[0].Text);
        Assert.False(read.Rows[1].Cells[0].IsNumeric);
    }

    [Fact]
    public async Task Shared_csv_exporter_quotes_rfc_fields_and_neutralises_spreadsheet_formulae()
    {
        var exporter = new TabularExporter();
        await using var stream = new MemoryStream();
        await exporter.WriteAsync("csv", new TabularExport(["value", "note"], Rows(["=1+1", "comma, quote\""])),
            stream, CancellationToken.None);

        Assert.Equal("value,note\r\n'=1+1,\"comma, quote\"\"\"\r\n", System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public async Task Legacy_xls_reader_reads_a_real_biff_workbook_and_returns_a_readable_converted_copy()
    {
        await using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-sample.xls"));
        var result = await new LegacyXlsSpreadsheetReader().ReadAsync(input, CancellationToken.None);

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("Serial", result.Rows[0].Cells[0].Text);
        Assert.Equal("SRV-RS1100-0002", result.Rows[2].Cells[0].Text);
        Assert.Equal("L-25", result.Rows[2].Cells[1].Text);
        Assert.NotNull(result.ConvertedXlsx);

        await using var converted = new MemoryStream(result.ConvertedXlsx!);
        var reread = await new OpenXmlSpreadsheetReader().ReadAsync(converted, CancellationToken.None);
        Assert.Equal(result.Rows.Select(row => row.Cells.Select(cell => cell.Text)),
            reread.Rows.Select(row => row.Cells.Select(cell => cell.Text)));
    }

    [Fact]
    public void Legacy_adapter_is_confined_to_xls()
    {
        var reader = new LegacyXlsSpreadsheetReader();
        Assert.True(reader.CanRead(ImportFormat.Xls));
        Assert.False(reader.CanRead(ImportFormat.Xlsx));
    }

    [Fact]
    public void Import_completed_1_0_has_an_embedded_schema()
    {
        using var schema = JsonDocument.Parse(PublishedEventSchemas.Read(ImportCompleted.Schema, ImportCompleted.Version));
        Assert.Equal("Import.Completed 1.0", schema.RootElement.GetProperty("title").GetString());
        Assert.Contains("original_hash", schema.RootElement.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
    }

    private static MemoryStream Workbook()
    {
        var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook, true))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var styles = workbook.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new Stylesheet(
                new Fonts(new Font()), new Fills(new Fill()), new Borders(new Border()),
                new CellStyleFormats(new CellFormat()),
                new CellFormats(new CellFormat(), new CellFormat { NumberFormatId = 14U, ApplyNumberFormat = true }));
            styles.Stylesheet.Save();
            var sheets = workbook.Workbook.AppendChild(new Sheets());
            var visible = workbook.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData(
                new Row(new Cell { CellReference = "A1", DataType = CellValues.InlineString, InlineString = Inline("Serial") },
                        new Cell { CellReference = "B" + "1", DataType = CellValues.InlineString, InlineString = Inline("Date") },
                        new Cell { CellReference = "C1", DataType = CellValues.InlineString, InlineString = Inline("Formula") },
                        new Cell { CellReference = "D1", DataType = CellValues.InlineString, InlineString = Inline("Merged") }) { RowIndex = 1U },
                new Row(new Cell { CellReference = "A2", DataType = CellValues.InlineString, InlineString = Inline("00123") },
                        new Cell { CellReference = "B2", CellValue = new CellValue("45536"), StyleIndex = 1U },
                        new Cell { CellReference = "C2", CellFormula = new CellFormula("1+1"), CellValue = new CellValue("2") }) { RowIndex = 2U },
                new Row(new Cell { CellReference = "A3", CellValue = new CellValue("123") }) { RowIndex = 3U },
                new Row(new Cell { CellReference = "A4" }) { RowIndex = 4U });
            visible.Worksheet = new Worksheet(sheetData, new MergeCells(new MergeCell { Reference = "D1:E1" }));
            visible.Worksheet.Save();
            sheets.Append(new Sheet { Id = workbook.GetIdOfPart(visible), SheetId = 1U, Name = "Visible" });
            var hidden = workbook.AddNewPart<WorksheetPart>();
            hidden.Worksheet = new Worksheet(new SheetData(new Row(new Cell { DataType = CellValues.InlineString, InlineString = Inline("ignore") })));
            hidden.Worksheet.Save();
            sheets.Append(new Sheet { Id = workbook.GetIdOfPart(hidden), SheetId = 2U, Name = "Hidden", State = SheetStateValues.Hidden });
            workbook.Workbook.Save();
        }
        stream.Position = 0;
        return stream;
    }

    private static InlineString Inline(string value) => new(new Text(value));

    private static async IAsyncEnumerable<IReadOnlyList<string?>> Rows(params string[][] rows)
    {
        foreach (var row in rows) yield return row;
        await Task.CompletedTask;
    }
}
