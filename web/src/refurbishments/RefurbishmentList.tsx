import { useMemo, useState } from "react";
import { Link, useNavigate } from "react-router";
import { DataGrid, StatusBadge, useGridQuery, type GridColumn } from "../components";
import { useReferenceNames } from "../corrections/ReferenceFields";
import { usePeople } from "../people/people";
import { useSession } from "../session/session";
import { Rail } from "../shell/Drawer";
import { openDrawer } from "../shell/drawerStore";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, UnitLink } from "../units/common";
import { fmtDateTime } from "../units/format";
import { completedOperations, humanise, identityTitle, money, refurbishmentTone } from "./format";
import { refurbishmentWorkOrderPage, type RefurbishmentWorkOrderRow } from "./types";
import "./refurbishments.css";

const PAGE_SIZE = 50;

export function Component() {
  const session = useSession();
  const navigate = useNavigate();
  const [status, setStatus] = useState("open");
  const [kind, setKind] = useState("");
  const [active, setActive] = useState<string | null>(null);
  const filters = useMemo(() => { const value = new URLSearchParams(); if (status) value.set("status", status); if (kind) value.set("kind", kind); return value; }, [status, kind]);
  const query = useGridQuery("/api/refurbishments/", refurbishmentWorkOrderPage, { gridId: "refurbishments", userId: session.user.id, defaultSort: { key: "opened", dir: "desc" }, defaultSize: PAGE_SIZE, params: filters, sortKeys: ["number", "opened", "status"] });
  const page = query.data;

  const rows = page?.items ?? [];
  const names = useReferenceNames({ items: rows.map((row) => row.itemId) });
  const people = usePeople(rows.flatMap((row) => [row.openedBy, row.releasedBy]));
  const open = (row: RefurbishmentWorkOrderRow) => {
    setActive(row.workOrderId);
    openDrawer({ eyebrow: "Refurbishment work order", title: row.workOrderNumber, mono: true }, <><Section title="Work"><Kv rows={[["Unit", <UnitLink unitId={row.targetUnitId}>{row.targetSerial ?? "Serial unavailable"}</UnitLink>], ["Item", names.item(row.itemId)], ["Kind", humanise(row.kind)], ["Status", humanise(row.status)], ["Opened", `${fmtDateTime(row.openedAt)} by ${people(row.openedBy)}`], ["Cycle", `${row.cycleNumber} of ${row.maxCycles}`], ["Identity", identityTitle(row.identityPolicy)], ["Cost", money(row.totalCost, row.currency)]]} /></Section><Section title="Open"><div className="act"><button type="button" className="btn primary block" onClick={() => navigate(`/refurbishments/${row.workOrderId}`)}>Open work order</button></div></Section></>);
  };
  const columns = useMemo<GridColumn<RefurbishmentWorkOrderRow>[]>(() => [
    { key: "number", label: "Work order", width: 14, sort: { key: "number", defaultDir: "asc" }, render: (row) => <span className="t serial">{row.workOrderNumber}</span> },
    { key: "unit", label: "Unit", width: 20, render: (row) => <><UnitLink unitId={row.targetUnitId}>{row.targetSerial ?? "Serial unavailable"}</UnitLink><span className="sub">{names.item(row.itemId)}</span></> },
    { key: "kind", label: "Kind · cycle", width: 12, render: (row) => <>{humanise(row.kind)}<span className="sub">cycle {row.cycleNumber} of {row.maxCycles}</span></> },
    { key: "progress", label: "Operations", width: 11, render: (row) => <>{completedOperations(row)} of {row.operations.length}<span className="sub">{row.operations.length - completedOperations(row)} open</span></> },
    { key: "identity", label: "Identity outcome", width: 15, render: (row) => identityTitle(row.identityPolicy) },
    { key: "opened", label: "Opened", width: 12, sort: { key: "opened", defaultDir: "desc" }, render: (row) => fmtDateTime(row.openedAt) },
    { key: "cost", label: "Cost", width: 8, numeric: true, render: (row) => money(row.totalCost, row.currency) },
    { key: "status", label: "Status", width: 10, sort: { key: "status", defaultDir: "asc" }, render: (row) => <StatusBadge tone={refurbishmentTone(row.status)}>{humanise(row.status)}</StatusBadge> },
  ], [names]);
  const gridPage = { rows, total: page?.total ?? 0, page: query.page, pageSize: query.pageSize, continuation: null };

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Refurbishment" }]} />
      <div className="page-header"><div><h1>Refurbishment</h1><p className="sub">Work orders against existing units · operations, parts, labour and costs stay together · release applies the server’s identity policy and condition grade</p></div><div className="actions"><Link className="btn" to="/settings/refurbishment">Refurbishment policy</Link></div></div>
      <ErrorNote error={query.error} />
      <section className="card" aria-label="Refurbishment filters"><div className="card-body refurb-toolbar"><div className="segmented plain" role="group" aria-label="Status">{[["open", "Open"], ["released", "Released"], ["cancelled", "Cancelled"], ["", "All"]].map(([value, label]) => <button type="button" key={label} aria-pressed={status === value} onClick={() => setStatus(value!)}>{label}</button>)}</div><label className="chip-field"><span className="chip-k">Kind</span><select className={`chip-select${kind ? " on" : ""}`} aria-label="Work kind" value={kind} onChange={(event) => setKind(event.target.value)}><option value="">All</option><option value="refurbish">Refurbish</option><option value="remanufacture">Remanufacture</option><option value="rework">Rework</option></select></label></div></section>
      <section className="card" aria-labelledby="refurb-list" style={{ marginTop: 16 }}><div className="card-header"><h2 id="refurb-list">{status ? humanise(status) : "All"} work orders <span className="meta">{page?.total.toLocaleString() ?? "—"}</span></h2><span className="meta">Newest opened first</span></div><DataGrid columns={columns} page={gridPage} rowId={(row) => row.workOrderId} onPage={query.onPage} loading={query.loading} viewportHeight={600} emptyText="No refurbishment work orders match this view." onRowClick={open} activeRowId={active} describedBy="refurb-list" gridId="refurbishments" userId={session.user.id} sort={query.sort} defaultSort={{ key: "opened", dir: "desc" }} onSort={query.onSort} pageSize={query.pageSize} defaultPageSize={PAGE_SIZE} onPageSize={query.onPageSize} /></section>
    </main>
    <Rail label="Refurbishment"><Section title="Work order boundary"><p className="muted">Refurbishment changes the existing physical unit. It is not a production order. The server owns cycle limits, identity outcome and release refusals.</p></Section></Rail>
  </>;
}
