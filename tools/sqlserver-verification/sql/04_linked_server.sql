-- Run with sqlcmd -v SA_PASSWORD="..." (the development sa password from .env); no password is stored here.
-- P0-T08 (ADR-0001 AI 54): linked-server implications for extraction (loopback linked server stands in for the source instance).
SET NOCOUNT ON;
USE yojana_verify;
GO
IF EXISTS (SELECT 1 FROM sys.servers WHERE name = 'SOURCE_LOOPBACK') EXEC sp_dropserver 'SOURCE_LOOPBACK', 'droplogins';
EXEC sp_addlinkedserver @server = 'SOURCE_LOOPBACK', @srvproduct = '', @provider = 'MSOLEDBSQL', @datasrc = 'localhost', @provstr = 'Encrypt=no;TrustServerCertificate=yes';
EXEC sp_addlinkedsrvlogin @rmtsrvname = 'SOURCE_LOOPBACK', @useself = 'false', @rmtuser = 'sa', @rmtpassword = '$(SA_PASSWORD)';
EXEC sp_serveroption 'SOURCE_LOOPBACK', 'rpc out', 'true';
GO
IF OBJECT_ID('dbo.legacy_serials') IS NOT NULL DROP TABLE dbo.legacy_serials;
CREATE TABLE dbo.legacy_serials (serial_key int NOT NULL PRIMARY KEY, item_code nvarchar(50) NOT NULL, serial nvarchar(100) NOT NULL, create_date datetime NOT NULL);
;WITH n AS (SELECT TOP (50000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.legacy_serials SELECT i, 'ITEM' + CAST(i % 100 AS nvarchar(10)), '2506-' + RIGHT('000000' + CAST(i AS nvarchar(10)), 6), DATEADD(SECOND, i, '2024-01-01') FROM n;
GO
-- Four-part name with a filter: does the remote side receive the predicate?
BEGIN TRY
  SET STATISTICS XML OFF;
  SELECT 'four-part query' AS step, COUNT(*) AS matched FROM SOURCE_LOOPBACK.yojana_verify.dbo.legacy_serials WHERE create_date >= '2024-01-01 13:00:00';
END TRY BEGIN CATCH SELECT 'four-part query' AS step, ERROR_MESSAGE() AS outcome; END CATCH;
BEGIN TRY
  SELECT 'openquery' AS step, COUNT(*) AS matched FROM OPENQUERY(SOURCE_LOOPBACK, 'SELECT serial_key FROM yojana_verify.dbo.legacy_serials WHERE create_date >= ''2024-01-01 13:00:00''');
END TRY BEGIN CATCH SELECT 'openquery' AS step, ERROR_MESSAGE() AS outcome; END CATCH;
GO
-- Distributed transaction across the linked server: expected to fail on Linux containers (no MSDTC), which is why extraction is read-only and never transactional across instances.
BEGIN TRY
  BEGIN DISTRIBUTED TRANSACTION;
  INSERT SOURCE_LOOPBACK.yojana_verify.dbo.legacy_serials VALUES (-1, 'X', 'X', GETDATE());
  ROLLBACK TRANSACTION;
  SELECT 'distributed transaction' AS step, 'succeeded (MSDTC available)' AS outcome;
END TRY BEGIN CATCH IF @@TRANCOUNT > 0 ROLLBACK; SELECT 'distributed transaction' AS step, ERROR_MESSAGE() AS outcome; END CATCH;
GO
-- Bulk pull into staging through the linked server (INSERT ... SELECT from four-part name) timing.
IF OBJECT_ID('dbo.stg_legacy_serials') IS NOT NULL DROP TABLE dbo.stg_legacy_serials;
SELECT TOP (0) * INTO dbo.stg_legacy_serials FROM dbo.legacy_serials;
DECLARE @t0 datetime2 = SYSUTCDATETIME();
INSERT dbo.stg_legacy_serials SELECT * FROM SOURCE_LOOPBACK.yojana_verify.dbo.legacy_serials;
SELECT 'staging pull of 50,000 rows over linked server' AS step, DATEDIFF(MILLISECOND, @t0, SYSUTCDATETIME()) AS ms, (SELECT COUNT(*) FROM dbo.stg_legacy_serials) AS rows;
GO
EXEC sp_dropserver 'SOURCE_LOOPBACK', 'droplogins';
GO
