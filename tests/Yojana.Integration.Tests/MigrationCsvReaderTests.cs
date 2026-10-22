using Yojana.Integration.LegacyMigration.LinkOverrides;

namespace Yojana.Integration.Tests;

public sealed class MigrationCsvReaderTests
{
    [Fact]
    public void Reads_utf8_bom_quotes_embedded_comma_and_crlf()
    {
        var rows = MigrationCsvReader.Read("\uFEFFsource_key,direction,note\r\n\"PAIR,1\",customer_facing,\"server \"\"rack\"\"\"\r\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal(["source_key", "direction", "note"], rows[0]);
        Assert.Equal(["PAIR,1", "customer_facing", "server \"rack\""], rows[1]);
    }

    [Fact]
    public void Rejects_characters_after_a_closing_quote()
    {
        var error = Assert.Throws<MigrationCsvFormatException>(() =>
            MigrationCsvReader.Read("source_key\r\n\"PAIR-1\"bad\r\n"));

        Assert.Equal(2, error.Row);
    }
}
