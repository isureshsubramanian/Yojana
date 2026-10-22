import { useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { openDrawer } from "../shell/drawerStore";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section } from "../units/common";
import { fmtDate, fmtDateTime, humanise, shortId } from "../units/format";
import { entitlementApi } from "./api";
import { gapCause } from "./format";
import type { GapPage, GapRow, GapRun } from "./types";

const PAGE = 50;

export function Component() {
  const [searchParams] = useSearchParams();
  const [itemIds, setItemIds] = useState("");
  const [saleFrom, setSaleFrom] = useState("");
  const [saleTo, setSaleTo] = useState("");
  const [channels, setChannels] = useState(() => searchParams.get("channels") ?? "");
  const [dropShip, setDropShip] = useState("include");
  const [requirementStatus, setRequirementStatus] = useState("");
  const [sort, setSort] = useState("sale_date_desc");
  const [page, setPage] = useState(1);
  const [data, setData] = useState<GapPage | null>(null);
  const [lastRun, setLastRun] = useState<GapRun | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [active, setActive] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const filters = useMemo(() => ({ items: itemIds || undefined, saleFrom: saleFrom || undefined, saleTo: saleTo || undefined, channels: channels || undefined, dropShip, requirementStatus: requirementStatus || undefined, sort, skip: (page - 1) * PAGE, take: PAGE }), [itemIds, saleFrom, saleTo, channels, dropShip, requirementStatus, sort, page]);
  const exportFilters = useMemo(() => ({ items: itemIds || undefined, saleFrom: saleFrom || undefined, saleTo: saleTo || undefined, channels: channels || undefined, dropShip, requirementStatus: requirementStatus || undefined, sort }), [itemIds, saleFrom, saleTo, channels, dropShip, requirementStatus, sort]);

  useEffect(() => {
    const controller = new AbortController(); setLoading(true); setError(null);
    entitlementApi.gaps(filters, controller.signal).then((result) => { setData(result); setLoading(false); }).catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) { setError(reason); setLoading(false); } });
    return () => controller.abort();
  }, [filters]);
  useEffect(() => { const controller = new AbortController(); entitlementApi.latestGapRun(controller.signal).then(setLastRun).catch(() => undefined); return () => controller.abort(); }, [data]);

  const rows = data?.rows ?? [];
  const selectedRows = rows.filter((row) => selected.has(row.unitId));
  const backfillSerials = selectedRows.map((row) => row.primarySerial).filter((value): value is string => !!value).join(",");
  const dropShipCount = lastRun?.counts.filter((row) => row.dropShip).reduce((sum, row) => sum + row.count, 0) ?? rows.filter((row) => row.dropShip).length;
  const exceptionCount = rows.filter((row) => row.requirementStatus === "exception").length;
  const columns: GridColumn<GapRow>[] = [
    { key: "serial", label: "Serial · item", width: 22, mono: true, render: (row) => <>{row.primarySerial ?? shortId(row.unitId)}<span className="sub" style={{ fontFamily: "var(--font-body)" }}>{shortId(row.itemId, 12)}</span></> },
    { key: "sold", label: "Sale date", width: 14, render: (row) => fmtDate(row.saleDate) },
    { key: "customer", label: "Customer", width: 17, mono: true, render: (row) => shortId(row.customerPartyRef, 12) },
    { key: "channel", label: "Channel", width: 13, render: (row) => humanise(row.channel) },
    { key: "drop", label: "Drop-ship", width: 10, render: (row) => <StatusBadge tone={row.dropShip ? "info" : "neutral"}>{row.dropShip ? "Yes" : "No"}</StatusBadge> },
    { key: "cause", label: "Requirement · cause", width: 24, render: (row) => <>{humanise(row.requirementStatus) || "No requirement"}<span className="sub">{humanise(gapCause(row))}</span></> },
  ];
  const openRow = (row: GapRow) => { setActive(row.unitId); openDrawer({ eyebrow: `Gap · ${shortId(row.itemId)} · ${humanise(row.channel)}`, title: row.primarySerial ?? shortId(row.unitId), mono: true }, <><h3>Ownership episode</h3><Kv rows={[["Sold", fmtDateTime(row.saleDate)], ["Customer", shortId(row.customerPartyRef, 12)], ["Channel", humanise(row.channel)], ["Drop-ship", row.dropShip ? "yes" : "no"], ["Unit", shortId(row.unitId, 12)]]} /><h3 style={{ marginTop: 16 }}>Why no entitlement</h3><div className="note bad"><strong>{humanise(gapCause(row))}.</strong>&nbsp;The latest customer ownership episode has no live or normally expired base entitlement.</div><div className="act">{row.primarySerial && <Link className="btn primary block" to={`/entitlements/bulk?operation=entitlement.backfill&serials=${encodeURIComponent(row.primarySerial)}&family=base_warranty`}>Backfill through preview…</Link>}<Link className="btn block" to={`/units/${row.unitId}`}>Open unit</Link></div></>); };

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Entitlements" }, { label: "Gap report" }]} />
      <div className="page-header"><div><div className="eyebrow">Entitlements · report · runs on demand and after every migration load</div><h1>Entitlement gap report</h1><p className="sub">Units sold to a customer with no live or expired base entitlement · {lastRun ? `last run ${fmtDateTime(lastRun.runAt)}` : "no completed run recorded"}</p></div><div className="actions"><a className="btn" href={entitlementApi.gapExportUrl(exportFilters)} download><Icon name="down" />Export CSV</a><Link className="btn" to="/entitlements/exceptions">Exception queue</Link><Link className="btn primary" to={`/entitlements/bulk?operation=entitlement.backfill&family=base_warranty${backfillSerials ? `&serials=${encodeURIComponent(backfillSerials)}` : ""}`}>Backfill entitlements…</Link></div></div>
      <ErrorNote error={error} />
      <div className="stack">
        <KpiStrip label="Gap figures" kpis={[{ label: "Units with gaps", value: (data?.total ?? 0).toLocaleString(), detail: lastRun ? `latest complete run ${fmtDateTime(lastRun.runAt)}` : "on-demand result", trend: null }, { label: "Drop-ship", value: dropShipCount.toLocaleString(), detail: "vendor-ASN path", trend: null }, { label: "Exceptions on page", value: exceptionCount.toLocaleString(), detail: "requirements needing attention", trend: null }, { label: "Selected", value: selected.size.toLocaleString(), detail: "ready for one backfill preview", trend: null }]} />
        <section className="card" aria-labelledby="gap-list"><div className="card-header"><h2 id="gap-list">Units without a base entitlement <span className="meta">{data?.total.toLocaleString() ?? "…"} units</span></h2><span className="meta">Last run {lastRun ? fmtDateTime(lastRun.runAt) : "not recorded"}</span></div>
          <div className="findbar">
            <input className="input" aria-label="Item ids" placeholder="Item UUIDs, comma-separated" value={itemIds} onChange={(event) => { setItemIds(event.target.value); setPage(1); }} />
            <input className="input" aria-label="Sale from" type="date" value={saleFrom} onChange={(event) => { setSaleFrom(event.target.value); setPage(1); }} />
            <input className="input" aria-label="Sale to" type="date" value={saleTo} onChange={(event) => { setSaleTo(event.target.value); setPage(1); }} />
            <input className="input" aria-label="Channels" placeholder="Channels, comma-separated" value={channels} onChange={(event) => { setChannels(event.target.value); setPage(1); }} />
            {[['include', 'Drop-ship · Any'], ['only', 'Drop-ship · Only'], ['exclude', 'Drop-ship · Exclude']].map(([value, label]) => <button type="button" className={`chip${dropShip === value ? " on" : ""}`} key={value} onClick={() => { setDropShip(value!); setPage(1); }}>{label}</button>)}
            {[["", "Cause · All"], ["open", "Cause · Open"], ["exception", "Cause · Exception"], ["waived", "Cause · Waived"]].map(([value, label]) => <button type="button" className={`chip${requirementStatus === value ? " on" : ""}`} key={value || "all"} onClick={() => { setRequirementStatus(value!); setPage(1); }}>{label}</button>)}
            <select className="chip-select" aria-label="Sort" value={sort} onChange={(event) => { setSort(event.target.value); setPage(1); }}><option value="sale_date_desc">Newest sale</option><option value="sale_date_asc">Oldest sale</option><option value="item">Item</option><option value="channel">Channel</option></select>
          </div>
          <DataGrid columns={columns} page={{ rows, total: data?.total ?? 0, page, pageSize: PAGE, continuation: null }} rowId={(row) => row.unitId} onPage={setPage} selection={{ selected, onChange: setSelected, label: (row) => row.primarySerial ?? shortId(row.unitId) }} onRowClick={openRow} activeRowId={active} loading={loading} emptyText="No entitlement gaps match these filters." viewportHeight={600} />
        </section>
        {lastRun && <section className="card"><div className="card-header"><h2>By item and channel <span className="meta">latest complete run</span></h2><span className="meta">{lastRun.total.toLocaleString()} gaps</span></div><table className="table dense"><thead><tr><th>Item</th><th>Channel</th><th>Fulfilment</th><th className="num">Gaps</th></tr></thead><tbody>{lastRun.counts.slice(0, 8).map((row) => <tr key={`${row.itemId}:${row.channel}:${row.dropShip}`}><td className="serial">{shortId(row.itemId, 12)}</td><td>{humanise(row.channel)}</td><td>{row.dropShip ? "Drop-ship" : "Standard"}</td><td className="num">{row.count.toLocaleString()}</td></tr>)}</tbody></table></section>}
      </div>
    </main>
    <Rail label="Gap report"><Section title="This report"><Kv rows={[["Run", lastRun ? shortId(lastRun.runId, 12) : "—"], ["Trigger", humanise(lastRun?.trigger)], ["Run at", fmtDateTime(lastRun?.runAt)], ["Rows", data?.total.toLocaleString() ?? "—"], ["Selection", selected.size.toLocaleString()]]} /></Section><Section title="Definition"><p className="muted">A gap is the latest customer ownership episode with no base-family entitlement that is live or closed as expired. Voided and correction-closed cover remains a gap.</p></Section></Rail>
  </>;
}
