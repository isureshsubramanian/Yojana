-- P0-T08 (ADR-0001 AI 54): NEWSEQUENTIALID() behaviour; native JSON type and JSON index availability on this version.
SET NOCOUNT ON;
USE yojana_verify;
GO
IF OBJECT_ID('dbo.seq_test') IS NOT NULL DROP TABLE dbo.seq_test;
CREATE TABLE dbo.seq_test (id uniqueidentifier NOT NULL CONSTRAINT df_seq DEFAULT NEWSEQUENTIALID(), n int NOT NULL, CONSTRAINT pk_seq PRIMARY KEY CLUSTERED (id));
GO
;WITH n AS (SELECT TOP (20000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.seq_test (n) SELECT i FROM n;
-- Are rows in insertion order when read by clustered key?
SELECT 'newsequentialid ordering' AS step,
       SUM(CASE WHEN n <> rn THEN 1 ELSE 0 END) AS out_of_order_rows, COUNT(*) AS total
FROM (SELECT n, ROW_NUMBER() OVER (ORDER BY id) AS rn FROM dbo.seq_test) x;
SELECT 'newsequentialid fragmentation' AS step, avg_fragmentation_in_percent, page_count
FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('dbo.seq_test'), 1, NULL, 'LIMITED');
-- NEWSEQUENTIALID() can only be used as a column default, never in an expression:
BEGIN TRY DECLARE @g uniqueidentifier = NEWSEQUENTIALID(); SELECT 'newsequentialid in expression' AS step, 'unexpectedly allowed' AS outcome; END TRY
BEGIN CATCH SELECT 'newsequentialid in expression' AS step, ERROR_MESSAGE() AS outcome; END CATCH;
GO
SELECT 'native json type present' AS step, CASE WHEN EXISTS (SELECT 1 FROM sys.types WHERE name = 'json') THEN 'yes' ELSE 'no (nvarchar(max) + ISJSON() convention applies on this version)' END AS outcome;
SELECT 'ISJSON and OPENJSON available' AS step, ISJSON(N'["a","b"]') AS isjson, (SELECT COUNT(*) FROM OPENJSON(N'["a","b"]')) AS elements;
SELECT 'compatibility level' AS step, compatibility_level FROM sys.databases WHERE name = DB_NAME();
GO
