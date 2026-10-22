using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.MigrationLoads;

/// <summary>
/// Set-based persistence for the cover portion of a migration chunk. The domain handler prepares rows and ledger
/// deltas; this writer only crosses the SQL boundary through one bulk copy per table in the caller's transaction.
/// It is deliberately beside <c>BulkIngestion</c>: live commands retain their event-first write path, while migration
/// can load hundreds of historical entitlement rows without one SaveChanges batch per fact.
/// </summary>
public sealed class MigrationCoverBulkWriter(SerialisationDbContext db, ITenantContext tenant)
{
    public sealed record Batch(
        IReadOnlyList<Entitlement> Entitlements,
        IReadOnlyList<EntitlementEvidence> Evidence,
        IReadOnlyList<EntitlementUsageLimit> UsageLimits,
        IReadOnlyList<ReplacementLink> ReplacementLinks);

    public sealed record Result(int Entitlements, int Evidence, int UsageLimits, int ReplacementLinks);

    public async Task<Result> WriteAsync(Batch batch, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Migration cover must be written inside the unit chunk transaction.");
        if (batch.Entitlements.Any(row => row.TenantId != tenant.TenantId)
            || batch.Evidence.Any(row => row.TenantId != tenant.TenantId)
            || batch.UsageLimits.Any(row => row.TenantId != tenant.TenantId)
            || batch.ReplacementLinks.Any(row => row.TenantId != tenant.TenantId))
            throw new InvalidOperationException("A migration cover batch cannot cross tenants.");

        await BulkCopyAsync(batch.Entitlements, cancellationToken);
        await BulkCopyAsync(batch.Evidence, cancellationToken);
        await BulkCopyAsync(batch.UsageLimits, cancellationToken);
        await BulkCopyAsync(batch.ReplacementLinks, cancellationToken);
        return new(batch.Entitlements.Count, batch.Evidence.Count, batch.UsageLimits.Count,
            batch.ReplacementLinks.Count);
    }

    private async Task BulkCopyAsync<TEntity>(IReadOnlyList<TEntity> rows, CancellationToken cancellationToken)
        where TEntity : class
    {
        if (rows.Count == 0) return;
        var entity = db.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not mapped.");
        var tableName = entity.GetTableName()!;
        var schema = entity.GetSchema()!;
        var store = StoreObjectIdentifier.Table(tableName, schema);
        var properties = entity.GetProperties()
            .Where(property => property.PropertyInfo is not null
                && property.ValueGenerated == ValueGenerated.Never
                && property.GetColumnName(store) is not null)
            .ToList();
        var table = new DataTable();
        foreach (var property in properties)
            table.Columns.Add(property.GetColumnName(store)!, ColumnType(property.ClrType));
        foreach (var row in rows)
        {
            var values = properties.Select(property => DbValue(property.PropertyInfo!.GetValue(row))).ToArray();
            table.Rows.Add(values);
        }

        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        using var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints,
            (SqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction())
        {
            DestinationTableName = $"[{schema}].[{tableName}]",
            BatchSize = 20_000,
            BulkCopyTimeout = 300,
        };
        foreach (DataColumn column in table.Columns)
            copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await copy.WriteToServerAsync(table, cancellationToken);
    }

    private static Type ColumnType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(TenantId)) return typeof(Guid);
        if (type == typeof(DateOnly)) return typeof(DateTime);
        return type.IsEnum ? Enum.GetUnderlyingType(type) : type;
    }

    private static object DbValue(object? value) => value switch
    {
        null => DBNull.Value,
        TenantId id => id.Value,
        DateOnly day => day.ToDateTime(TimeOnly.MinValue),
        Enum item => Convert.ChangeType(item, Enum.GetUnderlyingType(item.GetType()),
            System.Globalization.CultureInfo.InvariantCulture),
        _ => value,
    };
}
