using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Platform.Tenancy;

/// <summary>
/// Sets SQL Server <c>SESSION_CONTEXT('tenant_id')</c> read-only once per opened connection, so the
/// row-level-security predicate (<c>sec.fn_tenant_predicate</c>) sees the tenant of the current scope.
/// The connection pool clears session context when a connection is returned (sp_reset_connection), so
/// the next scope sets its own tenant on the same physical connection; verified in P0-T08.
/// When the scope has no tenant nothing is set and the predicate filters every tenant row out.
/// ORDERING (coupling with no visible seam): the tenant must be in the scope BEFORE the first connection
/// opens. For HTTP that is <see cref="Security.TenantResolutionMiddleware"/> ahead of endpoints; for
/// messages it is <see cref="Messaging.TenantEnvelopeMiddleware"/> registered before Wolverine's transaction
/// policy (see PlatformSetup); for jobs it is <see cref="Scheduling.TenantJob"/> before the job body. A
/// connection opened earlier carries no tenant and every tenant write on it is blocked by RLS.
/// </summary>
public sealed class TenantSessionContextInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    public const string SessionKey = "tenant_id";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (tenant.HasTenant)
        {
            using var command = Build(connection, tenant.TenantId);
            command.ExecuteNonQuery();
        }
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (tenant.HasTenant)
        {
            await using var command = Build(connection, tenant.TenantId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static DbCommand Build(DbConnection connection, TenantId tenantId)
    {
        var command = connection.CreateCommand();
        command.CommandText = "EXEC sp_set_session_context @key = N'" + SessionKey + "', @value = @tenant, @read_only = 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tenant";
        parameter.DbType = DbType.Guid;
        parameter.Value = tenantId.Value;
        command.Parameters.Add(parameter);
        return command;
    }
}
