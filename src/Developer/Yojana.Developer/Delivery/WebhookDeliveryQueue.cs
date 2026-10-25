using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Yojana.Developer.Data;

namespace Yojana.Developer.Delivery;

/// <summary>Reserves consecutive queue positions for deliveries inserted together in a known order.</summary>
/// <remarks>A multi-row insert does not promise to evaluate the column default in row order, so a caller that adds
/// several deliveries in one save (a period replay) takes a range and assigns it itself.</remarks>
public static class WebhookDeliveryQueue
{
    public static async Task<long> ReserveAsync(DeveloperDbContext db, int count, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        // Left open for the unit of work; EF closes it with the context (keeps the session context RLS reads).
        if (connection.State != ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "EXEC sys.sp_sequence_get_range @sequence_name = @name, @range_size = @size, @range_first_value = @first OUTPUT;";
        var name = command.CreateParameter(); name.ParameterName = "@name"; name.DbType = DbType.String; name.Value = $"[{DeveloperDbContext.SchemaName}].[{DeveloperDbContext.DeliverySequence}]"; command.Parameters.Add(name);
        var size = command.CreateParameter(); size.ParameterName = "@size"; size.DbType = DbType.Int64; size.Value = (long)count; command.Parameters.Add(size);
        var first = command.CreateParameter(); first.ParameterName = "@first"; first.DbType = DbType.Object; first.Direction = ParameterDirection.Output; command.Parameters.Add(first);
        await command.ExecuteNonQueryAsync(ct);
        return Convert.ToInt64(first.Value, CultureInfo.InvariantCulture);
    }
}
