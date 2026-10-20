import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { PartyField, useItemFamilies } from "../corrections/ReferenceFields";
import { ExportButtons } from "../p7/Common";
import { openDrawer } from "../shell/drawerStore";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { fmtDate } from "../units/format";
import { coverApi } from "./coverApi";
import { changeLabel, coverChangeLabel, coverKinds, coverQuery, moneyLabel } from "./coverModel";
import { CoverSinceSalePanel } from "./CoverSinceSalePanel";
import type { CoverFilters, CoverPage, CoverSummary, CoverUnit } from "./coverTypes";

export function Component() {
  const [filters, setFilters] = useState<CoverFilters>({});
  const [cursorStack, setCursorStack] = useState<(string | null)[]>([null]);
  const [pageNumber, setPageNumber] = useState(1);
  const [data, setData] = useState<CoverPage | null>(null);
  const [summary, setSummary] = useState<CoverSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [active, setActive] = useState<string | null>(null);
  const families = useItemFamilies();
  const cursor = cursorStack[pageNumber - 1] ?? null;
  const query = useMemo(() => coverQuery(filters), [filters]);
  useEffect(() => { const controller = new AbortController(); setLoading(true); setError(null); coverApi.page(filters, cursor, controller.signal).then((value) => { setData(value); setLoading(false); }).catch((e) => { if (!controller.signal.aborted) { setError(e); setLoading(false); } }); return () => controller.abort(); }, [filters, cursor]);
  useEffect(() => { const controller = new AbortController(); setSummary(null); coverApi.summary(filters, controller.signal).then(setSummary).catch((e) => { if (!controller.signal.aborted) setError(e); }); return () => controller.abort(); }, [filters]);
  const filter = (key: keyof CoverFilters, value: string | null) => { setFilters((old) => ({ ...old, [key]: value || undefined })); setPageNumber(1); setCursorStack([null]); setData(null); };
  const page = (next: number) => { if (next > pageNumber) { if (!data?.nextCursor) return; setCursorStack((old) => [...old.slice(0, pageNumber), data.nextCursor]); } setPageNumber(next); };
  const open = (row: CoverUnit) => { setActive(row.unitId); openDrawer({ eyebrow: "Cover since sale", title: row.serial, mono: true }, <><p>{row.itemName} · {row.customerName ?? "Customer not recorded"}</p><CoverSinceSalePanel unitId={row.unitId} compact /><div className="act"><Link className="btn primary" to={`/units/${row.unitId}?tab=cover`}>Open cover since sale</Link></div></>); };
  const columns: GridColumn<CoverUnit>[] = [
    { key: "serial", label: "Serial · item", width: 20, render: (r) => <><span className="serial">{r.serial}</span><span className="sub">{r.itemName}</span></> },
    { key: "customer", label: "Customer", width: 17, render: (r) => r.customerName ?? "Not recorded" },
    { key: "sold", label: "Sold · document", width: 15, render: (r) => <>{fmtDate(r.saleDate)}<span className="sub">{r.saleDocumentNumber}</span></> },
    { key: "changes", label: "Changes since sale", width: 24, render: (r) => <div className="cover-chips" title={r.changes.map(coverChangeLabel).join(" · ")}>{r.changes.map((change) => <StatusBadge key={`${change.kind}:${change.familyName}:${change.policyName}`} tone={change.kind === "added" ? "info" : "warn"}>{coverChangeLabel(change)}</StatusBadge>)}<span className="sub">{r.changeCount} changes</span></div> },
    { key: "value", label: "Value added", width: 14, numeric: true, render: (r) => moneyLabel(r.value) },
    { key: "last", label: "Last change", width: 10, render: (r) => fmtDate(r.lastChangeAt) },
  ];
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Entitlements", to: "/entitlements" }, { label: "Cover changes since sale" }]} />
    <div className="page-header"><div><div className="eyebrow">Entitlements · coverage deviation report</div><h1>Cover changes since sale{summary ? `: ${summary.unitsChanged.toLocaleString()} units` : ""}</h1><p className="sub">Cover bought later, upgraded, amended, suspended or closed early, compared with the original sale. Every change keeps its document and value.</p></div><div className="actions"><ExportButtons path={`/api/entitlements/cover-changes/export?${query}`} disabled={!summary || summary.changes > 100000} /></div></div>
    <nav className="tabs" aria-label="Entitlements"><Link to="/entitlements">Gap report</Link><Link to="/entitlements/policies">Policies</Link><Link to="/entitlements/bulk">Bulk changes</Link><Link to="/entitlements/cover-changes" aria-current="page">Cover changes since sale</Link></nav>
    <ErrorNote error={error} />
    <div className="stack">
      <section className="card"><div className="card-body cover-filters"><label className="field">Sold from<input className="input" type="date" value={filters.soldFrom ?? ""} onChange={(e) => filter("soldFrom", e.target.value)} /></label><label className="field">Sold to<input className="input" type="date" value={filters.soldTo ?? ""} onChange={(e) => filter("soldTo", e.target.value)} /></label><PartyField id="cover-customer" label="Customer" value={filters.customer ?? null} onChange={(value) => filter("customer", value)} optional /><label className="field">Item family<select className="select" value={filters.itemFamily ?? ""} onChange={(e) => filter("itemFamily", e.target.value)}><option value="">All item families</option>{families.options.map((f) => <option key={f.code} value={f.code}>{f.name}</option>)}</select></label><label className="field">Change<select className="select" value={filters.changeKind ?? ""} onChange={(e) => filter("changeKind", e.target.value)}><option value="">Any change</option>{coverKinds.map((k) => <option key={k} value={k}>{changeLabel(k)}</option>)}</select></label><label className="field">Serial<input className="input" type="search" placeholder="Serial starts with…" value={filters.serial ?? ""} onChange={(e) => filter("serial", e.target.value)} /></label></div></section>
      <KpiStrip kpis={[
        { label: "Units sold in period", value: summary?.unitsSold.toLocaleString() ?? "…", detail: "Serial-tracked, with a sale baseline", trend: null },
        { label: "Cover changed since sale", value: summary?.unitsChanged.toLocaleString() ?? "…", detail: summary?.unitsSold ? `${(summary.unitsChanged / summary.unitsSold * 100).toFixed(1)}% of units sold` : "Of units with a sale baseline", trend: null },
        { label: "Bought after the sale", value: summary ? moneyLabel(summary.value) : "…", detail: `${summary?.changes.toLocaleString() ?? "…"} changes · values by currency`, trend: null },
        { label: "Typical time to buy", value: summary ? summary.medianMonthsToFirstPurchase == null ? "—" : `${summary.medianMonthsToFirstPurchase} months` : "…", detail: "Median from sale to first priced add-on", trend: null },
        { label: "Closed early", value: summary ? String(summary.byKind.find((k) => k.kind === "closed_early")?.units ?? 0) : "…", detail: "Refunds, cancellations and voids", trend: null },
      ]} />
      <section className="card"><div className="card-header"><h2>What changed</h2><span className="meta">A unit counts once per kind · median over changes</span></div><table className="table dense"><thead><tr><th>Change</th><th className="num">Units</th><th className="num">Median months after sale</th><th className="num">Value added</th></tr></thead><tbody>{summary?.byKind.map((k) => <tr key={k.kind}><td className="primary">{changeLabel(k.kind)}</td><td className="num">{k.units.toLocaleString()}</td><td className="num">{k.medianMonthsAfterSale ?? "—"}</td><td className="num">{moneyLabel(k.value)}</td></tr>)}{summary?.byKind.length === 0 && <tr><td colSpan={4} className="muted">No changes match these filters.</td></tr>}</tbody></table></section>
      <section className="card"><div className="card-header"><h2>Units</h2><span className="meta">Latest change first · select a row for its cover history</span></div><DataGrid columns={columns} page={{ rows: data?.rows ?? [], total: summary?.unitsChanged ?? 0, page: pageNumber, pageSize: 25, continuation: data?.nextCursor ?? null, totalIsExact: false, hasMore: data?.nextCursor != null }} rowId={(r) => r.unitId} onPage={page} onRowClick={open} activeRowId={active} loading={loading} emptyText="No cover changes match these filters." footerNote="Export carries every counted change as its own row" /></section>
      {(summary?.changes ?? 0) > 100000 && <p className="hint">Narrow the filters to export no more than 100,000 changes.</p>}
    </div>
  </main>;
}
