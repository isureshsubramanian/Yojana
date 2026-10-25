using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Platform.Tenancy;

/// <summary>
/// Sets SQL Server <c>SESSION_CONTEXT('tenant_id')</c> read-only once per opened connection, so the
/// row-level-security predicate (<c>sec.fn_tenant_predicate</c>) sees the tenant of the current scope.
/// Customer-portal request scopes also set read-only <c>SESSION_CONTEXT('portal_party_ref')</c>; every other
/// scope leaves it NULL, which preserves the tenant-only predicate branch.
/// The connection pool clears session context when a connection is returned (sp_reset_connection), so
/// the next scope sets its own tenant on the same physical connection; verified in P0-T08.
/// When the scope has no tenant nothing is set and the predicate filters every tenant row out.
/// ORDERING (coupling with no visible seam): the tenant must be in the scope BEFORE the first connection
/// opens. For HTTP that is <see cref="Security.TenantResolutionMiddleware"/> ahead of endpoints; for
/// messages it is <see cref="Messaging.TenantEnvelopeMiddleware"/> registered before Wolverine's transaction
/// policy (see PlatformSetup); for jobs it is <see cref="Scheduling.TenantJob"/> before the job body. A
/// connection opened earlier carries no tenant and every tenant write on it is blocked by RLS.
/// P14-B COUPLING (public API party): PublicApiAuthenticationHandler also sets the portal party before this
/// interceptor opens a connection. Removing either side drops the public API's database defence-in-depth filter;
/// the explicit ownership checks remain authoritative but must not be treated as a reason to separate the pair.
/// </summary>
public sealed class TenantSessionContextInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    public const string SessionKey = "tenant_id";
    public const string PortalPartySessionKey = "portal_party_ref";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (tenant.HasTenant)
        {
            using var command = Build(connection, tenant.TenantId, PortalParty());
            command.ExecuteNonQuery();
        }
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (tenant.HasTenant)
        {
            await using var command = Build(connection, tenant.TenantId, PortalParty());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private Guid? PortalParty() => tenant is IPortalPartyContext { HasPortalParty: true } portal
        ? portal.PortalPartyRef
        : null;

    private static DbCommand Build(DbConnection connection, TenantId tenantId, Guid? portalPartyRef)
    {
        var command = connection.CreateCommand();
        command.CommandText = "EXEC sp_set_session_context @key = N'" + SessionKey
            + "', @value = @tenant, @read_only = 1;"
            + (portalPartyRef is null ? "" : " EXEC sp_set_session_context @key = N'" + PortalPartySessionKey
                + "', @value = @portal_party, @read_only = 1;");
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tenant";
        parameter.DbType = DbType.Guid;
        parameter.Value = tenantId.Value;
        command.Parameters.Add(parameter);
        if (portalPartyRef is { } partyRef)
        {
            var portalParameter = command.CreateParameter();
            portalParameter.ParameterName = "@portal_party";
            portalParameter.DbType = DbType.Guid;
            portalParameter.Value = partyRef;
            command.Parameters.Add(portalParameter);
        }
        return command;
    }
}
