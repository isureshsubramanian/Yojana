import { useCallback, useEffect, useMemo, useState } from "react";
import { useParams } from "react-router";
import { KpiStrip, StatusBadge, type GridColumn } from "../components";
import { Rail } from "../shell/Drawer";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { Icon } from "../shell/Icons";
import { closeDrawer, openDrawer } from "../shell/drawerStore";
import { ErrorNote, Section } from "../units/common";
import { date, PagedGrid, RoleAccess, useAction, useConnectionDetail, useCursorPage } from "./AdminCommon";
import { signInApi } from "./erpChangeApi";
import {
  daysUntil, expiryRuleLabel, historyBy, historyEvent, historyResult, parseReminderDays, signInNote, signInStatusLabel,
} from "./erpChangeModel";
import type { ConnectionAuthorisationView, ReminderSettingsView, SignInHistoryItem } from "./erpChangeTypes";
import { human } from "./model";
import { parallelDate } from "./parallelRunModel";
import "./integration.css";
import "./erp-change.css";

export function Component() { return <RoleAccess><SignInScreen /></RoleAccess>; }

const recipientChoices = [["integration_operator", "Integration administrators"], ["tenant_administrator", "Tenant administrators"], ["service_manager", "Service managers"]] as const;

function SignInScreen() {
  const connectionId = useParams().connectionId ?? "";
  const { detail, error: detailError } = useConnectionDetail(connectionId);
  const [view, setView] = useState<ConnectionAuthorisationView | null>(null); const [error, setError] = useState<unknown>(null); const [revision, setRevision] = useState(0);
  useEffect(() => {
    const controller = new AbortController(); setError(null);
    signInApi.read(connectionId, undefined, controller.signal).then(value => { if (!controller.signal.aborted) setView(value); }).catch(reason => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, [connectionId, revision]);
  const refresh = () => setRevision(value => value + 1);
  const action = useAction();
  const name = detail?.connection.connectionName ?? "Connection";
  const begin = () => void action.run(async () => { const start = await signInApi.begin(connectionId); window.location.assign(start.consentUrl); }, "Opening the ERP's sign-in page…");
  const disabled = view?.status === "disabled";
  const actions = view ? <>
    <button type="button" className="btn" aria-label="Open the setup guide for the API user" title="Setup guide" onClick={openSetupGuide}><Icon name="report" />Setup guide</button>
    {view.canDisable && <button type="button" className="btn danger" aria-label="Disable this connection's sign-in" title="Disable sign-in" onClick={() => openDisable(connectionId, name, view.waitingOutbound, refresh)}><Icon name="x" />Disable</button>}
    {view.status !== "active" || view.requiresFreshConsent || (daysUntil(view.refreshChainExpiresAt) ?? 99) <= Math.max(0, ...view.reminderDays)
      ? <button type="button" className="btn primary" aria-label={disabled ? "Sign the API user in to enable the connection" : "Sign the API user in again"} title={disabled ? "Sign in to enable" : "Sign in again"} disabled={action.busy} onClick={begin}><Icon name="key" />{disabled ? "Sign in to enable" : "Sign in again"}</button>
      : <button type="button" className="btn" aria-label="Sign the API user in again" title="Sign in again" disabled={action.busy} onClick={begin}><Icon name="key" />Sign in again</button>}
  </> : undefined;
  const eyebrow = detail ? `${name} · connector ${detail.connection.connectorCode}` : "Connection";
  return <><main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Integration", to: "/integration" }, { label: "Connections", to: "/integration/connections" }, { label: name }, { label: "Sign-in" }]} />
    <PageHeader eyebrow={eyebrow} title="Sign-in" actions={actions}
      sub="The module signs in to the ERP as one dedicated API user. Its password is never typed here and never stored: the ERP's administrator signs that user in once, and the module keeps only the tokens the ERP hands back, encrypted." />
    <div className="stack">
      <ErrorNote error={detailError ?? error} onReload={refresh} />{action.feedback}
      {!view && !error && <p role="status">Loading sign-in…</p>}
      {view && <SignInBody connectionId={connectionId} view={view} />}
    </div>
  </main><Rail label="Sign-in"><Section title="Password"><p className="muted">The module never asks for or keeps the API user's password. Disable deletes the tokens at once; the ERP administrator can also revoke the connected application in the ERP.</p></Section></Rail></>;
}

function SignInBody({ connectionId, view }: { connectionId: string; view: ConnectionAuthorisationView }) {
  const note = signInNote(view); const days = daysUntil(view.refreshChainExpiresAt);
  const fetch = useCallback((cursor?: string, signal?: AbortSignal) => signInApi.read(connectionId, cursor, signal).then(value => ({ items: value.history.items, nextCursor: value.history.nextCursor })), [connectionId]);
  const history = useCursorPage<SignInHistoryItem>(fetch);
  const columns = useMemo<GridColumn<SignInHistoryItem>[]>(() => [
    { key: "event", label: "Event", width: 34, render: item => <span className="primary"><span className="t">{historyEvent(item)}</span>{item.reason && <span className="sub">{item.reason}</span>}</span> },
    { key: "by", label: "By", width: 30, render: item => historyBy(item) },
    { key: "when", label: "When", width: 18, render: item => item.kind === "access_renewed" ? parallelDate(item.occurredAt) : date(item.occurredAt) },
    { key: "result", label: "Result", width: 18, render: item => { const [label, tone] = historyResult(item); return <StatusBadge tone={tone}>{label}</StatusBadge>; } },
  ], []);
  const disabled = view.status === "disabled";
  return <>
    {disabled && <div className="note bad" role="status"><strong>Disabled</strong> by {view.disabledBy ?? "a team member"}{view.disabledAt ? ` on ${date(view.disabledAt)}` : ""}.{view.disabledReason ? ` Reason given: "${view.disabledReason}".` : ""} The module's tokens were deleted at that moment and no call has been made since. {view.waitingOutbound.toLocaleString()} outgoing document{view.waitingOutbound === 1 ? " is" : "s are"} waiting in the queue; incoming changes will be caught up from the last read position.</div>}
    {!disabled && note && <div className={`note ${note.tone}`} role="status"><strong>{note.title}</strong>{view.refreshChainExpiresAt ? ` (${date(view.refreshChainExpiresAt)})` : ""}. {note.tone === "bad" ? "Sync is paused until someone signs in again; nothing is lost and it resumes where it stopped." : "After that, sync pauses until someone signs in again; nothing is lost and it resumes where it stopped."} Ask the ERP administrator to choose Sign in again: it takes about a minute.{view.remindersSent.length ? ` Reminders sent ${view.remindersSent.map(value => `${value} days`).join(" and ")} before expiry.` : ""}{view.nextReminderDays ? ` The next one goes ${view.nextReminderDays} days before.` : ""}</div>}
    <KpiStrip label="Sign-in figures" kpis={[
      { label: "Status", value: signInStatusLabel(view), detail: disabled ? `since ${date(view.disabledAt)}` : `last call ${date(view.lastCallAt)}` },
      disabled ? { label: "Waiting to send", value: view.waitingOutbound.toLocaleString(), detail: "sent after sign-in, oldest first" }
        : { label: "Expires", value: view.refreshChainExpiresAt ? parallelDate(view.refreshChainExpiresAt) : "—", detail: days === null ? "not signed in" : `in ${days} day${days === 1 ? "" : "s"} · set in the ERP` },
      { label: "Expiry rule", value: expiryRuleLabel(view), detail: "from sign-in · set in the ERP" },
      { label: "Password stored", value: "Never", detail: "tokens only · encrypted" },
    ]} />
    <div className="erp-change-columns">
      <section className="card" aria-labelledby="signin-history"><div className="card-header"><h2 id="signin-history">Sign-in history <span className="meta">token renewals summarised per day</span></h2></div>
        <PagedGrid source={history} columns={columns} rowId={item => String(item.sequence)} empty="No sign-in events yet." />
      </section>
      <section className="card" aria-labelledby="signin-account"><div className="card-header"><h2 id="signin-account">Account</h2></div><div className="card-body stack">
        <ul className="erp-checks">
          <li><span>Signed in by<span className="s">{view.signedInBy ? `${view.signedInBy} · ${parallelDate(view.signedInAt, true)}` : "Not signed in yet"}</span></span><StatusBadge tone="neutral">Info</StatusBadge></li>
          <li><span>Reminders before expiry<span className="s">{disabled ? "paused while disabled" : `${view.reminderDays.map(value => `${value} days`).join(" and ")} · to ${view.reminderRecipientRoles.map(role => human(role).toLocaleLowerCase()).join(", ")}`}</span></span><StatusBadge tone={disabled ? "neutral" : "ok"}>{disabled ? "Paused" : "On"}</StatusBadge></li>
          <li><span>Licence tier read at sign-in<span className="s">{view.licensedRequestsPerMinute.toLocaleString()} requests a minute · module stays below {view.governedRequestsPerMinute.toLocaleString()}</span></span><StatusBadge tone={view.requestHeadroomPerMinute >= 0 ? "ok" : "warn"}>{human(view.licenceTier)}</StatusBadge></li>
        </ul>
        <div className="note erp-consequence">{disabled
          ? <><strong>Sign in to enable</strong> opens the ERP's own sign-in page for the API user. The module never sees the password. After the ERP confirms, sync resumes and the waiting documents are sent. The new expiry date comes from the ERP's setting.</>
          : <><strong>Disable</strong> deletes the module's tokens now and stops all sync with this ERP. Queued changes stay queued. To use the connection again, someone must sign in again. To cut access off in the ERP as well, the ERP administrator revokes the connected application or deactivates the API user; the setup guide shows how.</>}</div>
      </div></section>
    </div>
    <ReminderSettings connectionId={connectionId} />
  </>;
}

function ReminderSettings({ connectionId }: { connectionId: string }) {
  const [settings, setSettings] = useState<ReminderSettingsView | null>(null); const [error, setError] = useState<unknown>(null);
  const [days, setDays] = useState(""); const [roles, setRoles] = useState<string[]>([]); const action = useAction();
  useEffect(() => { const controller = new AbortController(); signInApi.reminders(connectionId, controller.signal).then(value => { if (!controller.signal.aborted) { setSettings(value); setDays(value.daysBeforeExpiry.join(", ")); setRoles(value.recipientRoles); } }).catch(reason => { if (!controller.signal.aborted) setError(reason); }); return () => controller.abort(); }, [connectionId]);
  const parsed = parseReminderDays(days);
  return <section className="card" aria-labelledby="signin-reminders"><div className="card-header"><h2 id="signin-reminders">Expiry reminders <span className="meta">tenant setting · in-app and email</span></h2></div><div className="card-body stack">
    <ErrorNote error={error} />
    {settings && <form className="stack" onSubmit={event => { event.preventDefault(); if (!parsed) return; void action.run(async () => { const saved = await signInApi.saveReminders(connectionId, parsed, roles, settings.settingsVersion); setSettings(saved); setDays(saved.daysBeforeExpiry.join(", ")); setRoles(saved.recipientRoles); }, "Reminder settings saved."); }}>
      <label className="field"><span className="lbl">Days before expiry (one to three, 1–90)</span><input className="input" value={days} onChange={event => setDays(event.target.value)} aria-invalid={!parsed} /></label>
      {!parsed && <p className="help">Enter one to three different whole numbers from 1 to 90, for example 14, 3.</p>}
      <fieldset className="erp-options"><legend>Who is reminded</legend>{recipientChoices.map(([value, label]) => <label key={value} className={roles.includes(value) ? "on" : ""}><input type="checkbox" checked={roles.includes(value)} onChange={event => setRoles(current => event.target.checked ? [...current, value] : current.filter(role => role !== value))} />{label}</label>)}</fieldset>
      <p className="muted">The tenant administrator is always told when sync pauses, with the reason.</p>
      <button className="btn" disabled={action.busy || !parsed || roles.length === 0}>Save reminder settings</button>
    </form>}
    {action.feedback}
  </div></section>;
}

function openDisable(connectionId: string, name: string, waiting: number, onDone: () => void) {
  openDrawer({ eyebrow: "Sign-in", title: `Disable ${name}` }, <DisableForm connectionId={connectionId} waiting={waiting} onDone={onDone} />);
}

export function DisableForm({ connectionId, waiting, onDone }: { connectionId: string; waiting: number; onDone: () => void }) {
  const [reason, setReason] = useState(""); const [confirmed, setConfirmed] = useState(false); const action = useAction();
  return <form className="erp-drawer-form" onSubmit={event => { event.preventDefault(); void action.run(async () => { await signInApi.disable(connectionId, reason.trim()); onDone(); closeDrawer(); }, "Sign-in disabled; tokens deleted."); }}>
    <div className="note erp-consequence">The module's tokens are deleted now and all sync with this ERP stops. {waiting.toLocaleString()} queued document{waiting === 1 ? " stays" : "s stay"} queued. Someone must sign in again to use the connection.</div>
    <label className="field"><span className="lbl">Reason (required, shown in the history)</span><textarea className="input" required maxLength={500} value={reason} onChange={event => setReason(event.target.value)} /></label>
    <label className="erp-confirm"><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} />Delete the tokens and stop sync now.</label>
    <button className="btn danger" disabled={action.busy || !reason.trim() || !confirmed}><Icon name="x" />Disable sign-in</button>{action.feedback}
  </form>;
}

function openSetupGuide() {
  openDrawer({ eyebrow: "Sign-in", title: "Setup guide for the API user" }, <SetupGuide />);
}

function SetupGuide() {
  return <div className="stack">
    <ol className="erp-chain">
      {[["Create a dedicated API user", "used by nothing else; read and post rights only for the documents this connection exchanges"],
        ["Register the connected application", "for this company only; its callback address is this connection's sign-in callback page"],
        ["Set the token lifetime", "the expiry rule and number of days are the ERP's setting; the module reads and shows it"],
        ["Sign the API user in", "choose Sign in here; the ERP's own page asks for the password, never this module"],
        ["To cut access off in the ERP", "revoke the connected application or deactivate the API user there; Disable here deletes the module's tokens"]]
        .map(([title, sub], index) => <li key={title}><span className="v">{index + 1}</span><span><span className="t">{title}</span><span className="s">{sub}</span></span><span /></li>)}
    </ol>
    <p className="muted">Callback address: <span className="mono">{`${window.location.origin}/integration/connections/<connection>/sign-in/callback`}</span></p>
  </div>;
}
