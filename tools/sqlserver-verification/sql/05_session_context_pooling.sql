-- P0-T08: sp_set_session_context with @read_only = 1 is per session and cleared by sp_reset_connection; second set in the same session must fail.
SET NOCOUNT ON;
EXEC sp_set_session_context @key = N'tenant_id', @value = '11111111-1111-1111-1111-111111111111', @read_only = 1;
SELECT 'session context set' AS step, CAST(SESSION_CONTEXT(N'tenant_id') AS nvarchar(50)) AS value;
BEGIN TRY
  EXEC sp_set_session_context @key = N'tenant_id', @value = '22222222-2222-2222-2222-222222222222', @read_only = 1;
  SELECT 'second set in same session' AS step, 'unexpectedly allowed' AS outcome;
END TRY BEGIN CATCH SELECT 'second set in same session' AS step, ERROR_MESSAGE() AS outcome; END CATCH;
GO
