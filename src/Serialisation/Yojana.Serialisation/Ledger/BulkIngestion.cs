using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Wolverine;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Projections;
using Yojana.Serialisation.Search;

namespace Yojana.Serialisation.Ledger;

// EF1002: every interpolated value below is a compile-time object name; data travels as bulk-copied rows and
// parameters, never as SQL text.
#pragma warning disable EF1002

/// <summary>
/// The set-based path for bulk receipts and shipments (ADR-0001 "Set-based ingestion", verified P0-T08): rows go
/// into session temp tables with <see cref="SqlBulkCopy"/>, identifiers are validated as one set against the open
/// identifiers, events enter the ledger with one <c>INSERT … SELECT … WHERE NOT EXISTS</c> on the idempotency key,
/// every dependent row (unit, identifier, item assignment, episode) follows its writing event, and <c>unit_current</c>
/// is merged once with the seq guard of coupling 2. One transaction: the caller's when one is open, otherwise its own.
/// Row-level security applies throughout: the session carries the tenant, temp tables carry no policy.
/// </summary>
public sealed class BulkIngestion(SerialisationDbContext db, ITenantContext tenant, IClock clock, IMessageBus bus,
    IEnumerable<IUnitEventAppendObserver> observers)
{
    private const string S = SerialisationDbContext.SchemaName;

    /// <summary>What a bulk command hands over. Every dependent row names the event that writes it.</summary>
    public sealed record Batch(
        IReadOnlyList<UnitEventDraft> Events,
        IReadOnlyList<Unit> Units,
        IReadOnlyList<UnitIdentifier> Identifiers,
        IReadOnlyList<UnitItemAssignment> ItemAssignments,
        IReadOnlyList<CustodyEpisode> Episodes);

    public sealed record Result(int EventsInserted, int EventsDuplicate, int UnitsInserted, int IdentifiersInserted, int EpisodesInserted, int CurrentInserted, int CurrentUpdated, int CurrentOutOfOrder, long? FirstSeq, long? LastSeq);

    /// <summary>One identifier that cannot be commissioned, with the rule that stops it.</summary>
    public sealed record IdentifierConflict(string Scheme, string ScopeKey, string NormalisedValue, string Reason, Guid? ExistingUnitId);

    /// <summary>
    /// Identity rule 1 as one set query: identifiers already open on another unit in the same (scheme, scope), and
    /// values that repeat inside the batch. The caller decides what to do with the conflicts (skip, route to the workbench).
    /// </summary>
    public async Task<IReadOnlyList<IdentifierConflict>> ValidateIdentifiersAsync(IReadOnlyList<UnitIdentifier> identifiers, CancellationToken cancellationToken)
    {
        if (identifiers.Count == 0)
        {
            return [];
        }

        var conflicts = new List<IdentifierConflict>();
        foreach (var group in identifiers.GroupBy(i => (i.Scheme, i.ScopeKey, i.NormalisedValue)).Where(g => g.Count() > 1))
        {
            conflicts.Add(new IdentifierConflict(group.Key.Scheme, group.Key.ScopeKey, group.Key.NormalisedValue, "duplicate_in_batch", null));
        }

        await using var session = await SessionAsync(cancellationToken);
        await session.ExecuteAsync("CREATE TABLE #ids (scheme nvarchar(20) NOT NULL, scope_key nvarchar(100) NOT NULL, normalised_value nvarchar(100) NOT NULL);", cancellationToken);
        var table = new DataTable();
        table.Columns.Add("scheme", typeof(string)); table.Columns.Add("scope_key", typeof(string)); table.Columns.Add("normalised_value", typeof(string));
        foreach (var i in identifiers.DistinctBy(i => (i.Scheme, i.ScopeKey, i.NormalisedValue)))
        {
            table.Rows.Add(i.Scheme, i.ScopeKey, i.NormalisedValue);
        }

        await session.BulkCopyAsync("#ids", table, cancellationToken);
        await using var command = session.Command($"""
            SELECT x.scheme, x.scope_key, x.normalised_value, u.unit_id
            FROM #ids x JOIN [{S}].[unit_identifier] u
              ON u.scheme = x.scheme AND u.scope_key = x.scope_key AND u.normalised_value = x.normalised_value AND u.valid_to IS NULL
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            conflicts.Add(new IdentifierConflict(reader.GetString(0), reader.GetString(1), reader.GetString(2), "already_open_on_another_unit", reader.GetGuid(3)));
        }

        return conflicts;
    }

    public async Task<Result> IngestAsync(Batch batch, CancellationToken cancellationToken)
    {
        if (batch.Events.Count == 0)
        {
            return new Result(0, 0, 0, 0, 0, 0, 0, 0, null, null);
        }

        var tenantId = tenant.TenantId;
        var recordedTime = clock.UtcNow;
        await using var session = await SessionAsync(cancellationToken);

        // 1. Sequence numbers for the whole batch, in (unit, business time, caller order).
        var ordered = batch.Events.Select((d, i) => (Draft: d, Index: i)).OrderBy(x => x.Draft.UnitId).ThenBy(x => x.Draft.EventTime).ThenBy(x => x.Index).ToList();
        var firstSeq = await session.NextRangeAsync(ordered.Count, cancellationToken);
        var events = new List<(UnitEventDraft Draft, UnitEvent Row)>(ordered.Count);
        var seq = firstSeq;
        foreach (var (draft, _) in ordered)
        {
            events.Add((draft, new UnitEvent
            {
                TenantId = tenantId, EventTime = draft.EventTime, EventId = draft.PresetEventId ?? TimeOrderedId.New(draft.EventTime), UnitId = draft.UnitId, Seq = seq++,
                EventType = draft.EventType, BizStep = draft.BizStep, Disposition = draft.Disposition, LocationId = draft.LocationId, LegalEntityId = draft.LegalEntityId,
                ActorRef = draft.ActorRef, DocumentType = draft.DocumentType, DocumentId = draft.DocumentId, DocumentLine = draft.DocumentLine, RecordedTime = recordedTime,
                PostingPeriod = draft.PostingPeriod, IdempotencyKey = draft.IdempotencyKey, ReversesEventId = draft.ReversesEventId, Attributes = UnitEventWriter.AttributesJson(draft),
            }));
        }

        // 2. Stage everything.
        await session.ExecuteAsync(StagingDdl, cancellationToken);
        await session.BulkCopyAsync("#ev", EventTable(events.Select(e => e.Row)), cancellationToken);
        await session.BulkCopyAsync("#unit", UnitTable(batch.Units), cancellationToken);
        await session.BulkCopyAsync("#ident", IdentifierTable(batch.Identifiers), cancellationToken);
        await session.BulkCopyAsync("#assign", AssignmentTable(batch.ItemAssignments), cancellationToken);
        await session.BulkCopyAsync("#ep", EpisodeTable(batch.Episodes), cancellationToken);

        // 3. Events: one guarded insert; the keys of the inserted rows are captured so dependents can follow them.
        await session.ExecuteAsync($"""
            INSERT INTO [{S}].[unit_event] ({EventColumns})
            OUTPUT inserted.event_id INTO #inserted (event_id)
            SELECT {EventColumns} FROM #ev e
            WHERE NOT EXISTS (SELECT 1 FROM [{S}].[unit_event] x WHERE x.tenant_id = e.tenant_id AND x.idempotency_key = e.idempotency_key AND x.event_time = e.event_time);
            """, cancellationToken);
        var eventsInserted = await session.CountAsync("SELECT COUNT(*) FROM #inserted", cancellationToken);
        var eventsDuplicate = events.Count - eventsInserted;

        // HIDDEN COUPLING (base-order ledger projection): these detached event rows cannot use
        // LedgerBaseOrderReferenceObserver, whose EF rows commit with UnitEventWriter's caller-owned SaveChanges.
        // Project the identical base_order contract here from #ev, in this transaction and only for inserted facts;
        // LedgerBaseOrderBackfill carries the same field paths for facts from before this sparse projection existed.
        // SelectionSetResolver depends on this sparse row; omitting it makes bulk-posted shipments unselectable by
        // their originating order. Keep this statement and the observer/reader coupling comments in step.
        await session.ExecuteAsync($"""
            INSERT INTO [{S}].[ledger_base_order_ref]
                (tenant_id, base_document_type, base_document_id, base_document_line, unit_id, event_time, event_id)
            SELECT e.tenant_id,
                   CONVERT(nvarchar(40), JSON_VALUE(e.attributes, '$.base_order.type')),
                   TRY_CONVERT(uniqueidentifier, JSON_VALUE(e.attributes, '$.base_order.id')),
                   TRY_CONVERT(int, JSON_VALUE(e.attributes, '$.base_order.line')),
                   e.unit_id, e.event_time, e.event_id
            FROM #ev AS e
            INNER JOIN #inserted AS i ON i.event_id = e.event_id
            WHERE NULLIF(JSON_VALUE(e.attributes, '$.base_order.type'), N'') IS NOT NULL
              AND TRY_CONVERT(uniqueidentifier, JSON_VALUE(e.attributes, '$.base_order.id')) IS NOT NULL
              AND NOT EXISTS
                  (SELECT 1 FROM [{S}].[ledger_base_order_ref] AS existing
                   WHERE existing.tenant_id = e.tenant_id AND existing.event_time = e.event_time
                     AND existing.event_id = e.event_id);
            """, cancellationToken);

        // 4. Dependents follow their writing event (a duplicate event brings none of its rows).
        var unitsInserted = await session.ExecuteAsync($"""
            INSERT INTO [{S}].[unit] ({UnitColumns})
            SELECT {UnitColumns} FROM #unit u WHERE EXISTS (SELECT 1 FROM #inserted i WHERE i.event_id = u.created_event_id)
              AND NOT EXISTS (SELECT 1 FROM [{S}].[unit] x WHERE x.tenant_id = u.tenant_id AND x.unit_id = u.unit_id);
            """, cancellationToken);
        var identifiersInserted = await session.ExecuteAsync($"""
            INSERT INTO [{S}].[unit_identifier] ({IdentifierColumns})
            SELECT {IdentifierColumns} FROM #ident d WHERE EXISTS (SELECT 1 FROM #inserted i WHERE i.event_id = d.event_id);
            """, cancellationToken);
        await session.ExecuteAsync($"""
            INSERT INTO [{S}].[unit_item_assignment] ({AssignmentColumns})
            SELECT {AssignmentColumns} FROM #assign a WHERE EXISTS (SELECT 1 FROM #inserted i WHERE i.event_id = a.event_id);
            """, cancellationToken);
        // HIDDEN COUPLING with EpisodeService.PreloadOpenAsync (P1-T24): the document handlers keep an in-memory index of
        // the open episodes of the units they preloaded. Rows written here bypass that index, so this path must only open
        // episodes for units it creates (true today: commissioning), never for a unit a handler in the same scope preloaded.
        var episodesInserted = await session.ExecuteAsync($"""
            INSERT INTO [{S}].[custody_episode] ({EpisodeColumns})
            SELECT {EpisodeColumns} FROM #ep p WHERE EXISTS (SELECT 1 FROM #inserted i WHERE i.event_id = p.opening_event_id);
            """, cancellationToken);

        // 5. Current state: fold the inserted events' deltas per unit on top of the existing row, then one MERGE with
        //    the seq guard (coupling 2: an older seq never overwrites a newer row; it is counted, not applied).
        var insertedIds = await session.GuidsAsync("SELECT event_id FROM #inserted", cancellationToken);
        var byUnit = events.Where(e => insertedIds.Contains(e.Row.EventId)).GroupBy(e => e.Row.UnitId).ToList();
        var unitIds = byUnit.Select(g => g.Key).ToList();
        var existing = await db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && unitIds.Contains(c.UnitId)).ToDictionaryAsync(c => c.UnitId, cancellationToken);
        var currentRows = new List<UnitCurrent>();
        foreach (var group in byUnit)
        {
            var row = existing.TryGetValue(group.Key, out var found) ? Clone(found) : null;
            foreach (var (draft, @event) in group.OrderBy(e => e.Row.Seq))
            {
                if (draft.Delta?.Create is { } create)
                {
                    if (row is not null) throw new ProjectionStateException($"Unit {group.Key}: a creating event for a unit that already has current state.");
                    row = new UnitCurrent { TenantId = tenantId, UnitId = group.Key, ItemId = create.ItemId, ModelVersionId = create.ModelVersionId, LotId = create.LotId, LifecycleStatus = create.LifecycleStatus, Disposition = create.Disposition };
                }

                if (row is null) throw new ProjectionStateException($"Unit {group.Key}: event {@event.EventType} changes current state but the unit has no row; the first event must carry Create.");
                if (draft.Delta is not null) UnitCurrentStateProjector.Fold(row, draft.Delta);
                row.LastEventSeq = @event.Seq;
                row.LastEventTime = @event.EventTime;
            }

            currentRows.Add(row!);
        }

        await session.BulkCopyAsync("#cur", CurrentTable(currentRows), cancellationToken);
        var mergeCounts = await session.MergeCurrentAsync(cancellationToken);
        // The ledger was written directly, so no published event will re-index these units: ask the search index to (outbox, after commit).
        var documents = batch.Events.Where(e => e.DocumentType != null && e.DocumentId != null).Select(e => new DocumentRef(e.DocumentType!, e.DocumentId!.Value)).Distinct().ToList();
        foreach (var chunk in currentRows.Select(c => c.UnitId).Distinct().Chunk(2000))
        {
            await bus.PublishAsync(new SearchReindexRequested(chunk), new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        }

        if (documents.Count > 0)
        {
            // The documents go in their own message: every unit chunk would otherwise rebuild the same document entry and wait on its lock.
            await bus.PublishAsync(new SearchReindexRequested([], documents), new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        }

        // Direct bulk ingestion bypasses UnitEventWriter, so it completes the same narrow append-observer contract.
        // FlushTrackedAsync does not repeat it: those facts already crossed UnitEventWriter's logical append boundary.
        var written = events.Where(e => insertedIds.Contains(e.Row.EventId)).Select(e => e.Row).ToList();
        foreach (var observer in observers)
        {
            await observer.AppendedAsync(written, cancellationToken);
        }

        await session.CommitAsync(cancellationToken);
        return new Result(eventsInserted, eventsDuplicate, unitsInserted, identifiersInserted, episodesInserted, mergeCounts.Inserted, mergeCounts.Updated, currentRows.Count - mergeCounts.Inserted - mergeCounts.Updated,
            eventsInserted == 0 ? null : firstSeq, eventsInserted == 0 ? null : seq - 1);
    }

    /// <summary>What <see cref="FlushTrackedAsync"/> wrote; zero everywhere when it left the unit of work to SaveChanges.</summary>
    public sealed record FlushResult(int Events, int EpisodesOpened, int EpisodesClosed, int CurrentUpdated);

    private static readonly HashSet<string> ClosingProperties =
        [nameof(CustodyEpisode.ValidTo), nameof(CustodyEpisode.ClosingEventId), nameof(CustodyEpisode.ClosingDocumentType), nameof(CustodyEpisode.ClosingDocumentId), nameof(CustodyEpisode.ClosingDocumentLine)];

    /// <summary>
    /// The bulk write path for documents (P1-T26). A document handler prepares its moves through the per-unit rules
    /// (<see cref="Commands.EpisodeService"/>, <see cref="IUnitEventWriter"/>), which leave new events, new episodes,
    /// episode closures and <c>unit_current</c> changes tracked by the DbContext. EF Core would send those as batches of
    /// 42 rows each, and on a large ledger each batch costs 100–230 ms of server time. This writes the same rows from
    /// staging in one statement per table, with the same guards: the closures only close rows still open, the current
    /// rows only update where the <c>rowversion</c> is the one read (a race fails with
    /// <see cref="DbUpdateConcurrencyException"/>, as SaveChanges would). The written entries are then marked unchanged,
    /// so SaveChanges sends only what is left (outbox messages, pending lines); later changes to them are tracked as usual.
    /// Must run inside the document's transaction, after its last append.
    /// </summary>
    public async Task<FlushResult> FlushTrackedAsync(CancellationToken cancellationToken)
    {
        var tracker = db.ChangeTracker;
        var events = tracker.Entries<UnitEvent>().Where(e => e.State == EntityState.Added).ToList();
        if (events.Count == 0)
        {
            return new FlushResult(0, 0, 0, 0);
        }

        var opened = tracker.Entries<CustodyEpisode>().Where(e => e.State == EntityState.Added).ToList();
        // a closure only: anything else changed on an episode is left to SaveChanges, which knows how to write it
        var closed = tracker.Entries<CustodyEpisode>().Where(e => e.State == EntityState.Modified && e.Properties.Where(p => p.IsModified).All(p => ClosingProperties.Contains(p.Metadata.Name))).ToList();
        var current = tracker.Entries<UnitCurrent>().Where(e => e.State == EntityState.Modified).ToList();

        await using var session = await SessionAsync(cancellationToken);
        await session.ExecuteAsync(StagingDdl + FlushDdl, cancellationToken);
        await session.BulkCopyAsync("#ev", EventTable(events.Select(e => e.Entity)), cancellationToken);
        await session.BulkCopyAsync("#ep", EpisodeTable(opened.Select(e => e.Entity)), cancellationToken);
        await session.BulkCopyAsync("#close", CloseTable(closed.Select(e => e.Entity)), cancellationToken);
        await session.BulkCopyAsync("#curv", CurrentVersionTable(current.Select(e => (e.Entity, (byte[])e.OriginalValues[nameof(UnitCurrent.RowVersion)]!))), cancellationToken);

        // Closures before openings: the filtered unique index allows one open row per (unit, kind) at any moment.
        await using var command = session.Command($"""
            INSERT INTO [{S}].[unit_event] ({EventColumns}) SELECT {EventColumns} FROM #ev;
            UPDATE e SET valid_to = c.valid_to, closing_event_id = c.closing_event_id, closing_document_type = c.closing_document_type, closing_document_id = c.closing_document_id, closing_document_line = c.closing_document_line
            FROM [{S}].[custody_episode] e JOIN #close c ON e.tenant_id = c.tenant_id AND e.episode_id = c.episode_id
            WHERE e.valid_to IS NULL;
            DECLARE @closed int = @@ROWCOUNT;
            INSERT INTO [{S}].[custody_episode] ({EpisodeColumns}) SELECT {EpisodeColumns} FROM #ep;
            UPDATE t SET
                item_id = s.item_id, model_version_id = s.model_version_id, lot_id = s.lot_id, lifecycle_status = s.lifecycle_status, disposition = s.disposition,
                condition_grade = s.condition_grade, location_id = s.location_id, location_kind = s.location_kind, custody_party_ref = s.custody_party_ref, custody_kind = s.custody_kind,
                owner_party_ref = s.owner_party_ref, open_ownership_episode_id = s.open_ownership_episode_id, open_custody_episode_id = s.open_custody_episode_id,
                live_entitlement_ids = s.live_entitlement_ids, warranty_end_max = s.warranty_end_max, overlay_flags = s.overlay_flags,
                last_event_seq = s.last_event_seq, last_event_time = s.last_event_time, attributes = s.attributes
            OUTPUT inserted.unit_id, inserted.row_version
            FROM [{S}].[unit_current] t JOIN #curv s ON t.tenant_id = s.tenant_id AND t.unit_id = s.unit_id AND t.row_version = s.row_version;
            SELECT @closed;
            """);
        var versions = new Dictionary<Guid, byte[]>();
        var closedCount = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                versions[reader.GetGuid(0)] = (byte[])reader[1];
            }

            await reader.NextResultAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            closedCount = reader.GetInt32(0);
        }

        if (closedCount != closed.Count)
        {
            throw new DbUpdateConcurrencyException($"{closed.Count - closedCount} of {closed.Count} custody episodes were closed by another unit of work.");
        }

        if (versions.Count != current.Count)
        {
            throw new DbUpdateConcurrencyException($"{current.Count - versions.Count} of {current.Count} current rows changed since they were read.");
        }

        foreach (var entry in current)
        {
            entry.Entity.RowVersion = versions[entry.Entity.UnitId];
        }

        foreach (var entry in events.Cast<EntityEntry>().Concat(opened).Concat(closed).Concat(current))
        {
            entry.OriginalValues.SetValues(entry.CurrentValues);
            entry.State = EntityState.Unchanged;
        }

        return new FlushResult(events.Count, opened.Count, closed.Count, current.Count);
    }

    private static UnitCurrent Clone(UnitCurrent c) => new()
    {
        TenantId = c.TenantId, UnitId = c.UnitId, ItemId = c.ItemId, ModelVersionId = c.ModelVersionId, LotId = c.LotId, LifecycleStatus = c.LifecycleStatus, Disposition = c.Disposition,
        ConditionGrade = c.ConditionGrade, LocationId = c.LocationId, LocationKind = c.LocationKind, CustodyPartyRef = c.CustodyPartyRef, CustodyKind = c.CustodyKind, OwnerPartyRef = c.OwnerPartyRef,
        OpenOwnershipEpisodeId = c.OpenOwnershipEpisodeId, OpenCustodyEpisodeId = c.OpenCustodyEpisodeId, LiveEntitlementIds = c.LiveEntitlementIds, WarrantyEndMax = c.WarrantyEndMax, OverlayFlags = c.OverlayFlags,
        LastEventSeq = c.LastEventSeq, LastEventTime = c.LastEventTime, Attributes = c.Attributes,
    };

    private async Task<Session> SessionAsync(CancellationToken cancellationToken)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        var owned = db.Database.CurrentTransaction is null;
        var transaction = owned ? await db.Database.BeginTransactionAsync(cancellationToken) : db.Database.CurrentTransaction!;
        return new Session(connection, (SqlTransaction)transaction.GetDbTransaction(), owned ? transaction : null);
    }

    /// <summary>One connection, one transaction, the temp tables of this session.</summary>
    private sealed class Session(SqlConnection connection, SqlTransaction transaction, IDbContextTransaction? owned) : IAsyncDisposable
    {
        private bool committed;

        public SqlCommand Command(string sql) => new(sql, connection, transaction) { CommandTimeout = 300 };

        public async Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken)
        {
            await using var command = Command(sql);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<int> CountAsync(string sql, CancellationToken cancellationToken)
        {
            await using var command = Command(sql);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        public async Task<HashSet<Guid>> GuidsAsync(string sql, CancellationToken cancellationToken)
        {
            await using var command = Command(sql);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var set = new HashSet<Guid>();
            while (await reader.ReadAsync(cancellationToken))
            {
                set.Add(reader.GetGuid(0));
            }

            return set;
        }

        public async Task<long> NextRangeAsync(int size, CancellationToken cancellationToken)
        {
            await using var command = Command("EXEC sys.sp_sequence_get_range @sequence_name = @name, @range_size = @size, @range_first_value = @first OUTPUT;");
            command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 200) { Value = $"[{S}].[{SerialisationDbContext.EventSequence}]" });
            command.Parameters.Add(new SqlParameter("@size", SqlDbType.BigInt) { Value = (long)size });
            var first = new SqlParameter("@first", SqlDbType.Variant) { Direction = ParameterDirection.Output };
            command.Parameters.Add(first);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return Convert.ToInt64(first.Value, CultureInfo.InvariantCulture);
        }

        public async Task BulkCopyAsync(string table, DataTable rows, CancellationToken cancellationToken)
        {
            if (rows.Rows.Count == 0)
            {
                return;
            }

            using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = table, BatchSize = 20_000, BulkCopyTimeout = 300 };
            foreach (DataColumn column in rows.Columns)
            {
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulk.WriteToServerAsync(rows, cancellationToken);
        }

        public async Task<(int Inserted, int Updated)> MergeCurrentAsync(CancellationToken cancellationToken)
        {
            await using var command = Command($"""
                DECLARE @out TABLE (act nvarchar(10));
                MERGE [{S}].[unit_current] WITH (HOLDLOCK) AS t
                USING #cur AS s ON t.tenant_id = s.tenant_id AND t.unit_id = s.unit_id
                WHEN MATCHED AND t.last_event_seq < s.last_event_seq THEN UPDATE SET
                    item_id = s.item_id, model_version_id = s.model_version_id, lot_id = s.lot_id, lifecycle_status = s.lifecycle_status, disposition = s.disposition,
                    condition_grade = s.condition_grade, location_id = s.location_id, location_kind = s.location_kind, custody_party_ref = s.custody_party_ref, custody_kind = s.custody_kind,
                    owner_party_ref = s.owner_party_ref, open_ownership_episode_id = s.open_ownership_episode_id, open_custody_episode_id = s.open_custody_episode_id,
                    live_entitlement_ids = s.live_entitlement_ids, warranty_end_max = s.warranty_end_max, overlay_flags = s.overlay_flags,
                    last_event_seq = s.last_event_seq, last_event_time = s.last_event_time, attributes = s.attributes
                WHEN NOT MATCHED BY TARGET THEN INSERT ({CurrentColumns}) VALUES ({string.Join(", ", CurrentColumnList.Select(c => "s." + c))})
                OUTPUT $action INTO @out;
                SELECT SUM(CASE WHEN act = 'INSERT' THEN 1 ELSE 0 END), SUM(CASE WHEN act = 'UPDATE' THEN 1 ELSE 0 END) FROM @out;
                """);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return (reader.IsDBNull(0) ? 0 : reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (owned is not null)
            {
                await owned.CommitAsync(cancellationToken);
            }

            committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (owned is not null)
            {
                if (!committed)
                {
                    await owned.RollbackAsync();
                }

                await owned.DisposeAsync();
            }
            else
            {
                // Inside the caller's transaction the temp tables must not leak into its next statement batch.
                try { await ExecuteAsync("DROP TABLE IF EXISTS #ev, #unit, #ident, #assign, #ep, #cur, #inserted, #ids, #close, #curv;", CancellationToken.None); } catch (SqlException) { }
            }
        }
    }

    // ---- staging shapes: the same columns as the target tables, no constraints, no policy ----

    private const string EventColumns = "tenant_id, event_time, event_id, unit_id, seq, event_type, biz_step, disposition, location_id, legal_entity_id, actor_ref, document_type, document_id, document_line, recorded_time, posting_period, idempotency_key, reverses_event_id, attributes";
    private const string UnitColumns = "tenant_id, unit_id, lifecycle_status, overlay_flags, origin, brand_is_ours, lot_id, expiry_date, configuration_snapshot_id, derived_from_unit_id, merged_into_unit_id, serialisation_point, retention_class, history_depth, created_at, created_event_id, created_event_time";
    private const string IdentifierColumns = "tenant_id, identifier_id, unit_id, type, scheme, issuer_party_ref, enterprise_identifier, qualifier, raw_value, normalised_value, scope_key, valid_from, valid_to, superseded_by_identifier_id, supersedes_identifier_id, correction_reason, duplicate_acknowledged, actor_ref, recorded_time, event_id";
    private const string AssignmentColumns = "tenant_id, assignment_id, unit_id, item_id, model_version_id, valid_from, valid_to, reason, event_id";
    private const string EpisodeColumns = "tenant_id, episode_id, unit_id, kind, party_ref, party_type, legal_entity_id, location_id, location_kind, reason, due_back, source, confidence, valid_from, valid_to, opening_document_type, opening_document_id, opening_document_line, opening_event_id, closing_document_type, closing_document_id, closing_document_line, closing_event_id, party_is_group_member, external_order_ref, customer_po_ref, customer_item_ref";
    private static readonly string[] CurrentColumnList = ["tenant_id", "unit_id", "item_id", "model_version_id", "lot_id", "lifecycle_status", "disposition", "condition_grade", "location_id", "location_kind", "custody_party_ref", "custody_kind", "owner_party_ref", "open_ownership_episode_id", "open_custody_episode_id", "live_entitlement_ids", "warranty_end_max", "overlay_flags", "last_event_seq", "last_event_time", "attributes"];
    private static readonly string CurrentColumns = string.Join(", ", CurrentColumnList);

    private const string StagingDdl = """
        CREATE TABLE #ev (tenant_id uniqueidentifier NOT NULL, event_time datetime2(3) NOT NULL, event_id uniqueidentifier NOT NULL, unit_id uniqueidentifier NOT NULL, seq bigint NOT NULL, event_type nvarchar(60) NOT NULL, biz_step nvarchar(40) NULL, disposition nvarchar(40) NULL, location_id uniqueidentifier NULL, legal_entity_id uniqueidentifier NOT NULL, actor_ref nvarchar(200) NOT NULL, document_type nvarchar(40) NULL, document_id uniqueidentifier NULL, document_line int NULL, recorded_time datetime2(3) NOT NULL, posting_period char(7) NULL, idempotency_key varchar(64) NOT NULL, reverses_event_id uniqueidentifier NULL, attributes nvarchar(max) NULL);
        CREATE TABLE #unit (tenant_id uniqueidentifier NOT NULL, unit_id uniqueidentifier NOT NULL, lifecycle_status nvarchar(40) NOT NULL, overlay_flags nvarchar(max) NOT NULL, origin nvarchar(40) NOT NULL, brand_is_ours bit NOT NULL, lot_id uniqueidentifier NULL, expiry_date date NULL, configuration_snapshot_id uniqueidentifier NULL, derived_from_unit_id uniqueidentifier NULL, merged_into_unit_id uniqueidentifier NULL, serialisation_point nvarchar(40) NOT NULL, retention_class nvarchar(40) NOT NULL, history_depth nvarchar(20) NOT NULL, created_at datetime2(3) NOT NULL, created_event_id uniqueidentifier NOT NULL, created_event_time datetime2(3) NOT NULL);
        CREATE TABLE #ident (tenant_id uniqueidentifier NOT NULL, identifier_id uniqueidentifier NOT NULL, unit_id uniqueidentifier NOT NULL, type nvarchar(40) NOT NULL, scheme nvarchar(20) NOT NULL, issuer_party_ref uniqueidentifier NULL, enterprise_identifier nvarchar(50) NULL, qualifier nvarchar(100) NULL, raw_value nvarchar(100) NOT NULL, normalised_value nvarchar(100) NOT NULL, scope_key nvarchar(100) NOT NULL, valid_from datetime2(3) NOT NULL, valid_to datetime2(3) NULL, superseded_by_identifier_id uniqueidentifier NULL, supersedes_identifier_id uniqueidentifier NULL, correction_reason nvarchar(40) NULL, duplicate_acknowledged bit NOT NULL, actor_ref nvarchar(200) NOT NULL, recorded_time datetime2(3) NOT NULL, event_id uniqueidentifier NOT NULL);
        CREATE TABLE #assign (tenant_id uniqueidentifier NOT NULL, assignment_id uniqueidentifier NOT NULL, unit_id uniqueidentifier NOT NULL, item_id uniqueidentifier NOT NULL, model_version_id uniqueidentifier NULL, valid_from datetime2(3) NOT NULL, valid_to datetime2(3) NULL, reason nvarchar(20) NOT NULL, event_id uniqueidentifier NOT NULL);
        CREATE TABLE #ep (tenant_id uniqueidentifier NOT NULL, episode_id uniqueidentifier NOT NULL, unit_id uniqueidentifier NOT NULL, kind nvarchar(20) NOT NULL, party_ref uniqueidentifier NULL, party_type nvarchar(30) NULL, legal_entity_id uniqueidentifier NULL, location_id uniqueidentifier NULL, location_kind nvarchar(30) NULL, reason nvarchar(40) NULL, due_back date NULL, source nvarchar(40) NULL, confidence nvarchar(20) NULL, valid_from datetime2(3) NOT NULL, valid_to datetime2(3) NULL, opening_document_type nvarchar(40) NULL, opening_document_id uniqueidentifier NULL, opening_document_line int NULL, opening_event_id uniqueidentifier NOT NULL, closing_document_type nvarchar(40) NULL, closing_document_id uniqueidentifier NULL, closing_document_line int NULL, closing_event_id uniqueidentifier NULL, party_is_group_member bit NOT NULL, external_order_ref nvarchar(100) NULL, customer_po_ref nvarchar(100) NULL, customer_item_ref nvarchar(100) NULL);
        CREATE TABLE #cur (tenant_id uniqueidentifier NOT NULL, unit_id uniqueidentifier NOT NULL, item_id uniqueidentifier NOT NULL, model_version_id uniqueidentifier NULL, lot_id uniqueidentifier NULL, lifecycle_status nvarchar(40) NOT NULL, disposition nvarchar(40) NOT NULL, condition_grade nvarchar(40) NULL, location_id uniqueidentifier NULL, location_kind nvarchar(30) NULL, custody_party_ref uniqueidentifier NULL, custody_kind nvarchar(20) NULL, owner_party_ref uniqueidentifier NULL, open_ownership_episode_id uniqueidentifier NULL, open_custody_episode_id uniqueidentifier NULL, live_entitlement_ids nvarchar(max) NOT NULL, warranty_end_max date NULL, overlay_flags nvarchar(max) NOT NULL, last_event_seq bigint NOT NULL, last_event_time datetime2(3) NOT NULL, attributes nvarchar(max) NULL);
        CREATE TABLE #inserted (event_id uniqueidentifier NOT NULL PRIMARY KEY);
        """;

    // FlushTrackedAsync: an episode closure, and a current row with the rowversion it was read at.
    private const string FlushDdl = """
        CREATE TABLE #close (tenant_id uniqueidentifier NOT NULL, episode_id uniqueidentifier NOT NULL, valid_to datetime2(3) NOT NULL, closing_event_id uniqueidentifier NULL, closing_document_type nvarchar(40) NULL, closing_document_id uniqueidentifier NULL, closing_document_line int NULL, PRIMARY KEY (tenant_id, episode_id));
        CREATE TABLE #curv (tenant_id uniqueidentifier NOT NULL, unit_id uniqueidentifier NOT NULL, item_id uniqueidentifier NOT NULL, model_version_id uniqueidentifier NULL, lot_id uniqueidentifier NULL, lifecycle_status nvarchar(40) NOT NULL, disposition nvarchar(40) NOT NULL, condition_grade nvarchar(40) NULL, location_id uniqueidentifier NULL, location_kind nvarchar(30) NULL, custody_party_ref uniqueidentifier NULL, custody_kind nvarchar(20) NULL, owner_party_ref uniqueidentifier NULL, open_ownership_episode_id uniqueidentifier NULL, open_custody_episode_id uniqueidentifier NULL, live_entitlement_ids nvarchar(max) NOT NULL, warranty_end_max date NULL, overlay_flags nvarchar(max) NOT NULL, last_event_seq bigint NOT NULL, last_event_time datetime2(3) NOT NULL, attributes nvarchar(max) NULL, row_version binary(8) NOT NULL, PRIMARY KEY (tenant_id, unit_id));
        """;

    private static DataTable Table(params (string Name, Type Type)[] columns)
    {
        var t = new DataTable();
        foreach (var (name, type) in columns)
        {
            t.Columns.Add(name, type);
        }

        return t;
    }

    private static object V<T>(T? value) where T : struct => value.HasValue ? value.Value : DBNull.Value;

    private static object V(string? value) => value is null ? DBNull.Value : value;

    private static object D(DateOnly? value) => value.HasValue ? value.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;

    private static DataTable EventTable(IEnumerable<UnitEvent> rows)
    {
        var t = Table(("tenant_id", typeof(Guid)), ("event_time", typeof(DateTime)), ("event_id", typeof(Guid)), ("unit_id", typeof(Guid)), ("seq", typeof(long)), ("event_type", typeof(string)), ("biz_step", typeof(string)), ("disposition", typeof(string)), ("location_id", typeof(Guid)), ("legal_entity_id", typeof(Guid)), ("actor_ref", typeof(string)), ("document_type", typeof(string)), ("document_id", typeof(Guid)), ("document_line", typeof(int)), ("recorded_time", typeof(DateTime)), ("posting_period", typeof(string)), ("idempotency_key", typeof(string)), ("reverses_event_id", typeof(Guid)), ("attributes", typeof(string)));
        foreach (var e in rows)
        {
            t.Rows.Add(e.TenantId.Value, e.EventTime, e.EventId, e.UnitId, e.Seq, e.EventType, V(e.BizStep), V(e.Disposition), V(e.LocationId), e.LegalEntityId, e.ActorRef, V(e.DocumentType), V(e.DocumentId), V(e.DocumentLine), e.RecordedTime, V(e.PostingPeriod), e.IdempotencyKey, V(e.ReversesEventId), V(e.Attributes));
        }

        return t;
    }

    private static DataTable UnitTable(IEnumerable<Unit> rows)
    {
        var t = Table(("tenant_id", typeof(Guid)), ("unit_id", typeof(Guid)), ("lifecycle_status", typeof(string)), ("overlay_flags", typeof(string)), ("origin", typeof(string)), ("brand_is_ours", typeof(bool)), ("lot_id", typeof(Guid)), ("expiry_date", typeof(DateTime)), ("configuration_snapshot_id", typeof(Guid)), ("derived_from_unit_id", typeof(Guid)), ("merged_into_unit_id", typeof(Guid)), ("serialisation_point", typeof(string)), ("retention_class", typeof(string)), ("history_depth", typeof(string)), ("created_at", typeof(DateTime)), ("created_event_id", typeof(Guid)), ("created_event_time", typeof(DateTime)));
        foreach (var u in rows)
        {
            t.Rows.Add(u.TenantId.Value, u.UnitId, u.LifecycleStatus, u.OverlayFlags, u.Origin, u.BrandIsOurs, V(u.LotId), D(u.ExpiryDate), V(u.ConfigurationSnapshotId), V(u.DerivedFromUnitId), V(u.MergedIntoUnitId), u.SerialisationPoint, u.RetentionClass, u.HistoryDepth, u.CreatedAt, u.CreatedEventId, u.CreatedEventTime);
        }

        return t;
    }

    private static DataTable IdentifierTable(IEnumerable<UnitIdentifier> rows)
    {
        var t = Table(("tenant_id", typeof(Guid)), ("identifier_id", typeof(Guid)), ("unit_id", typeof(Guid)), ("type", typeof(string)), ("scheme", typeof(string)), ("issuer_party_ref", typeof(Guid)), ("enterprise_identifier", typeof(string)), ("qualifier", typeof(string)), ("raw_value", typeof(string)), ("normalised_value", typeof(string)), ("scope_key", typeof(string)), ("valid_from", typeof(DateTime)), ("valid_to", typeof(DateTime)), ("superseded_by_identifier_id", typeof(Guid)), ("supersedes_identifier_id", typeof(Guid)), ("correction_reason", typeof(string)), ("duplicate_acknowledged", typeof(bool)), ("actor_ref", typeof(string)), ("recorded_time", typeof(DateTime)), ("event_id", typeof(Guid)));
        foreach (var i in rows)
        {
            t.Rows.Add(i.TenantId.Value, i.IdentifierId, i.UnitId, i.Type, i.Scheme, V(i.IssuerPartyRef), V(i.EnterpriseIdentifier), V(i.Qualifier), i.RawValue, i.NormalisedValue, i.ScopeKey, i.ValidFrom, V(i.ValidTo), V(i.SupersededByIdentifierId), V(i.SupersedesIdentifierId), V(i.CorrectionReason), i.DuplicateAcknowledged, i.ActorRef, i.RecordedTime, i.EventId);
        }

        return t;
    }

    private static DataTable AssignmentTable(IEnumerable<UnitItemAssignment> rows)
    {
        var t = Table(("tenant_id", typeof(Guid)), ("assignment_id", typeof(Guid)), ("unit_id", typeof(Guid)), ("item_id", typeof(Guid)), ("model_version_id", typeof(Guid)), ("valid_from", typeof(DateTime)), ("valid_to", typeof(DateTime)), ("reason", typeof(string)), ("event_id", typeof(Guid)));
        foreach (var a in rows)
        {
            t.Rows.Add(a.TenantId.Value, a.AssignmentId, a.UnitId, a.ItemId, V(a.ModelVersionId), a.ValidFrom, V(a.ValidTo), a.Reason, a.EventId);
        }

        return t;
    }

    private static DataTable EpisodeTable(IEnumerable<CustodyEpisode> rows)
    {
        var t = Table(("tenant_id", typeof(Guid)), ("episode_id", typeof(Guid)), ("unit_id", typeof(Guid)), ("kind", typeof(string)), ("party_ref", typeof(Guid)), ("party_type", typeof(string)), ("legal_entity_id", typeof(Guid)), ("location_id", typeof(Guid)), ("location_kind", typeof(string)), ("reason", typeof(string)), ("due_back", typeof(DateTime)), ("source", typeof(string)), ("confidence", typeof(string)), ("valid_from", typeof(DateTime)), ("valid_to", typeof(DateTime)), ("opening_document_type", typeof(string)), ("opening_document_id", typeof(Guid)), ("opening_document_line", typeof(int)), ("opening_event_id", typeof(Guid)), ("closing_document_type", typeof(string)), ("closing_document_id", typeof(Guid)), ("closing_document_line", typeof(int)), ("closing_event_id", typeof(Guid)), ("party_is_group_member", typeof(bool)), ("external_order_ref", typeof(string)), ("customer_po_ref", typeof(string)), ("customer_item_ref", typeof(string)));
        foreach (var e in rows)
        {
            t.Rows.Add(e.TenantId.Value, e.EpisodeId, e.UnitId, e.Kind, V(e.PartyRef), V(e.PartyType), V(e.LegalEntityId), V(e.LocationId), V(e.LocationKind), V(e.Reason), D(e.DueBack), V(e.Source), V(e.Confidence), e.ValidFrom, V(e.ValidTo), V(e.OpeningDocumentType), V(e.OpeningDocumentId), V(e.OpeningDocumentLine), e.OpeningEventId, V(e.ClosingDocumentType), V(e.ClosingDocumentId), V(e.ClosingDocumentLine), V(e.ClosingEventId), e.PartyIsGroupMember, V(e.ExternalOrderRef), V(e.CustomerPoRef), V(e.CustomerItemRef));
        }

        return t;
    }

    private static DataTable CloseTable(IEnumerable<CustodyEpisode> rows)
    {
        var t = Table(("tenant_id", typeof(Guid)), ("episode_id", typeof(Guid)), ("valid_to", typeof(DateTime)), ("closing_event_id", typeof(Guid)), ("closing_document_type", typeof(string)), ("closing_document_id", typeof(Guid)), ("closing_document_line", typeof(int)));
        foreach (var e in rows)
        {
            t.Rows.Add(e.TenantId.Value, e.EpisodeId, e.ValidTo!.Value, V(e.ClosingEventId), V(e.ClosingDocumentType), V(e.ClosingDocumentId), V(e.ClosingDocumentLine));
        }

        return t;
    }

    private static DataTable CurrentVersionTable(IEnumerable<(UnitCurrent Row, byte[] RowVersion)> rows)
    {
        var t = CurrentTable(rows.Select(r => r.Row));
        t.Columns.Add("row_version", typeof(byte[]));
        var i = 0;
        foreach (var (_, version) in rows)
        {
            t.Rows[i++]["row_version"] = version;
        }

        return t;
    }

    private static DataTable CurrentTable(IEnumerable<UnitCurrent> rows)
    {
        var t = Table(("tenant_id", typeof(Guid)), ("unit_id", typeof(Guid)), ("item_id", typeof(Guid)), ("model_version_id", typeof(Guid)), ("lot_id", typeof(Guid)), ("lifecycle_status", typeof(string)), ("disposition", typeof(string)), ("condition_grade", typeof(string)), ("location_id", typeof(Guid)), ("location_kind", typeof(string)), ("custody_party_ref", typeof(Guid)), ("custody_kind", typeof(string)), ("owner_party_ref", typeof(Guid)), ("open_ownership_episode_id", typeof(Guid)), ("open_custody_episode_id", typeof(Guid)), ("live_entitlement_ids", typeof(string)), ("warranty_end_max", typeof(DateTime)), ("overlay_flags", typeof(string)), ("last_event_seq", typeof(long)), ("last_event_time", typeof(DateTime)), ("attributes", typeof(string)));
        foreach (var c in rows)
        {
            t.Rows.Add(c.TenantId.Value, c.UnitId, c.ItemId, V(c.ModelVersionId), V(c.LotId), c.LifecycleStatus, c.Disposition, V(c.ConditionGrade), V(c.LocationId), V(c.LocationKind), V(c.CustodyPartyRef), V(c.CustodyKind), V(c.OwnerPartyRef), V(c.OpenOwnershipEpisodeId), V(c.OpenCustodyEpisodeId), c.LiveEntitlementIds, D(c.WarrantyEndMax), c.OverlayFlags, c.LastEventSeq, c.LastEventTime, V(c.Attributes));
        }

        return t;
    }
}
