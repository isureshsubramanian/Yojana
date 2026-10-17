import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useNavigate } from "react-router";
import { DataGrid, StatusBadge, type GridColumn } from "../components";
import { useReferenceNames } from "../corrections/ReferenceFields";
import { usePeople } from "../people/people";
import { Rail } from "../shell/Drawer";
import { openDrawer } from "../shell/drawerStore";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, UnitLink } from "../units/common";
import { fmtDateTime } from "../units/format";
import { refurbishmentsApi } from "./api";
import { completedOperations, humanise, identityTitle, money, refurbishmentTone } from "./format";
import type { RefurbishmentWorkOrderPage, RefurbishmentWorkOrderRow } from "./types";
import "./refurbishments.css";

const PAGE_SIZE = 50;

export function Component() {
  const navigate = useNavigate();
  const [status, setStatus] = useState("open");
  const [kind, setKind] = useState("");
  const [pageNumber, setPageNumber] = useState(1);
  const [page, setPage] = useState<RefurbishmentWorkOrderPage | null>(null);
  const [active, setActive] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoading(true); setError(null);
    try { setPage(await refurbishmentsApi.list(status, kind, (pageNumber - 1) * PAGE_SIZE, PAGE_SIZE, signal)); }
    catch (reason) { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); }
    finally { setLoading(false); }
  }, [status, kind, pageNumber]);
  useEffect(() => { const controller = new AbortController(); void load(controller.signal); return () => controller.abort(); }, [load]);

  const rows = page?.items ?? [];
  const names = useReferenceNames({ items: rows.map((row) => row.itemId) });
  const people = usePeople(rows.flatMap((row) => [row.openedBy, row.releasedBy]));
  const open = (row: RefurbishmentWorkOrderRow) => {
    setActive(row.workOrderId);
    openDrawer({ eyebrow: "Refurbishment work order", title: row.workOrderNumber, mono: true }, <><Section title="Work"><Kv rows={[["Unit", <UnitLink unitId={row.targetUnitId}>{row.targetSerial ?? "Serial unavailable"}</UnitLink>], ["Item", names.item(row.itemId)], ["Kind", humanise(row.kind)], ["Status", humanise(row.status)], ["Opened", `${fmtDateTime(row.openedAt)} by ${people(row.openedBy)}`], ["Cycle", `${row.cycleNumber} of ${row.maxCycles}`], ["Identity", identityTitle(row.identityPolicy)], ["Cost", money(row.totalCost, row.currency)]]} /></Section><Section title="Open"><div className="act"><button type="button" className="btn primary block" onClick={() => navigate(`/refurbishments/${row.workOrderId}`)}>Open work order</button></div></Section></>);
  };
  const columns = useMemo<GridColumn<RefurbishmentWorkOrderRow>[]>(() => [
    { key: "number", label: "Work order", width: 15, render: (row) => <span className="primary"><span className="t serial">{row.workOrderNumber}</span><span className="sub">opened {fmtDateTime(row.openedAt)}</span></span> },
    { key: "unit", label: "Unit", width: 20, render: (row) => <><UnitLink unitId={row.targetUnitId}>{row.targetSerial ?? "Serial unavailable"}</UnitLink><span className="sub">{names.item(row.itemId)}</span></> },
    { key: "kind", label: "Kind · cycle", width: 13, render: (row) => <>{humanise(row.kind)}<span className="sub">cycle {row.cycleNumber} of {row.maxCycles}</span></> },
    { key: "progress", label: "Operations", width: 13, render: (row) => <>{completedOperations(row)} of {row.operations.length}<span className="sub">{row.operations.length - completedOperations(row)} open</span></> },
    { key: "identity", label: "Identity outcome", width: 19, render: (row) => identityTitle(row.identityPolicy) },
    { key: "cost", label: "Cost", width: 10, numeric: true, render: (row) => money(row.totalCost, row.currency) },
    { key: "status", label: "Status", width: 10, render: (row) => <StatusBadge tone={refurbishmentTone(row.status)}>{humanise(row.status)}</StatusBadge> },
  ], [names]);
  const gridPage = { rows, total: page?.total ?? 0, page: pageNumber, pageSize: PAGE_SIZE, continuation: null };

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Refurbishment" }]} />
      <div className="page-header"><div><h1>Refurbishment</h1><p className="sub">Work orders against existing units · operations, parts, labour and costs stay together · release applies the server’s identity policy and condition grade</p></div><div className="actions"><Link className="btn" to="/settings/refurbishment">Refurbishment policy</Link></div></div>
      <ErrorNote error={error} />
      <section className="card" aria-label="Refurbishment filters"><div className="card-body refurb-toolbar"><div className="segmented plain" role="group" aria-label="Status">{[["open", "Open"], ["released", "Released"], ["cancelled", "Cancelled"], ["", "All"]].map(([value, label]) => <button type="button" key={label} aria-pressed={status === value} onClick={() => { setStatus(value!); setPageNumber(1); }}>{label}</button>)}</div><select className={`chip-select${kind ? " on" : ""}`} aria-label="Work kind" value={kind} onChange={(event) => { setKind(event.target.value); setPageNumber(1); }}><option value="">Kind · All</option><option value="refurbish">Refurbish</option><option value="remanufacture">Remanufacture</option><option value="rework">Rework</option></select></div></section>
      <section className="card" aria-labelledby="refurb-list" style={{ marginTop: 16 }}><div className="card-header"><h2 id="refurb-list">{status ? humanise(status) : "All"} work orders <span className="meta">{page?.total.toLocaleString() ?? "—"}</span></h2><span className="meta">Newest opened first</span></div><DataGrid columns={columns} page={gridPage} rowId={(row) => row.workOrderId} onPage={setPageNumber} loading={loading} viewportHeight={600} emptyText="No refurbishment work orders match this view." onRowClick={open} activeRowId={active} describedBy="refurb-list" /></section>
    </main>
    <Rail label="Refurbishment"><Section title="Work order boundary"><p className="muted">Refurbishment changes the existing physical unit. It is not a production order. The server owns cycle limits, identity outcome and release refusals.</p></Section></Rail>
  </>;
}
