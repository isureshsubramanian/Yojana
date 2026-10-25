import { useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { DataGrid, KpiStrip, StatusBadge, useGridQuery, type GridColumn } from "../components";
import { useSession } from "../session/session";
import { openDrawer } from "../shell/drawerStore";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section } from "../units/common";
import { useReferenceNames } from "../corrections/ReferenceFields";
import { fmtDate, fmtDateTime, humanise, shortId } from "../units/format";
import { entitlementApi } from "./api";
import { gapCause } from "./format";
import { gapPageSchema, type GapRow, type GapRun } from "./types";

const PAGE = 50;

export function Component() {
  const [searchParams] = useSearchParams();
  const session = useSession();
  const [itemIds, setItemIds] = useState("");
  const [saleFrom, setSaleFrom] = useState("");
  const [saleTo, setSaleTo] = useState("");
  const [channels, setChannels] = useState(() => searchParams.get("channels") ?? "");
  const [dropShip, setDropShip] = useState("include");
  const [requirementStatus, setRequirementStatus] = useState("");
  const [lastRun, setLastRun] = useState<GapRun | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [active, setActive] = useState<string | null>(null);
  const filters = useMemo(() => { const value = new URLSearchParams(); if (itemIds) value.set("items", itemIds); if (saleFrom) value.set("saleFrom", saleFrom); if (saleTo) value.set("saleTo", saleTo); if (channels) value.set("channels", channels); value.set("dropShip", dropShip); if (requirementStatus) value.set("requirementStatus", requirementStatus); return value; }, [itemIds, saleFrom, saleTo, channels, dropShip, requirementStatus]);
  const query = useGridQuery("/api/entitlements/gap-report", gapPageSchema, { gridId: "entitlement-gaps", userId: session.user.id, defaultSort: { key: "sale_date", dir: "desc" }, defaultSize: PAGE, params: filters, sortKeys: ["sale_date", "item", "serial", "channel"] });
  const data = query.data;
  const exportFilters = useMemo(() => ({ items: itemIds || undefined, saleFrom: saleFrom || undefined, saleTo: saleTo || undefined, channels: channels || undefined, dropShip, requirementStatus: requirementStatus || undefined, sort: query.sort.key, dir: query.sort.dir }), [itemIds, saleFrom, saleTo, channels, dropShip, requirementStatus, query.sort]);
  useEffect(() => { const controller = new AbortController(); entitlementApi.latestGapRun(controller.signal).then(setLastRun).catch(() => undefined); return () => controller.abort(); }, [data]);

  const rows = data?.rows ?? [];
  const names = useReferenceNames({ items: [...rows.map(row => row.itemId), ...(lastRun?.counts.map(row => row.itemId) ?? [])], parties: rows.map(row => row.customerPartyRef) });
  const selectedRows = rows.filter((row) => selected.has(row.unitId));
  const backfillSerials = selectedRows.map((row) => row.primarySerial).filter((value): value is string => !!value).join(",");
  const dropShipCount = lastRun?.counts.filter((row) => row.dropShip).reduce((sum, row) => sum + row.count, 0) ?? rows.filter((row) => row.dropShip).length;
  const exceptionCount = rows.filter((row) => row.requirementStatus === "exception").length;
  const columns: GridColumn<GapRow>[] = [
    { key: "serial", label: "Serial · item", width: 22, mono: true, sort: { key: "serial", defaultDir: "asc" }, render: (row) => <>{row.primarySerial ?? shortId(row.unitId)}<span className="sub" style={{ fontFamily: "var(--font-body)" }}>{names.item(row.itemId)}</span></> },
    { key: "sold", label: "Sale date", width: 14, sort: { key: "sale_date", defaultDir: "desc" }, render: (row) => fmtDate(row.saleDate) },
    { key: "customer", label: "Customer", width: 17, render: (row) => names.party(row.customerPartyRef) },
    { key: "channel", label: "Channel", width: 13, sort: { key: "channel", defaultDir: "asc" }, render: (row) => humanise(row.channel) },
    { key: "drop", label: "Drop-ship", width: 10, render: (row) => <StatusBadge tone={row.dropShip ? "info" : "neutral"}>{row.dropShip ? "Yes" : "No"}</StatusBadge> },
    { key: "cause", label: "Requirement · cause", width: 24, render: (row) => <>{humanise(row.requirementStatus) || "No requirement"}<span className="sub">{humanise(gapCause(row))}</span></> },
  ];
  const openRow = (row: GapRow) => { setActive(row.unitId); openDrawer({ eyebrow: `Gap · ${names.item(row.itemId)} · ${humanise(row.channel)}`, title: row.primarySerial ?? shortId(row.unitId), mono: true }, <><h3>Ownership episode</h3><Kv rows={[["Sold", fmtDateTime(row.saleDate)], ["Customer", names.party(row.customerPartyRef)], ["Channel", humanise(row.channel)], ["Drop-ship", row.dropShip ? "yes" : "no"], ["Unit", row.primarySerial ?? "Unlabelled unit"]]} /><h3 style={{ marginTop: 16 }}>Why no entitlement</h3><div className="note bad"><strong>{humanise(gapCause(row))}.</strong>&nbsp;The latest customer ownership episode has no live or normally expired base entitlement.</div><div className="act">{row.primarySerial && <Link className="btn primary block" to="/entitlements/bulk?operation=entitlement.backfill&family=base_warranty" state={{ serials: row.primarySerial }}>Backfill through preview…</Link>}<Link className="btn block" to={`/units/${row.unitId}`}>Open unit</Link></div></>); };

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Entitlements" }, { label: "Gap report" }]} />
      <div className="page-header"><div><div className="eyebrow">Entitlements · report · runs on demand and after every migration load</div><h1>Entitlement gap report</h1><p className="sub">Units sold to a customer with no live or expired base entitlement · {lastRun ? `last run ${fmtDateTime(lastRun.runAt)}` : "no completed run recorded"}</p></div><div className="actions"><a className="btn" href={entitlementApi.gapExportUrl(exportFilters)} download><Icon name="down" />Export CSV</a><Link className="btn" to="/entitlements/exceptions">Exception queue</Link><Link className="btn primary" to="/entitlements/bulk?operation=entitlement.backfill&family=base_warranty" state={backfillSerials ? { serials: backfillSerials } : null}>Backfill entitlements…</Link></div></div>
      <ErrorNote error={query.error} />
      <div className="stack">
        <KpiStrip label="Gap figures" kpis={[{ label: "Units with gaps", value: (data?.total ?? 0).toLocaleString(), detail: lastRun ? `latest complete run ${fmtDateTime(lastRun.runAt)}` : "on-demand result", trend: null }, { label: "Drop-ship", value: dropShipCount.toLocaleString(), detail: "vendor-ASN path", trend: null }, { label: "Exceptions on page", value: exceptionCount.toLocaleString(), detail: "requirements needing attention", trend: null }, { label: "Selected", value: selected.size.toLocaleString(), detail: "ready for one backfill preview", trend: null }]} />
        <section className="card" aria-labelledby="gap-list"><div className="card-header"><h2 id="gap-list">Units without a base entitlement <span className="meta">{data?.total.toLocaleString() ?? "…"} units</span></h2><span className="meta">Last run {lastRun ? fmtDateTime(lastRun.runAt) : "not recorded"}</span></div>
          <div className="findbar">
            <input className="input" aria-label="Item ids" placeholder="Item UUIDs, comma-separated" value={itemIds} onChange={(event) => setItemIds(event.target.value)} />
            <input className="input" aria-label="Sale from" type="date" value={saleFrom} onChange={(event) => setSaleFrom(event.target.value)} />
            <input className="input" aria-label="Sale to" type="date" value={saleTo} onChange={(event) => setSaleTo(event.target.value)} />
            <input className="input" aria-label="Channels" placeholder="Channels, comma-separated" value={channels} onChange={(event) => setChannels(event.target.value)} />
            {[['include', 'Drop-ship · Any'], ['only', 'Drop-ship · Only'], ['exclude', 'Drop-ship · Exclude']].map(([value, label]) => <button type="button" className={`chip${dropShip === value ? " on" : ""}`} key={value} onClick={() => setDropShip(value!)}>{label}</button>)}
            {[["", "Cause · All"], ["open", "Cause · Open"], ["exception", "Cause · Exception"], ["waived", "Cause · Waived"]].map(([value, label]) => <button type="button" className={`chip${requirementStatus === value ? " on" : ""}`} key={value || "all"} onClick={() => setRequirementStatus(value!)}>{label}</button>)}
          </div>
          <DataGrid columns={columns} page={{ rows, total: data?.total ?? 0, page: query.page, pageSize: query.pageSize, continuation: null }} rowId={(row) => row.unitId} onPage={query.onPage} selection={{ selected, onChange: setSelected, label: (row) => row.primarySerial ?? shortId(row.unitId) }} onRowClick={openRow} activeRowId={active} loading={query.loading} emptyText="No entitlement gaps match these filters." viewportHeight={600}
            gridId="entitlement-gaps" userId={session.user.id} sort={query.sort} defaultSort={{ key: "sale_date", dir: "desc" }} onSort={query.onSort} pageSize={query.pageSize} defaultPageSize={PAGE} onPageSize={query.onPageSize} />
        </section>
        {lastRun && <section className="card"><div className="card-header"><h2>By item and channel <span className="meta">latest complete run</span></h2><span className="meta">{lastRun.total.toLocaleString()} gaps</span></div><table className="table dense"><thead><tr><th>Item</th><th>Channel</th><th>Fulfilment</th><th className="num">Gaps</th></tr></thead><tbody>{lastRun.counts.slice(0, 8).map((row) => <tr key={`${row.itemId}:${row.channel}:${row.dropShip}`}><td>{names.item(row.itemId)}</td><td>{humanise(row.channel)}</td><td>{row.dropShip ? "Drop-ship" : "Standard"}</td><td className="num">{row.count.toLocaleString()}</td></tr>)}</tbody></table></section>}
      </div>
    </main>
    <Rail label="Gap report"><Section title="This report"><Kv rows={[["Run", lastRun ? shortId(lastRun.runId, 12) : "—"], ["Trigger", humanise(lastRun?.trigger)], ["Run at", fmtDateTime(lastRun?.runAt)], ["Rows", data?.total.toLocaleString() ?? "—"], ["Selection", selected.size.toLocaleString()]]} /></Section><Section title="Definition"><p className="muted">A gap is the latest customer ownership episode with no base-family entitlement that is live or closed as expired. Voided and correction-closed cover remains a gap.</p></Section></Rail>
  </>;
}
