using System.Text;

namespace Yojana.Serialisation.Imports;

internal static class DelimitedText
{
    private static readonly char[] Candidates = [',', ';', '\t'];

    public static char Sniff(Stream input)
    {
        input.Position = 0;
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), true, 4096, leaveOpen: true);
        var counts = Candidates.ToDictionary(c => c, _ => 0);
        var quoted = false;
        while (reader.Read() is var value && value >= 0)
        {
            var c = (char)value;
            if (c == '"')
            {
                if (quoted && reader.Peek() == '"') { reader.Read(); continue; }
                quoted = !quoted;
            }
            else if (!quoted && (c == '\r' || c == '\n')) break;
            else if (!quoted && counts.TryGetValue(c, out var count)) counts[c] = count + 1;
        }

        input.Position = 0;
        var maximum = counts.Values.Max();
        if (maximum == 0) return ',';
        return Candidates.First(candidate => counts[candidate] == maximum);
    }

    /// <summary>RFC 4180 parser; quoted delimiters/newlines and doubled quotes are preserved correctly.</summary>
    public static IEnumerable<IReadOnlyList<string>> ReadRows(Stream input, char delimiter)
    {
        input.Position = 0;
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), true, 16 * 1024, leaveOpen: true);
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var sawAny = false;
        while (reader.Read() is var value && value >= 0)
        {
            sawAny = true;
            var c = (char)value;
            if (quoted)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }

            if (c == '"' && field.Length == 0) { quoted = true; continue; }
            if (c == delimiter) { row.Add(field.ToString()); field.Clear(); continue; }
            if (c is '\r' or '\n')
            {
                if (c == '\r' && reader.Peek() == '\n') reader.Read();
                row.Add(field.ToString());
                field.Clear();
                yield return row;
                row = [];
                sawAny = false;
                continue;
            }

            field.Append(c);
        }

        if (quoted) throw new InvalidDataException("csv_unclosed_quote");
        if (sawAny || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }
}
