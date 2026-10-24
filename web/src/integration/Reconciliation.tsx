import { useCallback, useState } from "react";
import { Link } from "react-router";
import { ErrorNote } from "../units/common";
import { KpiStrip, type GridColumn } from "../components";
import { recordLabel, human, reconciliationRange } from "./model";
import { adminApi } from "./adminApi";
import { ConnectionPicker, date, Frame, Panel, PagedGrid, RoleAccess, State, useAction, useConnectionDetail, useCursorPage } from "./AdminCommon";
import type { EntityDay, LocationCheck, Run } from "./adminTypes";

export function Component() { return <RoleAccess><ReconciliationScreen /></RoleAccess>; }
function ReconciliationScreen() {
  const [connection, setConnection] = useState(""); const [status, setStatus] = useState(""); const [selected, setSelected] = useState<Run | null>(null); const [start, setStart] = useState(""); const [end, setEnd] = useState(""); const action = useAction();
  const fetch = useCallback((cursor?: string, signal?: AbortSignal) => adminApi.runs(connection, status, cursor, signal), [connection, status]); const source = useCursorPage<Run>(fetch);
  const { detail, error } = useConnectionDetail(connection);
  const inactiveReason = !connection ? "Select a connection above to run either check." : !detail ? "Loading connection settings."
    : detail.lifecycleStatus !== "active" ? "Checks require an active connection. Saved results remain available below." : null;
  const range = reconciliationRange(start, end, detail?.reconciliationWindowDays ?? 0, detail?.reconciliationToday ?? new Date().toISOString().slice(0, 10));
  const columns: GridColumn<Run>[] = [
    { key: "connection", label: "Connection", width: 25, render: x => <span className="primary">{x.connectionName}</span> },
    { key: "range", label: "Business date range", width: 22, render: x => `${x.rangeStart} – ${x.rangeEnd}` },
    { key: "at", label: "Ran", width: 18, render: x => <>{date(x.startedAt)}<span className="sub">{human(x.trigger)} · {x.requestedByName}</span></> },
    { key: "state", label: "Result", width: 20, render: x => <State value={x.status} /> },
    { key: "diff", label: "Differences / stock", width: 15, numeric: true, render: x => `${x.mismatchCount} / ${x.locationMismatchCount}` },
  ];
  return <Frame title="Daily check" sub="Each night we compare counts, quantities, distinct serials and a fingerprint of key fields. Missing ranges are loaded automatically where supported; units in stock are compared with ERP on-hand quantities.">
    <ConnectionPicker value={connection} onChange={value => { setConnection(value); setSelected(null); }} />
    <Panel title="Run check now"><form className="integration-run-row" onSubmit={e => { e.preventDefault(); if (inactiveReason || range.error) return; void action.run(async () => { await adminApi.run(connection, start, end); setSelected(null); source.refresh(); }, "Daily comparison finished. Select its run below to review results."); }}><label className="field"><span className="lbl">From business date</span><input className="input" type="date" required min={range.startMin} max={range.startMax} value={start} onChange={e => setStart(e.target.value)} /></label><label className="field"><span className="lbl">To business date</span><input className="input" type="date" required min={range.endMin} max={range.endMax} value={end} onChange={e => setEnd(e.target.value)} /></label><button className="btn primary" disabled={action.busy || !!inactiveReason || !!range.error}>Run check now</button><button type="button" className="btn" disabled={action.busy || !!inactiveReason} onClick={() => void action.run(async () => { await adminApi.locationCheck(connection); setSelected(null); source.refresh(); }, "Stock count check finished. Select its run below.")}>Check stock by location now</button></form><ErrorNote error={error} /><span className="help">{inactiveReason ?? range.error ?? `Up to ${detail?.reconciliationWindowDays} days per check, including both dates.`}</span>{action.feedback}</Panel>
    <section className="card" aria-label="Daily check runs"><div className="card-header"><h2>Check runs</h2><label className="field integration-inline-field"><span className="lbl">Result</span><select className="select" value={status} onChange={e => { setStatus(e.target.value); setSelected(null); }}><option value="">All results</option>{["clean", "mismatch", "running", "failed"].map(x => <option key={x} value={x}>{human(x)}</option>)}</select></label></div><PagedGrid source={source} columns={columns} rowId={x => x.runId} select={setSelected} selected={selected?.runId} empty="No daily checks recorded for these filters." /></section>
    {selected ? <Results key={selected.runId} run={selected} /> : <div className="note">Select a check run to compare record types and units by location.</div>}
  </Frame>;
}
function Results({ run }: { run: Run }) {
  const [differences, setDifferences] = useState(true);
  const entitiesFetch = useCallback((cursor?: string, signal?: AbortSignal) => adminApi.entityDays(run.runId, cursor, signal), [run.runId]); const entities = useCursorPage<EntityDay>(entitiesFetch);
  const locationsFetch = useCallback((cursor?: string, signal?: AbortSignal) => adminApi.locations(run.runId, differences, cursor, signal), [run.runId, differences]); const locations = useCursorPage<LocationCheck>(locationsFetch);
  const entityColumns: GridColumn<EntityDay>[] = [
    { key: "record", label: "Record type / day", width: 22, render: x => <><span className="primary">{recordLabel(x.entity)}</span><span className="sub">{x.businessDate}</span></> },
    { key: "count", label: "Here / ERP / diff", width: 14, numeric: true, render: x => `${x.moduleCount} / ${x.sourceCount} / ${x.moduleCount - x.sourceCount}` },
    { key: "quantity", label: "Quantity here / ERP", width: 15, numeric: true, render: x => `${x.moduleQuantity} / ${x.sourceQuantity}` },
    { key: "serials", label: "Distinct serials here / ERP", width: 15, numeric: true, render: x => `${x.moduleDistinctSerials} / ${x.sourceDistinctSerials}` },
    { key: "hash", label: "Fingerprint", width: 14, render: x => <span title={`Here: ${x.moduleKeyFieldHash}\nERP: ${x.sourceKeyFieldHash}`}>{x.moduleKeyFieldHash === x.sourceKeyFieldHash ? "Same" : "Differs"}</span> },
    { key: "result", label: "Result / next step", width: 20, render: x => <><State value={x.status} /><span className="sub">{x.backfillJobId ? <Link to="/integration/history-loads">Review history load</Link> : x.attentionItemId ? <Link to="/integration/messages-attention">Review message</Link> : ""}</span></> },
  ];
  const locationColumns: GridColumn<LocationCheck>[] = [
    { key: "location", label: "Location", width: 27, render: x => <>{x.locationName ?? "Location"}<span className="sub mono">{x.locationCode}</span></> },
    { key: "item", label: "Item", width: 27, render: x => <>{x.itemName ?? "Item"}<span className="sub mono">{x.itemCode}</span></> },
    { key: "here", label: "Units here", width: 12, numeric: true, render: x => x.moduleUnitCount.toLocaleString() },
    { key: "source", label: "ERP on-hand", width: 12, numeric: true, render: x => x.sourceOnHand.toLocaleString() },
    { key: "diff", label: "Difference", width: 10, numeric: true, render: x => (x.moduleUnitCount - x.sourceOnHand).toLocaleString() },
    { key: "status", label: "Result", width: 12, render: x => <State value={x.status} /> },
  ];
  return <><KpiStrip kpis={[{ label: "Run result", value: human(run.status) }, { label: "Record differences", value: String(run.mismatchCount) }, { label: "Stock differences", value: String(run.locationMismatchCount) }, { label: "Completed", value: date(run.completedAt) }]} /><section className="card" aria-label="By record type"><div className="card-header"><h2>By record type · {run.rangeStart} to {run.rangeEnd}</h2><State value={run.status} /></div><PagedGrid source={entities} columns={entityColumns} rowId={x => x.resultId} empty="No record comparisons in this run. Stock-only checks are shown below." /></section><section className="card" aria-label="Units in stock by location"><div className="card-header"><h2>Units in stock by location against ERP on-hand</h2><div className="segmented" role="group" aria-label="Stock pairs"><button aria-pressed={differences} onClick={() => setDifferences(true)}>Differences</button><button aria-pressed={!differences} onClick={() => setDifferences(false)}>All pairs</button></div></div><PagedGrid source={locations} columns={locationColumns} rowId={x => x.resultId} empty={differences ? "No stock differences in this run." : "No stock pairs checked in this run."} /></section></>;
}
