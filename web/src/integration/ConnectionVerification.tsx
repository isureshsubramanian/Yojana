import { useEffect, useMemo, useState } from "react";
import { useParams } from "react-router";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn, type PageResult } from "../components";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { closeDrawer, openDrawer } from "../shell/drawerStore";
import { ErrorNote, Kv } from "../units/common";
import { date, RoleAccess, useAction } from "./AdminCommon";
import { ConnectionTabs } from "./ConnectionTabs";
import { human } from "./model";
import { verificationApi } from "./verificationApi";
import type { AcceptanceItem, AcceptanceItemResult, AcceptanceRun, AcceptanceRunSummary, ConnectorVerificationView, VerificationFact, VerificationFactKind } from "./verificationTypes";
import "./integration.css";
import "./erp-change.css";

export function Component() { return <RoleAccess><VerificationScreen /></RoleAccess>; }

const factPageSize = 25;
const factKinds: readonly ["all" | VerificationFactKind, string][] = [["all", "All facts"], ["documented", "Documented"], ["inferred", "Inferred"], ["assumed", "Assumed"]];

function VerificationScreen() {
  const connectionId = useParams().connectionId ?? "";
  const [view, setView] = useState<ConnectorVerificationView | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [revision, setRevision] = useState(0);
  useEffect(() => {
    const controller = new AbortController(); setError(null);
    verificationApi.overview(connectionId, controller.signal).then(value => { if (!controller.signal.aborted) setView(value); })
      .catch(reason => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, [connectionId, revision]);
  const refresh = () => setRevision(value => value + 1);
  const name = view?.connectionName ?? "Connection";
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Integration", to: "/integration" }, { label: "Connections", to: "/integration/connections" }, { label: name }, { label: "Verification record" }]} />
    <PageHeader eyebrow={view ? `${view.connectionName} · connector ${view.connectorCode}` : "Connection"} title="Verification record"
      sub="Protocol facts and this tenant's evidence that the connector was accepted in a customer-controlled sandbox. No credentials, payloads or secrets are stored here."
      actions={view && <button className="btn primary" onClick={() => openStart(connectionId, refresh)}>Start acceptance run</button>} />
    {view && <ConnectionTabs connectionId={connectionId} active="verification" features={view.features} />}
    <div className="stack erp-change-stack">
      <ErrorNote error={error} onReload={refresh} />
      {!view && !error && <p role="status">Loading verification record…</p>}
      {view && <VerificationBody connectionId={connectionId} view={view} refresh={refresh} />}
    </div>
  </main>;
}

function VerificationBody({ connectionId, view, refresh }: { connectionId: string; view: ConnectorVerificationView; refresh: () => void }) {
  return <>
    <div className={`note ${view.acceptancePassed ? "" : "warn"}`} role="status"><strong>This tenant's acceptance: {view.acceptancePassed ? "Passed" : "Not passed"}.</strong> The connector-level live-system flag changes only in a later product release after a first customer gate passes.</div>
    <KpiStrip label="Verification figures" kpis={[
      { label: "Checked against a live system", value: view.liveVerification.verified ? "Yes" : "No", detail: view.liveVerification.release ? `release ${view.liveVerification.release}` : "connector release manifest" },
      { label: "Documented facts", value: view.documentedFacts.toLocaleString(), detail: `${view.inferredFacts.toLocaleString()} inferred` },
      { label: "Assumed facts", value: view.assumedFacts.toLocaleString(), detail: "all require acceptance" },
      { label: "Edge scenarios on simulator", value: `${view.edgeScenariosApplicable} of ${view.edgeScenarios}`, detail: "applicable to this connection" },
    ]} />
    <FactsCard facts={view.facts} />
    <RunsCard connectionId={connectionId} runs={view.runs} refresh={refresh} />
  </>;
}

function FactsCard({ facts }: { facts: VerificationFact[] }) {
  const [kind, setKind] = useState<"all" | VerificationFactKind>("all"); const [page, setPage] = useState(1);
  const filtered = useMemo(() => kind === "all" ? facts : facts.filter(fact => fact.kind === kind), [facts, kind]);
  const maxPage = Math.max(1, Math.ceil(filtered.length / factPageSize));
  useEffect(() => { setPage(1); }, [kind]);
  const rows = filtered.slice((page - 1) * factPageSize, page * factPageSize);
  const grid: PageResult<VerificationFact> = { rows, total: filtered.length, page: Math.min(page, maxPage), pageSize: factPageSize, continuation: null };
  const columns: GridColumn<VerificationFact>[] = [
    { key: "id", label: "Fact", width: 12, mono: true, render: fact => fact.id },
    { key: "fact", label: "Protocol fact", width: 42, render: fact => <span className="primary"><span className="t">{fact.fact}</span><span className="sub">Used by {fact.usedAt}</span></span> },
    { key: "value", label: "Value used", width: 29, render: fact => fact.valueUsed },
    { key: "kind", label: "Kind", width: 17, render: fact => <StatusBadge tone={fact.kind === "assumed" ? "warn" : fact.kind === "documented" ? "ok" : "neutral"}>{human(fact.kind)}</StatusBadge> },
  ];
  return <section className="card" aria-labelledby="verification-facts"><div className="card-header"><h2 id="verification-facts">Protocol facts <span className="meta">client-paged · 25 per page</span></h2><label className="field"><span className="lbl">Kind</span><select className="select" value={kind} onChange={event => setKind(event.target.value as typeof kind)}>{factKinds.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label></div>
    <DataGrid columns={columns} page={grid} rowId={fact => fact.id} onPage={setPage} onRowClick={openFact} emptyText="No facts match this kind." viewportHeight={500} />
  </section>;
}

function openFact(fact: VerificationFact) {
  openDrawer({ eyebrow: `${human(fact.kind)} fact`, title: fact.id, mono: true }, <div className="stack"><p>{fact.fact}</p><Kv rows={[["Value used", fact.valueUsed], ["Source", fact.sourceLabel], ["Date read", fact.dateRead], ["Used at", fact.usedAt], ["Acceptance item", fact.acceptanceItem ? "Yes" : "No"]]} /></div>);
}

function RunsCard({ connectionId, runs, refresh }: { connectionId: string; runs: AcceptanceRunSummary[]; refresh: () => void }) {
  return <section className="card" aria-labelledby="acceptance-runs"><div className="card-header"><h2 id="acceptance-runs">Customer-sandbox acceptance runs</h2><button className="btn" onClick={() => openStart(connectionId, refresh)}>Start run</button></div><div className="card-body stack">
    {!runs.length && <div className="integration-empty"><div><h3>No acceptance run yet</h3><p className="muted">Run the checklist in the customer's own sandbox before go-live. The connector remains “Verified against a live system: no” until a passed first-customer run is included in a later release.</p></div></div>}
    {runs.map(run => <button type="button" className="verification-run" key={run.runId} onClick={() => openRun(connectionId, run.runId, refresh)} aria-label={`Open acceptance run for ${run.sandboxLabel}`} title="Open acceptance run"><span><strong>{run.sandboxLabel}</strong><span className="sub">Started by {run.startedBy} · {date(run.startedAt)}</span></span><StatusBadge tone={run.status === "passed" ? "ok" : run.status === "failed" ? "bad" : "warn"}>{human(run.status)}</StatusBadge><span className="muted">{run.passed} passed · {run.failed} failed · {run.notApplicable} not applicable · {run.pending} pending</span></button>)}
  </div></section>;
}

function openStart(connectionId: string, refresh: () => void) { openDrawer({ eyebrow: "Customer sandbox", title: "Start acceptance run" }, <StartRunForm connectionId={connectionId} refresh={refresh} />); }
function StartRunForm({ connectionId, refresh }: { connectionId: string; refresh: () => void }) {
  const [label, setLabel] = useState(""); const action = useAction();
  return <form className="erp-drawer-form" onSubmit={event => { event.preventDefault(); void action.run(async () => { const run = await verificationApi.start(connectionId, label.trim()); refresh(); openRun(connectionId, run.runId, refresh); }, "Acceptance run started."); }}>
    <div className="note">Use a descriptive sandbox label only. Do not enter a URL, account, token or secret.</div>
    <label className="field"><span className="lbl">Sandbox label</span><input className="input" required maxLength={160} value={label} onChange={event => setLabel(event.target.value)} placeholder="Northbridge acceptance sandbox" /></label>
    <button className="btn primary" disabled={action.busy || !label.trim()}>Start acceptance run</button>{action.feedback}
  </form>;
}

function openRun(connectionId: string, runId: string, refresh: () => void) { openDrawer({ eyebrow: "Acceptance run", title: "Checklist" }, <RunDetail connectionId={connectionId} runId={runId} refresh={refresh} />); }
function RunDetail({ connectionId, runId, refresh }: { connectionId: string; runId: string; refresh: () => void }) {
  const [run, setRun] = useState<AcceptanceRun | null>(null); const [error, setError] = useState<unknown>(null); const [revision, setRevision] = useState(0); const action = useAction();
  useEffect(() => { const controller = new AbortController(); verificationApi.run(connectionId, runId, controller.signal).then(setRun).catch(reason => { if (!controller.signal.aborted) setError(reason); }); return () => controller.abort(); }, [connectionId, runId, revision]);
  const reload = () => { setRevision(value => value + 1); refresh(); };
  if (!run) return <><ErrorNote error={error} />{!error && <p role="status">Loading acceptance run…</p>}</>;
  const pending = run.items.filter(item => item.result === "pending").length;
  return <div className="stack"><Kv rows={[["Sandbox", run.sandboxLabel], ["Status", human(run.status)], ["Started by", run.startedBy], ["Started", date(run.startedAt)], ["Completed by", run.completedBy ?? "—"], ["Completed", date(run.completedAt)]]} />
    {run.status === "open" && <><ImportResults connectionId={connectionId} runId={runId} reload={reload} /><div className="verification-items">{run.items.map(item => <button key={item.itemId} type="button" onClick={() => openMark(connectionId, runId, item, reload)} aria-label={`Mark ${item.label}`} title="Mark checklist item"><span><strong>{item.label}</strong><span className="sub">{human(item.kind)} · {item.note ?? item.evidence ?? "No note or evidence"}</span></span><StatusBadge tone={item.result === "passed" ? "ok" : item.result === "failed" ? "bad" : item.result === "pending" ? "warn" : "neutral"}>{human(item.result)}</StatusBadge></button>)}</div>
      <button className="btn primary" disabled={action.busy || pending > 0} onClick={() => void action.run(async () => { const completed = await verificationApi.complete(connectionId, runId); setRun(completed); refresh(); }, "Acceptance run completed.")}>Complete acceptance run{pending ? ` (${pending} pending)` : ""}</button></>}
    {action.feedback}
  </div>;
}

function ImportResults({ connectionId, runId, reload }: { connectionId: string; runId: string; reload: () => void }) {
  const [file, setFile] = useState<File | null>(null); const action = useAction();
  return <form className="stack" onSubmit={event => { event.preventDefault(); if (!file) return; void action.run(async () => { const parsed: unknown = JSON.parse(await file.text()); await verificationApi.importResults(connectionId, runId, parsed); reload(); setFile(null); }, "Edge-suite results imported."); }}>
    <label className="field"><span className="lbl">Edge-suite result file</span><input className="input" type="file" accept="application/json,.json" onChange={event => setFile(event.target.files?.[0] ?? null)} /></label>
    <button className="btn" disabled={action.busy || !file}>Import result file</button>{action.feedback}
  </form>;
}

function openMark(connectionId: string, runId: string, item: AcceptanceItem, reload: () => void) { openDrawer({ eyebrow: human(item.kind), title: item.label }, <MarkItemForm connectionId={connectionId} runId={runId} item={item} reload={reload} />); }
function MarkItemForm({ connectionId, runId, item, reload }: { connectionId: string; runId: string; item: AcceptanceItem; reload: () => void }) {
  const [result, setResult] = useState<AcceptanceItemResult>(item.result); const [note, setNote] = useState(item.note ?? ""); const [evidence, setEvidence] = useState(item.evidence ?? ""); const action = useAction();
  const invalid = result === "pending" || (result === "not_applicable" && !note.trim());
  return <form className="erp-drawer-form" onSubmit={event => { event.preventDefault(); if (invalid) return; void action.run(async () => { await verificationApi.mark(connectionId, runId, item.itemId, result, note.trim(), evidence.trim()); reload(); closeDrawer(); }, "Checklist item marked."); }}>
    <label className="field"><span className="lbl">Result</span><select className="select" value={result} onChange={event => setResult(event.target.value as AcceptanceItemResult)}><option value="pending" disabled>Pending</option><option value="passed">Passed</option><option value="failed">Failed</option><option value="not_applicable">Not applicable</option></select></label>
    <label className="field"><span className="lbl">Note{result === "not_applicable" ? " (reason required)" : ""}</span><textarea className="input" maxLength={2000} required={result === "not_applicable"} value={note} onChange={event => setNote(event.target.value)} /></label>
    <label className="field"><span className="lbl">Evidence text (no secrets or payloads)</span><textarea className="input" maxLength={4000} value={evidence} onChange={event => setEvidence(event.target.value)} /></label>
    <button className="btn primary" disabled={action.busy || invalid}>Save checklist item</button>{action.feedback}
  </form>;
}
