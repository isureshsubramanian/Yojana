import { useCallback, useEffect, useState } from "react";
import { Link, useBlocker } from "react-router";
import { type GridColumn } from "../components";
import { DEFAULT_LEGAL_ENTITY } from "../api/units";
import { useSession } from "../session/session";
import { ErrorNote, Kv } from "../units/common";
import { degradationLabel, human, recordLabel } from "./model";
import { adminApi } from "./adminApi";
import { date, Frame, Panel, PagedGrid, RoleAccess, State, useAction, useCursorPage } from "./AdminCommon";
import { ConnectorSettings, SiteRelays, TransportPanel, WarrantyContractMaintenance } from "./ConnectionSettings";
import type { ArchiveResult, ChecklistItem, CoexistenceSettings, Connection, ConnectionDetail, ConnectionPage, RegistrationConnector } from "./adminTypes";

export function Component() { return <RoleAccess><ConnectionsScreen /></RoleAccess>; }
function ConnectionsScreen() {
  const admin = useSession().roles.includes("tenant_administrator"); const [archived, setArchived] = useState(false); const [selected, setSelected] = useState<string | null>(null); const [register, setRegister] = useState(false);
  const [relayLocked, setRelayLocked] = useState(false);
  const blocker = useBlocker(relayLocked);
  useEffect(() => {
    if (!relayLocked) return;
    const guard = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ""; };
    window.addEventListener("beforeunload", guard);
    return () => window.removeEventListener("beforeunload", guard);
  }, [relayLocked]);
  const fetch = useCallback((cursor?: string, signal?: AbortSignal) => adminApi.connections(cursor, archived, signal), [archived]);
  const source = useCursorPage<Connection, ConnectionPage>(fetch);
  const columns: GridColumn<Connection>[] = [
    { key: "name", label: "Connection", width: 40, render: x => <><span className="primary">{x.connectionName}</span><span className="sub">{x.connectorName} · <span className="mono">{x.connectorCode}</span></span></> },
    { key: "state", label: "Status", width: 25, render: x => <State value={x.status} /> },
    { key: "mode", label: "Writes", width: 35, render: x => x.readOnly ? "Read-only" : human(x.outboundMode) },
  ];
  return <Frame title="Connections" sub="Sign-in, capabilities, onboarding and customer-side settings. Every connection keeps its own settings and audit history."
    actions={admin && <button className="btn primary" disabled={relayLocked} onClick={() => setRegister(x => !x)}>{register ? "Close registration" : "Register connection"}</button>}>
    {relayLocked && <div className="note warn" role="status">Store the one-time relay secret and configuration, then choose “I stored it” before changing connections or leaving this page.</div>}
    {blocker.state === "blocked" && <div className="note warn" role="alert">Navigation is paused to preserve the one-time relay details.<button className="btn" onClick={() => blocker.reset()}>Return to relay details</button></div>}
    {register && admin && <Registration complete={id => { setRegister(false); setSelected(id); source.refresh(); }} />}
    <section className="card" aria-label="Connections"><div className="card-header"><h2>Connections</h2><div className="segmented" role="group" aria-label="Connection state"><button disabled={relayLocked} aria-pressed={!archived} onClick={() => { setArchived(false); setSelected(null); }}>Active</button><button disabled={relayLocked} aria-pressed={archived} onClick={() => { setArchived(true); setSelected(null); }}>Archived</button></div></div><PagedGrid source={source} columns={columns} rowId={x => x.connectionId} select={relayLocked ? undefined : x => setSelected(x.connectionId)} selected={selected ?? undefined} empty={archived ? "No archived connections." : "No connections registered. An administrator can register one above."} /></section>
    {selected ? <ConnectionPanels key={selected} id={selected} admin={admin} changed={source.refresh} relayRegistrationAllowed={!register} onOneTimeChange={setRelayLocked} /> : <div className="note">Select a connection to see its sign-in state, checklist and settings.</div>}
    <TransportPanel admin={admin} />
  </Frame>;
}
export interface RegistrationBody { requestId: string; connectorCode: string; displayLabel: string; legalEntityId: string; connectorSettings: Record<string, string>; credentials: { purpose: string; secret: string; replaceBy: string | null }[] }
// The ERP change start form reuses this form; it submits the same body as part of its own request.
export function Registration({ complete, submit = async body => (await adminApi.register(body)).connectionId, title = "Register connection", submitLabel = "Register and test sign-in", success = "Connection registered. Review the smoke-read results and onboarding checklist." }: { complete: (id: string) => void; submit?: (body: RegistrationBody) => Promise<string>; title?: string; submitLabel?: string; success?: string }) {
  const fetch = useCallback((cursor?: string, signal?: AbortSignal) => adminApi.registrationConnectors(cursor, signal), []); const catalogue = useCursorPage<RegistrationConnector>(fetch);
  const [connector, setConnector] = useState<RegistrationConnector | null>(null); const [label, setLabel] = useState(""); const [settings, setSettings] = useState<Record<string, string>>({}); const [secrets, setSecrets] = useState<Record<string, string>>({}); const [replaceBy, setReplaceBy] = useState(""); const [requestId, setRequestId] = useState(() => crypto.randomUUID()); const action = useAction();
  function choose(value: RegistrationConnector) { if (value.code !== connector?.code) setRequestId(crypto.randomUUID()); setConnector(value); setSettings(Object.fromEntries(value.form.settings.map(x => [x.code, x.defaultValue ?? ""]))); setSecrets({}); }
  const onSite = connector?.form.settings.some(field => field.type === "route" && settings[field.code] === "site_relay") ?? false;
  return <Panel title={title}><div className="note">Use a dedicated integration identity. Secrets are typed once, encrypted on the server and never returned. A site relay can keep credentials at the customer site.</div>
    <section className="card" aria-label="Connector catalogue"><PagedGrid source={catalogue} columns={[{ key: "name", label: "Connector from tenant catalogue", width: 75, render: x => x.displayName }, { key: "code", label: "Code", width: 25, mono: true, render: x => x.code }]} rowId={x => x.code} select={choose} selected={connector?.code} empty="No enabled connectors available for registration." /></section>
    {connector && <form className="stack" onSubmit={e => { e.preventDefault(); const credentials = onSite ? [] : [{ purpose: connector.form.credentialPurpose, secret: JSON.stringify(secrets), replaceBy: replaceBy ? new Date(replaceBy).toISOString() : null }]; setSecrets({}); void action.run(async () => { complete(await submit({ requestId, connectorCode: connector.code, displayLabel: label.trim(), legalEntityId: DEFAULT_LEGAL_ENTITY, connectorSettings: settings, credentials })); }, success); }}>
      <label className="field"><span className="lbl">Name</span><input className="input" required maxLength={160} value={label} onChange={e => setLabel(e.target.value)} /></label>
      <div className="integration-form-grid">{connector.form.settings.map(field => <label className="field" key={field.code}><span className="lbl">{field.label}</span>{field.type === "route" ? <select className="select" value={settings[field.code] ?? "direct"} onChange={e => { const route = e.target.value; setSettings(x => ({ ...x, [field.code]: route })); if (route === "site_relay") setSecrets({}); }}><option value="direct">Direct from server</option><option value="site_relay">Customer site relay</option></select> : <input className="input" type={field.type} required value={settings[field.code] ?? ""} onChange={e => setSettings(x => ({ ...x, [field.code]: e.target.value }))} />}</label>)}</div>
      {!onSite && <>{connector.form.secretFields.map(field => <label className="field" key={field.code}><span className="lbl">{field.label}</span><input className="input" type={field.type} autoComplete="off" required value={secrets[field.code] ?? ""} onChange={e => setSecrets(x => ({ ...x, [field.code]: e.target.value }))} /></label>)}<label className="field"><span className="lbl">Replace by (optional; otherwise tenant policy applies)</span><input className="input" type="datetime-local" value={replaceBy} onChange={e => setReplaceBy(e.target.value)} /></label></>}
      <button className="btn primary" disabled={action.busy}>{submitLabel}</button>
    </form>}{action.feedback}
  </Panel>;
}
function ConnectionPanels({ id, admin, changed, relayRegistrationAllowed, onOneTimeChange }: { id: string; admin: boolean; changed: () => void; relayRegistrationAllowed: boolean; onOneTimeChange: (value: boolean) => void }) {
  const [detail, setDetail] = useState<ConnectionDetail | null>(null); const [error, setError] = useState<unknown>(null); const [revision, setRevision] = useState(0); const [report, setReport] = useState<ArchiveResult | null>(null); const [reason, setReason] = useState(""); const action = useAction();
  useEffect(() => { const controller = new AbortController(); setError(null); adminApi.detail(id, controller.signal).then(setDetail).catch(reason => { if (!controller.signal.aborted) setError(reason); }); return () => controller.abort(); }, [id, revision]);
  const refresh = () => { setRevision(x => x + 1); changed(); };
  if (!detail) return <><ErrorNote error={error} /><p role="status">{error ? "Connection details could not be loaded." : "Loading connection…"}</p><button className="btn" onClick={refresh}>Retry</button></>;
  const isArchived = detail.connection.status === "archived" || detail.archivedAt !== null;
  const active = detail.lifecycleStatus === "active";
  return <><ErrorNote error={error} /><Panel title={detail.connection.connectionName} actions={<State value={detail.connection.status} />}>
    <Kv rows={[["Connector", detail.connection.connectorName], ["Registered", date(detail.createdAt)], ["Archived", date(detail.archivedAt)], ["What we write", human(detail.settings.outboundMode)], ["ERP warranty fields", human(detail.settings.nativeWarrantyObjectMode)], ["Warranty contracts are maintained in", detail.settings.warrantyContractsMaintainedIn === "module" ? "Module" : "The incumbent until cutover"], ["Ordering wait", `${detail.settings.orderingWaitMinutes} minutes`]]} />
    {!isArchived && <div className="toolbar"><Link className="btn" to={`/integration/connections/${id}/verification`}>Verification record</Link><button className="btn" disabled={action.busy || !active} onClick={() => void action.run(async () => { await adminApi.smoke(id); refresh(); }, "Smoke-read finished. Review health and onboarding results.")}>Re-run smoke-read</button><button className="btn" disabled={action.busy} onClick={() => void action.run(async () => { const result = await adminApi.archive(id); setReport(result); refresh(); }, "Archive request checked.")}>Archive connection</button></div>}
    {report && <div className="stack"><h3>{report.archived ? "Connection archived" : "Archive preconditions"}</h3><Kv rows={[["Waiting to send", report.preconditions.waitingOutbox], ["Messages needing attention", report.preconditions.openDeadLetters], ["Running history loads", report.preconditions.runningBackfills], ["Open hand entries", report.preconditions.openManualTasks]]} />{report.cleanupErrorCode && <div className="note warn">Remote cleanup needs review. Check connection events before continuing.</div>}{!report.archived && admin && <form className="stack" onSubmit={e => { e.preventDefault(); void action.run(async () => { setReport(await adminApi.archive(id, true, reason.trim())); refresh(); }, "Forced archive recorded."); }}><div className="note warn">Force archives despite pending work. Existing records remain available for review.</div><label className="field"><span className="lbl">Mandatory force-archive reason</span><input className="input" required value={reason} onChange={e => setReason(e.target.value)} /></label><button className="btn" disabled={action.busy || !reason.trim()}>Force archive with reason</button></form>}</div>}{action.feedback}
  </Panel>
  <Panel title="Sign-in secrets"><p className="muted">Set / not set and dates only. Stored secrets cannot be viewed.</p>{detail.credentials.length ? detail.credentials.map(x => <div key={x.purpose}><h3>{human(x.purpose)}</h3><State value={x.isSet ? "set" : "not_set"} /><Kv rows={[["Created", date(x.createdAt)], ["Expires", date(x.expiresAt)], ["Replace by", date(x.replaceBy)], ["Retired", date(x.retiredAt)]]} /></div>) : <p>No stored credentials. Site credentials remain at the customer site.</p>}</Panel>
  <Panel title="Getting live"><div className="note">{detail.checklist.customerCostDisclosure}</div>{detail.checklist.items.map(item => <ChecklistRow key={item.code} id={id} item={item} admin={admin && !isArchived} refresh={refresh} />)}</Panel>
  <WarrantyContractMaintenance id={id} admin={admin} changed={refresh} />
  <NativeWarrantyMode id={id} admin={admin && !isArchived} changed={refresh} />
  <Panel title="What this ERP can do"><div className="integration-settings-list">{detail.capabilities.map(x => <div key={x.canonicalMessage}><span>{recordLabel(x.canonicalMessage)}</span><State value={x.supportLevel} /></div>)}</div><h3>When the ERP cannot</h3>{detail.settings.degradationRules.length ? detail.settings.degradationRules.map(x => <p key={x.ruleNumber}>{degradationLabel(x.ruleNumber)} · {x.enabled ? "Enabled" : "Disabled"}</p>) : <p className="muted">No per-connection overrides recorded.</p>}{detail.settings.throttle && <Kv rows={Object.entries(detail.settings.throttle).map(([key, value]) => [human(key), String(value ?? "No cap")])} />}</Panel>
  {detail.connection.connectorCode === "CX01" && <ConnectorSettings id={id} admin={admin && !isArchived} />}
  {detail.connection.connectorCode === "CX04" && <Panel title="Sign-in" actions={<Link className="btn sm" to={`/integration/connections/${id}/sign-in`}>Open sign-in</Link>}><p className="muted">This connection signs in as a dedicated API user with tokens only. See its expiry, history, reminders and Disable.</p></Panel>}
  {!relayRegistrationAllowed && <p className="help">Close connection registration before registering a site relay.</p>}
  <SiteRelays id={id} admin={admin && !isArchived} canRegister={active && relayRegistrationAllowed} onOneTimeChange={onOneTimeChange} />
  </>;
}
function NativeWarrantyMode({ id, admin, changed }: { id: string; admin: boolean; changed: () => void }) {
  const [value, setValue] = useState<CoexistenceSettings | null>(null); const [error, setError] = useState<unknown>(null); const action = useAction();
  useEffect(() => { const controller = new AbortController(); adminApi.coexistence(id, controller.signal).then(setValue).catch(reason => { if (!controller.signal.aborted) setError(reason); }); return () => controller.abort(); }, [id]);
  if (!value) return <Panel title="ERP warranty fields"><ErrorNote error={error} /><p role="status">{error ? "Warranty field mode could not be loaded." : "Loading warranty field mode…"}</p></Panel>;
  return <Panel title="ERP warranty fields"><p className="muted">Frozen leaves the remote installed-base record unchanged. Pushed sends the module’s current status and, when contracts are maintained here, its latest warranty end date.</p>
    <form className="stack" onSubmit={event => { event.preventDefault(); void action.run(async () => { const saved = await adminApi.saveCoexistence(id, value, value.nativeWarrantyObjectMode); setValue(saved); changed(); }, "Warranty field mode saved."); }}>
      <label className="field"><span className="lbl">Mode</span><select className="select" disabled={!admin || action.busy} value={value.nativeWarrantyObjectMode} onChange={event => setValue(current => current ? { ...current, nativeWarrantyObjectMode: event.target.value as CoexistenceSettings["nativeWarrantyObjectMode"] } : current)}><option value="frozen">Frozen</option><option value="pushed" disabled={!value.pushedModeAvailable}>Pushed</option></select></label>
      {!value.pushedModeAvailable && <p className="help">This connector does not declare support for pushed warranty fields.</p>}
      {admin && <button className="btn primary" disabled={action.busy}>Save warranty field mode</button>}
    </form>{action.feedback}
  </Panel>;
}
function ChecklistRow({ id, item, admin, refresh }: { id: string; item: ChecklistItem; admin: boolean; refresh: () => void }) {
  const [evidence, setEvidence] = useState(""); const action = useAction();
  return <details className="integration-disclosure"><summary>{item.label}{item.required ? " · required" : ""}<State value={item.status} /></summary><div className="integration-checklist"><p>{item.detail}</p>{item.evidence && <p>Evidence: {item.evidence} · {date(item.confirmedAt)}</p>}{admin && item.manualConfirmation && item.status !== "confirmed" && <form className="toolbar" onSubmit={e => { e.preventDefault(); void action.run(async () => { await adminApi.confirm(id, item.code, evidence.trim()); refresh(); }, "Checklist confirmation recorded."); }}><label className="field"><span className="lbl">{item.evidencePrompt ?? "Evidence (optional)"}</span><input className="input" required={!!item.evidencePrompt} value={evidence} onChange={e => setEvidence(e.target.value)} /></label><button className="btn sm" disabled={action.busy || (!!item.evidencePrompt && !evidence.trim())}>Confirm</button></form>}{action.feedback}</div></details>;
}
