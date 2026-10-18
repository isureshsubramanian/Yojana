using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P7T08OwnershipRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TIGHT COUPLING (portal RLS to the effective-dated ownership model)
            // WHAT: these schema-bound predicates depend on custody_episode and assembly_link column names and
            // half-open interval semantics. WHY: SQL Server RLS must enforce the same direct-or-contained ownership
            // contract even for raw SQL that bypasses PortalOwnershipScope. WHAT BREAKS IF SEPARATED: renaming those
            // columns or changing either interval convention without rebuilding these functions can block valid
            // portal rows or disclose another owner's rows. The matching warning lives on PortalOwnershipScope.
            migrationBuilder.Sql("""
                CREATE FUNCTION [sec].[fn_portal_episode_predicate]
                (
                    @tenant_id uniqueidentifier,
                    @kind varchar(20),
                    @party_ref uniqueidentifier
                )
                RETURNS TABLE WITH SCHEMABINDING AS
                RETURN
                    SELECT 1 AS ok
                    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
                      AND SESSION_CONTEXT(N'portal_party_ref') IS NULL
                    UNION ALL
                    SELECT 1 AS ok
                    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
                      AND SESSION_CONTEXT(N'portal_party_ref') IS NOT NULL
                      AND @kind = 'ownership'
                      AND @party_ref = CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier);
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION [sec].[fn_portal_link_predicate]
                (
                    @tenant_id uniqueidentifier,
                    @parent_unit_id uniqueidentifier,
                    @installed_at datetime2(3),
                    @removed_at datetime2(3)
                )
                RETURNS TABLE WITH SCHEMABINDING AS
                RETURN
                    SELECT 1 AS ok
                    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
                      AND SESSION_CONTEXT(N'portal_party_ref') IS NULL
                    UNION ALL
                    SELECT 1 AS ok
                    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
                      AND SESSION_CONTEXT(N'portal_party_ref') IS NOT NULL
                      AND EXISTS
                      (
                          SELECT 1
                          FROM [serialisation].[custody_episode] AS episode
                          WHERE episode.[tenant_id] = @tenant_id
                            AND episode.[unit_id] = @parent_unit_id
                            AND episode.[kind] = 'ownership'
                            AND episode.[party_ref] = CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier)
                            AND (@removed_at IS NULL OR episode.[valid_from] < @removed_at)
                            AND (episode.[valid_to] IS NULL OR @installed_at < episode.[valid_to])
                      );
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION [sec].[fn_portal_unit_predicate]
                (
                    @tenant_id uniqueidentifier,
                    @unit_id uniqueidentifier
                )
                RETURNS TABLE WITH SCHEMABINDING AS
                RETURN
                    SELECT 1 AS ok
                    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
                      AND SESSION_CONTEXT(N'portal_party_ref') IS NULL
                    UNION ALL
                    SELECT 1 AS ok
                    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
                      AND SESSION_CONTEXT(N'portal_party_ref') IS NOT NULL
                      AND
                      (
                          EXISTS
                          (
                              SELECT 1
                              FROM [serialisation].[custody_episode] AS episode
                              WHERE episode.[tenant_id] = @tenant_id
                                AND episode.[unit_id] = @unit_id
                                AND episode.[kind] = 'ownership'
                                AND episode.[party_ref] = CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier)
                          )
                          OR EXISTS
                          (
                              SELECT 1
                              FROM [serialisation].[assembly_link] AS link
                              INNER JOIN [serialisation].[custody_episode] AS episode
                                  ON episode.[tenant_id] = link.[tenant_id]
                                 AND episode.[unit_id] = link.[parent_unit_id]
                              WHERE link.[tenant_id] = @tenant_id
                                AND link.[child_unit_id] = @unit_id
                                AND episode.[kind] = 'ownership'
                                AND episode.[party_ref] = CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier)
                                AND (episode.[valid_to] IS NULL OR link.[installed_at] < episode.[valid_to])
                                AND (link.[removed_at] IS NULL OR episode.[valid_from] < link.[removed_at])
                          )
                      );
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION [sec].[fn_portal_unit_interval_predicate]
                (
                    @tenant_id uniqueidentifier,
                    @unit_id uniqueidentifier,
                    @valid_from datetime2(3),
                    @valid_to datetime2(3)
                )
                RETURNS TABLE WITH SCHEMABINDING AS
                RETURN
                    SELECT 1 AS ok
                    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
                      AND SESSION_CONTEXT(N'portal_party_ref') IS NULL
                    UNION ALL
                    SELECT 1 AS ok
                    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
                      AND SESSION_CONTEXT(N'portal_party_ref') IS NOT NULL
                      AND
                      (
                          EXISTS
                          (
                              SELECT 1
                              FROM [serialisation].[custody_episode] AS episode
                              WHERE episode.[tenant_id] = @tenant_id
                                AND episode.[unit_id] = @unit_id
                                AND episode.[kind] = 'ownership'
                                AND episode.[party_ref] = CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier)
                                AND (@valid_to IS NULL OR episode.[valid_from] < @valid_to)
                                AND (episode.[valid_to] IS NULL OR @valid_from < episode.[valid_to])
                          )
                          OR EXISTS
                          (
                              SELECT 1
                              FROM [serialisation].[assembly_link] AS link
                              INNER JOIN [serialisation].[custody_episode] AS episode
                                  ON episode.[tenant_id] = link.[tenant_id]
                                 AND episode.[unit_id] = link.[parent_unit_id]
                              WHERE link.[tenant_id] = @tenant_id
                                AND link.[child_unit_id] = @unit_id
                                AND episode.[kind] = 'ownership'
                                AND episode.[party_ref] = CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier)
                                AND (@valid_to IS NULL OR episode.[valid_from] < @valid_to)
                                AND (episode.[valid_to] IS NULL OR @valid_from < episode.[valid_to])
                                AND (@valid_to IS NULL OR link.[installed_at] < @valid_to)
                                AND (link.[removed_at] IS NULL OR @valid_from < link.[removed_at])
                                AND (episode.[valid_to] IS NULL OR link.[installed_at] < episode.[valid_to])
                                AND (link.[removed_at] IS NULL OR episode.[valid_from] < link.[removed_at])
                          )
                      );
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION [sec].[fn_portal_unit_time_predicate]
                (
                    @tenant_id uniqueidentifier,
                    @unit_id uniqueidentifier,
                    @at datetime2(3)
                )
                RETURNS TABLE WITH SCHEMABINDING AS
                RETURN
                    SELECT 1 AS ok
                    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
                      AND SESSION_CONTEXT(N'portal_party_ref') IS NULL
                    UNION ALL
                    SELECT 1 AS ok
                    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
                      AND SESSION_CONTEXT(N'portal_party_ref') IS NOT NULL
                      AND
                      (
                          EXISTS
                          (
                              SELECT 1
                              FROM [serialisation].[custody_episode] AS episode
                              WHERE episode.[tenant_id] = @tenant_id
                                AND episode.[unit_id] = @unit_id
                                AND episode.[kind] = 'ownership'
                                AND episode.[party_ref] = CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier)
                                AND episode.[valid_from] <= @at
                                AND (episode.[valid_to] IS NULL OR @at < episode.[valid_to])
                          )
                          OR EXISTS
                          (
                              SELECT 1
                              FROM [serialisation].[assembly_link] AS link
                              INNER JOIN [serialisation].[custody_episode] AS episode
                                  ON episode.[tenant_id] = link.[tenant_id]
                                 AND episode.[unit_id] = link.[parent_unit_id]
                              WHERE link.[tenant_id] = @tenant_id
                                AND link.[child_unit_id] = @unit_id
                                AND link.[installed_at] <= @at
                                AND (link.[removed_at] IS NULL OR @at < link.[removed_at])
                                AND episode.[kind] = 'ownership'
                                AND episode.[party_ref] = CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier)
                                AND episode.[valid_from] <= @at
                                AND (episode.[valid_to] IS NULL OR @at < episode.[valid_to])
                          )
                      );
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION [sec].[fn_portal_current_predicate]
                (
                    @tenant_id uniqueidentifier,
                    @unit_id uniqueidentifier,
                    @owner_party_ref uniqueidentifier
                )
                RETURNS TABLE WITH SCHEMABINDING AS
                RETURN
                    SELECT 1 AS ok
                    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
                      AND SESSION_CONTEXT(N'portal_party_ref') IS NULL
                    UNION ALL
                    SELECT 1 AS ok
                    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
                      AND SESSION_CONTEXT(N'portal_party_ref') IS NOT NULL
                      AND
                      (
                          (
                              @owner_party_ref = CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier)
                              AND EXISTS
                              (
                                  SELECT 1
                                  FROM [serialisation].[custody_episode] AS episode
                                  WHERE episode.[tenant_id] = @tenant_id
                                    AND episode.[unit_id] = @unit_id
                                    AND episode.[kind] = 'ownership'
                                    AND episode.[party_ref] = CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier)
                                    AND episode.[valid_to] IS NULL
                              )
                          )
                          OR EXISTS
                          (
                              SELECT 1
                              FROM [serialisation].[assembly_link] AS link
                              INNER JOIN [serialisation].[custody_episode] AS episode
                                  ON episode.[tenant_id] = link.[tenant_id]
                                 AND episode.[unit_id] = link.[parent_unit_id]
                              WHERE link.[tenant_id] = @tenant_id
                                AND link.[child_unit_id] = @unit_id
                                AND link.[removed_at] IS NULL
                                AND episode.[kind] = 'ownership'
                                AND episode.[party_ref] = CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier)
                                AND episode.[valid_to] IS NULL
                          )
                      );
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION [sec].[fn_portal_entitlement_predicate]
                (
                    @tenant_id uniqueidentifier,
                    @unit_id uniqueidentifier,
                    @owner_party_ref uniqueidentifier
                )
                RETURNS TABLE WITH SCHEMABINDING AS
                RETURN
                    SELECT 1 AS ok
                    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
                      AND SESSION_CONTEXT(N'portal_party_ref') IS NULL
                    UNION ALL
                    SELECT 1 AS ok
                    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
                      AND SESSION_CONTEXT(N'portal_party_ref') IS NOT NULL
                      AND @owner_party_ref = CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier)
                      AND EXISTS
                      (
                          SELECT 1
                          FROM [serialisation].[custody_episode] AS episode
                          WHERE episode.[tenant_id] = @tenant_id
                            AND episode.[unit_id] = @unit_id
                            AND episode.[kind] = 'ownership'
                            AND episode.[party_ref] = CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier)
                      );
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION [sec].[fn_portal_certificate_predicate]
                (
                    @tenant_id uniqueidentifier,
                    @unit_id uniqueidentifier,
                    @document_kind nvarchar(30),
                    @issued_at datetime2(3)
                )
                RETURNS TABLE WITH SCHEMABINDING AS
                RETURN
                    SELECT 1 AS ok
                    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
                      AND SESSION_CONTEXT(N'portal_party_ref') IS NULL
                    UNION ALL
                    SELECT 1 AS ok
                    WHERE @tenant_id = CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier)
                      AND SESSION_CONTEXT(N'portal_party_ref') IS NOT NULL
                      AND
                      (
                          (
                              @document_kind = N'release_record'
                              AND EXISTS
                              (
                                  SELECT 1
                                  FROM [serialisation].[custody_episode] AS episode
                                  WHERE episode.[tenant_id] = @tenant_id
                                    AND episode.[unit_id] = @unit_id
                                    AND episode.[kind] = 'ownership'
                                    AND episode.[party_ref] = CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier)
                              )
                          )
                          OR EXISTS
                          (
                              SELECT 1
                              FROM [serialisation].[custody_episode] AS episode
                              WHERE episode.[tenant_id] = @tenant_id
                                AND episode.[unit_id] = @unit_id
                                AND episode.[kind] = 'ownership'
                                AND episode.[party_ref] = CAST(SESSION_CONTEXT(N'portal_party_ref') AS uniqueidentifier)
                                AND episode.[valid_from] <= @issued_at
                                AND (episode.[valid_to] IS NULL OR @issued_at < episode.[valid_to])
                          )
                      );
                """);

            migrationBuilder.Sql("""
                ALTER SECURITY POLICY [sec].[tenant_policy_serialisation] WITH (STATE = OFF);

                ALTER SECURITY POLICY [sec].[tenant_policy_serialisation]
                    DROP FILTER PREDICATE ON [serialisation].[unit],
                    DROP FILTER PREDICATE ON [serialisation].[unit_identifier],
                    DROP FILTER PREDICATE ON [serialisation].[assembly_link],
                    DROP FILTER PREDICATE ON [serialisation].[custody_episode],
                    DROP FILTER PREDICATE ON [serialisation].[unit_event],
                    DROP FILTER PREDICATE ON [serialisation].[unit_event_archive],
                    DROP FILTER PREDICATE ON [serialisation].[unit_current],
                    DROP FILTER PREDICATE ON [serialisation].[entitlement],
                    DROP FILTER PREDICATE ON [serialisation].[certificate_instance];
                """);

            migrationBuilder.Sql("""
                ALTER SECURITY POLICY [sec].[tenant_policy_serialisation]
                    ADD FILTER PREDICATE [sec].[fn_portal_unit_predicate]([tenant_id], [unit_id])
                        ON [serialisation].[unit],
                    ADD FILTER PREDICATE [sec].[fn_portal_unit_interval_predicate]([tenant_id], [unit_id], [valid_from], [valid_to])
                        ON [serialisation].[unit_identifier],
                    ADD FILTER PREDICATE [sec].[fn_portal_link_predicate]([tenant_id], [parent_unit_id], [installed_at], [removed_at])
                        ON [serialisation].[assembly_link],
                    ADD FILTER PREDICATE [sec].[fn_portal_episode_predicate]([tenant_id], [kind], [party_ref])
                        ON [serialisation].[custody_episode],
                    ADD FILTER PREDICATE [sec].[fn_portal_unit_time_predicate]([tenant_id], [unit_id], [event_time])
                        ON [serialisation].[unit_event],
                    ADD FILTER PREDICATE [sec].[fn_portal_unit_time_predicate]([tenant_id], [unit_id], [event_time])
                        ON [serialisation].[unit_event_archive],
                    ADD FILTER PREDICATE [sec].[fn_portal_current_predicate]([tenant_id], [unit_id], [owner_party_ref])
                        ON [serialisation].[unit_current],
                    ADD FILTER PREDICATE [sec].[fn_portal_entitlement_predicate]([tenant_id], [unit_id], [owner_party_ref])
                        ON [serialisation].[entitlement],
                    ADD FILTER PREDICATE [sec].[fn_portal_certificate_predicate]([tenant_id], [unit_id], [document_kind], [issued_at])
                        ON [serialisation].[certificate_instance];

                ALTER SECURITY POLICY [sec].[tenant_policy_serialisation] WITH (STATE = ON);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER SECURITY POLICY [sec].[tenant_policy_serialisation] WITH (STATE = OFF);

                ALTER SECURITY POLICY [sec].[tenant_policy_serialisation]
                    DROP FILTER PREDICATE ON [serialisation].[unit],
                    DROP FILTER PREDICATE ON [serialisation].[unit_identifier],
                    DROP FILTER PREDICATE ON [serialisation].[assembly_link],
                    DROP FILTER PREDICATE ON [serialisation].[custody_episode],
                    DROP FILTER PREDICATE ON [serialisation].[unit_event],
                    DROP FILTER PREDICATE ON [serialisation].[unit_event_archive],
                    DROP FILTER PREDICATE ON [serialisation].[unit_current],
                    DROP FILTER PREDICATE ON [serialisation].[entitlement],
                    DROP FILTER PREDICATE ON [serialisation].[certificate_instance];
                """);

            migrationBuilder.Sql("""
                ALTER SECURITY POLICY [sec].[tenant_policy_serialisation]
                    ADD FILTER PREDICATE [sec].[fn_tenant_predicate]([tenant_id]) ON [serialisation].[unit],
                    ADD FILTER PREDICATE [sec].[fn_tenant_predicate]([tenant_id]) ON [serialisation].[unit_identifier],
                    ADD FILTER PREDICATE [sec].[fn_tenant_predicate]([tenant_id]) ON [serialisation].[assembly_link],
                    ADD FILTER PREDICATE [sec].[fn_tenant_predicate]([tenant_id]) ON [serialisation].[custody_episode],
                    ADD FILTER PREDICATE [sec].[fn_tenant_predicate]([tenant_id]) ON [serialisation].[unit_event],
                    ADD FILTER PREDICATE [sec].[fn_tenant_predicate]([tenant_id]) ON [serialisation].[unit_event_archive],
                    ADD FILTER PREDICATE [sec].[fn_tenant_predicate]([tenant_id]) ON [serialisation].[unit_current],
                    ADD FILTER PREDICATE [sec].[fn_tenant_predicate]([tenant_id]) ON [serialisation].[entitlement],
                    ADD FILTER PREDICATE [sec].[fn_tenant_predicate]([tenant_id]) ON [serialisation].[certificate_instance];

                ALTER SECURITY POLICY [sec].[tenant_policy_serialisation] WITH (STATE = ON);

                DROP FUNCTION [sec].[fn_portal_certificate_predicate];
                DROP FUNCTION [sec].[fn_portal_entitlement_predicate];
                DROP FUNCTION [sec].[fn_portal_current_predicate];
                DROP FUNCTION [sec].[fn_portal_unit_time_predicate];
                DROP FUNCTION [sec].[fn_portal_unit_interval_predicate];
                DROP FUNCTION [sec].[fn_portal_unit_predicate];
                DROP FUNCTION [sec].[fn_portal_link_predicate];
                DROP FUNCTION [sec].[fn_portal_episode_predicate];
                """);
        }
    }
}
