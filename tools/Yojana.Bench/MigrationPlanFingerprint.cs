using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Yojana.Bench;

/// <summary>
/// Produces a stable fingerprint of every semantic mapping-plan row. Generated ids, tenant-specific policy ids and
/// pseudonym values are represented by their meaning so independently-created rehearsals remain comparable.
/// </summary>
public static class MigrationPlanFingerprint
{
    private static readonly string[] Queries =
    [
        """
        SELECT unit.source_key, unit.item_code, unit.item_name, unit.origin, unit.history_depth,
               unit.location_state, unit.warehouse_code, unit.custody_location_kind, unit.condition_grade,
               unit.condition_provenance, unit.note
          FROM migration.migration_plan_unit AS unit
         WHERE unit.tenant_id = @tenant_id AND unit.map_run_id = @map_run_id
         ORDER BY unit.source_key
         FOR JSON PATH, INCLUDE_NULL_VALUES
        """,
        """
        SELECT unit.source_key, row.identifier_type, row.raw_value, row.issuer_kind, row.source_data_value,
               row.provenance
          FROM migration.migration_plan_identifier AS row
          JOIN migration.migration_plan_unit AS unit
            ON unit.tenant_id = row.tenant_id AND unit.plan_unit_id = row.plan_unit_id
         WHERE row.tenant_id = @tenant_id AND row.map_run_id = @map_run_id
         ORDER BY unit.source_key, row.identifier_type, row.raw_value, row.issuer_kind
         FOR JSON PATH, INCLUDE_NULL_VALUES
        """,
        """
        SELECT unit.source_key, row.sequence, row.movement_kind, row.effective_date, row.document_type_label,
               row.document_number, row.party_code, row.warehouse_code, row.quantity, row.provenance
          FROM migration.migration_plan_event AS row
          JOIN migration.migration_plan_unit AS unit
            ON unit.tenant_id = row.tenant_id AND unit.plan_unit_id = row.plan_unit_id
         WHERE row.tenant_id = @tenant_id AND row.map_run_id = @map_run_id
         ORDER BY unit.source_key, row.sequence
         FOR JSON PATH, INCLUDE_NULL_VALUES
        """,
        """
        SELECT unit.source_key, row.tier, row.party_code, row.party_name,
               CASE WHEN row.pseudonymised = 1 THEN N'pseudonymised' ELSE row.party_reference END AS party_reference,
               row.pseudonymised, row.is_current, row.evidence_source, row.evidence_date
          FROM migration.migration_plan_ownership AS row
          JOIN migration.migration_plan_unit AS unit
            ON unit.tenant_id = row.tenant_id AND unit.plan_unit_id = row.plan_unit_id
         WHERE row.tenant_id = @tenant_id AND row.map_run_id = @map_run_id
         ORDER BY unit.source_key, row.tier, row.evidence_source, row.evidence_date
         FOR JSON PATH, INCLUDE_NULL_VALUES
        """,
        """
        SELECT unit.source_key, row.coverage_family,
               CASE WHEN row.policy_id IS NULL THEN 0 ELSE 1 END AS has_policy,
               row.policy_version, row.start_date, row.end_date, row.termination_date, row.remaining_days,
               row.expired, row.direction, row.provenance, row.date_source, row.source_key AS evidence_source_key
          FROM migration.migration_plan_entitlement AS row
          JOIN migration.migration_plan_unit AS unit
            ON unit.tenant_id = row.tenant_id AND unit.plan_unit_id = row.plan_unit_id
         WHERE row.tenant_id = @tenant_id AND row.map_run_id = @map_run_id
         ORDER BY unit.source_key, row.coverage_family, row.source_key
         FOR JSON PATH, INCLUDE_NULL_VALUES
        """,
        """
        SELECT unit.source_key, row.source_kind, row.source_key AS evidence_source_key, row.start_date,
               row.end_date, row.termination_date, row.direction, row.selected_for_entitlement
          FROM migration.migration_plan_warranty_evidence AS row
          JOIN migration.migration_plan_unit AS unit
            ON unit.tenant_id = row.tenant_id AND unit.plan_unit_id = row.plan_unit_id
         WHERE row.tenant_id = @tenant_id AND row.map_run_id = @map_run_id
         ORDER BY unit.source_key, row.source_kind, row.source_key
         FOR JSON PATH, INCLUDE_NULL_VALUES
        """,
        """
        SELECT replaced.source_key AS replaced_source_key, replacement.source_key AS replacement_source_key,
               row.policy_applied, row.source_key
          FROM migration.migration_plan_replacement_link AS row
          JOIN migration.migration_plan_unit AS replaced
            ON replaced.tenant_id = row.tenant_id AND replaced.plan_unit_id = row.replaced_plan_unit_id
          JOIN migration.migration_plan_unit AS replacement
            ON replacement.tenant_id = row.tenant_id AND replacement.plan_unit_id = row.replacement_plan_unit_id
         WHERE row.tenant_id = @tenant_id AND row.map_run_id = @map_run_id
         ORDER BY replaced.source_key, replacement.source_key, row.source_key
         FOR JSON PATH, INCLUDE_NULL_VALUES
        """,
        """
        SELECT unit.source_key AS unit_source_key, row.source_key, row.path, row.file_name, row.file_size,
               row.content_hash
          FROM migration.migration_plan_attachment AS row
          LEFT JOIN migration.migration_plan_unit AS unit
            ON unit.tenant_id = row.tenant_id AND unit.plan_unit_id = row.plan_unit_id
         WHERE row.tenant_id = @tenant_id AND row.map_run_id = @map_run_id
         ORDER BY unit.source_key, row.source_key, row.file_name
         FOR JSON PATH, INCLUDE_NULL_VALUES
        """,
        """
        SELECT unit.source_key, row.version, row.provenance
          FROM migration.migration_plan_certificate AS row
          JOIN migration.migration_plan_unit AS unit
            ON unit.tenant_id = row.tenant_id AND unit.plan_unit_id = row.plan_unit_id
         WHERE row.tenant_id = @tenant_id AND row.map_run_id = @map_run_id
         ORDER BY unit.source_key
         FOR JSON PATH, INCLUDE_NULL_VALUES
        """,
        """
        SELECT unit.source_key AS unit_source_key, row.kind, row.reason, row.source_key
          FROM migration.migration_plan_gap AS row
          LEFT JOIN migration.migration_plan_unit AS unit
            ON unit.tenant_id = row.tenant_id AND unit.plan_unit_id = row.plan_unit_id
         WHERE row.tenant_id = @tenant_id AND row.map_run_id = @map_run_id
         ORDER BY unit.source_key, row.kind, row.source_key, row.reason
         FOR JSON PATH, INCLUDE_NULL_VALUES
        """,
        """
        SELECT unit.source_key AS unit_source_key, row.kind, row.source_key, row.related_source_key, row.note
          FROM migration.migration_plan_log AS row
          LEFT JOIN migration.migration_plan_unit AS unit
            ON unit.tenant_id = row.tenant_id AND unit.plan_unit_id = row.plan_unit_id
         WHERE row.tenant_id = @tenant_id AND row.map_run_id = @map_run_id
         ORDER BY unit.source_key, row.kind, row.source_key, row.related_source_key, row.note
         FOR JSON PATH, INCLUDE_NULL_VALUES
        """,
    ];

    public static async Task<string> CalculateAsync(string connectionString, Guid tenantId, Guid mapRunId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await SetTenantAsync(connection, tenantId, cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var query in Queries)
        {
            await using var command = new SqlCommand(query, connection) { CommandTimeout = 600 };
            command.Parameters.AddWithValue("@tenant_id", tenantId);
            command.Parameters.AddWithValue("@map_run_id", mapRunId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                hash.AppendData(Encoding.UTF8.GetBytes(reader.GetString(0)));
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static async Task<Guid> FirstCompletedMapAsync(string connectionString, Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await SetTenantAsync(connection, tenantId, cancellationToken);
        await using var command = new SqlCommand("""
            SELECT TOP (1) run_id
              FROM migration.migration_map_run
             WHERE tenant_id = @tenant_id AND status = N'completed'
             ORDER BY requested_at, run_id
            """, connection);
        command.Parameters.AddWithValue("@tenant_id", tenantId);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("A completed mapping run was not found."));
    }

    private static async Task SetTenantAsync(SqlConnection connection, Guid tenantId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "EXEC sys.sp_set_session_context @key=N'tenant_id', @value=@tenant_id;", connection);
        command.Parameters.AddWithValue("@tenant_id", tenantId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
