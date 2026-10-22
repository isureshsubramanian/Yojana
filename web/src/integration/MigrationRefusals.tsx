import { type FormEvent, useCallback, useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router";
import { DataGrid, KpiStrip, type GridColumn } from "../components";
import { ErrorNote } from "../units/common";
import { MigrationAccess, MigrationPage } from "./MigrationCommon";
import { migrationApi } from "./migrationApi";
import { refusalReasonLabel } from "./migrationModel";
import { MigrationRankedList } from "./MigrationRankedList";
import type { MigrationApplyRefusal, MigrationApplyRefusalPage } from "./migrationTypes";

const pageSize = 50;
type RefusalFilters = { reason: string; itemCode: string; serial: string };
const emptyFilters: RefusalFilters = { reason: "", itemCode: "", serial: "" };

export function Component() { return <MigrationAccess><MigrationRefusals /></MigrationAccess>; }

function MigrationRefusals() {
  const loadId = useParams().loadId ?? "";
  const [draft, setDraft] = useState<RefusalFilters>(emptyFilters);
  const [filters, setFilters] = useState<RefusalFilters>(emptyFilters);
  const [result, setResult] = useState<MigrationApplyRefusalPage | null>(null);
  const [page, setPage] = useState(1); const [cursors, setCursors] = useState<(string | undefined)[]>([undefined]);
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);

  const loadPage = useCallback(async (target: number, cursor?: string, active = filters) => {
    if (!loadId) return;
    setBusy(true); setError(null);
    try {
      const response = await migrationApi.loadRefusals(loadId, { ...active, cursor });
      setResult(response); setPage(target);
    } catch (reason) { setError(reason); }
    finally { setBusy(false); }
  }, [filters, loadId]);
  useEffect(() => { void loadPage(1); }, [loadPage]);

  const applyFilters = (event: FormEvent) => {
    event.preventDefault();
    const next = { reason: draft.reason, itemCode: draft.itemCode.trim(), serial: draft.serial.trim() };
    setCursors([undefined]); setPage(1); setFilters(next);
  };
  const clearFilters = () => { setDraft(emptyFilters); setCursors([undefined]); setPage(1); setFilters(emptyFilters); };
  const go = (target: number) => {
    const cursor = target > page ? result?.nextCursor ?? undefined : cursors[target - 1];
    if (target < 1 || (target > page && !cursor)) return;
    setCursors(current => { const next = current.slice(); next[target - 1] = cursor; return next; });
    void loadPage(target, cursor);
  };

  const columns = useMemo<GridColumn<MigrationApplyRefusal>[]>(() => [
    { key: "item", label: "Item code", width: 22, mono: true, render: row => row.itemCode },
    { key: "serial", label: "Serial", width: 27, mono: true, render: row => row.serial ?? "No serial recorded" },
    { key: "reasons", label: "Why it was refused", width: 51, render: row => row.reasons.map(refusalReasonLabel).join(" · ") },
  ], []);
  const exportUrl = migrationApi.loadRefusalsExportUrl(loadId, filters);
  const kpis = [
    { label: "Total refused", value: (result?.loadTotal ?? 0).toLocaleString(), detail: result && result.total !== result.loadTotal ? `${result.total.toLocaleString()} match the filters` : "in this load" },
    { label: "Reasons", value: (result?.countsByReason.length ?? 0).toLocaleString(), detail: "a unit can carry more than one" },
  ];
  const showReason = (reason: string) => { const next = { ...filters, reason }; setDraft(next); setCursors([undefined]); setPage(1); setFilters(next); };

  return <MigrationPage title="Refused units" eyebrow="Migration load · units not written" sub="Find every refused serial, see the full blocking reason, and export the filtered result." actions={<><Link className="btn" to="/integration/migration">Back to console</Link></>}>
    <KpiStrip label="Refusal counts" kpis={kpis} />
    <MigrationRankedList headingId="migration-refusal-reasons" title="Refused by reason" meta="whole load · largest first · share of all refused units · select a reason to filter" rows={(result?.countsByReason ?? []).map(row => ({ key: row.reason, label: row.label, count: row.count }))} total={result?.loadTotal ?? 0} selected={filters.reason} onSelect={showReason} />
    <section className="card" aria-labelledby="migration-refusal-filters"><div className="card-header"><h2 id="migration-refusal-filters">Filters <span className="meta">server-side · exact total</span></h2></div><div className="card-body"><form className="toolbar migration-refusal-filters" role="search" aria-label="Refused unit filters" onSubmit={applyFilters}>
      <label className="field"><span className="lbl">Reason</span><select className="select" value={draft.reason} onChange={event => setDraft(current => ({ ...current, reason: event.target.value }))}><option value="">Any reason</option>{result?.countsByReason.map(row => <option key={row.reason} value={row.reason}>{row.label} · {row.count.toLocaleString()}</option>)}</select></label>
      <label className="field"><span className="lbl">Item code</span><input className="input" value={draft.itemCode} onChange={event => setDraft(current => ({ ...current, itemCode: event.target.value }))} placeholder="Exact item code" /></label>
      <label className="field"><span className="lbl">Serial contains</span><input className="input" type="search" value={draft.serial} onChange={event => setDraft(current => ({ ...current, serial: event.target.value }))} placeholder="Search serial" /></label>
      <button className="btn" disabled={busy}>Apply filters</button><button type="button" className="btn ghost" disabled={busy || (!draft.reason && !draft.itemCode && !draft.serial && !filters.reason && !filters.itemCode && !filters.serial)} onClick={clearFilters}>Clear</button>
    </form></div></section>
    <ErrorNote error={error} />
    <section className="card migration-refusals-card" aria-labelledby="migration-refusals"><div className="card-header"><h2 id="migration-refusals">Refused units <span className="meta">keyset paged · reasons shown in full</span></h2><a className="btn sm" href={exportUrl} download>Export filtered CSV</a></div><DataGrid columns={columns} page={{ rows: result?.items ?? [], total: result?.total ?? 0, page, pageSize, continuation: result?.nextCursor ?? null, totalIsExact: true, hasMore: !!result?.nextCursor }} rowId={row => row.refusalId} onPage={go} loading={busy} emptyText="No refused units match these filters." viewportHeight={12_000} /></section>
  </MigrationPage>;
}
