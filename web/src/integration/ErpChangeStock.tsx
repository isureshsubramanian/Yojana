import { useCallback, useMemo, useState } from "react";
import { StatusBadge, type GridColumn } from "../components";
import { LocationField, UnitField } from "../corrections/ReferenceFields";
import { Icon } from "../shell/Icons";
import { closeDrawer, openDrawer } from "../shell/drawerStore";
import { ErrorNote, Kv } from "../units/common";
import { date, PagedGrid, useAction, useCursorPage } from "./AdminCommon";
import { ErpChangeAccess, ErpChangePage, NoActiveChange, useActiveRun, useErpChangeRights } from "./ErpChangeCommon";
import { erpChangeApi } from "./erpChangeApi";
import { resolutionLabel, stockReasonFilters, stockReasonLabel, stockReasonTone, suggestionLabel } from "./erpChangeModel";
import type { ErpChangeRunbookView, StockDifferencePage, StockDifferenceRow, StockResolutionCommand } from "./erpChangeTypes";
import { parallelDate } from "./parallelRunModel";

export function Component() { return <ErpChangeAccess><StockScreen /></ErpChangeAccess>; }

const locationKinds = [["warehouse", "Warehouse"], ["bin", "Bin"], ["in_repair", "In repair"], ["quarantine", "Quarantine"], ["in_transit", "In transit"], ["customer_site", "Customer site"], ["at_3pl", "At a logistics provider"], ["at_subcontractor", "At a subcontractor"], ["vendor", "At the vendor"]] as const;

function StockScreen() {
  const rights = useErpChangeRights();
  const { run, missing, error, loading, refresh } = useActiveRun();
  const action = useAction(); const [revision, setRevision] = useState(0);
  const onStep = run?.status === "active" && run.currentStep === 3;
  const actions = run ? <>
    {rights.stock && onStep && <button type="button" className="btn" disabled={action.busy} onClick={() => void action.run(async () => { await erpChangeApi.reconcileStock(run.runId); await refresh(); setRevision(value => value + 1); }, "Stock compared again.")}><Icon name="cycle" />Compare again</button>}
    {rights.stock && onStep && <button type="button" className="btn primary" onClick={() => openSignOff(run, () => void refresh())}><Icon name="check" />Sign off the rest…</button>}
    {rights.control && onStep && <button type="button" className="btn" disabled={action.busy} onClick={() => void action.run(async () => { await erpChangeApi.check(run.runId); await refresh(); }, "Exit criterion checked.")}>Check step 3 exit criterion</button>}
  </> : undefined;
  return <ErpChangePage title="Stock differences" run={run} actions={actions}
    eyebrow={run ? `Step 3 · compared ${run.unitsCompared.toLocaleString()} units in stock · module, current ERP, new ERP` : "Step 3"}
    sub={'The inventory controller resolves and signs off. Each difference is resolved with a unit command (correct identifier, merge, change custody) and the reason "ERP change reconciliation". Nothing is edited directly.'}>
    <ErrorNote error={error} onReload={() => void refresh()} />{action.feedback}
    {loading && !run && !missing && <p role="status">Loading ERP change…</p>}
    {missing && <NoActiveChange control={rights.control} />}
    {run && <DifferenceList key={revision} run={run} mayResolve={rights.stock && onStep} onResolved={() => void refresh()} />}
  </ErpChangePage>;
}

function DifferenceList({ run, mayResolve, onResolved }: { run: ErpChangeRunbookView; mayResolve: boolean; onResolved: () => void }) {
  const [reason, setReason] = useState(""); const [resolved, setResolved] = useState(false); const [coveredOnly, setCoveredOnly] = useState(false); const [query, setQuery] = useState("");
  const fetch = useCallback((cursor?: string, signal?: AbortSignal) => erpChangeApi.stock(run.runId, { reason, status: resolved ? "resolved" : "open", liveCover: coveredOnly || undefined, query: query.trim() }, cursor, signal), [run.runId, reason, resolved, coveredOnly, query]);
  const source = useCursorPage<StockDifferenceRow, StockDifferencePage>(fetch);
  const page = source.result;
  const columns = useMemo<GridColumn<StockDifferenceRow>[]>(() => [
    { key: "unit", label: "Unit", width: 20, render: row => <span className="primary"><span className="serial t mono">{row.identifier}</span><span className="sub">{row.itemName ?? "Unknown item"} · {row.hasLiveEntitlement ? `cover to ${parallelDate(row.warrantyEnd, true)}` : "no cover"}</span></span> },
    { key: "reason", label: "Reason", width: 16, render: row => <StatusBadge tone={stockReasonTone(row)}>{stockReasonLabel(row.reason)}</StatusBadge> },
    { key: "module", label: "Module", width: 16, render: row => row.moduleLocation ?? "—" },
    { key: "source", label: "Current ERP", width: 16, render: row => row.sourceLocation ?? "—" },
    { key: "target", label: "New ERP", width: 16, render: row => row.targetLocation ?? "—" },
    { key: "suggested", label: resolved ? "Resolved by" : "Suggested command", width: 16, render: row => row.status === "resolved" ? resolutionLabel(row.resolutionCommand) : suggestionLabel(row.suggestedResolution) },
  ], [resolved]);
  return <>
    <ol className="erp-stages" aria-label="Reason">
      {stockReasonFilters.map(([value, label]) => <li key={value || "all"}><button type="button" aria-pressed={!resolved && reason === value} onClick={() => { setResolved(false); setReason(value); }}><div className="l">{label}</div>{value === "" && page && !resolved && !reason ? <><div className={`n${page.coveredOpen ? " bad-text" : ""}`}>{page.open.toLocaleString()}</div><div className="s">{page.coveredOpen.toLocaleString()} with live cover</div></> : <div className="s">{value ? "filter" : "every reason"}</div>}</button></li>)}
      <li><button type="button" aria-pressed={resolved} onClick={() => { setResolved(true); setReason(""); }}><div className="l">Resolved</div>{page && <div className="n">{page.resolved.toLocaleString()}</div>}<div className="s">by command · audited</div></button></li>
    </ol>
    <section className="card" aria-labelledby="erp-stock"><div className="card-header"><h2 id="erp-stock">Differences <span className="meta">{page ? `${page.compared.toLocaleString()} compared` : ""}</span></h2></div>
      <div className="erp-filters"><label className="field grow"><span className="lbl">Search this table as you type</span><input className="input" type="search" placeholder="Serial, item or location" value={query} onChange={event => setQuery(event.target.value)} /></label>
        <label className="field"><span className="lbl">Live cover</span><select className="select" value={coveredOnly ? "covered" : ""} onChange={event => setCoveredOnly(event.target.value === "covered")}><option value="">Any</option><option value="covered">Covered units only</option></select></label></div>
      <PagedGrid source={source} columns={columns} rowId={row => row.differenceId} select={row => openDifference(run, row, mayResolve, () => { source.refresh(); onResolved(); })} empty="No stock differences match." />
    </section>
  </>;
}

function openDifference(run: ErpChangeRunbookView, row: StockDifferenceRow, mayResolve: boolean, onResolved: () => void) {
  openDrawer({ eyebrow: "Stock difference", title: row.identifier, mono: true }, <DifferenceDecision run={run} row={row} mayResolve={mayResolve} onResolved={onResolved} />);
}

export function DifferenceDecision({ run, row, mayResolve, onResolved }: { run: ErpChangeRunbookView; row: StockDifferenceRow; mayResolve: boolean; onResolved: () => void }) {
  const [mode, setMode] = useState<"fix_in_new_erp" | StockResolutionCommand>("fix_in_new_erp");
  const [locationKind, setLocationKind] = useState("warehouse"); const [locationId, setLocationId] = useState<string | null>(null);
  const [identifierType, setIdentifierType] = useState("manufacturer_serial"); const [identifier, setIdentifier] = useState("");
  const [otherUnit, setOtherUnit] = useState<string | null>(null); const [keepThis, setKeepThis] = useState(true); const [note, setNote] = useState("");
  const action = useAction();
  const where = <Kv rows={[["Module", row.moduleLocation ?? "not in stock"], ["Current ERP", row.sourceLocation ?? "not on hand"], ["New ERP", row.targetLocation ?? "not on hand"]]} />;
  if (row.status === "resolved") return <div className="erp-drawer-form">{where}<p>{resolutionLabel(row.resolutionCommand)}{row.resolvedBy ? ` by ${row.resolvedBy}` : ""}{row.resolvedAt ? ` · ${date(row.resolvedAt)}` : ""}</p></div>;
  if (!mayResolve) return <div className="erp-drawer-form">{where}<p className="muted">The inventory controller resolves stock differences during step 3.</p></div>;
  const unitId = row.unitId;
  const ready = mode === "fix_in_new_erp" || (mode === "change_custody" && !!locationKind) || (mode === "correct_identifier" && !!identifier.trim()) || (mode === "merge_units" && !!otherUnit && !!unitId);
  const submit = () => void action.run(async () => {
    if (mode !== "fix_in_new_erp") {
      await erpChangeApi.resolveStock(run.runId, row.differenceId, {
        command: mode, commandId: crypto.randomUUID(), note: note.trim() || undefined,
        ...(mode === "change_custody" ? { locationKind, locationId: locationId || undefined } : {}),
        ...(mode === "correct_identifier" ? { identifierType, newIdentifier: identifier.trim() } : {}),
        ...(mode === "merge_units" && unitId && otherUnit ? { survivorUnitId: keepThis ? unitId : otherUnit, absorbedUnitId: keepThis ? otherUnit : unitId } : {}),
      });
      onResolved();
    }
    closeDrawer();
  }, mode === "fix_in_new_erp" ? "Left for the new ERP; it is compared again on the next run." : "Unit command recorded.");
  return <form className="erp-drawer-form" onSubmit={event => { event.preventDefault(); submit(); }}>
    <h3>Where each side has it</h3>{where}
    <fieldset className="erp-options"><legend>Resolve</legend>
      <label className={mode === "fix_in_new_erp" ? "on" : ""}><input type="radio" name="stock-mode" checked={mode === "fix_in_new_erp"} onChange={() => setMode("fix_in_new_erp")} />Fix in the new ERP<span className="s">compared again on the next run</span></label>
      {unitId && <label className={mode === "change_custody" ? "on" : ""}><input type="radio" name="stock-mode" checked={mode === "change_custody"} onChange={() => setMode("change_custody")} />Change custody here<span className="s">unit command · reason ERP change reconciliation</span></label>}
      {unitId && <label className={mode === "correct_identifier" ? "on" : ""}><input type="radio" name="stock-mode" checked={mode === "correct_identifier"} onChange={() => setMode("correct_identifier")} />Correct the identifier<span className="s">the module's serial was recorded wrongly</span></label>}
      {unitId && <label className={mode === "merge_units" ? "on" : ""}><input type="radio" name="stock-mode" checked={mode === "merge_units"} onChange={() => setMode("merge_units")} />Merge with another unit<span className="s">two module units are the same physical unit</span></label>}
    </fieldset>
    {mode === "change_custody" && <><label className="field"><span className="lbl">Location kind</span><select className="select" value={locationKind} onChange={event => setLocationKind(event.target.value)}>{locationKinds.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
      <LocationField id={`stock-location-${row.differenceId}`} label="Location (optional)" kind={locationKind} value={locationId} onChange={setLocationId} optional /></>}
    {mode === "correct_identifier" && <><label className="field"><span className="lbl">Identifier type</span><select className="select" value={identifierType} onChange={event => setIdentifierType(event.target.value)}><option value="manufacturer_serial">Manufacturer serial</option><option value="internal_serial">Internal serial</option><option value="vendor_serial">Vendor serial</option><option value="customer_serial">Customer serial</option><option value="mac">MAC address</option><option value="imei">IMEI</option></select></label>
      <label className="field"><span className="lbl">Correct identifier (paste exactly)</span><input className="input mono" required maxLength={120} value={identifier} onChange={event => setIdentifier(event.target.value)} /></label></>}
    {mode === "merge_units" && <><UnitField id={`stock-merge-${row.differenceId}`} label="The other unit" value={otherUnit} onChange={setOtherUnit} />
      <label className="erp-confirm"><input type="checkbox" checked={keepThis} onChange={event => setKeepThis(event.target.checked)} />Keep {row.identifier} as the surviving unit</label></>}
    {mode !== "fix_in_new_erp" && <label className="field"><span className="lbl">Note (optional)</span><textarea className="input" maxLength={500} value={note} onChange={event => setNote(event.target.value)} /></label>}
    <button className="btn primary" disabled={action.busy || !ready}>Save</button>{action.feedback}
  </form>;
}

function openSignOff(run: ErpChangeRunbookView, onSigned: () => void) {
  openDrawer({ eyebrow: "Step 3", title: "Sign off the rest" }, <SignOffForm run={run} onSigned={onSigned} />);
}

function SignOffForm({ run, onSigned }: { run: ErpChangeRunbookView; onSigned: () => void }) {
  const [note, setNote] = useState(""); const [confirmed, setConfirmed] = useState(false); const action = useAction();
  const residual = run.openDifferences - run.coveredOpenDifferences;
  return <form className="erp-drawer-form" onSubmit={event => { event.preventDefault(); void action.run(async () => { await erpChangeApi.signOffStock(run.runId, note.trim()); onSigned(); closeDrawer(); }, "Residual differences signed off."); }}>
    {run.coveredOpenDifferences > 0 && <div className="note warn">{run.coveredOpenDifferences.toLocaleString()} open differences are on units with live cover. They block step 3 and cannot be signed off; resolve them first.</div>}
    <p>You sign off the {residual.toLocaleString()} open differences on units without live cover. They stay recorded and audited; nothing changes on the units.</p>
    <label className="field"><span className="lbl">Note (required)</span><textarea className="input" required maxLength={1000} value={note} onChange={event => setNote(event.target.value)} /></label>
    <label className="erp-confirm"><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} />I am the inventory controller and accept these differences.</label>
    <button className="btn primary" disabled={action.busy || !note.trim() || !confirmed}><Icon name="check" />Sign off</button>{action.feedback}
  </form>;
}
