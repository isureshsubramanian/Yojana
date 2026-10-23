using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Settings;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;

namespace Yojana.Platform.Settings;

public sealed class UsersSettingsSummaryContributor(
    PlatformDbContext db,
    ITenantContext tenant,
    IClock clock) : ISettingsSummaryContributor
{
    // Hidden coupling: this key must also appear as summaryKey in web/src/settings/registry.ts. The architecture
    // cross-check protects both otherwise-independent sides from silently drifting.
    public const string Key = "users";

    public IReadOnlyList<SettingsSummaryKey> Keys { get; } =
        [new(Key, [YojanaRole.TenantAdministrator])];

    public async Task<IReadOnlyList<SettingsSummaryLine>> ReadAsync(
        IReadOnlySet<string> keys,
        CancellationToken cancellationToken)
    {
        if (!keys.Contains(Key)) return [];

        var rows = await db.Database.SqlQuery<CountRow>($"""
            SELECT grouped.metric AS [Metric], COUNT_BIG(DISTINCT grouped.value) AS [Value]
            FROM
            (
                SELECT N'staff' AS metric, CONVERT(nvarchar(36), membership.user_id) AS value
                FROM [platform].[user_tenant_membership] AS membership
                INNER JOIN [platform].[app_user] AS app_user ON app_user.id = membership.user_id
                WHERE membership.tenant_id = {tenant.TenantId.Value}
                  AND membership.deactivated_at IS NULL
                  AND membership.customer_party_ref IS NULL
                  AND app_user.is_active = CAST(1 AS bit)

                UNION ALL

                SELECT N'roles' AS metric, roles.value
                FROM [platform].[user_tenant_membership] AS membership
                INNER JOIN [platform].[app_user] AS app_user ON app_user.id = membership.user_id
                CROSS APPLY OPENJSON(membership.roles) AS roles
                WHERE membership.tenant_id = {tenant.TenantId.Value}
                  AND membership.deactivated_at IS NULL
                  AND membership.customer_party_ref IS NULL
                  AND app_user.is_active = CAST(1 AS bit)

                UNION ALL

                SELECT N'invitationsOpen' AS metric, CONVERT(nvarchar(36), invitation.id) AS value
                FROM [platform].[user_invitation] AS invitation
                WHERE invitation.tenant_id = {tenant.TenantId.Value}
                  AND invitation.accepted_at IS NULL
                  AND invitation.revoked_at IS NULL
                  AND invitation.expires_at > {clock.UtcNow}
            ) AS grouped
            GROUP BY grouped.metric
            """).ToListAsync(cancellationToken);

        var counts = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["staff"] = 0,
            ["roles"] = 0,
            ["invitationsOpen"] = 0,
        };
        foreach (var row in rows) counts[row.Metric] = row.Value;
        return [new SettingsSummaryLine(Key, counts, null)];
    }

    private sealed class CountRow
    {
        public required string Metric { get; init; }
        public long Value { get; init; }
    }
}
