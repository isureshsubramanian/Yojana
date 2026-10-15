using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Yojana.Platform.Contracts.Identifiers;

// P0-T08 (ADR-0001 AI 54): SqlBulkCopy as the set-based bulk path, and GUID clustering behaviour.
var connectionString = args.Length > 0 ? args[0] : throw new ArgumentException("connection string required");
await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();

async Task Exec(string sql)
{
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 600 };
    await command.ExecuteNonQueryAsync();
}

async Task<T> Scalar<T>(string sql)
{
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 600 };
    return (T)(await command.ExecuteScalarAsync())!;
}

Console.WriteLine($"edition: {await Scalar<string>("SELECT CAST(SERVERPROPERTY('Edition') AS nvarchar(100)) + ' ' + CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50))")}");

// 1. SqlBulkCopy into a heap staging table, then one INSERT ... SELECT guarded by WHERE NOT EXISTS into the partitioned ledger.
await Exec("""
    IF OBJECT_ID('dbo.stg_events') IS NOT NULL DROP TABLE dbo.stg_events;
    CREATE TABLE dbo.stg_events (tenant_id uniqueidentifier NOT NULL, event_time datetime2(3) NOT NULL, event_id uniqueidentifier NOT NULL, unit_id uniqueidentifier NOT NULL, idempotency_key varchar(64) NOT NULL);
    TRUNCATE TABLE dbo.unit_event;
    """);
var tenant = Guid.NewGuid();
const int rows = 200_000;
var table = new DataTable();
table.Columns.Add("tenant_id", typeof(Guid));
table.Columns.Add("event_time", typeof(DateTime));
table.Columns.Add("event_id", typeof(Guid));
table.Columns.Add("unit_id", typeof(Guid));
table.Columns.Add("idempotency_key", typeof(string));
var start = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
for (var i = 0; i < rows; i++)
{
    var at = start.AddSeconds(i);
    table.Rows.Add(tenant, at, TimeOrderedId.New(at), Guid.NewGuid(), "k" + i);
}

var sw = Stopwatch.StartNew();
using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, null) { DestinationTableName = "dbo.stg_events", BatchSize = 20_000, BulkCopyTimeout = 600 })
{
    await bulk.WriteToServerAsync(table);
}

Console.WriteLine($"SqlBulkCopy {rows:N0} rows into heap staging: {sw.ElapsedMilliseconds:N0} ms");
sw.Restart();
await Exec("""
    INSERT dbo.unit_event (tenant_id, event_time, event_id, unit_id, idempotency_key)
    SELECT s.tenant_id, s.event_time, s.event_id, s.unit_id, s.idempotency_key
    FROM dbo.stg_events s
    WHERE NOT EXISTS (SELECT 1 FROM dbo.unit_event e WHERE e.tenant_id = s.tenant_id AND e.idempotency_key = s.idempotency_key AND e.event_time = s.event_time);
    """);
Console.WriteLine($"INSERT ... SELECT WHERE NOT EXISTS into partitioned table: {sw.ElapsedMilliseconds:N0} ms, rows now {await Scalar<int>("SELECT COUNT(*) FROM dbo.unit_event")}");
sw.Restart();
await Exec("""
    INSERT dbo.unit_event (tenant_id, event_time, event_id, unit_id, idempotency_key)
    SELECT s.tenant_id, s.event_time, s.event_id, s.unit_id, s.idempotency_key
    FROM dbo.stg_events s
    WHERE NOT EXISTS (SELECT 1 FROM dbo.unit_event e WHERE e.tenant_id = s.tenant_id AND e.idempotency_key = s.idempotency_key AND e.event_time = s.event_time);
    """);
Console.WriteLine($"same batch replayed (idempotent, expect 0 new): {sw.ElapsedMilliseconds:N0} ms, rows now {await Scalar<int>("SELECT COUNT(*) FROM dbo.unit_event")}");
Console.WriteLine($"rows per partition: {await Scalar<string>("SELECT STRING_AGG(CAST(partition_number AS varchar) + ':' + CAST(rows AS varchar), ', ') FROM sys.partitions WHERE object_id = OBJECT_ID('dbo.unit_event') AND index_id = 1")}");

// 2. Clustered-key behaviour of three GUID strategies.
foreach (var (name, generator) in new (string, Func<Guid>)[] { ("Guid.NewGuid (random)", Guid.NewGuid), ("Guid.CreateVersion7 (UUIDv7)", Guid.CreateVersion7), ("TimeOrderedId (SQL Server ordered)", TimeOrderedId.New) })
{
    await Exec("IF OBJECT_ID('dbo.guid_test') IS NOT NULL DROP TABLE dbo.guid_test; CREATE TABLE dbo.guid_test (id uniqueidentifier NOT NULL, n int NOT NULL, filler char(200) NOT NULL DEFAULT 'x', CONSTRAINT pk_guid_test PRIMARY KEY CLUSTERED (id));");
    var t = new DataTable();
    t.Columns.Add("id", typeof(Guid));
    t.Columns.Add("n", typeof(int));
    t.Columns.Add("filler", typeof(string));
    for (var i = 0; i < 50_000; i++)
    {
        t.Rows.Add(generator(), i, "x");
    }

    using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "dbo.guid_test", BatchSize = 1_000, BulkCopyTimeout = 600 })
    {
        await bulk.WriteToServerAsync(t);
    }

    var outOfOrder = await Scalar<int>("SELECT SUM(CASE WHEN n <> rn - 1 THEN 1 ELSE 0 END) FROM (SELECT n, ROW_NUMBER() OVER (ORDER BY id) AS rn FROM dbo.guid_test) x");
    var frag = await Scalar<double>("SELECT avg_fragmentation_in_percent FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('dbo.guid_test'), 1, NULL, 'LIMITED')");
    var pages = await Scalar<long>("SELECT page_count FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('dbo.guid_test'), 1, NULL, 'LIMITED')");
    Console.WriteLine($"{name}: rows out of insertion order under clustered order = {outOfOrder:N0} / 50,000; fragmentation {frag:F1} %; pages {pages:N0}");
}
