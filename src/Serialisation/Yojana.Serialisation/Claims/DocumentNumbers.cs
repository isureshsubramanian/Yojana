using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Claims;

/// <summary>
/// Allocates human document numbers. Sequence mode uses one SQL Server sequence per tenant, series and year, so
/// independent issuers never update a shared counter row. SQL sequences advance outside the creating transaction:
/// a rollback can therefore leave a documented gap. Gap-free mode retains the transactional counter-row allocator.
/// </summary>
public sealed class DocumentNumbers(SerialisationDbContext db, ITenantContext tenant)
{
    public const string Claim = "CLM";
    public const string Recovery = "RCV";
    public const string Rma = "RMA";
    public const string WarrantyReplacement = "WRP";
    public const string Refurbishment = "RFB";
    public const string Certificate = "CRT";
    public const string PrintJob = "PJ";

    public static readonly IReadOnlyList<string> Series = [Rma, Claim, WarrantyReplacement, Refurbishment, Recovery, Certificate, PrintJob];

    public async Task<string> NextAsync(string series, DateTime at, CancellationToken cancellationToken) =>
        (await NextRangeAsync(series, at, 1, cancellationToken))[0];

    /// <summary>
    /// Allocates <paramref name="count"/> consecutive numbers of one series and year in a single allocation (a migration
    /// load records thousands of certificates per chunk). Gap-free series stay gap-free: the counter moves by the whole
    /// range under the same locks, inside the caller's transaction.
    /// </summary>
    public async Task<IReadOnlyList<string>> NextRangeAsync(string series, DateTime at, int count,
        CancellationToken cancellationToken)
    {
        ValidateSeries(series);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await AcquireLockAsync(ModeLock(tenant.TenantId.Value, series), "Shared", cancellationToken);
            var mode = await ModeAsync(series, cancellationToken);
            int first;
            if (mode == NumberingMode.GapFree)
            {
                // TIGHT COUPLING (document-number lock order)
                // WHAT: allocation takes the shared mode lock before this exclusive counter lock; the settings handler
                // takes only the exclusive mode lock. WHY: a missing counter row has no stable SQL key to lock, while
                // MERGE range locks deadlock when first creators race. WHAT BREAKS IF SEPARATED: reversing or omitting
                // this order can deadlock first allocation or let a mode switch overtake an in-flight document number.
                await AcquireLockAsync(CounterLock(tenant.TenantId.Value, series, at.Year), "Exclusive", cancellationToken);
                first = await NextGapFreeAsync(series, at.Year, count, cancellationToken);
            }
            else
            {
                first = await NextSequenceAsync(series, at.Year, count, cancellationToken);
            }
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return Enumerable.Range(first, count).Select(value => Format(series, at.Year, value)).ToList();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    internal Task AcquireModeChangeLockAsync(string series, CancellationToken cancellationToken) =>
        AcquireLockAsync(ModeLock(tenant.TenantId.Value, series), "Exclusive", cancellationToken);

    internal async Task SynchroniseAsync(string series, string oldMode, string newMode, CancellationToken cancellationToken)
    {
        if (oldMode == newMode) return;
        await db.Database.OpenConnectionAsync(cancellationToken);
        if (newMode == NumberingMode.GapFree)
        {
            await ExecuteAsync("""
                DECLARE @prefix sysname = @sequence_prefix;
                DECLARE @name sysname, @year int, @value bigint;
                DECLARE sequences CURSOR LOCAL FAST_FORWARD FOR
                    SELECT s.name, TRY_CONVERT(int, RIGHT(s.name, 4)), CONVERT(bigint, s.current_value)
                    FROM sys.sequences s
                    WHERE s.schema_id = SCHEMA_ID(N'serialisation') AND s.name LIKE @prefix + N'%' AND s.current_value IS NOT NULL;
                OPEN sequences; FETCH NEXT FROM sequences INTO @name, @year, @value;
                WHILE @@FETCH_STATUS = 0
                BEGIN
                    MERGE [serialisation].[number_series] WITH (HOLDLOCK) AS target
                    USING (SELECT @tenant_id AS tenant_id, @series AS series, @year AS [year], @value AS last_value) AS source
                    ON target.tenant_id = source.tenant_id AND target.series = source.series AND target.[year] = source.[year]
                    WHEN MATCHED AND target.last_value < source.last_value THEN UPDATE SET last_value = CONVERT(int, source.last_value)
                    WHEN NOT MATCHED THEN INSERT (tenant_id, series, [year], last_value) VALUES (source.tenant_id, source.series, source.[year], CONVERT(int, source.last_value));
                    FETCH NEXT FROM sequences INTO @name, @year, @value;
                END
                CLOSE sequences; DEALLOCATE sequences;
                """, cancellationToken,
                ("@sequence_prefix", SequencePrefix(tenant.TenantId.Value, series)),
                ("@tenant_id", tenant.TenantId.Value), ("@series", series));
        }
        else
        {
            var rows = await db.NumberSeries.AsNoTracking().Where(n => n.Series == series)
                .Select(n => new { n.Year, n.LastValue }).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                await EnsureSequenceAsync(series, row.Year, row.LastValue + 1, restartExisting: true, cancellationToken);
            }
        }
    }

    private async Task<string> ModeAsync(string series, CancellationToken cancellationToken)
    {
        await using var command = Command("SELECT mode FROM [serialisation].[number_series_setting] WHERE tenant_id = @tenant_id AND series = @series;");
        Add(command, "@tenant_id", tenant.TenantId.Value);
        Add(command, "@series", series);
        return await command.ExecuteScalarAsync(cancellationToken) as string ?? NumberingMode.Sequence;
    }

    /// <summary>Returns the first value of the allocated range.</summary>
    private async Task<int> NextGapFreeAsync(string series, int year, int count, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            MERGE [serialisation].[number_series] WITH (HOLDLOCK) AS target
            USING (SELECT @tenant_id AS tenant_id, @series AS series, @year AS [year]) AS source
            ON target.tenant_id = source.tenant_id AND target.series = source.series AND target.[year] = source.[year]
            WHEN MATCHED THEN UPDATE SET last_value = target.last_value + @count
            WHEN NOT MATCHED THEN INSERT (tenant_id, series, [year], last_value) VALUES (source.tenant_id, source.series, source.[year], @count)
            OUTPUT inserted.last_value;
            """);
        Add(command, "@tenant_id", tenant.TenantId.Value);
        Add(command, "@series", series);
        Add(command, "@year", year);
        Add(command, "@count", count);
        var last = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        return last - count + 1;
    }

    /// <summary>Returns the first value of the allocated range; a range comes from sp_sequence_get_range in one call.</summary>
    private async Task<int> NextSequenceAsync(string series, int year, int count, CancellationToken cancellationToken)
    {
        var start = await db.NumberSeries.AsNoTracking().Where(n => n.Series == series && n.Year == year)
            .Select(n => (int?)n.LastValue + 1).SingleOrDefaultAsync(cancellationToken) ?? 1;
        var name = SequenceName(tenant.TenantId.Value, series, year);
        if (!await SequenceExistsAsync(name, cancellationToken))
        {
            await EnsureSequenceCommittedAsync(name, start, cancellationToken);
        }
        if (count == 1)
        {
            await using var command = Command($"SELECT NEXT VALUE FOR [serialisation].[{name}];");
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }
        await using var range = Command("""
            DECLARE @first sql_variant;
            EXEC sys.sp_sequence_get_range @sequence_name = @name, @range_size = @count, @range_first_value = @first OUTPUT;
            SELECT CONVERT(int, @first);
            """);
        Add(range, "@name", $"serialisation.{name}");
        Add(range, "@count", (long)count);
        return Convert.ToInt32(await range.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task EnsureSequenceAsync(string series, int year, int start, bool restartExisting, CancellationToken cancellationToken)
    {
        var name = SequenceName(tenant.TenantId.Value, series, year);
        var found = await SequenceExistsAsync(name, cancellationToken);
        if (!found || restartExisting)
        {
            var sql = found
                ? $"ALTER SEQUENCE [serialisation].[{name}] RESTART WITH {start};"
                : $"CREATE SEQUENCE [serialisation].[{name}] AS int START WITH {start} INCREMENT BY 1 NO CYCLE CACHE 50;";
            await ExecuteAsync(sql, cancellationToken);
        }
    }

    private async Task<bool> SequenceExistsAsync(string name, CancellationToken cancellationToken)
    {
        await using var exists = Command("SELECT COUNT(*) FROM sys.sequences WHERE schema_id = SCHEMA_ID(N'serialisation') AND name = @name;");
        Add(exists, "@name", name);
        return Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// Sequence DDL is deliberately committed on its own connection. If it lived in the document transaction, the first
    /// creator would hold schema visibility until its business work committed and every otherwise-independent creator
    /// would wait. The mode's shared application lock still prevents a settings switch during this short initialisation.
    /// </summary>
    private async Task EnsureSequenceCommittedAsync(string name, int start, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = $"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive',
                @LockOwner = 'Session', @LockTimeout = -1, @DbPrincipal = 'public';
            IF @result < 0 THROW 51000, 'Could not initialise the document-number sequence.', 1;
            IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE schema_id = SCHEMA_ID(N'serialisation') AND name = @name)
                EXEC(N'CREATE SEQUENCE [serialisation].[{name}] AS int START WITH {start} INCREMENT BY 1 NO CYCLE CACHE 50;');
            EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session', @DbPrincipal = 'public';
            """;
        command.Parameters.AddWithValue("@resource", $"numbering-sequence:{name}");
        command.Parameters.AddWithValue("@name", name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task AcquireLockAsync(string resource, string mode, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = @mode,
                @LockOwner = 'Transaction', @LockTimeout = -1, @DbPrincipal = 'public';
            IF @result < 0 THROW 51000, 'Could not acquire the document-numbering lock.', 1;
            """);
        command.CommandTimeout = 0;
        Add(command, "@resource", resource);
        Add(command, "@mode", mode);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql);
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand Command(string sql)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    internal static string SequenceName(Guid tenantId, string series, int year) => $"number_{tenantId:N}_{series}_{year}";
    private static string SequencePrefix(Guid tenantId, string series) => $"number_{tenantId:N}_{series}_";
    private static string ModeLock(Guid tenantId, string series) => $"numbering-mode:{tenantId:N}:{series}";
    private static string CounterLock(Guid tenantId, string series, int year) => $"numbering-counter:{tenantId:N}:{series}:{year}";

    public static void ValidateSeries(string series)
    {
        if (!Series.Contains(series, StringComparer.Ordinal)) throw new ArgumentException($"Unknown document-number series '{series}'.", nameof(series));
    }

    public static string Format(string series, int year, int value) => series switch
    {
        Claim => $"{Claim}-{year}-{value:D5}",
        Recovery => $"{Recovery}-{year}-{value:D4}",
        WarrantyReplacement => $"{WarrantyReplacement}-{year}-{value:D5}",
        Refurbishment => $"{Refurbishment}-{year}-{value:D5}",
        Certificate => $"{Certificate}-{year}-{value:D6}",
        _ => $"{series}-{year}-{value:D5}",
    };
}
