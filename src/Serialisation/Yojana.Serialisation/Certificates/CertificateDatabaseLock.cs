using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Certificates;

/// <summary>Serialises certificate-owned insert-if-absent and version-allocation boundaries in SQL Server.</summary>
public sealed class CertificateDatabaseLock(SerialisationDbContext db, ITenantContext tenant)
{
    public Task AcquireChainAsync(Guid unitId, string documentKind, CancellationToken cancellationToken) =>
        AcquireAsync($"cert-chain:{tenant.TenantId}:{unitId:N}:{documentKind}", cancellationToken);

    /// <summary>
    /// The same chain locks as <see cref="AcquireChainAsync"/> for many units in one round trip (a migration chunk). The
    /// resources are taken in a fixed order, so two callers holding overlapping lists cannot deadlock on each other.
    /// </summary>
    public async Task AcquireChainsAsync(IReadOnlyCollection<Guid> unitIds, string documentKind, CancellationToken cancellationToken)
    {
        var resources = unitIds.Distinct().Order().Select(unitId => $"cert-chain:{tenant.TenantId}:{unitId:N}:{documentKind}").ToList();
        if (resources.Count == 0) return;
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Certificate database locks require an active transaction.");
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        // The list travels as one JSON parameter; a cursor takes each lock with the same mode, owner and timeout.
        command.CommandText = """
            DECLARE @resource nvarchar(255), @result int;
            DECLARE chains CURSOR LOCAL FAST_FORWARD FOR SELECT [value] FROM OPENJSON(@resources) ORDER BY CONVERT(int, [key]);
            OPEN chains;
            FETCH NEXT FROM chains INTO @resource;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 20000;
                IF @result < 0 THROW 51000, 'Certificate database lock timed out.', 1;
                FETCH NEXT FROM chains INTO @resource;
            END
            CLOSE chains;
            DEALLOCATE chains;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resources";
        parameter.DbType = DbType.String;
        parameter.Value = System.Text.Json.JsonSerializer.Serialize(resources);
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task AcquireStartersAsync(CancellationToken cancellationToken) =>
        AcquireAsync($"cert-starters:{tenant.TenantId}", cancellationToken);

    private async Task AcquireAsync(string resource, CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Certificate database locks require an active transaction.");
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 20000; IF @result < 0 THROW 51000, 'Certificate database lock timed out.', 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.DbType = DbType.String;
        parameter.Value = resource;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

internal static class CertificateTriggerOutcome
{
    public const string Issued = "issued";
    public const string Absorbed = "absorbed";
    public const string Refused = "refused";
}
