-- P0-T08 (ADR-0001 AI 54): SWITCH PARTITION archival and MERGE RANGE semantics.
SET NOCOUNT ON;
IF DB_ID('yojana_verify') IS NULL CREATE DATABASE yojana_verify;
GO
USE yojana_verify;
GO
IF OBJECT_ID('dbo.unit_event_archive') IS NOT NULL DROP TABLE dbo.unit_event_archive;
IF OBJECT_ID('dbo.unit_event') IS NOT NULL DROP TABLE dbo.unit_event;
IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = 'ps_event_month') DROP PARTITION SCHEME ps_event_month;
IF EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = 'pf_event_month') DROP PARTITION FUNCTION pf_event_month;
GO
CREATE PARTITION FUNCTION pf_event_month (datetime2(3)) AS RANGE RIGHT FOR VALUES ('2026-01-01','2026-02-01','2026-03-01','2026-04-01');
CREATE PARTITION SCHEME ps_event_month AS PARTITION pf_event_month ALL TO ([PRIMARY]);
CREATE TABLE dbo.unit_event (
  tenant_id uniqueidentifier NOT NULL, event_time datetime2(3) NOT NULL, event_id uniqueidentifier NOT NULL,
  unit_id uniqueidentifier NOT NULL, idempotency_key varchar(64) NOT NULL,
  CONSTRAINT pk_unit_event PRIMARY KEY CLUSTERED (tenant_id, event_time, event_id)
) ON ps_event_month (event_time);
CREATE UNIQUE NONCLUSTERED INDEX ux_event_idem ON dbo.unit_event (tenant_id, idempotency_key, event_time) ON ps_event_month (event_time);
CREATE NONCLUSTERED INDEX ix_event_unit ON dbo.unit_event (tenant_id, unit_id, event_time DESC) ON ps_event_month (event_time);
CREATE TABLE dbo.unit_event_archive (
  tenant_id uniqueidentifier NOT NULL, event_time datetime2(3) NOT NULL, event_id uniqueidentifier NOT NULL,
  unit_id uniqueidentifier NOT NULL, idempotency_key varchar(64) NOT NULL,
  CONSTRAINT pk_unit_event_archive PRIMARY KEY CLUSTERED (tenant_id, event_time, event_id)
) ON ps_event_month (event_time);
CREATE UNIQUE NONCLUSTERED INDEX ux_event_idem_a ON dbo.unit_event_archive (tenant_id, idempotency_key, event_time) ON ps_event_month (event_time);
CREATE NONCLUSTERED INDEX ix_event_unit_a ON dbo.unit_event_archive (tenant_id, unit_id, event_time DESC) ON ps_event_month (event_time);
GO
DECLARE @t uniqueidentifier = NEWID();
;WITH n AS (SELECT TOP (40000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.unit_event (tenant_id, event_time, event_id, unit_id, idempotency_key)
SELECT @t, DATEADD(MINUTE, i * 4, '2026-01-01'), NEWID(), NEWID(), CONVERT(varchar(64), i) FROM n;
SELECT 'rows per partition before' AS step, p.partition_number, p.rows FROM sys.partitions p WHERE p.object_id = OBJECT_ID('dbo.unit_event') AND p.index_id = 1 ORDER BY p.partition_number;
GO
-- 1. SWITCH the January partition (number 2, RANGE RIGHT: partition 1 is everything before 2026-01-01) into the archive.
ALTER TABLE dbo.unit_event SWITCH PARTITION 2 TO dbo.unit_event_archive PARTITION 2;
SELECT 'after switch: live' AS step, p.partition_number, p.rows FROM sys.partitions p WHERE p.object_id = OBJECT_ID('dbo.unit_event') AND p.index_id = 1 ORDER BY p.partition_number;
SELECT 'after switch: archive' AS step, p.partition_number, p.rows FROM sys.partitions p WHERE p.object_id = OBJECT_ID('dbo.unit_event_archive') AND p.index_id = 1 ORDER BY p.partition_number;
GO
-- 2. SWITCH into a non-empty target partition must fail (archive partition 2 now holds rows).
BEGIN TRY
  ALTER TABLE dbo.unit_event SWITCH PARTITION 2 TO dbo.unit_event_archive PARTITION 2;
  SELECT 'switch into non-empty target' AS step, 'unexpectedly succeeded' AS outcome;
END TRY BEGIN CATCH SELECT 'switch into non-empty target' AS step, ERROR_MESSAGE() AS outcome; END CATCH;
GO
-- 3. MERGE RANGE removes the emptied boundary; the partition scheme is shared by both tables so both lose the partition.
ALTER PARTITION FUNCTION pf_event_month() MERGE RANGE ('2026-01-01');
SELECT 'after merge: live' AS step, p.partition_number, p.rows FROM sys.partitions p WHERE p.object_id = OBJECT_ID('dbo.unit_event') AND p.index_id = 1 ORDER BY p.partition_number;
SELECT 'after merge: archive' AS step, p.partition_number, p.rows FROM sys.partitions p WHERE p.object_id = OBJECT_ID('dbo.unit_event_archive') AND p.index_id = 1 ORDER BY p.partition_number;
GO
-- 4. A non-aligned index on the source blocks SWITCH.
CREATE NONCLUSTERED INDEX ix_not_aligned ON dbo.unit_event (unit_id) ON [PRIMARY];
BEGIN TRY
  ALTER TABLE dbo.unit_event SWITCH PARTITION 2 TO dbo.unit_event_archive PARTITION 2;
  SELECT 'switch with non-aligned index' AS step, 'unexpectedly succeeded' AS outcome;
END TRY BEGIN CATCH SELECT 'switch with non-aligned index' AS step, ERROR_MESSAGE() AS outcome; END CATCH;
DROP INDEX ix_not_aligned ON dbo.unit_event;
GO
-- 5. SPLIT RANGE ahead of time (the Quartz job pre-creates boundaries) on an empty rightmost partition is a metadata operation.
ALTER PARTITION SCHEME ps_event_month NEXT USED [PRIMARY];
ALTER PARTITION FUNCTION pf_event_month() SPLIT RANGE ('2026-05-01');
SELECT 'after split' AS step, COUNT(*) AS boundary_count FROM sys.partition_range_values r JOIN sys.partition_functions f ON f.function_id = r.function_id WHERE f.name = 'pf_event_month';
SELECT 'edition' AS step, CAST(SERVERPROPERTY('Edition') AS nvarchar(100)) AS edition, CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)) AS version;
GO
