SET NOCOUNT ON;
EXEC sys.sp_set_session_context @key = N'tenant_id', @value = 'b0000000-0000-4000-8000-000000000001', @read_only = 1;
SET STATISTICS IO ON;
SET STATISTICS TIME ON;

DECLARE @tenant uniqueidentifier = 'b0000000-0000-4000-8000-000000000001';
DECLARE @rma uniqueidentifier;
DECLARE @dispatchedAt datetime2;
DECLARE @rmaEventType nvarchar(60);
SELECT TOP (1) @rma = document_id, @dispatchedAt = event_time, @rmaEventType = event_type
FROM serialisation.unit_event
WHERE tenant_id = @tenant AND document_type = N'rma'
ORDER BY event_time;

SELECT N'RELEVANT_INDEXES' AS marker, index_name = index_definition.name, column_name = columns.name,
       index_columns.key_ordinal, index_columns.is_included_column
FROM sys.indexes AS index_definition
JOIN sys.index_columns AS index_columns ON index_columns.object_id = index_definition.object_id AND index_columns.index_id = index_definition.index_id
JOIN sys.columns AS columns ON columns.object_id = index_columns.object_id AND columns.column_id = index_columns.column_id
WHERE index_definition.object_id IN (OBJECT_ID(N'serialisation.unit_identifier'), OBJECT_ID(N'serialisation.unit_event'))
  AND (index_definition.name LIKE N'%normalised%' OR index_definition.name LIKE N'%document%')
ORDER BY index_definition.name, index_columns.key_ordinal, index_columns.index_column_id;

SELECT N'RMA_DETAIL_BEFORE' AS marker;
SELECT TOP (1) event_time, actor_ref, attributes
FROM serialisation.unit_event
WHERE tenant_id = @tenant AND event_type = @rmaEventType AND document_id = @rma
ORDER BY seq;

SELECT N'RMA_DETAIL_AFTER' AS marker;
SELECT TOP (1) event_time, actor_ref, attributes
FROM serialisation.unit_event
WHERE tenant_id = @tenant AND document_type = N'rma' AND document_id = @rma
  AND event_time = @dispatchedAt AND event_type = @rmaEventType;

SELECT N'UNIT_SEARCH_COUNT_BEFORE' AS marker;
SELECT COUNT(*)
FROM serialisation.unit_identifier AS i
JOIN serialisation.unit_current AS c ON c.tenant_id = i.tenant_id AND c.unit_id = i.unit_id
WHERE i.tenant_id = @tenant;

SELECT N'UNIT_SEARCH_SUMMARY_BEFORE' AS marker;
SELECT COUNT(DISTINCT i.unit_id), COUNT(DISTINCT c.item_id), COUNT(DISTINCT i.type),
       COUNT(CASE WHEN i.valid_to IS NOT NULL THEN 1 END)
FROM serialisation.unit_identifier AS i
JOIN serialisation.unit_current AS c ON c.tenant_id = i.tenant_id AND c.unit_id = i.unit_id
WHERE i.tenant_id = @tenant;

SELECT N'UNIT_SEARCH_PAGE_BEFORE' AS marker;
SELECT i.unit_id, i.normalised_value, i.type, c.item_id, c.last_event_time
FROM serialisation.unit_identifier AS i
JOIN serialisation.unit_current AS c ON c.tenant_id = i.tenant_id AND c.unit_id = i.unit_id
WHERE i.tenant_id = @tenant
ORDER BY i.normalised_value, i.type, i.unit_id OFFSET 0 ROWS FETCH NEXT 25 ROWS ONLY;

SELECT N'UNIT_SEARCH_BOUNDED_AFTER' AS marker;
SELECT i.unit_id, i.normalised_value, i.type, c.item_id, c.last_event_time
FROM serialisation.unit_identifier AS i
JOIN serialisation.unit_current AS c ON c.tenant_id = i.tenant_id AND c.unit_id = i.unit_id
WHERE i.tenant_id = @tenant
ORDER BY i.normalised_value, i.type, i.unit_id OFFSET 0 ROWS FETCH NEXT 26 ROWS ONLY;

DECLARE @unitIds nvarchar(max) = (
    SELECT TOP (25) c.unit_id AS [value]
    FROM serialisation.unit_current AS c WHERE c.tenant_id = @tenant ORDER BY c.unit_id
    FOR JSON PATH);

SELECT N'LATEST_EVENT_BEFORE' AS marker;
SELECT e.unit_id,
       (SELECT TOP (1) x.event_type FROM serialisation.unit_event AS x
        WHERE x.tenant_id = @tenant
          AND x.unit_id IN (SELECT value FROM OPENJSON(@unitIds) WITH (value uniqueidentifier '$.value'))
          AND x.unit_id = e.unit_id ORDER BY x.seq DESC) AS event_type
FROM serialisation.unit_event AS e
WHERE e.tenant_id = @tenant
  AND e.unit_id IN (SELECT value FROM OPENJSON(@unitIds) WITH (value uniqueidentifier '$.value'))
GROUP BY e.unit_id;

SELECT N'LATEST_EVENT_AFTER' AS marker;
SELECT c.unit_id, e.event_type
FROM serialisation.unit_current AS c
JOIN serialisation.unit_event AS e
  ON e.tenant_id = c.tenant_id AND e.unit_id = c.unit_id AND e.event_time = c.last_event_time AND e.seq = c.last_event_seq
WHERE c.tenant_id = @tenant
  AND c.unit_id IN (SELECT value FROM OPENJSON(@unitIds) WITH (value uniqueidentifier '$.value'));

DECLARE @keys nvarchar(max) = (
    SELECT TOP (25) e.idempotency_key AS [value]
    FROM serialisation.unit_event AS e WHERE e.tenant_id = @tenant ORDER BY e.event_time DESC
    FOR JSON PATH);
DECLARE @times nvarchar(max) = (
    SELECT TOP (25) CONVERT(varchar(30), e.event_time, 126) AS [value]
    FROM serialisation.unit_event AS e WHERE e.tenant_id = @tenant ORDER BY e.event_time DESC
    FOR JSON PATH);
DECLARE @pairs nvarchar(max) = (
    SELECT TOP (25) e.idempotency_key AS [key], e.event_time AS [at]
    FROM serialisation.unit_event AS e WHERE e.tenant_id = @tenant ORDER BY e.event_time DESC
    FOR JSON PATH);

SELECT N'IDEMPOTENCY_TIME_FIRST_BEFORE' AS marker;
SELECT e.idempotency_key, e.event_time
FROM serialisation.unit_event AS e
WHERE e.tenant_id = @tenant
  AND e.event_time IN (SELECT value FROM OPENJSON(@times) WITH (value datetime2(3) '$.value'))
  AND e.idempotency_key IN (SELECT value FROM OPENJSON(@keys) WITH (value varchar(64) '$.value'));

SELECT N'IDEMPOTENCY_EXACT_PAIRS_AFTER' AS marker;
SELECT e.idempotency_key, e.event_time
FROM OPENJSON(@pairs) WITH ([key] varchar(64) '$.key', [at] datetime2(3) '$.at') AS requested
INNER LOOP JOIN serialisation.unit_event AS e
  WITH (FORCESEEK, INDEX(ux_unit_event_tenant_id_idempotency_key_event_time))
  ON e.tenant_id = @tenant AND e.idempotency_key = requested.[key] AND e.event_time = requested.[at];

SELECT N'RMA_LIST_CORRELATED_BEFORE' AS marker;
SELECT r.rma_id,
       (SELECT COUNT(*) FROM serialisation.rma_line AS line WHERE line.tenant_id = @tenant AND line.rma_id = r.rma_id) AS line_count,
       (SELECT COUNT(*) FROM serialisation.rma_line AS line WHERE line.tenant_id = @tenant AND line.rma_id = r.rma_id
          AND (line.provisional_unit_id IS NOT NULL OR line.receipt_outcome IS NOT NULL AND line.receipt_outcome <> N'accepted')) AS exception_count
FROM serialisation.rma AS r
WHERE r.tenant_id = @tenant
ORDER BY CASE WHEN EXISTS (SELECT 1 FROM serialisation.rma_line AS line WHERE line.tenant_id = @tenant AND line.rma_id = r.rma_id
          AND (line.provisional_unit_id IS NOT NULL OR line.receipt_outcome IS NOT NULL AND line.receipt_outcome <> N'accepted')) THEN 1 ELSE 0 END DESC,
         r.requested_at, r.rma_id
OFFSET 0 ROWS FETCH NEXT 25 ROWS ONLY;

SELECT N'RMA_LIST_BATCHED_AFTER' AS marker;
SELECT DISTINCT line.rma_id INTO #exception_rmas
FROM serialisation.rma_line AS line
WHERE line.tenant_id = @tenant AND (line.provisional_unit_id IS NOT NULL
    OR line.receipt_outcome IS NOT NULL AND line.receipt_outcome <> N'accepted');
SELECT r.rma_id
FROM serialisation.rma AS r LEFT JOIN #exception_rmas AS x ON x.rma_id = r.rma_id
WHERE r.tenant_id = @tenant
ORDER BY CASE WHEN x.rma_id IS NULL THEN 0 ELSE 1 END DESC, r.requested_at, r.rma_id
OFFSET 0 ROWS FETCH NEXT 25 ROWS ONLY;

SELECT N'CLAIM_LIST_CORRELATED_BEFORE' AS marker;
SELECT c.claim_id,
       (SELECT SUM(line.requested_amount) FROM serialisation.claim_line AS line WHERE line.tenant_id = @tenant AND line.claim_id = c.claim_id),
       (SELECT SUM(line.approved_amount) FROM serialisation.claim_line AS line WHERE line.tenant_id = @tenant AND line.claim_id = c.claim_id),
       (SELECT COUNT(*) FROM serialisation.claim_line AS line WHERE line.tenant_id = @tenant AND line.claim_id = c.claim_id),
       (SELECT TOP (1) evidence.kind FROM serialisation.claim_evidence AS evidence WHERE evidence.tenant_id = @tenant AND evidence.claim_id = c.claim_id ORDER BY evidence.recorded_at DESC)
FROM serialisation.claim AS c WHERE c.tenant_id = @tenant
ORDER BY CASE WHEN c.status IN (N'settled', N'closed') THEN 1 ELSE 0 END, c.submitted_at, c.claim_id
OFFSET 0 ROWS FETCH NEXT 25 ROWS ONLY;

SELECT N'CLAIM_LIST_BATCHED_AFTER' AS marker;
SELECT c.claim_id INTO #claim_page
FROM serialisation.claim AS c WHERE c.tenant_id = @tenant
ORDER BY c.list_order_bucket, c.submitted_at, c.claim_id
OFFSET 0 ROWS FETCH NEXT 25 ROWS ONLY;
SELECT line.claim_id, SUM(line.requested_amount), SUM(line.approved_amount), COUNT(*)
FROM serialisation.claim_line AS line JOIN #claim_page AS page ON page.claim_id = line.claim_id
WHERE line.tenant_id = @tenant GROUP BY line.claim_id;
SELECT evidence.claim_id, evidence.kind
FROM serialisation.claim_evidence AS evidence JOIN #claim_page AS page ON page.claim_id = evidence.claim_id
WHERE evidence.tenant_id = @tenant ORDER BY evidence.claim_id, evidence.recorded_at DESC;
