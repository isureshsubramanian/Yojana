import { useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router";
import { DataGrid, KpiStrip, useGridQuery, type GridColumn } from "../components";
import { useSession } from "../session/session";
import { ErrorNote } from "../units/common";
import { MigrationAccess, MigrationPage } from "./MigrationCommon";
import { migrationApi } from "./migrationApi";
import { refusalReasonLabel } from "./migrationModel";
import { MigrationRankedList } from "./MigrationRankedList";
import { migrationApplyRefusalPage, type MigrationApplyRefusal } from "./migrationTypes";

const pageSize = 50;
type RefusalFilters = { reason: string; itemCode: string; serial: string };
const emptyFilters: RefusalFilters = { reason: "", itemCode: "", serial: "" };

export function Component() { return <MigrationAccess><MigrationRefusals /></MigrationAccess>; }

function MigrationRefusals() {
  const loadId = useParams().loadId ?? "";
  const session = useSession();
  const [draft, setDraft] = useState<RefusalFilters>(emptyFilters);
  const [filters, setFilters] = useState<RefusalFilters>(emptyFilters);
  const filterParams = useMemo(() => { const value = new URLSearchParams(); if (filters.reason) value.set("reason", filters.reason); if (filters.itemCode) value.set("itemCode", filters.itemCode); if (filters.serial) value.set("serial", filters.serial); return value; }, [filters]);
  const query = useGridQuery(`/api/migration/loads/${loadId}/refusals`, migrationApplyRefusalPage, {
    gridId: "migration-load-refusals", userId: session.user.id,
    defaultSort: { key: "load_order", dir: "asc" }, defaultSize: pageSize, params: filterParams,
    paging: "cursor", sortKeys: ["load_order", "item"], enabled: !!loadId,
    pageFacts: value => ({ continuation: value.nextCursor }),
  });
  const result = query.data;

  // Typing searches: the draft becomes the server filter once typing pauses, so a serial is found without an Apply press or an export.
  useEffect(() => {
    const next = { reason: draft.reason, itemCode: draft.itemCode.trim(), serial: draft.serial.trim() };
    if (next.reason === filters.reason && next.itemCode === filters.itemCode && next.serial === filters.serial) return;
    const timer = setTimeout(() => setFilters(next), 300);
    return () => clearTimeout(timer);
  }, [draft, filters]);
  const clearFilters = () => { setDraft(emptyFilters); setFilters(emptyFilters); };

  const columns = useMemo<GridColumn<MigrationApplyRefusal>[]>(() => [
    { key: "item", label: "Item code", width: 22, mono: true, sort: { key: "item", defaultDir: "asc" }, render: row => row.itemCode },
    { key: "serial", label: "Serial", width: 27, mono: true, render: row => row.serial ?? "No serial recorded" },
    { key: "reasons", label: "Why it was refused", width: 51, render: row => row.reasons.map(refusalReasonLabel).join(" · ") },
  ], []);
  const exportUrl = migrationApi.loadRefusalsExportUrl(loadId, { ...filters, sort: query.sort.key, dir: query.sort.dir });
  const kpis = [
    { label: "Total refused", value: (result?.loadTotal ?? 0).toLocaleString(), detail: result && result.total !== result.loadTotal ? `${result.total.toLocaleString()} match the filters` : "in this load" },
    { label: "Reasons", value: (result?.countsByReason.length ?? 0).toLocaleString(), detail: "a unit can carry more than one" },
  ];
  const showReason = (reason: string) => { const next = { ...filters, reason }; setDraft(next); setFilters(next); };

  return <MigrationPage title="Refused units" eyebrow="Migration load · units not written" sub="Find every refused serial, see the full blocking reason, and export the filtered result." actions={<><Link className="btn" to="/integration/migration">Back to console</Link></>}>
    <KpiStrip label="Refusal counts" kpis={kpis} />
    <MigrationRankedList headingId="migration-refusal-reasons" title="Refused by reason" meta="whole load · largest first · share of all refused units · select a reason to filter" rows={(result?.countsByReason ?? []).map(row => ({ key: row.reason, label: row.label, count: row.count }))} total={result?.loadTotal ?? 0} selected={filters.reason} onSelect={showReason} />
    <ErrorNote error={query.error} />
    <section className="card migration-refusals-card" aria-labelledby="migration-refusals"><div className="card-header"><h2 id="migration-refusals">Refused units <span className="meta">{(result?.total ?? 0).toLocaleString()} · search, or export what is shown · keyset paged · reasons shown in full</span></h2><a className="btn sm" href={exportUrl} download>Export filtered CSV</a></div><form className="toolbar migration-refusal-filters migration-findings-search" role="search" aria-label="Search refused units" onSubmit={event => event.preventDefault()}><label className="field"><span className="lbl">Serial contains</span><input className="input" type="search" value={draft.serial} onChange={event => setDraft(current => ({ ...current, serial: event.target.value }))} placeholder="Type part of a serial" aria-describedby="migration-refusal-search-hint" /></label><label className="field"><span className="lbl">Reason</span><select className="select" value={draft.reason} onChange={event => setDraft(current => ({ ...current, reason: event.target.value }))}><option value="">Any reason</option>{result?.countsByReason.map(row => <option key={row.reason} value={row.reason}>{row.label} · {row.count.toLocaleString()}</option>)}</select></label><label className="field"><span className="lbl">Item code</span><input className="input" value={draft.itemCode} onChange={event => setDraft(current => ({ ...current, itemCode: event.target.value }))} placeholder="Exact item code" /></label><button type="button" className="btn ghost" disabled={query.loading || (!draft.reason && !draft.itemCode && !draft.serial && !filters.reason && !filters.itemCode && !filters.serial)} onClick={clearFilters}>Clear</button><span className="muted" id="migration-refusal-search-hint" role="status">{(filters.reason || filters.itemCode || filters.serial) ? `${(result?.total ?? 0).toLocaleString()} of ${(result?.loadTotal ?? 0).toLocaleString()} refused units match` : "Results update as you type"}</span></form><DataGrid columns={columns} page={{ rows: result?.items ?? [], total: result?.total ?? 0, page: query.page, pageSize: query.pageSize, continuation: result?.nextCursor ?? null, totalIsExact: true, hasMore: !!result?.nextCursor }} rowId={row => row.refusalId} onPage={query.onPage} loading={query.loading} emptyText="No refused units match these filters." viewportHeight={12_000}
      gridId="migration-load-refusals" userId={session.user.id} sort={query.sort} defaultSort={{ key: "load_order", dir: "asc" }} onSort={query.onSort}
      pageSize={query.pageSize} defaultPageSize={pageSize} onPageSize={query.onPageSize} footerNote="Sorted in load order" /></section>
  </MigrationPage>;
}
