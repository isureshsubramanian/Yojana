import { useState } from "react";
import { Link } from "react-router";
import { DataGrid, KpiStrip, ScanInput, StatusBadge, openSearch, useGridState, type GridColumn } from "../components";
import { Icon } from "../shell/Icons";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { unitKpis, unitsPage, type UnitRow } from "./fixtures";
import { Rail } from "../shell/Drawer";
import { useSession } from "../session/session";

const columns: GridColumn<UnitRow>[] = [
  { key: "serial", label: "Serial", width: 15, sort: { key: "serial" }, render: (r) => <Link className="serial" to="/patterns/detail">{r.serial}</Link> },
  { key: "item", label: "Item", width: 15, sort: { key: "item" }, render: (r) => <><span className="primary">{r.item}</span><span className="sub">{r.itemName}</span></> },
  { key: "status", label: "Status", width: 14, sort: { key: "status" }, render: (r) => <StatusBadge tone={r.tone}>{r.status}</StatusBadge> },
  { key: "custody", label: "Custody · owner", width: 21, render: (r) => <>{r.custody}<span className="sub">{r.custodySub}</span></> },
  { key: "warranty", label: "Warranty", width: 12, numeric: true, render: (r) => r.warrantyEnd ?? <span className="muted">—</span> },
  { key: "last", label: "Last event", width: 18, sort: { key: "last_event", defaultDir: "desc" }, render: (r) => <>{r.lastEvent}<span className="sub">{r.lastEventAt}</span></> },
];

/** List pattern: KPI strip, scan-first toolbar, server-paged grid with visible totals, selection rail. */
export function Component() {
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const session = useSession();
  const grid = useGridState("patterns-units", session.user.id, { key: "last_event", dir: "desc" }, 50);
  const data = unitsPage(grid.page, grid.pageSize);
  return (
    <>
      <main className="content" id="main" tabIndex={-1}>
        <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Patterns", to: "/patterns" }, { label: "List" }]} />
        <PageHeader
          title="Units"
          sub="1,284,317 in service · 212,448 in stock · 3,004 received today"
          actions={<><button type="button" className="btn"><Icon name="down" />Export</button><button type="button" className="btn">Print labels</button><button type="button" className="btn primary"><Icon name="plus" />Commission units</button></>}
        />
        <div className="stack">
          <KpiStrip kpis={unitKpis} />
          <section className="card" aria-labelledby="h-units">
            <div className="card-header">
              <div className="toolbar" style={{ flex: 1 }}>
                <ScanInput primary ariaLabel="Scan or type an identifier" placeholder="Scan or type an identifier" hint={<kbd className="key">/</kbd>} onSubmit={(s) => openSearch(s.value)} />
                <button type="button" className="chip on">Status · In service<Icon name="x" /></button>
                <button type="button" className="chip">Item · All</button>
                <button type="button" className="chip">Location · All</button>
                <button type="button" className="chip"><Icon name="filter" />Filter</button>
              </div>
              <span className="meta nowrap" id="h-units">Sorted by last event</span>
            </div>
            <DataGrid columns={columns} page={data} rowId={(r) => r.id} onPage={grid.onPage} selection={{ selected, onChange: setSelected, label: (r) => r.serial }} viewportHeight={560}
              gridId="patterns-units" userId={session.user.id} sort={grid.sort} defaultSort={{ key: "last_event", dir: "desc" }} onSort={grid.onSort}
              pageSize={grid.pageSize} defaultPageSize={50} onPageSize={grid.onPageSize} footerNote={`Sorted by ${grid.sort.key.replace("_", " ")}`} />
          </section>
        </div>
      </main>
      <Rail label="Selection">
        <div className="section">
          <h3>Selection</h3>
          <p className="muted">{selected.size === 0 ? "No units selected. Select rows, or address a selection set by document, item, date range or pasted list." : `${selected.size.toLocaleString()} units selected on this page.`}</p>
          <div style={{ display: "grid", gap: 8, marginTop: 12 }}>
            <Link className="btn block" to="/patterns/form">Amend entitlements…</Link>
            <button type="button" className="btn block">Print labels…</button>
            <button type="button" className="btn block">Issue certificates…</button>
          </div>
        </div>
        <div className="section">
          <h3>Saved views</h3>
          <div className="shortcut"><span>Past return window</span><StatusBadge tone="bad">17</StatusBadge></div>
          <div className="shortcut"><span>Awaiting vendor serial</span><StatusBadge tone="warn">1,212</StatusBadge></div>
          <div className="shortcut"><span>Warranty ends in 90 days</span><StatusBadge tone="neutral">8,402</StatusBadge></div>
          <div className="shortcut"><span>Loaners out</span><StatusBadge tone="neutral">63</StatusBadge></div>
        </div>
        <div className="section">
          <h3>Scale</h3>
          <div className="kv"><span className="k">Units</span><span className="v">1,496,765</span><span className="k">Events</span><span className="v">148.2 M</span><span className="k">Partitions</span><span className="v">118</span><span className="k">Lookup p99</span><span className="v">6 ms</span></div>
        </div>
      </Rail>
    </>
  );
}
