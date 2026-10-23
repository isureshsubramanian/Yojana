import { useCallback, useMemo, useState } from "react";
import { KpiStrip, StatusBadge, type GridColumn } from "../components";
import { ItemField, PartyField } from "../corrections/ReferenceFields";
import { Icon } from "../shell/Icons";
import { closeDrawer, openDrawer } from "../shell/drawerStore";
import { ErrorNote } from "../units/common";
import { PagedGrid, useAction, useCursorPage } from "./AdminCommon";
import { ErpChangeAccess, ErpChangePage, NoActiveChange, useActiveRun, useErpChangeRights } from "./ErpChangeCommon";
import { erpChangeApi } from "./erpChangeApi";
import { exceptionDecision, problemLabel } from "./erpChangeModel";
import type { ErpChangeRunbookView, MasterDataExceptionPage, MasterDataExceptionRow } from "./erpChangeTypes";

export function Component() { return <ErpChangeAccess><MasterDataScreen /></ErpChangeAccess>; }

function MasterDataScreen() {
  const rights = useErpChangeRights();
  const { run, missing, error, loading, refresh } = useActiveRun();
  const action = useAction();
  const actions = run && rights.control && run.status === "active" && run.currentStep === 2 ? <>
    <button type="button" className="btn" disabled={action.busy} onClick={() => void action.run(async () => { await erpChangeApi.remap(run.runId); await refresh(); }, "Master data re-mapped.")}><Icon name="cycle" />Re-map again</button>
    <button type="button" className="btn primary" disabled={action.busy} onClick={() => void action.run(async () => { await erpChangeApi.check(run.runId); await refresh(); }, "Exit criterion checked.")}><Icon name="check" />Check step 2 exit criterion</button>
  </> : undefined;
  return <ErpChangePage title="Master data exceptions" run={run} actions={actions}
    eyebrow="Step 2 · matched by manufacturer item code, normalised description, tax id and country · items and parties with live units only"
    sub="Records the new ERP holds that could not be matched to one module record. Two different item codes are never merged automatically.">
    <ErrorNote error={error} onReload={() => void refresh()} />{action.feedback}
    {loading && !run && !missing && <p role="status">Loading ERP change…</p>}
    {missing && <NoActiveChange control={rights.control} />}
    {run && <ExceptionList run={run} mayDecide={rights.masterData && run.status === "active"} onDecided={() => void refresh()} />}
  </ErpChangePage>;
}

function ExceptionList({ run, mayDecide, onDecided }: { run: ErpChangeRunbookView; mayDecide: boolean; onDecided: () => void }) {
  const [kind, setKind] = useState(""); const [status, setStatus] = useState(""); const [query, setQuery] = useState("");
  const fetch = useCallback((cursor?: string, signal?: AbortSignal) => erpChangeApi.masterData(run.runId, { kind, status, query: query.trim() }, cursor, signal), [run.runId, kind, status, query]);
  const source = useCursorPage<MasterDataExceptionRow, MasterDataExceptionPage>(fetch);
  const page = source.result;
  const columns = useMemo<GridColumn<MasterDataExceptionRow>[]>(() => [
    { key: "record", label: "New ERP record", width: 24, render: row => <span className="primary"><span className="t mono">{row.targetExternalId}</span><span className="sub">{row.kind === "party" ? "Party" : "Item"} · {row.displayName}{row.taxId ? ` · ${row.taxId}` : ""}</span></span> },
    { key: "problem", label: "Problem", width: 14, render: row => <StatusBadge tone={row.status === "open" ? (row.problem === "ambiguous" ? "warn" : "bad") : row.problem === "ambiguous" ? "info" : "neutral"}>{problemLabel(row)}</StatusBadge> },
    { key: "candidates", label: "Candidates in the module", width: 30, render: row => row.candidates.length ? row.candidates.map(candidate => candidate.label).join(" · ") : "none" },
    { key: "units", label: "Live units", width: 12, render: row => row.liveUnitsAffected.toLocaleString() },
    { key: "decision", label: "Decision", width: 20, render: row => exceptionDecision(row) },
  ], []);
  return <>
    {page && <KpiStrip label="Master data figures" kpis={[
      { label: "Items mapped", value: page.itemsMapped.toLocaleString(), detail: "with live units" },
      { label: "Parties mapped", value: page.partiesMapped.toLocaleString(), detail: "with live units" },
      { label: "Open exceptions", value: page.openExceptions.toLocaleString(), detail: page.openExceptions ? "block step 2" : "all decided" },
      { label: "Were ambiguous", value: page.ambiguous.toLocaleString(), detail: "two or more candidates" },
    ]} />}
    <section className="card" aria-labelledby="erp-master"><div className="card-header"><h2 id="erp-master">Exceptions <span className="meta">most live units first</span></h2></div>
      <div className="erp-filters"><label className="field grow"><span className="lbl">Search this table as you type</span><input className="input" type="search" placeholder="Code, name or tax id" value={query} onChange={event => setQuery(event.target.value)} /></label>
        <label className="field"><span className="lbl">Kind</span><select className="select" value={kind} onChange={event => setKind(event.target.value)}><option value="">All</option><option value="item">Items</option><option value="party">Parties</option></select></label>
        <label className="field"><span className="lbl">Status</span><select className="select" value={status} onChange={event => setStatus(event.target.value)}><option value="">All</option><option value="open">Open</option><option value="mapped">Mapped</option><option value="not_needed">Not needed</option></select></label></div>
      <PagedGrid source={source} columns={columns} rowId={row => row.exceptionId} select={row => openException(run, row, mayDecide, () => { source.refresh(); onDecided(); })} empty="No master data exceptions match." />
    </section>
  </>;
}

function openException(run: ErpChangeRunbookView, row: MasterDataExceptionRow, mayDecide: boolean, onDecided: () => void) {
  openDrawer({ eyebrow: "Master data exception", title: row.targetExternalId, mono: true }, <ExceptionDecision run={run} row={row} mayDecide={mayDecide} onDecided={onDecided} />);
}

export function ExceptionDecision({ run, row, mayDecide, onDecided }: { run: ErpChangeRunbookView; row: MasterDataExceptionRow; mayDecide: boolean; onDecided: () => void }) {
  const [mode, setMode] = useState<"candidate" | "hand" | "not_needed">(row.candidates.length ? "candidate" : "hand");
  const [candidate, setCandidate] = useState(row.candidates[0]?.moduleId ?? ""); const [moduleId, setModuleId] = useState<string | null>(null); const [note, setNote] = useState("");
  const action = useAction();
  if (row.status !== "open") return <p>{exceptionDecision(row)}{row.resolutionNote ? ` · ${row.resolutionNote}` : ""}</p>;
  if (!mayDecide) return <p className="muted">An integration operator or the item master owner decides master data exceptions.</p>;
  const submit = () => void action.run(async () => {
    if (mode === "candidate") await erpChangeApi.chooseCandidate(run.runId, row.exceptionId, candidate, note.trim() || undefined);
    else if (mode === "hand" && moduleId) await erpChangeApi.mapByHand(run.runId, row.exceptionId, moduleId, note.trim() || undefined);
    else await erpChangeApi.notNeeded(run.runId, row.exceptionId, note.trim());
    onDecided(); closeDrawer();
  }, "Decision recorded.");
  const ready = mode === "candidate" ? !!candidate : mode === "hand" ? !!moduleId : !!note.trim();
  return <form className="erp-drawer-form" onSubmit={event => { event.preventDefault(); submit(); }}>
    <p className="muted">{row.kind === "party" ? "Party" : "Item"} · {row.displayName}{row.country ? ` · ${row.country}` : ""} · {row.liveUnitsAffected.toLocaleString()} live units</p>
    <fieldset className="erp-options"><legend>Decision</legend>
      {row.candidates.length > 0 && <label className={mode === "candidate" ? "on" : ""}><input type="radio" name="exception-mode" checked={mode === "candidate"} onChange={() => setMode("candidate")} />Map to a candidate<span className="s">one of the records the match found</span></label>}
      <label className={mode === "hand" ? "on" : ""}><input type="radio" name="exception-mode" checked={mode === "hand"} onChange={() => setMode("hand")} />Map by hand<span className="s">search the module's {row.kind === "party" ? "parties" : "items"}</span></label>
      <label className={mode === "not_needed" ? "on" : ""}><input type="radio" name="exception-mode" checked={mode === "not_needed"} onChange={() => setMode("not_needed")} />Not needed<span className="s">no live unit depends on it; a note is required</span></label>
    </fieldset>
    {mode === "candidate" && <fieldset className="erp-options"><legend>Map to</legend>{row.candidates.map(value => <label key={value.moduleId} className={candidate === value.moduleId ? "on" : ""}><input type="radio" name="exception-candidate" checked={candidate === value.moduleId} onChange={() => setCandidate(value.moduleId)} />{value.label}</label>)}</fieldset>}
    {mode === "hand" && (row.kind === "party"
      ? <PartyField id={`exception-${row.exceptionId}`} label="Module party" value={moduleId} onChange={setModuleId} />
      : <ItemField id={`exception-${row.exceptionId}`} label="Module item" value={moduleId} onChange={setModuleId} />)}
    <label className="field"><span className="lbl">{mode === "not_needed" ? "Note (required)" : "Note (optional)"}</span><textarea className="input" maxLength={500} required={mode === "not_needed"} value={note} onChange={event => setNote(event.target.value)} /></label>
    <p className="hint">The mapping starts today; the current ERP's mapping stays valid for its history.</p>
    <button className="btn primary" disabled={action.busy || !ready}>{mode === "not_needed" ? "Mark not needed" : "Map"}</button>{action.feedback}
  </form>;
}
