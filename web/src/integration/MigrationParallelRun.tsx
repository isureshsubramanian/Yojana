import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { useSession } from "../session/session";
import { Rail } from "../shell/Drawer";
import { openDrawer } from "../shell/drawerStore";
import { ErrorNote, Section } from "../units/common";
import { fmtDateTime } from "../units/format";
import { date, useCursorPage } from "./AdminCommon";
import { MigrationAccess, MigrationPage } from "./MigrationCommon";
import { adminApi } from "./adminApi";
import { mayOperateIntegration } from "./model";
import type {
  CoexistenceConflictPage, CoexistenceConflictView, GoLiveItemView, GoLiveRecordView, HandoverStepView,
  ParallelRunDashboard, Run,
} from "./adminTypes";
import { parallelRunApi } from "./parallelRunApi";
import {
  conflictRemoteLabel, goLiveStatus, goLiveTone, handoverState, handoverTone, parallelDate, parallelEyebrow, parallelKpis,
} from "./parallelRunModel";

export function Component() { return <MigrationAccess><ParallelRunScreen /></MigrationAccess>; }

function ParallelRunScreen() {
  const [params, setParams] = useSearchParams();
  const connectionId = params.get("connection") ?? "";
  const roles = useSession().roles;
  const admin = roles.includes("tenant_administrator");
  const operator = mayOperateIntegration(roles);
  const fetchConnections = useCallback((cursor?: string, signal?: AbortSignal) => adminApi.connectionChoices(cursor, signal), []);
  const connections = useCursorPage(fetchConnections);
  const [dashboard, setDashboard] = useState<ParallelRunDashboard | null>(null);
  const [record, setRecord] = useState<GoLiveRecordView | null>(null);
  const [conflicts, setConflicts] = useState<CoexistenceConflictPage | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [issueBusy, setIssueBusy] = useState(false);

  useEffect(() => {
    if (connectionId || !connections.result?.items[0]) return;
    setParams({ connection: connections.result.items[0].connectionId }, { replace: true });
  }, [connectionId, connections.result, setParams]);

  useEffect(() => { setDashboard(null); setRecord(null); setConflicts(null); setError(null); }, [connectionId]);

  const load = useCallback(async (signal?: AbortSignal) => {
    if (!connectionId) { setDashboard(null); setConflicts(null); return; }
    setLoading(true); setError(null);
    try {
      const next = await parallelRunApi.dashboard(connectionId, signal);
      if (signal?.aborted) return;
      setDashboard(next);
      // Conflicts and reconciliation runs are operations' lists; a migration lead reads the dashboard counts only.
      if (next.status !== "not_started" && operator) {
        const page = await parallelRunApi.conflicts(connectionId, undefined, signal);
        if (!signal?.aborted) setConflicts(page);
      } else setConflicts(null);
    } catch (reason) {
      if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason);
    } finally { if (!signal?.aborted) setLoading(false); }
  }, [connectionId, operator]);

  useEffect(() => {
    const controller = new AbortController(); void load(controller.signal); return () => controller.abort();
  }, [load]);

  useEffect(() => {
    const recordId = dashboard?.goLive.recordId;
    if (!recordId) { setRecord(null); return; }
    const controller = new AbortController();
    parallelRunApi.goLiveRecord(recordId, controller.signal).then(value => { if (!controller.signal.aborted) setRecord(value); })
      .catch(reason => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, [dashboard?.goLive.recordId]);

  const replaceDashboard = useCallback((next: ParallelRunDashboard) => { setDashboard(next); setError(null); }, []);
  const refresh = useCallback(() => load(), [load]);
  const selectedConnection = connections.result?.items.find(item => item.connectionId === connectionId);
  const connectionLabel = dashboard?.connectionLabel ?? selectedConnection?.connectionName ?? "Choose a connection";
  const actions = connectionId ? <>
    <Link className="btn" to={`/integration?connection=${encodeURIComponent(connectionId)}`}>Connection health</Link>
    <Link className="btn" to={`/integration/migration?connection=${encodeURIComponent(connectionId)}#migration-loads`}>Roll back…</Link>
  </> : undefined;

  const issue = async () => {
    if (!dashboard?.goLive.canIssue || !admin) return;
    setIssueBusy(true); setError(null);
    try { const issued = await parallelRunApi.issueGoLiveRecord(connectionId); setRecord(issued); await refresh(); }
    catch (reason) { setError(reason); }
    finally { setIssueBusy(false); }
  };

  return <><MigrationPage title="Parallel run" eyebrow={dashboard ? parallelEyebrow(dashboard) : connectionLabel}
    sub="The incumbent and the module side by side until one month-end close passes. Hand over one kind of record at a time."
    actions={actions}>
    <div className="toolbar migration-parallel-picker"><label className="field"><span className="lbl">Connection</span><select className="select" value={connectionId} onChange={event => setParams({ connection: event.target.value })}>
      <option value="">Choose a connection</option>
      {connectionId && dashboard && !connections.result?.items.some(item => item.connectionId === connectionId) && <option value={connectionId}>{dashboard.connectionLabel}</option>}
      {connections.result?.items.map(item => <option value={item.connectionId} key={item.connectionId}>{item.connectionName}</option>)}
    </select></label><button type="button" className="btn sm" disabled={connections.grid.page === 1 || connections.busy} onClick={() => connections.onPage(connections.grid.page - 1)}>Previous connections</button><button type="button" className="btn sm" disabled={!connections.grid.hasMore || connections.busy} onClick={() => connections.onPage(connections.grid.page + 1)}>More connections</button></div>
    <ErrorNote error={connections.error ?? error} onReload={() => void refresh()} />
    {loading && !dashboard && <p role="status">Loading parallel run…</p>}
    {dashboard?.status === "not_started" && <StartParallelRunCard dashboard={dashboard} admin={admin} onStarted={refresh} />}
    {dashboard && dashboard.status !== "not_started" && <>
      <div className="migration-parallel-kpis"><KpiStrip label="Parallel-run figures" kpis={parallelKpis(dashboard)} />{operator && <button type="button" className="btn sm migration-reconciliation-link" onClick={() => openReconciliation(dashboard, refresh)}>Review reconciliation runs…</button>}</div>
      <div className="migration-parallel-columns">
        <HandoverCard dashboard={dashboard} admin={admin} onChanged={replaceDashboard} />
        <GoLiveCard dashboard={dashboard} record={record} admin={admin} operator={operator} issueBusy={issueBusy} onIssue={() => void issue()} onChanged={replaceDashboard} />
      </div>
      {operator && <ConflictsCard dashboard={dashboard} page={conflicts} onResolved={() => void refresh()} />}
    </>}
  </MigrationPage><Rail label="Parallel run"><Section title="Safe handover"><p className="muted">Each kind of record has one writer. Confirm the source application is read-only for that kind before the module takes ownership.</p></Section></Rail></>;
}

export function StartParallelRunCard({ dashboard, admin, onStarted }: { dashboard: ParallelRunDashboard; admin: boolean; onStarted: () => void | Promise<void> }) {
  const [days, setDays] = useState<30 | 60 | 90>(30); const [startDate, setStartDate] = useState("");
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);
  const start = async () => {
    setBusy(true); setError(null);
    try { await parallelRunApi.start(dashboard.connectionId, days, startDate); await onStarted(); }
    catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };
  return <section className="card migration-parallel-start" aria-labelledby="parallel-start"><div className="card-header"><h2 id="parallel-start">Start parallel run</h2></div><div className="card-body stack"><p>Run both systems side by side for 30, 60 or 90 days, then hand records over one kind at a time.</p>{admin ? <form className="migration-parallel-start-form" onSubmit={event => { event.preventDefault(); void start(); }}><fieldset><legend>Duration</legend>{([30, 60, 90] as const).map(value => <label key={value} className={days === value ? "on" : ""}><input type="radio" name="parallel-days" checked={days === value} onChange={() => setDays(value)} />{value} days</label>)}</fieldset><label className="field"><span className="lbl">Start date</span><input className="input" type="date" required value={startDate} onChange={event => setStartDate(event.target.value)} /></label><button className="btn primary" disabled={busy || !startDate}>{busy ? "Starting…" : "Start parallel run"}</button></form> : <div className="note">A tenant administrator starts the parallel run.</div>}<ErrorNote error={error} /></div></section>;
}

export function HandoverCard({ dashboard, admin, onChanged }: { dashboard: ParallelRunDashboard; admin: boolean; onChanged: (value: ParallelRunDashboard) => void }) {
  const steps = [...dashboard.handover].sort((left, right) => left.order - right.order);
  return <section className="card migration-handover-card" aria-labelledby="handover-heading"><div className="card-header"><h2 id="handover-heading">Handover by kind of record</h2><div className="right"><span className="muted">phased, never two writers at once</span>{admin && <button type="button" className="btn sm" onClick={() => openMonthEndClose(dashboard, onChanged)}>Month-end close…</button>}</div></div><div className="card-body"><ol className="migration-handover-chain">{steps.map(step => <li key={step.kind} aria-current={step.state === "next" ? "step" : undefined}><span className="migration-chain-number">{step.order}</span><div><strong>{step.label}</strong><p>{step.description}</p>{step.facts.length > 0 && <ul className="migration-step-facts">{step.facts.map(fact => <li key={fact}>{fact}</li>)}</ul>}{step.state === "module_owns" && <p className="migration-confirmed-by">Since {parallelDate(step.ownedSince)}{step.confirmedByName ? ` · ${step.confirmedByName}` : ""}{step.evidence ? ` · ${step.evidence}` : ""}</p>}{admin && step.state !== "module_owns" && <div className="migration-step-actions"><button type="button" className="btn sm" onClick={() => openPlanHandover(dashboard, step, onChanged)}>Plan date…</button>{step.canConfirm && <button type="button" className="btn primary sm" onClick={() => openConfirmHandover(dashboard, step, onChanged)}>Confirm module owns…</button>}</div>}{!step.canConfirm && step.state !== "module_owns" && <Refusals reasons={step.confirmRefusals} />}</div><StatusBadge tone={handoverTone(step)}>{handoverState(step)}</StatusBadge></li>)}</ol></div></section>;
}

export function GoLiveCard({ dashboard, record, admin, operator, issueBusy, onIssue, onChanged }: { dashboard: ParallelRunDashboard; record: GoLiveRecordView | null; admin: boolean; operator: boolean; issueBusy: boolean; onIssue: () => void; onChanged: (value: ParallelRunDashboard) => void }) {
  const issuedAt = record?.issuedAt ?? dashboard.goLive.issuedAt;
  return <section className="card migration-go-live-card" aria-labelledby="go-live-heading"><div className="card-header"><h2 id="go-live-heading">Go-live record</h2><span className="meta">{dashboard.goLive.done} of {dashboard.goLive.total}</span></div><div className="card-body"><ul className="migration-go-live-checks">{dashboard.goLive.items.map(item => <li key={item.code}><div><strong>{item.label}</strong><span className="sub">{item.detail}</span>{item.evidence && <span className="sub">Evidence: {item.evidence}</span>}{item.confirmedByName && <span className="sub">Confirmed by {item.confirmedByName}{item.confirmedAt ? ` · ${fmtDateTime(item.confirmedAt)}` : ""}</span>}</div><div className="migration-go-live-state"><StatusBadge tone={goLiveTone(item)}>{goLiveStatus(item)}</StatusBadge>{item.source === "confirmed" && item.status !== "done" && (item.code === "customer_signoff" ? admin : operator) && <button type="button" className="btn sm" onClick={() => openConfirmGoLive(dashboard, item, onChanged)}>Confirm…</button>}</div></li>)}</ul>{issuedAt ? <div className="note ok migration-issued"><span><strong>Issued {parallelDate(issuedAt, true)}</strong>{record?.issuedByName ? ` by ${record.issuedByName}` : ""}</span>{dashboard.goLive.recordId && <a className="btn sm" download href={parallelRunApi.goLiveCsvUrl(dashboard.goLive.recordId)}>Download CSV</a>}</div> : <div className="migration-issue-actions">{admin && <button type="button" className="btn primary" disabled={issueBusy || !dashboard.goLive.canIssue} onClick={onIssue}>{issueBusy ? "Issuing…" : "Issue go-live record"}</button>} {!dashboard.goLive.canIssue && <Refusals reasons={dashboard.goLive.issueRefusals} />}</div>}</div></section>;
}

export function ConflictsCard({ dashboard, page, onResolved }: { dashboard: ParallelRunDashboard; page: CoexistenceConflictPage | null; onResolved: () => void }) {
  const columns = useMemo<GridColumn<CoexistenceConflictView>[]>(() => [
    { key: "since", label: "Since", width: 14, render: row => date(row.incumbentChangedAt ?? row.detectedAt) },
    { key: "unit", label: "Unit", width: 20, render: row => <span className="primary"><span className="t serial">{row.recordLabel}</span></span> },
    { key: "remote", label: "Changed in the incumbent", width: 26, render: row => conflictRemoteLabel(row.fieldName, row.remoteValue) },
    { key: "module", label: "Module has", width: 26, render: row => row.moduleValueLabel ?? row.baselineValue ?? "No value recorded" },
    { key: "decider", label: "Who decides", width: 14, render: row => row.deciderRoleLabel ?? "Integration operations" },
  ], []);
  const rows = page?.items ?? [];
  return <section className="card migration-conflicts-card" aria-labelledby="conflicts-heading"><div className="card-header"><h2 id="conflicts-heading">Conflicts waiting <span className="meta">{dashboard.conflicts.open.toLocaleString()} · the incumbent wins on stock and documents, the module on owner, condition, cover, returns and certificates</span></h2></div><DataGrid columns={columns} page={{ rows, total: dashboard.conflicts.open, page: 1, pageSize: 5, continuation: page?.nextCursor ?? null, totalIsExact: true, hasMore: !!page?.nextCursor }} rowId={row => row.conflictId} onPage={() => undefined} onRowClick={row => openConflict(row, onResolved)} loading={!page} emptyText="No unresolved conflicts for this connection." viewportHeight={320} footer={<Link to={`/integration?connection=${encodeURIComponent(dashboard.connectionId)}`}>All conflicts on the coexistence screen</Link>} /></section>;
}

function Refusals({ reasons }: { reasons: readonly { code: string; message: string }[] }) {
  return reasons.length ? <ul className="migration-parallel-refusals" aria-label="Why this action is unavailable">{reasons.map(reason => <li key={reason.code}>{reason.message}</li>)}</ul> : null;
}

function openMonthEndClose(dashboard: ParallelRunDashboard, onChanged: (value: ParallelRunDashboard) => void) {
  openDrawer({ eyebrow: "Parallel run plan", title: "Month-end close" }, <DateForm label="Close date" initial={dashboard.monthEndCloseOn ?? ""} submitLabel="Save month-end close" save={value => parallelRunApi.setMonthEndClose(dashboard.connectionId, value)} onSaved={onChanged} />);
}

function openPlanHandover(dashboard: ParallelRunDashboard, step: HandoverStepView, onChanged: (value: ParallelRunDashboard) => void) {
  openDrawer({ eyebrow: `Handover step ${step.order}`, title: step.label }, <DateForm label="Planned date" initial={step.plannedOn ?? ""} submitLabel="Save planned date" save={value => parallelRunApi.planHandover(dashboard.connectionId, step.kind, value)} onSaved={onChanged} />);
}

function DateForm({ label, initial, submitLabel, save, onSaved }: { label: string; initial: string; submitLabel: string; save: (value: string) => Promise<ParallelRunDashboard>; onSaved: (value: ParallelRunDashboard) => void }) {
  const [value, setValue] = useState(initial); const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null); const [saved, setSaved] = useState(false);
  const submit = async () => { setBusy(true); setError(null); setSaved(false); try { const next = await save(value); onSaved(next); setSaved(true); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  return <form className="migration-drawer-form" onSubmit={event => { event.preventDefault(); void submit(); }}><label className="field"><span className="lbl">{label}</span><input className="input" type="date" required value={value} onChange={event => setValue(event.target.value)} /></label><ErrorNote error={error} />{saved && <span role="status">Saved.</span>}<button className="btn primary" disabled={busy || !value}>{busy ? "Saving…" : submitLabel}</button></form>;
}

function openConfirmHandover(dashboard: ParallelRunDashboard, step: HandoverStepView, onChanged: (value: ParallelRunDashboard) => void) {
  openDrawer({ eyebrow: `Handover step ${step.order}`, title: `Confirm ${step.label}` }, <EvidenceForm prompt="Evidence that the source application is read-only for this kind of record" submitLabel="Confirm module owns" save={evidence => parallelRunApi.confirmHandover(dashboard.connectionId, step.kind, evidence)} onSaved={onChanged} />);
}

function openConfirmGoLive(dashboard: ParallelRunDashboard, item: GoLiveItemView, onChanged: (value: ParallelRunDashboard) => void) {
  openDrawer({ eyebrow: "Go-live record", title: item.label }, <EvidenceForm prompt="Evidence" submitLabel="Confirm item" save={evidence => parallelRunApi.confirmGoLiveItem(dashboard.connectionId, item.code, evidence)} onSaved={onChanged} />);
}

function EvidenceForm({ prompt, submitLabel, save, onSaved }: { prompt: string; submitLabel: string; save: (evidence: string) => Promise<ParallelRunDashboard>; onSaved: (value: ParallelRunDashboard) => void }) {
  const [evidence, setEvidence] = useState(""); const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null); const [saved, setSaved] = useState(false);
  const submit = async () => { setBusy(true); setError(null); setSaved(false); try { const next = await save(evidence.trim()); onSaved(next); setSaved(true); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  return <form className="migration-drawer-form" onSubmit={event => { event.preventDefault(); void submit(); }}><label className="field"><span className="lbl">{prompt}</span><textarea className="input" required maxLength={500} value={evidence} onChange={event => setEvidence(event.target.value)} /><span className="help">{evidence.length} of 500 characters</span></label><ErrorNote error={error} />{saved && <span role="status">Confirmed.</span>}<button className="btn primary" disabled={busy || !evidence.trim()}>{busy ? "Confirming…" : submitLabel}</button></form>;
}

function openConflict(conflict: CoexistenceConflictView, onResolved: () => void) {
  openDrawer({ eyebrow: "Conflict", title: conflict.recordLabel, mono: true }, <ConflictDecision conflict={conflict} onResolved={onResolved} />);
}

function ConflictDecision({ conflict, onResolved }: { conflict: CoexistenceConflictView; onResolved: () => void }) {
  const [decision, setDecision] = useState<"keep_module" | "accept_remote">("keep_module"); const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null); const [saved, setSaved] = useState(false);
  const submit = async () => { setBusy(true); setError(null); setSaved(false); try { await parallelRunApi.resolveConflict(conflict.conflictId, decision, note.trim()); setSaved(true); onResolved(); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  return <form className="migration-drawer-form" onSubmit={event => { event.preventDefault(); void submit(); }}><div className="migration-conflict-pair"><div><span className="k">Changed in the incumbent</span><span>{conflictRemoteLabel(conflict.fieldName, conflict.remoteValue)}</span></div><div><span className="k">Module has</span><span>{conflict.moduleValueLabel ?? conflict.baselineValue ?? "No value recorded"}</span></div></div><fieldset className="migration-decision-options"><legend>Decision</legend><label className={decision === "keep_module" ? "on" : ""}><input type="radio" name="conflict-decision" checked={decision === "keep_module"} onChange={() => setDecision("keep_module")} />Keep the module value</label><label className={decision === "accept_remote" ? "on" : ""}><input type="radio" name="conflict-decision" checked={decision === "accept_remote"} onChange={() => setDecision("accept_remote")} />Accept the source application change</label></fieldset><label className="field"><span className="lbl">Reason</span><textarea className="input" required maxLength={2000} value={note} onChange={event => setNote(event.target.value)} /></label><ErrorNote error={error} />{saved && <span role="status">Decision recorded.</span>}<button className="btn primary" disabled={busy || !note.trim()}>{busy ? "Deciding…" : "Decide"}</button></form>;
}

function openReconciliation(dashboard: ParallelRunDashboard, onChanged: () => void) {
  openDrawer({ eyebrow: "Nightly reconciliation", title: `${dashboard.reconciliation.clean + dashboard.reconciliation.explained} of ${dashboard.reconciliation.runs} clean or explained` }, <ReconciliationRuns connectionId={dashboard.connectionId} startDate={dashboard.startDate!} onChanged={onChanged} />);
}

function ReconciliationRuns({ connectionId, startDate, onChanged }: { connectionId: string; startDate: string; onChanged: () => void }) {
  const fetch = useCallback((cursor?: string, signal?: AbortSignal) => adminApi.runs(connectionId, "", cursor, signal), [connectionId]);
  const source = useCursorPage<Run>(fetch);
  const rows = (source.result?.items ?? []).filter(run => run.rangeEnd >= startDate);
  return <><Section title="Runs since the parallel run started"><div className="migration-reconciliation-runs">{rows.map(run => <ReconciliationRunRow key={run.runId} run={run} onSaved={() => { source.refresh(); onChanged(); }} />)}{!source.busy && rows.length === 0 && <p className="muted">No reconciliation runs since the start date.</p>}</div><ErrorNote error={source.error} /><div className="migration-drawer-pager"><button type="button" className="btn sm" disabled={source.grid.page === 1 || source.busy} onClick={() => source.onPage(source.grid.page - 1)}>Previous</button><span>Page {source.grid.page}</span><button type="button" className="btn sm" disabled={!source.grid.hasMore || source.busy} onClick={() => source.onPage(source.grid.page + 1)}>Next</button></div></Section></>;
}

function ReconciliationRunRow({ run, onSaved }: { run: Run; onSaved: () => void }) {
  const [explaining, setExplaining] = useState(false); const [note, setNote] = useState(""); const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);
  const save = async () => { setBusy(true); setError(null); try { await parallelRunApi.explainReconciliation(run.runId, note.trim()); setExplaining(false); onSaved(); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  return <article><div className="migration-reconciliation-head"><span><strong>{parallelDate(run.rangeEnd, true)}</strong><span className="sub">{run.mismatchCount.toLocaleString()} record mismatches · {run.locationMismatchCount.toLocaleString()} stock mismatches</span></span><StatusBadge tone={run.explainedAt ? "info" : run.status === "clean" ? "ok" : run.status === "failed" ? "bad" : "warn"}>{run.explainedAt ? "Explained" : run.status === "clean" ? "Clean" : run.status === "failed" ? "Failed" : "Mismatch"}</StatusBadge></div>{run.explanationNote && <p>{run.explanationNote}<span className="sub">{run.explainedByName ? `Explained by ${run.explainedByName}` : "Explained"}{run.explainedAt ? ` · ${fmtDateTime(run.explainedAt)}` : ""}</span></p>}{run.status === "mismatch" && !run.explainedAt && !explaining && <button type="button" className="btn sm" onClick={() => setExplaining(true)}>Explain…</button>}{explaining && <form onSubmit={event => { event.preventDefault(); void save(); }}><label className="field"><span className="lbl">Explanation</span><textarea className="input" required maxLength={500} value={note} onChange={event => setNote(event.target.value)} /></label><ErrorNote error={error} /><div className="migration-step-actions"><button type="button" className="btn sm" onClick={() => setExplaining(false)}>Cancel</button><button className="btn primary sm" disabled={busy || !note.trim()}>{busy ? "Saving…" : "Save explanation"}</button></div></form>}</article>;
}
