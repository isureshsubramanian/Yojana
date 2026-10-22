import { type FormEvent, useCallback, useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { useSession } from "../session/session";
import { ErrorNote } from "../units/common";
import { date } from "./AdminCommon";
import { MigrationAccess, MigrationPage } from "./MigrationCommon";
import { migrationApi } from "./migrationApi";
import {
  deltaKindTone, duration, loadKindLabel, loadStatusLabel, loadStatusTone, mayAdministerMigration, migrationTimestamp,
} from "./migrationModel";
import { MigrationUndoPanel, UndoLoadButton, useMigrationLoadUndo } from "./MigrationLoadUndo";
import { MigrationRankedList } from "./MigrationRankedList";
import type { MigrationDeltaFinding, MigrationDeltaFindingPage, MigrationLoadDetail } from "./migrationTypes";

const pageSize = 100;
type DeltaFilters = { kind: string; itemCode: string; serial: string };
const emptyFilters: DeltaFilters = { kind: "", itemCode: "", serial: "" };

export function Component() { return <MigrationAccess><MigrationDelta /></MigrationAccess>; }

function MigrationDelta() {
  const loadId = useParams().loadId ?? "";
  const admin = mayAdministerMigration(useSession().roles);
  const [load, setLoad] = useState<MigrationLoadDetail | null>(null);
  const [draft, setDraft] = useState<DeltaFilters>(emptyFilters);
  const [filters, setFilters] = useState<DeltaFilters>(emptyFilters);
  const [result, setResult] = useState<MigrationDeltaFindingPage | null>(null);
  const [page, setPage] = useState(1); const [cursors, setCursors] = useState<(string | undefined)[]>([undefined]);
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);
  const [detailError, setDetailError] = useState<unknown>(null);

  const loadDetail = useCallback(async (signal?: AbortSignal) => {
    if (!loadId) return;
    try {
      const response = await migrationApi.loadSummary(loadId, signal);
      if (signal?.aborted) return;
      setLoad(response); setDetailError(null);
    } catch (reason) { if (!(reason instanceof DOMException && reason.name === "AbortError")) setDetailError(reason); }
  }, [loadId]);
  useEffect(() => { const controller = new AbortController(); void loadDetail(controller.signal); return () => controller.abort(); }, [loadDetail]);

  const loadPage = useCallback(async (target: number, cursor?: string, active = filters) => {
    if (!loadId) return;
    setBusy(true); setError(null);
    try { const response = await migrationApi.deltaFindings(loadId, { ...active, cursor }); setResult(response); setPage(target); }
    catch (reason) { setError(reason); }
    finally { setBusy(false); }
  }, [filters, loadId]);
  useEffect(() => { void loadPage(1); }, [loadPage]);

  const applyFilters = (event: FormEvent) => {
    event.preventDefault();
    const next = { kind: draft.kind, itemCode: draft.itemCode.trim(), serial: draft.serial.trim() };
    setCursors([undefined]); setPage(1); setFilters(next);
  };
  const clearFilters = () => { setDraft(emptyFilters); setCursors([undefined]); setPage(1); setFilters(emptyFilters); };
  const showKind = (kind: string) => { const next = { ...filters, kind }; setDraft(next); setCursors([undefined]); setPage(1); setFilters(next); };
  const go = (target: number) => {
    const cursor = target > page ? result?.nextCursor ?? undefined : cursors[target - 1];
    if (target < 1 || (target > page && !cursor)) return;
    setCursors(current => { const next = current.slice(); next[target - 1] = cursor; return next; });
    void loadPage(target, cursor);
  };

  const undo = useMigrationLoadUndo(async () => { await loadDetail(); await loadPage(1); });
  const columns = useMemo<GridColumn<MigrationDeltaFinding>[]>(() => [
    { key: "serial", label: "Serial", width: 17, mono: true, render: row => row.serial ?? "No serial recorded" },
    { key: "item", label: "Item", width: 14, mono: true, render: row => row.itemCode ?? "—" },
    { key: "kind", label: "Kind", width: 16, render: row => <StatusBadge tone={deltaKindTone(row.kind)}>{row.kindLabel}</StatusBadge> },
    { key: "detail", label: "What happened", width: 43, render: row => row.detail ?? "No further detail recorded." },
    // No coexistence-conflict screen exists yet: an owner difference links to its unit and says a conflict is open.
    { key: "link", label: "", width: 10, render: row => <>{row.unitId ? <Link to={`/units/${row.unitId}`}>Open unit</Link> : null}
      {row.kind === "owner_differs" && row.conflictId ? <span className="sub">Conflict open</span> : null}</> },
  ], []);

  const exportFilters = { kind: filters.kind, itemCode: filters.itemCode, serial: filters.serial };
  const actions = <><Link className="btn" to="/integration/migration">Back to console</Link>{admin && load && <UndoLoadButton load={load} hideWhenDisabled onRequest={undo.request} />}</>;
  const filtered = !!(filters.kind || filters.itemCode || filters.serial);
  const extraction = load?.batchStartedAt ? `${date(load.batchStartedAt)}${load.batchFinishedAt ? `–${date(load.batchFinishedAt)}` : ""}` : "Extraction time not recorded";

  return <MigrationPage title={load ? `Delta load ${load.label} · findings` : "Delta load · findings"} eyebrow={`Migration load · delta · ${(result?.loadTotal ?? 0).toLocaleString()} findings`} sub="Every serial the delta load looked at differently from the final load, what happened to it, and why. Reasons are shown in full." actions={actions}>
    <ErrorNote error={detailError} />
    {load && <section className="card migration-delta-header" aria-labelledby="migration-delta-load"><div className="card-header"><h2 id="migration-delta-load">{load.label} <span className="meta">{loadKindLabel(load.kind).toLowerCase()} load · {loadStatusLabel(load.status).toLowerCase()}</span></h2><StatusBadge tone={loadStatusTone(load.status)}>{loadStatusLabel(load.status)}</StatusBadge></div><div className="card-body migration-delta-kv">
      <span className="k">Load</span><span className="v mono">{load.label}</span><span className="k">Base load</span><span className="v">{load.baseLabel ? <span className="mono">{load.baseLabel}</span> : "—"}</span>
      <span className="k">Batch</span><span className="v"><span className="mono">{load.batchDisplayNumber}</span> · extracted {extraction}</span><span className="k">Started by</span><span className="v">{load.requestedByName}</span>
      <span className="k">Started</span><span className="v">{load.startedAt ? migrationTimestamp(load.startedAt) : "—"}</span><span className="k">Finished</span><span className="v">{load.finishedAt ? `${migrationTimestamp(load.finishedAt)} · ${load.startedAt ? duration(new Date(load.finishedAt).getTime() - new Date(load.startedAt).getTime()) : ""}` : "—"}</span>
    </div></section>}
    <KpiStrip label="Delta findings summary" kpis={[
      { label: "Findings", value: (result?.loadTotal ?? 0).toLocaleString(), detail: filtered ? `${(result?.total ?? 0).toLocaleString()} match the filters` : "in this load" },
      { label: "Undo", value: load?.undo.allowed ? "Allowed" : "Not available", detail: load?.undo.allowed ? `Undo ${load.label} while it is the latest` : load?.undo.reason ?? "Load details unavailable" },
    ]} />
    <MigrationRankedList headingId="migration-delta-kinds" title="Findings by kind" meta="whole load · largest first · share of all findings · select a kind to filter" rows={(result?.counts ?? []).map(row => ({ key: row.kind, label: row.label, count: row.count }))} total={result?.loadTotal ?? 0} selected={filters.kind} onSelect={showKind} />
    {load && <div className="note"><strong>Owner differences are coexistence conflicts.</strong>&nbsp;When the source names a different owner and no delivery, return or transfer document explains it, the load never writes the owner: the unit keeps its owner in the module and the difference goes to Coexistence conflicts for a person to decide. <strong>Undoing {load.label}</strong> removes the {load.applied.toLocaleString()} units it wrote and restores the {load.coverReplaced.toLocaleString()} cover rows it replaced; refused, owner and gone findings change nothing, so there is nothing to undo for them.</div>}
    <MigrationUndoPanel undo={undo} />
    <section className="card" aria-labelledby="migration-delta-filters"><div className="card-header"><h2 id="migration-delta-filters">Filters <span className="meta">server-side · exact total</span></h2></div><div className="card-body"><form className="toolbar migration-refusal-filters" role="search" aria-label="Delta finding filters" onSubmit={applyFilters}>
      <label className="field"><span className="lbl">Kind</span><select className="select" value={draft.kind} onChange={event => setDraft(current => ({ ...current, kind: event.target.value }))}><option value="">Any kind · {(result?.loadTotal ?? 0).toLocaleString()}</option>{result?.counts.map(row => <option key={row.kind} value={row.kind}>{row.label} · {row.count.toLocaleString()}</option>)}</select></label>
      <label className="field"><span className="lbl">Item code</span><input className="input mono" value={draft.itemCode} onChange={event => setDraft(current => ({ ...current, itemCode: event.target.value }))} placeholder="Exact item code" /></label>
      <label className="field"><span className="lbl">Serial contains</span><input className="input" type="search" value={draft.serial} onChange={event => setDraft(current => ({ ...current, serial: event.target.value }))} placeholder="Search serial" /></label>
      <button className="btn" disabled={busy}>Apply filters</button><button type="button" className="btn ghost" disabled={busy || (!draft.kind && !draft.itemCode && !draft.serial && !filtered)} onClick={clearFilters}>Clear</button>
    </form></div></section>
    <ErrorNote error={error} />
    <section className="card migration-refusals-card migration-delta-findings" aria-labelledby="migration-delta-findings"><div className="card-header"><h2 id="migration-delta-findings">Findings <span className="meta">{(result?.total ?? 0).toLocaleString()} · keyset paged · reasons in full</span></h2><span className="migration-export-actions" role="group" aria-label="Export filtered findings"><a className="btn sm" href={migrationApi.deltaExportUrl(loadId, "csv", exportFilters)} download>CSV</a><a className="btn sm" href={migrationApi.deltaExportUrl(loadId, "xlsx", exportFilters)} download>Excel</a></span></div><DataGrid columns={columns} page={{ rows: result?.items ?? [], total: result?.total ?? 0, page, pageSize, continuation: result?.nextCursor ?? null, totalIsExact: true, hasMore: !!result?.nextCursor }} rowId={row => row.itemId} onPage={go} loading={busy} emptyText="No delta findings match these filters." viewportHeight={12_000} /></section>
  </MigrationPage>;
}
