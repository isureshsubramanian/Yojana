import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link } from "react-router";
import { DataGrid, FindBar, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { useReferenceNames } from "../corrections/ReferenceFields";
import { usePeople } from "../people/people";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, UnitLink } from "../units/common";
import { fmtDate, fmtDateTime } from "../units/format";
import { returnsApi, type RmaFilters } from "./api";
import { RMA_STAGES, ageBucketLabel, humanise, receiptTone, stageNumber, stageTone } from "./format";
import type { RmaDetail, RmaListResult, RmaListRow } from "./types";
import "./returns.css";

const PAGE_SIZE = 50;
const INITIAL_FILTERS: RmaFilters = { stage: "", minimumAgeDays: null, maximumAgeDays: null, q: "" };

export function Component() {
  const [filters, setFilters] = useState<RmaFilters>(INITIAL_FILTERS);
  const [pageNumber, setPageNumber] = useState(1);
  const [result, setResult] = useState<RmaListResult | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [active, setActive] = useState<string | null>(null);
  const cursors = useRef<(string | null)[]>([null]);
  const load = useCallback((signal?: AbortSignal) => {
    setLoading(true); setError(null);
    return returnsApi.list(filters, cursors.current[pageNumber - 1] ?? null, PAGE_SIZE, signal)
      .then(setResult).catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); })
      .finally(() => setLoading(false));
  }, [filters, pageNumber]);
  useEffect(() => { const controller = new AbortController(); void load(controller.signal); return () => controller.abort(); }, [load]);
  const rows = result?.rows ?? [];
  const names = useReferenceNames({ items: rows.map((row) => row.primaryItemId), parties: rows.map((row) => row.returningPartyRef) });
  const people = usePeople(rows.flatMap((row) => [row.requestedBy, row.lastActorRef]));
  const setFilter = (patch: Partial<RmaFilters>) => { cursors.current = [null]; setFilters((current) => ({ ...current, ...patch })); setPageNumber(1); };
  const setPage = (next: number) => {
    if (next > pageNumber) {
      if (!result?.continuation) return;
      cursors.current[next - 1] = result.continuation;
    }
    setPageNumber(Math.max(1, next));
  };
  const openReturn = async (row: RmaListRow) => {
    setActive(row.rmaId);
    openDrawer({ eyebrow: "Return", title: row.rmaNumber ?? "Return request", mono: true }, <p className="muted">Loading return…</p>);
    try { updateDrawer({ eyebrow: "Return", title: row.rmaNumber ?? "Return request", mono: true }, <RmaDrawer row={row} detail={await returnsApi.detail(row.rmaId)} />); }
    catch (reason) { updateDrawer({ eyebrow: "Return", title: row.rmaNumber ?? "Return request", mono: true }, <ErrorNote error={reason} />); }
  };
  const columns = useMemo<GridColumn<RmaListRow>[]>(() => [
    { key: "rma", label: "RMA", width: 15, render: (row) => <span className="primary"><Link className="t serial" to={`/returns/${row.rmaId}`}>{row.rmaNumber ?? "Awaiting number"}</Link><span className="sub">requested {fmtDate(row.requestDate)}</span></span> },
    { key: "unit", label: "Unit", width: 18, render: (row) => row.primaryUnitId ? <><UnitLink unitId={row.primaryUnitId}>{row.primarySerial ?? "No serial recorded"}</UnitLink><span className="sub">{names.item(row.primaryItemId)}{row.lineCount > 1 ? ` · ${row.lineCount} lines` : ""}</span></> : <span className="muted">No unit resolved</span> },
    { key: "customer", label: "Customer", width: 20, render: (row) => <>{names.party(row.returningPartyRef)}<span className="sub">requested by {people(row.requestedBy)}</span></> },
    { key: "stage", label: "Stage", width: 15, render: (row) => <><StatusBadge tone={stageTone(row.stage)}>{stageNumber(row.stage)} · {humanise(row.stage)}</StatusBadge><span className="sub">updated {fmtDate(row.updatedAt)}</span></> },
    { key: "receipt", label: "Receipt", width: 19, render: (row) => row.receiptOutcome ? <><StatusBadge tone={receiptTone(row.receiptOutcome)}>{humanise(row.receiptOutcome)}</StatusBadge><span className="sub">{row.conditionGrade ? `grade ${humanise(row.conditionGrade).toLowerCase()}` : "condition pending"}</span></> : <span className="muted">Not received</span> },
    { key: "exceptions", label: "Exceptions", width: 7, numeric: true, render: (row) => row.exceptionCount ? <span className="bad-text">{row.exceptionCount}</span> : "—" },
    { key: "age", label: "Age", width: 6, numeric: true, render: (row) => <span title={ageBucketLabel(row.ageDays)}>{row.ageDays} d</span> },
  ], [names, people]);
  const summary = result?.summary ?? [];
  const openCount = summary.filter((row) => row.stage !== "closed").reduce((total, row) => total + row.count, 0);
  const received = summary.find((row) => row.stage === "received");
  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Returns" }]} />
      <div className="page-header"><div><h1>Returns</h1><p className="sub">Every RMA in its six stages · requested, authorised, in transit, received, dispositioned, closed · ageing counted from the request</p></div><div className="actions"><Link className="btn" to="/returns/receipt"><Icon name="scan" />Receipt bench</Link></div></div>
      <div className="returns-stack">
        <KpiStrip label="Return figures" kpis={[
          { label: "Open returns", value: openCount.toLocaleString(), detail: `${summary.filter((row) => row.stage !== "closed").reduce((total, row) => total + row.exceptionCount, 0)} with exceptions`, trend: null },
          { label: "In quarantine", value: (received?.count ?? 0).toLocaleString(), detail: received?.oldestAgeDays == null ? "No received returns" : `oldest ${received.oldestAgeDays} d · ${received.exceptionCount} exceptions`, trend: null },
          { label: "Exceptions", value: summary.reduce((total, row) => total + row.exceptionCount, 0).toLocaleString(), detail: "mismatch, unauthorised, counterfeit or provisional", trend: null },
        ]} />
        <section className="card" aria-labelledby="returns-stages-heading"><div className="card-header"><h2 id="returns-stages-heading">Stages <span className="meta">click a stage to filter</span></h2><span className="meta">age = days since request · oldest shown</span></div><div className="card-body"><div className="returns-stages" role="group" aria-label="Stage filter">
          {RMA_STAGES.map((stage, index) => { const row = summary.find((item) => item.stage === stage); const pressed = filters.stage === stage; return <button type="button" className="returns-stage" key={stage} aria-pressed={pressed} onClick={() => setFilter({ stage: pressed ? "" : stage })}><span className="l">{index + 1} · {humanise(stage)}</span><span className="n">{row?.count.toLocaleString() ?? "—"}</span><span className="s">{row?.exceptionCount ? `${row.exceptionCount} exceptions · ` : ""}{row?.oldestAgeDays == null ? "no returns" : `oldest ${row.oldestAgeDays} d`}</span></button>; })}
        </div></div></section>
        <section className="card" aria-label="Return filters"><div className="card-body"><div className="toolbar">
          <FindBar query={filters.q} onQuery={(q) => setFilter({ q })} facets={[]} activeFacet={null} onFacet={() => undefined} compact ariaLabel="Find a return" placeholder="RMA, serial or asset tag" />
          <label className="field"><span className="lbl">Minimum age</span><select className="select" value={filters.minimumAgeDays ?? ""} onChange={(event) => setFilter({ minimumAgeDays: event.target.value ? Number(event.target.value) : null })}><option value="">Any</option><option value="3">3 days</option><option value="8">8 days</option><option value="15">15 days</option><option value="30">30 days</option></select></label>
          <label className="field"><span className="lbl">Maximum age</span><select className="select" value={filters.maximumAgeDays ?? ""} onChange={(event) => setFilter({ maximumAgeDays: event.target.value ? Number(event.target.value) : null })}><option value="">Any</option><option value="2">2 days</option><option value="7">7 days</option><option value="14">14 days</option><option value="29">29 days</option></select></label>
        </div></div></section>
        <section className="card" aria-labelledby="returns-list-heading"><div className="card-header"><h2 id="returns-list-heading">{filters.stage ? humanise(filters.stage) : "All returns"} <span className="meta">{rows.length.toLocaleString()} shown{result?.hasMore ? " · more" : ""}</span></h2><span className="meta">Exceptions first, then oldest</span></div>
          <DataGrid columns={columns} page={{ rows, total: (pageNumber - 1) * PAGE_SIZE + rows.length + (result?.hasMore ? 1 : 0), page: pageNumber, pageSize: PAGE_SIZE, continuation: result?.continuation ?? null, totalIsExact: false, hasMore: result?.hasMore ?? false }} rowId={(row) => row.rmaId} onPage={setPage} onRowClick={(row) => void openReturn(row)} activeRowId={active} loading={loading} emptyText="No returns match these filters." />
        </section>
        <ErrorNote error={error} />
      </div>
    </main>
    <Rail label="Returns"><Section title="Pipeline"><p className="muted">Select a row for its customer, receipt and next action. Open the return for line-level decisions.</p></Section></Rail>
  </>;
}

function RmaDrawer({ row, detail }: { row: RmaListRow; detail: RmaDetail }) {
  const names = useReferenceNames({ items: detail.lines.flatMap((line) => [line.requestedItemId, line.receivedItemId]), parties: [detail.returningPartyRef], locations: detail.lines.map((line) => line.receivingLocationId) });
  const people = usePeople([detail.requestedBy, detail.lastActorRef]);
  return <><Section title="Return"><Kv rows={[
    ["Customer", names.party(detail.returningPartyRef)], ["Stage", `${stageNumber(detail.stage)} · ${humanise(detail.stage)}`],
    ["Requested", `${fmtDateTime(detail.requestedAt)} · ${people(detail.requestedBy)}`], ["Updated", `${fmtDateTime(row.updatedAt)} · ${people(detail.lastActorRef)}`],
    ["Lines", `${detail.lines.length} · ${detail.lines.filter((line) => line.receiptOutcome && line.receiptOutcome !== "accepted").length} exceptions`],
  ]} /></Section><Section title="Units">{detail.lines.map((line) => <div className="job" key={line.rmaLineId}><div><div className="t serial">{line.receivedSerial ?? line.authorisedSerial ?? line.requestedSerial ?? "No serial"}</div><div className="s">{names.item(line.receivedItemId ?? line.requestedItemId)} · {humanise(line.receiptOutcome ?? detail.stage)}</div>{line.receivingLocationId && <div className="s">{names.location(line.receivingLocationId)}</div>}</div><StatusBadge tone={receiptTone(line.receiptOutcome)}>{line.sequence}</StatusBadge></div>)}</Section><div className="act"><Link className="btn block" to={`/returns/${detail.rmaId}`}>Open return</Link></div></>;
}
