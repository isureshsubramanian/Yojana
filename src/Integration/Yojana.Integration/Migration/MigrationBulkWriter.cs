using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Yojana.Integration.Data;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration;

/// <summary>
/// Persists already-prepared migration rows with one bulk stream per table. Business rules remain in each phase;
/// this class is only their narrow SQL persistence seam, avoiding thousands of parameter-limited INSERT batches.
/// </summary>
public sealed class MigrationBulkWriter(IntegrationDbContext db, ITenantContext tenant)
{
    public async Task<int> WriteTrackedAddsAsync(IReadOnlyList<Type> tableOrder,
        CancellationToken cancellationToken)
    {
        var written = 0;
        foreach (var type in tableOrder)
        {
            var entries = db.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added &&
                entry.Metadata.ClrType == type).ToList();
            if (entries.Count == 0) continue;
            if (entries.Any(entry => entry.Entity is not ITenantScoped scoped || scoped.TenantId != tenant.TenantId))
                throw new InvalidOperationException("A migration bulk-write batch cannot cross tenants.");
            await WriteAsync(entries, cancellationToken);
            foreach (var entry in entries) entry.State = EntityState.Detached;
            written += entries.Count;
        }
        return written;
    }

    private async Task WriteAsync(List<EntityEntry> entries, CancellationToken cancellationToken)
    {
        var entity = entries[0].Metadata;
        var tableName = entity.GetTableName() ?? throw new InvalidOperationException(
            $"{entity.ClrType.Name} is not mapped to a table.");
        var schema = entity.GetSchema() ?? throw new InvalidOperationException(
            $"{entity.ClrType.Name} has no mapped schema.");
        var store = StoreObjectIdentifier.Table(tableName, schema);
        var properties = entity.GetProperties().Where(property => property.PropertyInfo is not null &&
                property.ValueGenerated == ValueGenerated.Never && property.GetColumnName(store) is not null)
            .ToList();
        var table = new DataTable();
        foreach (var property in properties)
            table.Columns.Add(property.GetColumnName(store)!, ColumnType(property.ClrType));
        foreach (var entry in entries)
            table.Rows.Add(properties.Select(property => DbValue(property.PropertyInfo!.GetValue(entry.Entity)))
                .ToArray());

        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await db.Database.OpenConnectionAsync(cancellationToken);
        using var copy = new SqlBulkCopy(connection,
            SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.TableLock, null)
        {
            DestinationTableName = $"[{schema}].[{tableName}]",
            BatchSize = 20_000,
            BulkCopyTimeout = 300,
        };
        foreach (DataColumn column in table.Columns) copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
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
