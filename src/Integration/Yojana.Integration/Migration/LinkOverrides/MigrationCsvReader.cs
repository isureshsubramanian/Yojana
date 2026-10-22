using System.Text;

namespace Yojana.Integration.LegacyMigration.LinkOverrides;

internal sealed class MigrationCsvFormatException(int row, string message) : FormatException(message)
{
    public int Row { get; } = row;
}

internal static class MigrationCsvReader
{
    public static IReadOnlyList<IReadOnlyList<string>> Read(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var afterQuote = false;
        var line = 1;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                        afterQuote = true;
                    }
                }
                else
                {
                    if (ch == '\n') line++;
                    field.Append(ch);
                }
                continue;
            }
            if (afterQuote && ch is not (',' or '\r' or '\n'))
                throw new MigrationCsvFormatException(line, "Characters follow a closing quote.");
            if (ch == '"')
            {
                if (field.Length != 0) throw new MigrationCsvFormatException(line, "A quote appears inside an unquoted field.");
                quoted = true;
                afterQuote = false;
            }
            else if (ch == ',')
            {
                row.Add(field.ToString());
                field.Clear();
                afterQuote = false;
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
                afterQuote = false;
                line++;
            }
            else field.Append(ch);
        }
        if (quoted) throw new MigrationCsvFormatException(line, "A quoted field is not closed.");
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
