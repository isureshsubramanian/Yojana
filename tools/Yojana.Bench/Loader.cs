using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Yojana.Platform.Contracts.Identifiers;

namespace Yojana.Bench;

/// <summary>
/// Generates the ledger and its dependents in business-time order straight into the tables with SqlBulkCopy
/// (the shape the set-based ingestion path produces): one commissioning event per unit, then events spread over
/// the months from <see cref="Start"/> to now, seq ascending with time, unit_current folded from the last event
/// of each unit. Units are created linearly over the period so the live population grows the way a tenant's does.
/// </summary>
public sealed class Loader(BenchOptions options, BenchReport report)
{
    public static readonly DateTime Start = new(2013, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Types = ["custody.changed", "custody.changed", "custody.changed", "unit.shipped", "unit.delivered", "ownership.opened", "unit.released", "unit.assembled", "unit.identifier_assigned", "ownership.closed", "unit.flag_set", "custody.changed"];
    private static readonly string[] LocationKinds = ["warehouse", "in_transit", "customer_site", "warehouse", "warehouse", "bin"];
    private const int ItemCount = 200;
    private const int LocationCount = 40;

    public async Task RunAsync()
    {
        var totalWatch = Stopwatch.StartNew();
        long totalEvents = 0;
        long totalUnits = 0;
        long maxSeq = 0;
        await using (var connection = await Sql.OpenAsync(options.ConnectionString))
        {
            maxSeq = await Sql.ScalarAsync<long?>(connection, "SELECT MAX(seq) FROM serialisation.unit_event") ?? 0;
        }

        for (var t = 1; t <= options.Tenants; t++)
        {
            var units = t == 1 ? options.Units : options.SmallUnits;
            var events = t == 1 ? options.Events : options.SmallEvents;
            var (u, e, seq) = await LoadTenantAsync(t, units, events, maxSeq);
            totalUnits += u;
            totalEvents += e;
            maxSeq = seq;
        }

        await using (var connection = await Sql.OpenAsync(options.ConnectionString))
        {
            await Sql.ExecAsync(connection, $"ALTER SEQUENCE serialisation.seq_unit_event RESTART WITH {maxSeq + 1}");
            report.Log("load: updating statistics");
            await Sql.ExecAsync(connection, "UPDATE STATISTICS serialisation.unit_event; UPDATE STATISTICS serialisation.unit_identifier; UPDATE STATISTICS serialisation.unit_current; UPDATE STATISTICS serialisation.unit;");
        }

        totalWatch.Stop();
        report.Fact("Load", "Units loaded", totalUnits);
        report.Fact("Load", "Events loaded", totalEvents);
        report.Fact("Load", "Load time", BenchReport.Ms(totalWatch.Elapsed.TotalMilliseconds));
        report.Fact("Load", "Load rate", $"{totalEvents / Math.Max(1, totalWatch.Elapsed.TotalSeconds):N0} events/s including dependents");
    }

    private async Task<(long Units, long Events, long MaxSeq)> LoadTenantAsync(int tenantIndex, int units, long events, long seq)
    {
        var tenant = BenchOptions.TenantId(tenantIndex);
        var random = new Random(1000 + tenantIndex);
        var items = Enumerable.Range(0, ItemCount).Select(i => Guid.ParseExact($"1e000000-0000-4000-8000-{i:D12}", "D")).ToArray();
        var locations = Enumerable.Range(0, LocationCount).Select(i => Guid.ParseExact($"10c00000-0000-4000-8000-{i:D12}", "D")).ToArray();
        var parties = Enumerable.Range(0, 500).Select(i => Guid.ParseExact($"9a000000-0000-4000-8000-{i:D12}", "D")).ToArray();
        var end = DateTime.UtcNow;
        var months = (end.Year - Start.Year) * 12 + end.Month - Start.Month + 1;
        var perMonth = Math.Max(0, (events - units) / months);

        var unitIds = new Guid[units];
        var createdEvent = new Guid[units];
        var lastSeq = new long[units];
        var lastTime = new DateTime[units];
        var lastLocation = new int[units];
        var lastLocationKind = new byte[units];
        var owner = new int[units];
        var status = new byte[units]; // 0 pre_release, 1 released
        Array.Fill(owner, -1);
        var episodeId = new Guid[units];

        var connection = await Sql.OpenAsync(options.ConnectionString, tenant);
        // A dropped LAN connection or a sleeping laptop must not end a multi-hour load: every bulk copy is one transaction
        // (UseInternalTransaction), so on failure the connection is reopened with its session context and the same rows
        // are written again, for up to two hours.
        async Task WriteAsync(string table, DataTable rows)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await Sql.BulkAsync(connection, table, rows);
                    return;
                }
                catch (Exception ex) when (attempt < 240)
                {
                    var first = ex.Message.Split('\n')[0];
                    report.Log($"load: {table} attempt {attempt} failed ({first[..Math.Min(120, first.Length)]}); reconnecting in 30 s");
                    try { await connection.DisposeAsync(); } catch { /* already broken */ }
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    try { connection = await Sql.OpenAsync(options.ConnectionString, tenant); } catch { connection = new SqlConnection(options.ConnectionString); }
                }
            }
        }

        var already = await Sql.ScalarAsync<int>(connection, "SELECT COUNT(*) FROM serialisation.unit");
        if (already >= units)
        {
            report.Log($"load: tenant {tenantIndex} already has {already:N0} units; skipping");
            return (0, 0, seq);
        }

        // Resume after an interruption. The generator is deterministic (fixed seed, same code), so the months already on
        // the server are replayed without writing to rebuild the in-memory fold. Events are flushed chronologically in
        // 200,000-row batches and a month's units are written at the end of the month, so everything before the month
        // preceding the newest written event is complete; the load restarts from that month after removing its rows.
        var resumeMonth = 0;
        if (already > 0)
        {
            var newest = await Sql.ScalarAsync<DateTime?>(connection, "SELECT MAX(event_time) FROM serialisation.unit_event", 600) ?? Start;
            resumeMonth = Math.Max(0, (newest.Year - Start.Year) * 12 + newest.Month - Start.Month - 1);
            var from = Start.AddMonths(resumeMonth);
            seq = (await Sql.ScalarAsync<long?>(connection, "SELECT MIN(seq) FROM serialisation.unit_event", 600) ?? (seq + 1)) - 1;
            report.Log($"load: tenant {tenantIndex} resumes at {from:yyyy-MM} ({already:N0} units present, first seq {seq + 1}); removing rows from that month");
            await Sql.ExecAsync(connection, "DELETE FROM serialisation.unit_current;", 0);
            while (await Sql.ExecAsync(connection, "DELETE TOP (200000) FROM serialisation.unit_event WHERE event_time >= @from", 0, ("@from", from)) > 0) { }
            await Sql.ExecAsync(connection, "DELETE i FROM serialisation.unit_identifier AS i JOIN serialisation.unit AS u ON u.tenant_id = i.tenant_id AND u.unit_id = i.unit_id WHERE u.created_at >= @from; DELETE a FROM serialisation.unit_item_assignment AS a JOIN serialisation.unit AS u ON u.tenant_id = a.tenant_id AND u.unit_id = a.unit_id WHERE u.created_at >= @from; DELETE c FROM serialisation.custody_episode AS c JOIN serialisation.unit AS u ON u.tenant_id = c.tenant_id AND u.unit_id = c.unit_id WHERE u.created_at >= @from; DELETE FROM serialisation.unit WHERE created_at >= @from;", 0, ("@from", from));
        }

        var watch = Stopwatch.StartNew();
        long written = 0;
        var docCounter = 0;
        var docId = Guid.Empty;
        var docLine = 0;
        var eventRows = EventTable();
        for (var m = 0; m < months; m++)
        {
            var monthStart = Start.AddMonths(m);
            var monthEnd = m == months - 1 ? end : Start.AddMonths(m + 1);
            var span = (monthEnd - monthStart).TotalSeconds;
            var lo = (int)((long)m * units / months);
            var hi = (int)((long)(m + 1) * units / months);
            if (m == months - 1) hi = units;
            if (m == resumeMonth && resumeMonth > 0)
            {
                eventRows.Clear(); // everything before this month is on the server already
            }

            var monthEvents = new List<(DateTime Time, int Unit, byte Kind)>((hi - lo) + (int)perMonth + 16);
            for (var n = lo; n < hi; n++) monthEvents.Add((monthStart.AddSeconds(random.NextDouble() * span), n, 255)); // 255 = commissioned
            var alive = Math.Max(hi, 1);
            for (long i = 0; i < perMonth; i++) monthEvents.Add((monthStart.AddSeconds(random.NextDouble() * span), random.Next(alive), (byte)random.Next(Types.Length)));
            monthEvents.Sort((a, b) => a.Time.CompareTo(b.Time));

            // dependents of the units created this month
            var unitRows = UnitTable();
            var identifierRows = IdentifierTable();
            var assignmentRows = AssignmentTable();
            var episodeRows = EpisodeTable();

            foreach (var (time, n, kind) in monthEvents)
            {
                var truncated = new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
                var eventId = DeterministicId(truncated, tenantIndex, 1, seq + 1);
                seq++;
                string type;
                string? bizStep = null;
                string? documentType = null;
                Guid? documentId = null;
                int? documentLine = null;
                Guid? locationId = null;
                if (kind == 255)
                {
                    type = "unit.commissioned";
                    bizStep = "commissioning";
                    unitIds[n] = DeterministicId(truncated, tenantIndex, 2, n);
                    createdEvent[n] = eventId;
                    lastLocation[n] = random.Next(LocationCount);
                    lastLocationKind[n] = 0;
                    locationId = locations[lastLocation[n]];
                    episodeId[n] = DeterministicId(truncated, tenantIndex, 3, n);
                    var item = items[n % ItemCount];
                    unitRows.Rows.Add(tenant, unitIds[n], "pre_release", "[]", "manufactured", true, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, "production_release", "standard_10y", "full", truncated, eventId);
                    identifierRows.Rows.Add(tenant, DeterministicId(truncated, tenantIndex, 4, n), unitIds[n], "internal_serial", "internal", DBNull.Value, DBNull.Value, DBNull.Value, Serial(tenantIndex, n), Normalised(tenantIndex, n), item.ToString("N"), truncated, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, false, "bench", truncated, eventId);
                    assignmentRows.Rows.Add(tenant, DeterministicId(truncated, tenantIndex, 5, n), unitIds[n], item, DBNull.Value, truncated, DBNull.Value, "initial", eventId);
                    episodeRows.Rows.Add(tenant, episodeId[n], unitIds[n], "custody", DBNull.Value, "us", BenchOptions.LegalEntity, locationId, "warehouse", "production", DBNull.Value, "receipt", "high", truncated, DBNull.Value, "production_order", DeterministicId(truncated, tenantIndex, 7, n), 1, eventId, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, false, DBNull.Value, DBNull.Value, DBNull.Value);
                }
                else
                {
                    if (unitIds[n] == Guid.Empty) continue; // not created yet in this month's ordering
                    type = Types[kind];
                    switch (type)
                    {
                        case "custody.changed":
                            lastLocation[n] = random.Next(LocationCount);
                            lastLocationKind[n] = (byte)random.Next(LocationKinds.Length);
                            locationId = locations[lastLocation[n]];
                            bizStep = "storing";
                            break;
                        case "unit.shipped":
                        case "unit.delivered":
                            if (docCounter % 500 == 0) { docId = DeterministicId(truncated, tenantIndex, 6, docCounter); docLine = 0; }
                            docCounter++;
                            documentType = "delivery";
                            documentId = docId;
                            documentLine = ++docLine;
                            lastLocationKind[n] = (byte)(type == "unit.shipped" ? 1 : 2);
                            bizStep = type == "unit.shipped" ? "shipping" : "receiving";
                            break;
                        case "ownership.opened":
                            owner[n] = random.Next(parties.Length);
                            break;
                        case "ownership.closed":
                            owner[n] = -1;
                            break;
                        case "unit.released":
                            status[n] = 1;
                            bizStep = "inspecting";
                            break;
                    }
                }

                lastSeq[n] = seq;
                lastTime[n] = truncated;
                eventRows.Rows.Add(tenant, truncated, eventId, unitIds[n], seq, type, (object?)bizStep ?? DBNull.Value, status[n] == 1 ? "active" : "in_progress", (object?)locationId ?? DBNull.Value, BenchOptions.LegalEntity, "bench", (object?)documentType ?? DBNull.Value, (object?)documentId ?? DBNull.Value, (object?)documentLine ?? DBNull.Value, truncated.AddSeconds(2), DBNull.Value, "b-" + seq.ToString(CultureInfo.InvariantCulture), DBNull.Value, "{\"bench\":true}");
                if (eventRows.Rows.Count >= 200_000)
                {
                    if (m >= resumeMonth) { await WriteAsync("serialisation.unit_event", eventRows); written += eventRows.Rows.Count; }
                    eventRows.Clear();
                }
            }

            if (unitRows.Rows.Count > 0 && m >= resumeMonth)
            {
                await WriteAsync("serialisation.unit", unitRows);
                await WriteAsync("serialisation.unit_identifier", identifierRows);
                await WriteAsync("serialisation.unit_item_assignment", assignmentRows);
                await WriteAsync("serialisation.custody_episode", episodeRows);
            }

            if (m % 12 == 11 || m == months - 1)
            {
                var done = written + eventRows.Rows.Count;
                report.Log($"load: tenant {tenantIndex} · {monthStart:yyyy-MM} · {hi:N0} units · {done:N0} events · {done / Math.Max(1, watch.Elapsed.TotalSeconds):N0} events/s");
            }
        }

        if (eventRows.Rows.Count > 0)
        {
            await WriteAsync("serialisation.unit_event", eventRows);
            written += eventRows.Rows.Count;
            eventRows.Clear();
        }

        // unit_current from the fold kept in memory (coupling 2: last_event_seq is the newest event's seq)
        var current = CurrentTable();
        for (var n = 0; n < units; n++)
        {
            if (unitIds[n] == Guid.Empty) continue;
            current.Rows.Add(tenant, unitIds[n], items[n % ItemCount], DBNull.Value, DBNull.Value, status[n] == 1 ? "released" : "pre_release", status[n] == 1 ? "active" : "in_progress", DBNull.Value, locations[lastLocation[n]], LocationKinds[lastLocationKind[n]], DBNull.Value, "custody", owner[n] >= 0 ? parties[owner[n]] : DBNull.Value, DBNull.Value, episodeId[n], "[]", DBNull.Value, "[]", lastSeq[n], lastTime[n], DBNull.Value);
            if (current.Rows.Count >= 200_000) { await WriteAsync("serialisation.unit_current", current); current.Clear(); }
        }

        if (current.Rows.Count > 0) await WriteAsync("serialisation.unit_current", current);
        await connection.DisposeAsync();
        watch.Stop();
        report.Log($"load: tenant {tenantIndex} done · {units:N0} units · {written:N0} events · {watch.Elapsed:hh\\:mm\\:ss} · {written / Math.Max(1, watch.Elapsed.TotalSeconds):N0} events/s");
        return (units, written, seq);
    }

    /// <summary>A time-ordered id whose random tail is replaced by a hash of (tenant, kind, salt): the same inputs always give the same id, which a resumed load relies on.</summary>
    private static Guid DeterministicId(DateTime time, int tenantIndex, byte kind, long salt)
    {
        var bytes = TimeOrderedId.New(time).ToByteArray();
        var h = unchecked((ulong)salt * 0x9E3779B97F4A7C15UL ^ ((ulong)tenantIndex << 56) ^ ((ulong)kind << 48));
        for (var i = 0; i < 8; i++) bytes[8 + i] = (byte)(h >> (8 * i));
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC variant bits stay valid
        return new Guid(bytes);
    }

    public static string Serial(int tenantIndex, int n) => $"B{tenantIndex}-{n:D7}";

    public static string Normalised(int tenantIndex, int n) => $"B{tenantIndex}{n:D7}";

    private static DataTable EventTable() => Sql.Table(("tenant_id", typeof(Guid)), ("event_time", typeof(DateTime)), ("event_id", typeof(Guid)), ("unit_id", typeof(Guid)), ("seq", typeof(long)), ("event_type", typeof(string)), ("biz_step", typeof(string)), ("disposition", typeof(string)), ("location_id", typeof(Guid)), ("legal_entity_id", typeof(Guid)), ("actor_ref", typeof(string)), ("document_type", typeof(string)), ("document_id", typeof(Guid)), ("document_line", typeof(int)), ("recorded_time", typeof(DateTime)), ("posting_period", typeof(string)), ("idempotency_key", typeof(string)), ("reverses_event_id", typeof(Guid)), ("attributes", typeof(string)));

    private static DataTable UnitTable() => Sql.Table(("tenant_id", typeof(Guid)), ("unit_id", typeof(Guid)), ("lifecycle_status", typeof(string)), ("overlay_flags", typeof(string)), ("origin", typeof(string)), ("brand_is_ours", typeof(bool)), ("lot_id", typeof(Guid)), ("expiry_date", typeof(DateTime)), ("configuration_snapshot_id", typeof(Guid)), ("derived_from_unit_id", typeof(Guid)), ("merged_into_unit_id", typeof(Guid)), ("serialisation_point", typeof(string)), ("retention_class", typeof(string)), ("history_depth", typeof(string)), ("created_at", typeof(DateTime)), ("created_event_id", typeof(Guid)));

    private static DataTable IdentifierTable() => Sql.Table(("tenant_id", typeof(Guid)), ("identifier_id", typeof(Guid)), ("unit_id", typeof(Guid)), ("type", typeof(string)), ("scheme", typeof(string)), ("issuer_party_ref", typeof(Guid)), ("enterprise_identifier", typeof(string)), ("qualifier", typeof(string)), ("raw_value", typeof(string)), ("normalised_value", typeof(string)), ("scope_key", typeof(string)), ("valid_from", typeof(DateTime)), ("valid_to", typeof(DateTime)), ("superseded_by_identifier_id", typeof(Guid)), ("supersedes_identifier_id", typeof(Guid)), ("correction_reason", typeof(string)), ("duplicate_acknowledged", typeof(bool)), ("actor_ref", typeof(string)), ("recorded_time", typeof(DateTime)), ("event_id", typeof(Guid)));

    private static DataTable AssignmentTable() => Sql.Table(("tenant_id", typeof(Guid)), ("assignment_id", typeof(Guid)), ("unit_id", typeof(Guid)), ("item_id", typeof(Guid)), ("model_version_id", typeof(Guid)), ("valid_from", typeof(DateTime)), ("valid_to", typeof(DateTime)), ("reason", typeof(string)), ("event_id", typeof(Guid)));

    private static DataTable EpisodeTable() => Sql.Table(("tenant_id", typeof(Guid)), ("episode_id", typeof(Guid)), ("unit_id", typeof(Guid)), ("kind", typeof(string)), ("party_ref", typeof(Guid)), ("party_type", typeof(string)), ("legal_entity_id", typeof(Guid)), ("location_id", typeof(Guid)), ("location_kind", typeof(string)), ("reason", typeof(string)), ("due_back", typeof(DateTime)), ("source", typeof(string)), ("confidence", typeof(string)), ("valid_from", typeof(DateTime)), ("valid_to", typeof(DateTime)), ("opening_document_type", typeof(string)), ("opening_document_id", typeof(Guid)), ("opening_document_line", typeof(int)), ("opening_event_id", typeof(Guid)), ("closing_document_type", typeof(string)), ("closing_document_id", typeof(Guid)), ("closing_document_line", typeof(int)), ("closing_event_id", typeof(Guid)), ("party_is_group_member", typeof(bool)), ("external_order_ref", typeof(string)), ("customer_po_ref", typeof(string)), ("customer_item_ref", typeof(string)));

    private static DataTable CurrentTable() => Sql.Table(("tenant_id", typeof(Guid)), ("unit_id", typeof(Guid)), ("item_id", typeof(Guid)), ("model_version_id", typeof(Guid)), ("lot_id", typeof(Guid)), ("lifecycle_status", typeof(string)), ("disposition", typeof(string)), ("condition_grade", typeof(string)), ("location_id", typeof(Guid)), ("location_kind", typeof(string)), ("custody_party_ref", typeof(Guid)), ("custody_kind", typeof(string)), ("owner_party_ref", typeof(Guid)), ("open_ownership_episode_id", typeof(Guid)), ("open_custody_episode_id", typeof(Guid)), ("live_entitlement_ids", typeof(string)), ("warranty_end_max", typeof(DateTime)), ("overlay_flags", typeof(string)), ("last_event_seq", typeof(long)), ("last_event_time", typeof(DateTime)), ("attributes", typeof(string)));
}
