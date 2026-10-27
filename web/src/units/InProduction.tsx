import { useEffect, useMemo, useState } from "react";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { DataGrid, FormActions, KpiStrip, StatusBadge, useGridQuery, type GridColumn } from "../components";
import { openDrawer, closeDrawer } from "../shell/drawerStore";
import { ErrorNote } from "./common";
import { fmtDateTime, humanise } from "./format";
import { useReferenceNames } from "../corrections/ReferenceFields";
import { inProductionOrderPage, productionApi, type InProductionDetail, type InProductionOrder } from "./productionApi";
import { newCommandId } from "../api/units";
import { useSession } from "../session/session";

export function Component() {
  const session = useSession();
  const [version, setVersion] = useState(0);
  const query = useGridQuery("/api/production-holds", inProductionOrderPage, { gridId: "in-production-orders",
    userId: session.user.id, defaultSort: { key: "oldest", dir: "asc" }, defaultSize: 25, paging: "cursor",
    pageFacts: result => ({ continuation: result.nextCursor }), reloadKey: version, sortKeys: ["oldest"] });
  const rows = query.data?.items ?? [];
  const names = useReferenceNames({ items: rows.map(row => row.producedItemId), documents: rows.map(row => row.productionOrder) });
  const columns = useMemo<GridColumn<InProductionOrder>[]>(() => [
    { key: "order", label: "Production order", width: 24, render: row => <span className="primary">{names.document(row.productionOrder)}</span> },
    { key: "item", label: "Produced item", width: 20, render: row => row.producedItemId ? names.item(row.producedItemId) : "Not supplied" },
    { key: "planned", label: "Planned", width: 8, numeric: true, render: row => row.plannedQuantity?.toLocaleString() ?? "Unknown" },
    { key: "held", label: "Held", width: 10, numeric: true, render: row => row.heldCount.toLocaleString() },
    { key: "oldest", label: "Oldest issue", width: 18, render: row => fmtDateTime(row.oldestIssuedAt) },
    { key: "parents", label: "Parents received", width: 10, numeric: true, render: row => row.parentsReceived.toLocaleString() },
    { key: "status", label: "Status", width: 14, render: row => <StatusBadge tone={row.status === "waiting_for_assignment" ? "warn" : "neutral"}>{humanise(row.status)}</StatusBadge> },
  ], [names]);
  const open = (row: InProductionOrder) => { openDrawer({ eyebrow: `In production · ${names.document(row.productionOrder)}`, title: humanise(row.status) }, <OrderDrawer order={row} done={() => { closeDrawer(); setVersion(value => value + 1); }} />); };
  return <main className="content" id="main" tabIndex={-1}><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Units", to: "/units" }, { label: "In production" }]} />
    <PageHeader eyebrow="Serialisation · production custody" title="In production" sub="Serialised components held against an order until the received parent is known." />
    <ErrorNote error={query.error} /><KpiStrip label="Production hold figures" kpis={[{ label: "Orders on this page", value: rows.length.toLocaleString(), detail: "server-paged" }, { label: "Components held", value: rows.reduce((sum, row) => sum + row.heldCount, 0).toLocaleString(), detail: "not in stock" }, { label: "Need assignment", value: rows.filter(row => row.status === "waiting_for_assignment").length.toLocaleString(), detail: "person confirms scans" }]} />
    <section className="card" aria-label="Orders with held components"><DataGrid columns={columns} page={{ rows, total: query.data?.nextCursor ? query.page * query.pageSize + 1 : (query.page - 1) * query.pageSize + rows.length, page: query.page, pageSize: query.pageSize, continuation: query.data?.nextCursor ?? null, totalIsExact: !query.data?.nextCursor, hasMore: !!query.data?.nextCursor }} rowId={row => `${row.productionOrder.type}:${row.productionOrder.id}`} onPage={query.onPage} onRowClick={open} loading={query.loading} emptyText="No serialised components are held in production." gridId="in-production-orders" userId={session.user.id} sort={query.sort} defaultSort={{ key: "oldest", dir: "asc" }} onSort={query.onSort} pageSize={query.pageSize} defaultPageSize={25} onPageSize={query.onPageSize} /></section>
  </main>;
}

function OrderDrawer({ order, done }: { order: InProductionOrder; done: () => void }) {
  const [detail, setDetail] = useState<InProductionDetail | null>(null); const [parent, setParent] = useState("");
  const [components, setComponents] = useState(""); const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);
  const names = useReferenceNames({ items: [detail?.producedItemId, ...(detail?.components.map(row => row.itemId) ?? [])], documents: [order.productionOrder, ...(detail?.components.map(row => row.issue) ?? []), ...(detail?.parents.map(row => row.receipt) ?? [])] });
  useEffect(() => { const controller = new AbortController(); void productionApi.detail(order.productionOrder, controller.signal).then(value => { setDetail(value); if (value.parents.length === 1) setParent(value.parents[0]!.serial); }).catch(setError); return () => controller.abort(); }, [order.productionOrder]);
  const scans = components.split(/[\n,]+/).map(value => value.trim()).filter(Boolean);
  const assign = async () => { setBusy(true); setError(null); try { await productionApi.assign({ commandId: newCommandId(), productionOrder: order.productionOrder, parentSerial: parent.trim(), componentSerials: scans }); done(); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  return <div className="stack"><ErrorNote error={error} /><p>{detail?.components.length ?? order.heldCount} components held · {detail?.parents.length ?? order.parentsReceived} parents received · planned quantity {detail?.plannedQuantity?.toLocaleString() ?? "unknown"}.</p>
    <table className="table dense"><thead><tr><th>Component</th><th>Item</th><th>Age begins</th><th>Issue</th></tr></thead><tbody>{detail?.components.map(row => <tr key={row.holdId}><td className="serial">{row.serial}</td><td>{names.item(row.itemId)}</td><td>{fmtDateTime(row.issuedAt)}</td><td>{names.document(row.issue)}</td></tr>)}</tbody></table>
    {detail?.status === "waiting_for_assignment" && <form className="stack" onSubmit={event => { event.preventDefault(); void assign(); }}><label className="field"><span className="lbl">Scan parent serial</span><input className="input" value={parent} onChange={event => setParent(event.target.value)} autoComplete="off" /></label><label className="field"><span className="lbl">Scan component serials</span><textarea className="input" rows={8} value={components} onChange={event => setComponents(event.target.value)} placeholder="One serial per line" /></label><span className="help">Each component must be held against this order, match a serialised BOM line and stay within its quantity. Nothing is applied until you confirm.</span><FormActions><button type="submit" className="btn primary" disabled={busy || !parent.trim() || scans.length === 0}>{busy ? "Assigning…" : "Assign components"}</button></FormActions></form>}
  </div>;
}
