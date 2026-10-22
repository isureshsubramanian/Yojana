using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Yojana.Integration.LegacyMigration.Staging;

/// <summary>
/// Creates a source-shaped database from the extraction protocol. Test fixtures and the migration benchmark use this
/// seam so their source schema cannot drift from what extraction actually reads.
/// </summary>
internal static class SourceProtocolDatabase
{
    public static async Task CreateSchemaAsync(SqlConnection connection, CancellationToken cancellationToken = default)
    {
        foreach (var table in SourceProtocol.Tables)
        foreach (var read in table.Reads)
        {
            var columns = string.Join(", ", table.Columns
                .Select(column => (Column: column, Name: SourceColumn(read, column)))
                .Where(item => item.Name is not null)
                .Select(item => $"[{Escape(item.Name!)}] {Declaration(item.Column)} NULL"));
            await using var command = new SqlCommand(
                $"CREATE TABLE [dbo].[{Escape(read.SourceTable)}] ({columns});", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public static string? SourceColumn(SourceRead read, RawColumn column)
    {
        if (read.TextConstants?.ContainsKey(column.PropertyName) == true ||
            read.NullProperties?.Contains(column.PropertyName) == true) return null;
        return read.SourceColumnOverrides?.GetValueOrDefault(column.PropertyName) ?? column.SourceName;
    }

    private static string Declaration(RawColumn column) => column.Kind switch
    {
        RawColumnKind.Int32 => "int",
        RawColumnKind.Int64 => "bigint",
        RawColumnKind.Decimal => "decimal(28,8)",
        RawColumnKind.DateTime => "datetime2(3)",
        _ => $"nvarchar({column.MaxLength!.Value.ToString(CultureInfo.InvariantCulture)})",
    };

    public static string Escape(string value) => value.Replace("]", "]]", StringComparison.Ordinal);
}
