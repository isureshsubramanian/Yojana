-- P0-T08 (ADR-0001 AI 54): Ledger tables (SQL Server 2022+, Azure SQL) and their operational constraints.
SET NOCOUNT ON;
USE yojana_verify;
GO
IF OBJECT_ID('dbo.certificate_ledger') IS NOT NULL DROP TABLE dbo.certificate_ledger;
GO
BEGIN TRY
  CREATE TABLE dbo.certificate_ledger (
    tenant_id uniqueidentifier NOT NULL, certificate_id uniqueidentifier NOT NULL, version int NOT NULL,
    payload_hash binary(32) NOT NULL, issued_at datetime2(3) NOT NULL,
    CONSTRAINT pk_certificate_ledger PRIMARY KEY CLUSTERED (tenant_id, certificate_id, version)
  ) WITH (LEDGER = ON (APPEND_ONLY = ON));
  SELECT 'create append-only ledger table' AS step, 'ok' AS outcome;
END TRY BEGIN CATCH SELECT 'create append-only ledger table' AS step, ERROR_MESSAGE() AS outcome; END CATCH;
GO
INSERT dbo.certificate_ledger VALUES (NEWID(), NEWID(), 1, HASHBYTES('SHA2_256', 'v1'), SYSUTCDATETIME());
BEGIN TRY UPDATE dbo.certificate_ledger SET version = 2; SELECT 'update on append-only ledger' AS step, 'unexpectedly succeeded' AS outcome; END TRY
BEGIN CATCH SELECT 'update on append-only ledger' AS step, ERROR_MESSAGE() AS outcome; END CATCH;
BEGIN TRY DELETE dbo.certificate_ledger; SELECT 'delete on append-only ledger' AS step, 'unexpectedly succeeded' AS outcome; END TRY
BEGIN CATCH SELECT 'delete on append-only ledger' AS step, ERROR_MESSAGE() AS outcome; END CATCH;
GO
-- Schema changes
BEGIN TRY ALTER TABLE dbo.certificate_ledger ADD note nvarchar(100) NULL; SELECT 'add column to ledger table' AS step, 'ok' AS outcome; END TRY
BEGIN CATCH SELECT 'add column to ledger table' AS step, ERROR_MESSAGE() AS outcome; END CATCH;
BEGIN TRY ALTER TABLE dbo.certificate_ledger DROP COLUMN note; SELECT 'drop column from ledger table' AS step, 'ok' AS outcome; END TRY
BEGIN CATCH SELECT 'drop column from ledger table' AS step, ERROR_MESSAGE() AS outcome; END CATCH;
BEGIN TRY ALTER TABLE dbo.certificate_ledger ALTER COLUMN issued_at datetime2(7) NOT NULL; SELECT 'alter column type on ledger table' AS step, 'ok' AS outcome; END TRY
BEGIN CATCH SELECT 'alter column type on ledger table' AS step, ERROR_MESSAGE() AS outcome; END CATCH;
GO
-- Digest generation (what a verifier would store externally)
BEGIN TRY EXEC sys.sp_generate_database_ledger_digest; SELECT 'generate database ledger digest' AS step, 'ok (see row above)' AS outcome; END TRY
BEGIN CATCH SELECT 'generate database ledger digest' AS step, ERROR_MESSAGE() AS outcome; END CATCH;
GO
-- Dropping: ledger tables are soft-dropped and kept as "dropped ledger tables".
DROP TABLE dbo.certificate_ledger;
SELECT 'dropped ledger tables kept' AS step, name, is_dropped_ledger_table FROM sys.tables WHERE ledger_type <> 0;
GO
