import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import { Link, useLocation } from "react-router";
import { KpiStrip, StatusBadge, type GridColumn } from "../components";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { closeDrawer, openDrawer } from "../shell/drawerStore";
import { ErrorNote, Section } from "../units/common";
import { date, PagedGrid, useAction, useCursorPage } from "./AdminCommon";
import { adminApi } from "./adminApi";
import { Registration } from "./Connections";
import { ErpChangeAccess, ErpChangePage, NoActiveChange, useActiveRun, useErpChangeRights } from "./ErpChangeCommon";
import { erpChangeApi } from "./erpChangeApi";
import {
  canCancel, canReverse, checklistLabel, checklistState, checklistTone, commandName, connectionLine, criterionLabel, criterionTone,
  divergenceReviewer, divergenceState, exitCriteriaStates, mayDecideDivergence, runbookKpis, stepSummaries, stepTitles, validationCommand,
  type ErpChangeRights,
} from "./erpChangeModel";
import type { CutoverChecklistItemView, CutoverChecklistView, ErpChangeCleanPeriodView, ErpChangeDivergencePage, ErpChangeDivergenceRow, ErpChangeRunbookView } from "./erpChangeTypes";
import { parallelDate } from "./parallelRunModel";

export function Component() { return <ErpChangeAccess><RunbookScreen /></ErpChangeAccess>; }

function RunbookScreen() {
  const rights = useErpChangeRights();
  const { run, setRun, missing, error, loading, refresh } = useActiveRun();
  const { hash } = useLocation();
  useEffect(() => { if (run && hash) document.getElementById(hash.slice(1))?.scrollIntoView({ block: "start" }); }, [run, hash]);
  const replace = useCallback((next: ErpChangeRunbookView) => setRun(next), [setRun]);

  const eyebrow = run ? `from "${run.source.name}" (connector ${run.source.connectorCode}) to "${run.target.name}" (connector ${run.target.connectorCode}) · started ${parallelDate(run.startedAt, true)} by ${run.startedBy}` : "Integration";
  const actions = run && rights.control ? <RunActions run={run} onChanged={replace} /> : undefined;
  return <><ErpChangePage title="ERP change" eyebrow={eyebrow} run={run} actions={actions}
    sub="Move the tenant to a new ERP in six steps. Unit history never moves: the module keeps it. The new ERP is read for an opening position only.">
    <ErrorNote error={error} onReload={() => void refresh()} />
    {loading && !run && !missing && <p role="status">Loading ERP change…</p>}
    {missing && <><NoActiveChange control={rights.control} />{rights.control && <StartErpChange onStarted={replace} />}</>}
    {run && <RunbookBody run={run} rights={rights} onChanged={replace} refresh={() => void refresh()} />}
  </ErpChangePage><Rail label="ERP change"><Section title="Unit history"><p className="muted">Every correction made during an ERP change is an ordinary unit command with the reason "ERP change reconciliation". Nothing is edited directly, and the unit event log is never rewritten.</p></Section></Rail></>;
}

function RunActions({ run, onChanged }: { run: ErpChangeRunbookView; onChanged: (value: ErpChangeRunbookView) => void }) {
  const action = useAction();
  const latest = run.checks.filter(check => check.step === run.currentStep).sort((left, right) => right.checkedAt.localeCompare(left.checkedAt))[0];
  const passed = latest?.passed ?? false;
  if (run.status !== "active") return null;
  return <>
    {canCancel(run) && <button type="button" className="btn" onClick={() => openCancel(run, onChanged)}>Roll back to the current ERP…</button>}
    {canReverse(run) && <button type="button" className="btn" onClick={() => openReverse(run, onChanged)}>Start an ERP change back…</button>}
    <button type="button" className={`btn${passed ? "" : " primary"}`} disabled={action.busy} onClick={() => void action.run(async () => onChanged(await erpChangeApi.check(run.runId)), "Exit criterion checked.")}><Icon name="check" />Check step {run.currentStep} exit criterion</button>
    {passed && <button type="button" className="btn primary" disabled={action.busy} onClick={() => void action.run(async () => onChanged(await erpChangeApi.advance(run.runId)), run.currentStep === 6 ? "ERP change completed." : `Step ${run.currentStep + 1} opened.`)}>{run.currentStep === 6 ? "Complete the ERP change" : `Open step ${run.currentStep + 1}`}</button>}
    {action.feedback}
  </>;
}

function RunbookBody({ run, rights, onChanged, refresh }: { run: ErpChangeRunbookView; rights: ErpChangeRights; onChanged: (value: ErpChangeRunbookView) => void; refresh: () => void }) {
  const steps = stepSummaries(run); const criteria = exitCriteriaStates(run);
  const target = (step: number) => step === 2 ? "/integration/erp-change/master-data" : step === 3 ? "/integration/erp-change/stock" : step === 4 ? "/integration/erp-change#dual" : step === 5 ? "/integration/erp-change#cutover" : "/integration/erp-change";
  return <>
    {run.status !== "active" && <div className={`note ${run.status === "cancelled" ? "warn" : "ok"}`} role="status"><strong>{run.status === "cancelled" ? "Rolled back" : "Completed"}</strong> {run.closedAt ? parallelDate(run.closedAt, true) : ""}{run.closedBy ? ` by ${run.closedBy}` : ""}{run.closeReason ? ` · ${run.closeReason}` : ""}</div>}
    <ol className="erp-stages" aria-label="Steps">{steps.map(step => <li key={step.step}><Link to={target(step.step)} aria-current={step.current ? "true" : undefined}><div className="l">{step.label}</div><div className={`n${step.tone === "bad" ? " bad-text" : step.tone === "ok" ? " ok-text" : ""}`}>{step.value}</div><div className="s">{step.detail}</div></Link></li>)}</ol>
    <KpiStrip label="ERP change figures" kpis={runbookKpis(run)} />
    <div className="erp-change-columns">
      <section className="card" aria-labelledby="erp-exit"><div className="card-header"><h2 id="erp-exit">Exit criteria</h2><span className="right"><span className="muted">a step opens only when the one before passes</span></span></div><div className="card-body">
        <ul className="erp-checks">{criteria.map(item => <li key={item.step}><span>{item.label}<span className="s">{item.detail}</span></span><StatusBadge tone={criterionTone(item.state)}>{criterionLabel(item.state)}</StatusBadge></li>)}</ul>
      </div></section>
      <section className="card" aria-labelledby="erp-connections"><div className="card-header"><h2 id="erp-connections">Connections</h2></div><div className="card-body">
        <div className="pair">
          <div className="side"><div className="k">Current · {run.source.connectorCode}</div><strong>{run.source.name}</strong><div className="muted">{connectionLine(run.source)}</div></div>
          <div className="arrow" aria-hidden="true">→</div>
          <div className="side"><div className="k">New · {run.target.connectorCode}</div><strong>{run.target.name}</strong><div className="muted">{connectionLine(run.target)}</div></div>
        </div>
        <p className="hint erp-pair-hint">{run.currentStep < 5 ? "Rolling back before step 5 pauses the new connection and keeps its sign-in, so a later restart is quick. The current connection is untouched. Nothing the module recorded because of the new ERP has to be undone by hand: each change was an ordinary unit event." : "After cutover there is no one-click undo. To go back, start a new ERP change from the new ERP to the old one, while the old connection is not archived."}</p>
      </div></section>
    </div>
    {run.status === "active" && <StepWork run={run} rights={rights} onChanged={onChanged} refresh={refresh} />}
    <DivergencesCard run={run} rights={rights} refresh={refresh} />
    <CutoverCard run={run} rights={rights} refresh={refresh} />
  </>;
}

function StepWork({ run, rights, onChanged, refresh }: { run: ErpChangeRunbookView; rights: ErpChangeRights; onChanged: (value: ErpChangeRunbookView) => void; refresh: () => void }) {
  const action = useAction();
  const reload = async () => onChanged(await erpChangeApi.detail(run.runId));
  const title = `Step ${run.currentStep} · ${stepTitles[run.currentStep - 1] ?? ""}`;
  let body: ReactNode = null;
  if (run.currentStep === 1) body = <p>Check that the new connection is healthy, read-only and sends nothing. Then check the exit criterion.</p>;
  if (run.currentStep === 2) body = <><p>Match items and parties with live units to the new ERP's records. Two different item codes are never merged automatically; anything unclear goes to the exceptions list.</p>{rights.control && <button type="button" className="btn" disabled={action.busy} onClick={() => void action.run(async () => { await erpChangeApi.remap(run.runId); await reload(); }, "Master data re-mapped. Review the exceptions.")}>Re-map master data</button>} <Link className="btn" to="/integration/erp-change/master-data">Master data exceptions</Link></>;
  if (run.currentStep === 3) body = <><p>The inventory controller compares stock per unit, resolves differences with unit commands and signs off the rest.</p><Link className="btn" to="/integration/erp-change/stock">Stock differences</Link></>;
  if (run.currentStep === 4) body = <DualRunWork run={run} rights={rights} onChanged={onChanged} refresh={refresh} />;
  if (run.currentStep === 5) body = <><p>Work through the cutover checklist below in order. The new ERP stays read-only until every outbound kind has been checked on it.</p><Link className="btn" to="/integration/erp-change#cutover">Cutover checklist</Link></>;
  if (run.currentStep === 6) body = <><p>Archive the old connection: its sign-in is retired and polling stops. Its id mappings and inbound history are kept, so unit history still shows its documents.</p>{run.archive && <p className="muted">{run.archive.sourceArchived ? `Archived ${date(run.archive.archivedAt)}` : "Not archived yet"} · {run.archive.activeConnections.toLocaleString()} active connection{run.archive.activeConnections === 1 ? "" : "s"} · {run.archive.retainedMappings.toLocaleString()} mappings kept</p>}{rights.control && !run.archive?.sourceArchived && <button type="button" className="btn primary" disabled={action.busy} onClick={() => void action.run(async () => { await erpChangeApi.archive(run.runId); await reload(); }, "Old connection archived.")}>Archive the old ERP</button>}</>;
  return <section className="card" aria-labelledby="erp-step-work"><div className="card-header"><h2 id="erp-step-work">{title}</h2></div><div className="card-body stack">{body}{action.feedback}</div></section>;
}

function DualRunWork({ run, rights, onChanged, refresh }: { run: ErpChangeRunbookView; rights: ErpChangeRights; onChanged: (value: ErpChangeRunbookView) => void; refresh: () => void }) {
  const action = useAction(); const dual = run.dualRun;
  const [setting, setSetting] = useState<ErpChangeCleanPeriodView | null>(null); const [days, setDays] = useState(7); const [error, setError] = useState<unknown>(null);
  useEffect(() => { const controller = new AbortController(); erpChangeApi.cleanPeriod(controller.signal).then(value => { if (!controller.signal.aborted) { setSetting(value); setDays(value.days); } }).catch(reason => { if (!controller.signal.aborted) setError(reason); }); return () => controller.abort(); }, []);
  const reload = async () => { onChanged(await erpChangeApi.detail(run.runId)); refresh(); };
  return <>
    <p>The current ERP stays authoritative on stock and documents; the new ERP is read-only and its version is kept and compared. Step 4 passes after at least {dual?.minimumDays ?? 30} days and the agreed run of clean days.</p>
    {dual?.startedAt ? <p className="muted">Started {parallelDate(dual.startedAt, true)} · day {dual.elapsedDays.toLocaleString()} of at least {dual.minimumDays.toLocaleString()} · {dual.consecutiveCleanDays.toLocaleString()} of {dual.cleanDaysRequired.toLocaleString()} clean days in a row · last checked {parallelDate(dual.lastCheckedOn)}</p>
      : rights.control && <button type="button" className="btn primary" disabled={action.busy} onClick={() => void action.run(async () => { await erpChangeApi.startDualRun(run.runId); await reload(); }, "Dual run started.")}>Start the dual run</button>}
    {dual?.startedAt && rights.control && <button type="button" className="btn" disabled={action.busy} onClick={() => void action.run(async () => { await erpChangeApi.dailyCheck(run.runId); await reload(); }, "Daily check recorded.")}><Icon name="reconcile" />Run today's check</button>}
    <ErrorNote error={error} />
    {setting && <form className="toolbar" onSubmit={event => { event.preventDefault(); void action.run(async () => { const saved = await erpChangeApi.saveCleanPeriod(run.runId, days, setting.settingsVersion); setSetting(saved); setDays(saved.days); await reload(); }, "Clean period saved."); }}>
      <label className="field"><span className="lbl">Clean days in a row (tenant setting, 1–90)</span><input className="input" type="number" min={1} max={90} required disabled={!rights.control} value={days} onChange={event => setDays(Number(event.target.value))} /></label>
      {rights.control && <button className="btn" disabled={action.busy || days === setting.days || days < 1 || days > 90}>Save clean period</button>}
    </form>}
    {action.feedback}
  </>;
}

function DivergencesCard({ run, rights, refresh }: { run: ErpChangeRunbookView; rights: ErpChangeRights; refresh: () => void }) {
  const [kind, setKind] = useState(""); const [status, setStatus] = useState("open"); const [query, setQuery] = useState("");
  const fetch = useCallback((cursor?: string, signal?: AbortSignal) => erpChangeApi.divergences(run.runId, { kind, status, query: query.trim() }, cursor, signal), [run.runId, kind, status, query]);
  const source = useCursorPage<ErpChangeDivergenceRow, ErpChangeDivergencePage>(fetch);
  const columns = useMemo<GridColumn<ErpChangeDivergenceRow>[]>(() => [
    { key: "found", label: "Found", width: 13, render: row => date(row.firstSeenAt) },
    { key: "record", label: "Record", width: 19, render: row => <span className="primary"><span className={`t${row.kind === "stock" ? " serial mono" : ""}`}>{row.subjectRef}</span><span className="sub">{row.kind === "stock" ? "Stock" : "Document"}</span></span> },
    { key: "source", label: "Current ERP says", width: 24, render: row => row.sourceValue },
    { key: "target", label: "New ERP says", width: 24, render: row => row.targetValue },
    { key: "review", label: "Review", width: 20, render: row => row.status === "open" ? <StatusBadge tone={new Date(row.reviewDueAt).getTime() < Date.now() ? "warn" : "neutral"}>{divergenceReviewer(row)}</StatusBadge> : <StatusBadge tone="ok">{divergenceState(row)}</StatusBadge> },
  ], []);
  const counts = source.result;
  return <section className="card" id="dual" aria-labelledby="erp-dual"><div className="card-header"><h2 id="erp-dual">Dual-run divergences <span className="meta">step 4 · the current ERP wins on stock and documents until cutover; the new ERP's version is kept and compared{counts ? ` · ${counts.open.toLocaleString()} open, ${counts.agedOpen.toLocaleString()} past review date, ${counts.resolved.toLocaleString()} resolved` : ""}</span></h2></div>
    {!run.dualRun?.startedAt ? <div className="card-body"><p className="muted">Shown once step 4 starts.</p></div> : <>
      <div className="erp-filters"><label className="field grow"><span className="lbl">Search this table as you type</span><input className="input" type="search" placeholder="Serial or document" value={query} onChange={event => setQuery(event.target.value)} /></label>
        <label className="field"><span className="lbl">Kind</span><select className="select" value={kind} onChange={event => setKind(event.target.value)}><option value="">All</option><option value="stock">Stock</option><option value="document">Documents</option></select></label>
        <label className="field"><span className="lbl">Status</span><select className="select" value={status} onChange={event => setStatus(event.target.value)}><option value="open">Open</option><option value="resolved">Resolved</option><option value="">All</option></select></label></div>
      <PagedGrid source={source} columns={columns} rowId={row => row.divergenceId} select={row => openDivergence(run, row, rights, () => { source.refresh(); refresh(); })} empty="No divergences match." />
    </>}
  </section>;
}

function openDivergence(run: ErpChangeRunbookView, row: ErpChangeDivergenceRow, rights: ErpChangeRights, onDecided: () => void) {
  openDrawer({ eyebrow: "Divergence", title: row.subjectRef, mono: row.kind === "stock" }, <DivergenceDecision run={run} row={row} allowed={mayDecideDivergence(row, rights)} onDecided={onDecided} />);
}

function DivergenceDecision({ run, row, allowed, onDecided }: { run: ErpChangeRunbookView; row: ErpChangeDivergenceRow; allowed: boolean; onDecided: () => void }) {
  const [note, setNote] = useState(""); const action = useAction();
  const decide = (work: () => Promise<unknown>, message: string) => void action.run(async () => { await work(); onDecided(); closeDrawer(); }, message);
  return <div className="erp-drawer-form">
    <div className="pair"><div className="side"><div className="k">Current ERP · wins until cutover</div>{row.sourceValue}</div><div className="arrow" aria-hidden="true">≠</div><div className="side bad"><div className="k">New ERP</div>{row.targetValue}</div></div>
    {row.status === "resolved" ? <p>{divergenceState(row)}{row.resolvedBy ? ` by ${row.resolvedBy}` : ""}{row.resolvedAt ? ` · ${date(row.resolvedAt)}` : ""}{row.resolutionNote ? ` · ${row.resolutionNote}` : ""}</p> : allowed ? <>
      <p className="hint">The module keeps the current ERP's value. Fix the new ERP before cutover, or record here why the difference is expected.</p>
      <button type="button" className="btn" disabled={action.busy} onClick={() => decide(() => erpChangeApi.acceptSource(run.runId, row.kind, row.divergenceId), "Current ERP value kept.")}>Keep the current ERP's value</button>
      <label className="field"><span className="lbl">Explanation</span><textarea className="input" maxLength={2000} value={note} onChange={event => setNote(event.target.value)} /></label>
      <button type="button" className="btn primary" disabled={action.busy || !note.trim()} onClick={() => decide(() => erpChangeApi.explain(run.runId, row.kind, row.divergenceId, note.trim()), "Marked explained.")}>Mark explained</button>
      {row.kind === "stock" && row.subjectId && <Link className="btn" to={`/units/${row.subjectId}`}>Open unit</Link>}
    </> : <p className="muted">{divergenceReviewer(row)} decides this divergence.</p>}
    {action.feedback}
  </div>;
}

function CutoverCard({ run, rights, refresh }: { run: ErpChangeRunbookView; rights: ErpChangeRights; refresh: () => void }) {
  const [view, setView] = useState<CutoverChecklistView | null>(null); const [error, setError] = useState<unknown>(null); const [mode, setMode] = useState<"reference_only" | "documents">("documents"); const action = useAction();
  const started = (run.cutoverChecklist?.length ?? 0) > 0;
  useEffect(() => {
    if (!started) { setView(null); return; }
    const controller = new AbortController(); erpChangeApi.cutover(run.runId, controller.signal).then(value => { if (!controller.signal.aborted) setView(value); }).catch(reason => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, [run.runId, started, run.cutoverChecklist]);
  const apply = (work: () => Promise<CutoverChecklistView>, message: string) => void action.run(async () => { setView(await work()); refresh(); }, message);
  const control = rights.control && run.status === "active" && run.currentStep === 5;
  const items = view?.items ?? [];
  return <section className="card" id="cutover" aria-labelledby="erp-cutover"><div className="card-header"><h2 id="erp-cutover">Cutover checklist <span className="meta">step 5 · in this order</span></h2>{started && <a className="btn sm" download href={erpChangeApi.pendingActionsCsvUrl(run.runId)}><Icon name="down" />Pending returns and claims CSV</a>}</div><div className="card-body stack">
    <ErrorNote error={error} />
    {!started && (control ? <form className="toolbar" onSubmit={event => { event.preventDefault(); apply(() => erpChangeApi.beginCutover(run.runId, mode), "Cutover started: outbound to the current ERP is stopped."); }}>
      <label className="field"><span className="lbl">Outbound mode on the new ERP after cutover</span><select className="select" value={mode} onChange={event => setMode(event.target.value as "reference_only" | "documents")}><option value="documents">Documents</option><option value="reference_only">References only</option></select></label>
      <button className="btn primary" disabled={action.busy}>Start cutover</button></form>
      : <p className="muted">{run.currentStep < 5 ? "Opens after the dual run passes." : "A tenant administrator or an integration operator starts the cutover."}</p>)}
    {items.length > 0 && <ol className="erp-chain">{items.map((item, index) => <CutoverRow key={item.code} index={index + 1} item={item} runId={run.runId} control={control} busy={action.busy} apply={apply} />)}</ol>}
    {view && !view.canAdvance && started && <p className="muted">Step 5 passes when every item above has passed.</p>}
    {action.feedback}
  </div></section>;
}

function CutoverRow({ index, item, runId, control, busy, apply }: { index: number; item: CutoverChecklistItemView; runId: string; control: boolean; busy: boolean; apply: (work: () => Promise<CutoverChecklistView>, message: string) => void }) {
  const [title, sub] = checklistLabel(item); const command = validationCommand(item.code); const waiting = item.state !== "passed";
  let act: ReactNode = null;
  if (control && waiting) {
    if (item.code === "drain_outbox") act = <button type="button" className="btn sm" disabled={busy} onClick={() => apply(() => erpChangeApi.drain(runId), "Queue drained to the new ERP where possible.")}>Drain now</button>;
    else if (item.code === "enable_target") act = <button type="button" className="btn sm primary" disabled={busy} onClick={() => apply(() => erpChangeApi.enableTarget(runId), "New ERP switched to read-write.")}>Switch to read-write</button>;
    else if (item.code === "reconcile_after_cutover") act = <button type="button" className="btn sm" disabled={busy} onClick={() => apply(() => erpChangeApi.reconcileAfterCutover(runId), "Reconciliation run.")}>Run reconciliation</button>;
    else if (command) act = <button type="button" className="btn sm" onClick={() => openEvidence(`Check ${commandName(command)}`, "Evidence (test company run or manual check)", true, (evidence, method) => erpChangeApi.recordValidation(runId, command, method, evidence), view => apply(async () => view, "Check recorded."))}>Record check…</button>;
    else if (item.code === "first_outbound_confirmed") act = <button type="button" className="btn sm" onClick={() => openFirstOutbound(runId, view => apply(async () => view, "First document confirmed."))}>Confirm a document…</button>;
  }
  return <li><span className="v">{index}</span><span><span className="t">{title}</span><span className="s">{item.detail || sub}</span>{item.completedBy && <span className="s">{item.completedBy}{item.completedAt ? ` · ${date(item.completedAt)}` : ""}{item.evidence ? ` · ${item.evidence}` : ""}</span>}{act && <span className="erp-item-actions">{act}</span>}</span><StatusBadge tone={checklistTone(item.state)}>{checklistState(item.state)}</StatusBadge></li>;
}

function openEvidence(title: string, prompt: string, withMethod: boolean, save: (evidence: string, method: "test_company" | "manual") => Promise<CutoverChecklistView>, onSaved: (view: CutoverChecklistView) => void) {
  openDrawer({ eyebrow: "Cutover checklist", title }, <EvidenceForm prompt={prompt} withMethod={withMethod} save={save} onSaved={onSaved} />);
}

function EvidenceForm({ prompt, withMethod, save, onSaved }: { prompt: string; withMethod: boolean; save: (evidence: string, method: "test_company" | "manual") => Promise<CutoverChecklistView>; onSaved: (view: CutoverChecklistView) => void }) {
  const [evidence, setEvidence] = useState(""); const [method, setMethod] = useState<"test_company" | "manual">("test_company"); const action = useAction();
  return <form className="erp-drawer-form" onSubmit={event => { event.preventDefault(); void action.run(async () => { onSaved(await save(evidence.trim(), method)); closeDrawer(); }, "Recorded."); }}>
    {withMethod && <fieldset className="erp-options"><legend>How it was checked</legend>{([["test_company", "Run in the new ERP's test company"], ["manual", "Checked by hand"]] as const).map(([value, label]) => <label key={value} className={method === value ? "on" : ""}><input type="radio" name="validation-method" checked={method === value} onChange={() => setMethod(value)} />{label}</label>)}</fieldset>}
    <label className="field"><span className="lbl">{prompt}</span><textarea className="input" required maxLength={1000} value={evidence} onChange={event => setEvidence(event.target.value)} /></label>
    <button className="btn primary" disabled={action.busy || !evidence.trim()}>Record</button>{action.feedback}
  </form>;
}

function openFirstOutbound(runId: string, onSaved: (view: CutoverChecklistView) => void) {
  openDrawer({ eyebrow: "Cutover checklist", title: "Confirm the first document" }, <FirstOutboundForm runId={runId} onSaved={onSaved} />);
}

function FirstOutboundForm({ runId, onSaved }: { runId: string; onSaved: (view: CutoverChecklistView) => void }) {
  const [commandType, setCommandType] = useState("CreateCreditMemo"); const [reference, setReference] = useState(""); const [evidence, setEvidence] = useState(""); const action = useAction();
  return <form className="erp-drawer-form" onSubmit={event => { event.preventDefault(); void action.run(async () => { onSaved(await erpChangeApi.confirmFirstOutbound(runId, commandType, reference.trim(), evidence.trim())); closeDrawer(); }, "Confirmed."); }}>
    <label className="field"><span className="lbl">Kind of document</span><select className="select" value={commandType} onChange={event => setCommandType(event.target.value)}>{["CreateCreditMemo", "CreateReplacementDelivery", "PostWarrantyExpense", "ChangeStockStatusOrLocation", "CreateServiceOrder", "AttachDocument"].map(value => <option key={value} value={value}>{commandName(value)}</option>)}</select></label>
    <label className="field"><span className="lbl">Document number in the new ERP (paste exactly)</span><input className="input mono" required maxLength={120} value={reference} onChange={event => setReference(event.target.value)} /></label>
    <label className="field"><span className="lbl">Evidence</span><textarea className="input" required maxLength={1000} value={evidence} onChange={event => setEvidence(event.target.value)} /></label>
    <button className="btn primary" disabled={action.busy || !reference.trim() || !evidence.trim()}>Confirm document</button>{action.feedback}
  </form>;
}

function openCancel(run: ErpChangeRunbookView, onChanged: (value: ErpChangeRunbookView) => void) {
  openDrawer({ eyebrow: "ERP change", title: "Roll back to the current ERP" }, <CancelForm run={run} onChanged={onChanged} />);
}

function CancelForm({ run, onChanged }: { run: ErpChangeRunbookView; onChanged: (value: ErpChangeRunbookView) => void }) {
  const [reason, setReason] = useState(""); const [confirmed, setConfirmed] = useState(false); const action = useAction();
  return <form className="erp-drawer-form" onSubmit={event => { event.preventDefault(); void action.run(async () => { onChanged(await erpChangeApi.cancel(run.runId, reason.trim())); closeDrawer(); }, "ERP change rolled back."); }}>
    <div className="note erp-consequence">"{run.target.name}" is paused (read-only, no outbound) and keeps its sign-in so a later restart is quick. "{run.source.name}" carries on unchanged.</div>
    <label className="field"><span className="lbl">Reason (required)</span><textarea className="input" required maxLength={500} value={reason} onChange={event => setReason(event.target.value)} /></label>
    <label className="erp-confirm"><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} />I understand the change stops here.</label>
    <button className="btn danger" disabled={action.busy || !reason.trim() || !confirmed}><Icon name="x" />Roll back</button>{action.feedback}
  </form>;
}

function openReverse(run: ErpChangeRunbookView, onChanged: (value: ErpChangeRunbookView) => void) {
  openDrawer({ eyebrow: "ERP change", title: "Start an ERP change back" }, <ReverseForm run={run} onChanged={onChanged} />);
}

function ReverseForm({ run, onChanged }: { run: ErpChangeRunbookView; onChanged: (value: ErpChangeRunbookView) => void }) {
  const action = useAction();
  return <div className="erp-drawer-form">
    <p>There is no one-click undo after cutover. This starts a new ERP change at step 1, from "{run.target.name}" back to "{run.source.name}", and every step runs again with its own exit criteria.</p>
    <p className="muted">If "{run.source.name}" has been archived, a new connection to it is needed instead.</p>
    <button type="button" className="btn primary" disabled={action.busy} onClick={() => void action.run(async () => { onChanged(await erpChangeApi.reverse(run.runId)); closeDrawer(); }, "New ERP change started.")}>Start the change back</button>{action.feedback}
  </div>;
}

function StartErpChange({ onStarted }: { onStarted: (value: ErpChangeRunbookView) => void }) {
  const fetch = useCallback((cursor?: string, signal?: AbortSignal) => adminApi.connections(cursor, false, signal), []);
  const connections = useCursorPage(fetch);
  const [sourceId, setSourceId] = useState("");
  return <section className="card" aria-labelledby="erp-start"><div className="card-header"><h2 id="erp-start">Start an ERP change</h2></div><div className="card-body stack">
    <label className="field"><span className="lbl">Current ERP connection</span><select className="select" value={sourceId} onChange={event => setSourceId(event.target.value)}><option value="">Choose a connection</option>{connections.result?.items.map(item => <option key={item.connectionId} value={item.connectionId}>{item.connectionName} · {item.connectorCode}</option>)}</select></label>
    <ErrorNote error={connections.error} />
    {sourceId && <Registration title="New ERP connection" submitLabel="Register read-only and start the ERP change" success="ERP change started at step 1."
      submit={async body => { const started = await erpChangeApi.start(sourceId, body); onStarted(started); return started.target.connectionId; }} complete={() => undefined} />}
  </div></section>;
}
