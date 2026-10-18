using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Projections;

/// <summary>
/// Replay-and-diff over <c>unit_current</c> (ADR-0001 AI 8), a safety net behind coupling 2, never the primary path.
/// Two passes, so a million units fit a nightly window (P1-T21: one fold query per unit cost 36 ms over a LAN and
/// would take ten hours): first a set-based comparison of every row's <c>last_event_seq</c> with the newest seq the
/// ledger holds for the unit (live and archive), which catches the realistic drift, an apply that was skipped or
/// lost; then a full fold, through <see cref="UnitCurrentStateProjector.Fold"/>, of only the units that differ,
/// the units with events but no row, and a random sample of <c>pageSize</c> rows whose seq agrees, which catches
/// silent corruption of a row's fields. Divergence is reported field by field and, when asked, repaired by
/// overwriting the row with the folded state (the ledger is the truth).
/// HIDDEN COUPLING with MergeUnitsHandler: a re-pointed absorbed fact has <c>merged_from_unit_id</c>. Its original delta
/// describes the alias, not the survivor, so replay advances through its seq but never folds that delta. Removing this
/// check makes an absorbed commissioning fact replace survivor state; omitting the fact loses reachable history.
/// </summary>
public sealed class UnitCurrentHealer(SerialisationDbContext db, ITenantContext tenant, ILogger<UnitCurrentHealer> logger)
{
    public sealed record Divergence(Guid UnitId, IReadOnlyList<string> Fields, bool RowMissing);

    /// <param name="UnitsChecked">Rows compared set-based plus units with events and no row.</param>
    /// <param name="UnitsFolded">Units replayed in full: stale, missing and sampled.</param>
    public sealed record Report(int UnitsChecked, IReadOnlyList<Divergence> Divergent, int Repaired, int UnitsFolded = 0, int Stale = 0);

    private const string StaleSql = """
        WITH live AS (SELECT unit_id, MAX(seq) AS seq FROM [serialisation].[unit_event] WHERE tenant_id = @tenant GROUP BY unit_id),
             arch AS (SELECT unit_id, MAX(seq) AS seq FROM [serialisation].[unit_event_archive] WHERE tenant_id = @tenant GROUP BY unit_id),
             ledger AS (SELECT COALESCE(l.unit_id, a.unit_id) AS unit_id, CASE WHEN ISNULL(l.seq, -1) > ISNULL(a.seq, -1) THEN l.seq ELSE a.seq END AS seq
                        FROM live AS l FULL JOIN arch AS a ON a.unit_id = l.unit_id)
        SELECT c.unit_id AS [Value]
        FROM [serialisation].[unit_current] AS c LEFT JOIN ledger AS g ON g.unit_id = c.unit_id
        WHERE c.tenant_id = @tenant AND (g.seq IS NULL OR g.seq <> c.last_event_seq)
        """;

    public async Task<Report> RunAsync(bool repair, int pageSize, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var divergent = new List<Divergence>();
        var repaired = 0;
        var folded = 0;

        // 1. Units with events but no current row: the anti-join runs on the covering unit index.
        var missing = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId)
            .Select(e => e.UnitId).Distinct()
            .Where(id => !db.Current.Any(c => c.TenantId == tenantId && c.UnitId == id))
            .ToListAsync(cancellationToken);
        foreach (var unitId in missing)
        {
            folded++;
            var row = await FoldAsync(tenantId, unitId, cancellationToken);
            if (row is null)
            {
                continue; // events without a creating delta: nothing to project (for example a pending identifier only)
            }

            divergent.Add(new Divergence(unitId, ["row_missing"], true));
            if (repair)
            {
                db.Current.Add(row);
                repaired++;
            }
        }

        // 2. Set-based: rows whose last_event_seq is not the ledger's newest seq for the unit.
        var compared = await db.Current.AsNoTracking().CountAsync(c => c.TenantId == tenantId, cancellationToken);
        var stale = await db.Database.SqlQueryRaw<Guid>(StaleSql, new Microsoft.Data.SqlClient.SqlParameter("@tenant", tenantId.Value)).ToListAsync(cancellationToken);

        // 3. A random window of rows whose seq agrees, folded in full: silent corruption of a field shows up here over the nights.
        var start = Guid.NewGuid();
        var sample = await db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && c.UnitId.CompareTo(start) > 0).OrderBy(c => c.UnitId).Take(pageSize).Select(c => c.UnitId).ToListAsync(cancellationToken);
        if (sample.Count < pageSize)
        {
            sample.AddRange(await db.Current.AsNoTracking().Where(c => c.TenantId == tenantId).OrderBy(c => c.UnitId).Take(pageSize - sample.Count).Select(c => c.UnitId).ToListAsync(cancellationToken));
        }

        var candidates = stale.Concat(sample).Distinct().ToList();
        foreach (var chunk in candidates.Chunk(Math.Max(1, pageSize)))
        {
            var rows = await db.Current.Where(c => c.TenantId == tenantId && chunk.Contains(c.UnitId)).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                folded++;
                var replayed = await FoldAsync(tenantId, row.UnitId, cancellationToken);
                if (replayed is null)
                {
                    divergent.Add(new Divergence(row.UnitId, ["no_creating_event"], false));
                    continue;
                }

                var fields = Diff(row, replayed);
                if (fields.Count == 0)
                {
                    continue;
                }

                divergent.Add(new Divergence(row.UnitId, fields, false));
                logger.LogWarning("unit_current diverges from the ledger for unit {Unit}: {Fields}", row.UnitId, string.Join(", ", fields));
                if (repair)
                {
                    Copy(replayed, row);
                    repaired++;
                }
            }

            if (repair)
            {
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        if (repair && missing.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        var checkedCount = compared + missing.Count;
        logger.LogInformation("Healer for tenant {Tenant}: {Checked} units compared, {Stale} stale by seq, {Folded} folded, {Divergent} divergent, {Repaired} repaired", tenantId, checkedCount, stale.Count, folded, divergent.Count, repaired);
        return new Report(checkedCount, divergent, repaired, folded, stale.Count);
    }

    /// <summary>Folds the unit's deltas in seq order into a fresh row; null when no event created current state.</summary>
    public async Task<UnitCurrent?> FoldAsync(TenantId tenantId, Guid unitId, CancellationToken cancellationToken)
    {
        var live = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId).Select(e => new { e.Seq, e.EventTime, e.Attributes, e.MergedFromUnitId }).ToListAsync(cancellationToken);
        var archived = await db.ArchivedEvents.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId).Select(e => new { e.Seq, e.EventTime, e.Attributes, e.MergedFromUnitId }).ToListAsync(cancellationToken);
        UnitCurrent? row = null;
        foreach (var e in live.Concat(archived).OrderBy(e => e.Seq))
        {
            // TIGHT COUPLING (state-neutral ledger facts): UnitEventWriter persists this marker and omits the same
            // fact from the live projection. Advancing it here would make the healer manufacture a rowversion write
            // which the command path deliberately avoids; change both sides together.
            if (!UnitCurrentStateProjector.ProjectsCurrentState(e.Attributes)) continue;
            var delta = e.MergedFromUnitId is not null || e.Attributes is null ? null : DeltaOf(e.Attributes);
            if (delta?.Create is { } create)
            {
                row = new UnitCurrent { TenantId = tenantId, UnitId = unitId, ItemId = create.ItemId, ModelVersionId = create.ModelVersionId, LotId = create.LotId, LifecycleStatus = create.LifecycleStatus, Disposition = create.Disposition };
            }

            if (row is null)
            {
                continue;
            }

            if (delta is not null)
            {
                UnitCurrentStateProjector.Fold(row, delta);
            }

            row.LastEventSeq = e.Seq;
            row.LastEventTime = e.EventTime;
        }

        // MergeUnits deliberately moves every pre-merge fact to the survivor. The alias therefore has no creating fact;
        // its retained current row is the readable alias projection and the merge fact is its ledger authority.
        if (row is null && live.Concat(archived).Any())
        {
            var alias = await db.Units.AsNoTracking().AnyAsync(u => u.TenantId == tenantId && u.UnitId == unitId && u.LifecycleStatus == Yojana.Serialisation.Contracts.LifecycleStatus.Merged, cancellationToken);
            if (alias)
            {
                row = await db.Current.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.UnitId == unitId, cancellationToken);
                if (row is not null)
                {
                    var last = live.Concat(archived).MaxBy(e => e.Seq)!;
                    row.LastEventSeq = last.Seq;
                    row.LastEventTime = last.EventTime;
                }
            }
        }

        return row;
    }

    private static CurrentStateDelta? DeltaOf(string attributes)
    {
        using var doc = JsonDocument.Parse(attributes);
        return doc.RootElement.TryGetProperty(UnitCurrentStateProjector.StateKey, out var state) ? state.Deserialize<CurrentStateDelta>(LedgerJson.Options) : null;
    }

    public static IReadOnlyList<string> Diff(UnitCurrent row, UnitCurrent folded)
    {
        var fields = new List<string>();
        void Check<T>(string name, T a, T b) { if (!EqualityComparer<T>.Default.Equals(a, b)) fields.Add(name); }
        Check("item_id", row.ItemId, folded.ItemId);
        Check("model_version_id", row.ModelVersionId, folded.ModelVersionId);
        Check("lot_id", row.LotId, folded.LotId);
        Check("lifecycle_status", row.LifecycleStatus, folded.LifecycleStatus);
        Check("disposition", row.Disposition, folded.Disposition);
        Check("condition_grade", row.ConditionGrade, folded.ConditionGrade);
        Check("location_id", row.LocationId, folded.LocationId);
        Check("location_kind", row.LocationKind, folded.LocationKind);
        Check("custody_party_ref", row.CustodyPartyRef, folded.CustodyPartyRef);
        Check("custody_kind", row.CustodyKind, folded.CustodyKind);
        Check("owner_party_ref", row.OwnerPartyRef, folded.OwnerPartyRef);
        Check("open_ownership_episode_id", row.OpenOwnershipEpisodeId, folded.OpenOwnershipEpisodeId);
        Check("open_custody_episode_id", row.OpenCustodyEpisodeId, folded.OpenCustodyEpisodeId);
        Check("live_entitlement_ids", Canonical(row.LiveEntitlementIds), Canonical(folded.LiveEntitlementIds));
        Check("warranty_end_max", row.WarrantyEndMax, folded.WarrantyEndMax);
        Check("overlay_flags", Canonical(row.OverlayFlags), Canonical(folded.OverlayFlags));
        Check("last_event_seq", row.LastEventSeq, folded.LastEventSeq);
        Check("last_event_time", row.LastEventTime, folded.LastEventTime);
        Check("attributes", Canonical(row.Attributes), Canonical(folded.Attributes));
        return fields;
    }

    private static string? Canonical(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    private static void Copy(UnitCurrent from, UnitCurrent to)
    {
        to.ItemId = from.ItemId; to.ModelVersionId = from.ModelVersionId; to.LotId = from.LotId;
        to.LifecycleStatus = from.LifecycleStatus; to.Disposition = from.Disposition; to.ConditionGrade = from.ConditionGrade;
        to.LocationId = from.LocationId; to.LocationKind = from.LocationKind; to.CustodyPartyRef = from.CustodyPartyRef; to.CustodyKind = from.CustodyKind;
        to.OwnerPartyRef = from.OwnerPartyRef; to.OpenOwnershipEpisodeId = from.OpenOwnershipEpisodeId; to.OpenCustodyEpisodeId = from.OpenCustodyEpisodeId;
        to.LiveEntitlementIds = from.LiveEntitlementIds; to.WarrantyEndMax = from.WarrantyEndMax; to.OverlayFlags = from.OverlayFlags;
        to.LastEventSeq = from.LastEventSeq; to.LastEventTime = from.LastEventTime; to.Attributes = from.Attributes;
    }
}
