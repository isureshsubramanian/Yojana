using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Imports;

public sealed class TabularExporter : ITabularExporter
{
    public string ContentType(string format) => format.ToLowerInvariant() switch
    {
        "csv" => "text/csv; charset=utf-8",
        "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => throw new ArgumentException("export_format_unknown", nameof(format)),
    };

    public string Extension(string format) => format.ToLowerInvariant() switch
    {
        "csv" => ".csv",
        "xlsx" => ".xlsx",
        _ => throw new ArgumentException("export_format_unknown", nameof(format)),
    };

    public async Task WriteAsync(string format, TabularExport table, Stream output, CancellationToken cancellationToken)
    {
        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            await using var writer = new StreamWriter(output, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
            await WriteCsvRow(writer, table.Headers, cancellationToken);
            await foreach (var row in table.Rows.WithCancellation(cancellationToken)) await WriteCsvRow(writer, row, cancellationToken);
            await writer.FlushAsync(cancellationToken);
            return;
        }
        if (!format.Equals("xlsx", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("export_format_unknown", nameof(format));

        // OpenXmlWriter is the SDK's forward-only SAX writer: no worksheet DOM or complete row set is retained.
        using var document = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook, autoSave: true);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new Workbook();
        var worksheet = workbook.AddNewPart<WorksheetPart>();
        using (var xml = OpenXmlWriter.Create(worksheet))
        {
            xml.WriteStartElement(new Worksheet());
            xml.WriteStartElement(new SheetData());
            WriteRow(xml, table.Headers);
            await foreach (var row in table.Rows.WithCancellation(cancellationToken)) WriteRow(xml, row);
            xml.WriteEndElement();
            xml.WriteEndElement();
        }
        workbook.Workbook.AppendChild(new Sheets(new Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = 1U, Name = "Export" }));
        workbook.Workbook.Save();
    }

    private static async Task WriteCsvRow(TextWriter writer, IReadOnlyList<string?> row, CancellationToken cancellationToken)
    {
        for (var i = 0; i < row.Count; i++)
        {
            if (i > 0) await writer.WriteAsync(',');
            var value = row[i] ?? "";
            // Spreadsheet applications treat these prefixes as formulae in CSV; exported business data stays text.
            if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
            if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0) value = '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
            await writer.WriteAsync(value.AsMemory(), cancellationToken);
        }
        await writer.WriteAsync("\r\n".AsMemory(), cancellationToken);
    }

    internal static void WriteRow(OpenXmlWriter writer, IReadOnlyList<string?> values)
    {
        writer.WriteStartElement(new Row());
        foreach (var value in values)
        {
            writer.WriteElement(new Cell { DataType = CellValues.InlineString, InlineString = new InlineString(new Text(value ?? "")) });
        }
        writer.WriteEndElement();
    }
}

internal static class OpenXmlTableWriter
{
    public static void WriteWorkbook(Stream output, IEnumerable<(string Name, IEnumerable<IReadOnlyList<string?>> Rows)> sheets)
    {
        using var document = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook, autoSave: true);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new Workbook();
        var list = workbook.Workbook.AppendChild(new Sheets());
        uint id = 1;
        foreach (var source in sheets)
        {
            var worksheet = workbook.AddNewPart<WorksheetPart>();
            using (var xml = OpenXmlWriter.Create(worksheet))
            {
                xml.WriteStartElement(new Worksheet());
                xml.WriteStartElement(new SheetData());
                foreach (var row in source.Rows) TabularExporter.WriteRow(xml, row);
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
            list.Append(new Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = id++, Name = SafeName(source.Name, id) });
        }
        workbook.Workbook.Save();
    }

    private static string SafeName(string name, uint fallback)
    {
        var safe = string.Concat(name.Where(c => c is not ':' and not '\\' and not '/' and not '?' and not '*' and not '[' and not ']'));
        if (string.IsNullOrWhiteSpace(safe)) safe = "Sheet" + fallback;
        return safe.Length <= 31 ? safe : safe[..31];
    }
}
