import { useEffect, useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import { ActionDialog } from "../components";
import { PasswordChangeForm } from "../security/PasswordChangeForm";
import { passkeyApi, type PasskeyAccount } from "../security/passkeyApi";
import { rememberPasskeyFirst } from "../security/passkeyHint";
import { cancelled, passkeysSupported } from "../security/webauthn";
import { secondStepApi, type EnrolmentStart, type SecondStepState } from "../security/secondStepApi";
import { Icon } from "../shell/Icons";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { QuestionNotificationPreferences } from "../questions/QuestionNotificationPreferences";

type Dialog = "setup" | "codes" | "remove" | null;

export function Component() {
  const [saved, setSaved] = useState(false);
  return <main className="content" id="main" tabIndex={-1}><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Your account" }, { label: "Security" }]} /><PageHeader eyebrow="Your account" title="Security" sub="Manage the password, second step and passkeys used for your Yojana account." />
    {saved && <div className="note ok"><strong>Password changed.</strong>&nbsp;You stayed signed in here and every other session ended.</div>}
    <div className="stack top" style={{ maxWidth: 720 }}><QuestionNotificationPreferences /><section className="card" aria-labelledby="account-password"><div className="card-header"><h2 id="account-password">Password</h2><span className="meta">Never expires</span></div><div className="card-body"><PasswordChangeForm onChanged={() => setSaved(true)} /></div></section><SecondStepCard /><PasskeysCard /></div>
  </main>;
}

function SecondStepCard() {
  const [state, setState] = useState<SecondStepState | null>(null); const [dialog, setDialog] = useState<Dialog>(null); const [shownCodes, setShownCodes] = useState<string[]>([]); const [error, setError] = useState<string | null>(null);
  const load = () => secondStepApi.state().then(setState).catch(() => setError("Second-step settings could not be loaded."));
  useEffect(() => { void load(); }, []);
  if (!state) return <section className="card"><div className="card-body">{error ?? "Loading second-step settings…"}</div></section>;
  return <><section className="card" aria-labelledby="account-second-step"><div className="card-header"><h2 id="account-second-step">Second step <span className={`badge ${state.enrolled || state.passkeys > 0 ? "ok" : "neutral"}`}>{state.enrolled || state.passkeys > 0 ? "On" : "Off"}</span></h2><span className="badge neutral">{state.required ? "Required for your role" : state.mode === "off" ? "Off for your roles" : "Optional"}</span></div><div className="card-body">
    <div className="kv"><span className="k">Authenticator app</span><span className="v">{state.enrolled ? `Added ${new Date(state.addedAt!).toLocaleDateString()}` : "Not set up"}</span><span className="k">Recovery codes</span><span className="v">{state.enrolled ? `${state.recoveryCodesLeft} of 10 left` : "—"}</span><span className="k">Passkeys</span><span className="v">{state.passkeys > 0 ? `${state.passkeys} (count as the second step)` : "None"}</span></div>
  </div><div className="card-footer">{state.enrolled ? <><button type="button" className="btn sm" onClick={() => setDialog("setup")}>Replace authenticator…</button><button type="button" className="btn sm" onClick={() => setDialog("codes")}>New recovery codes…</button></> : <button type="button" className="btn sm primary" onClick={() => setDialog("setup")}>Set up authenticator…</button>}<span className="grow" />{state.enrolled && state.mode === "optional" ? <button type="button" className="btn sm danger" onClick={() => setDialog("remove")}>Turn off second step</button> : state.enrolled && state.required ? <span className="muted">You cannot turn the second step off: your company requires it</span> : state.enrolled && <span className="muted">Your roles do not currently ask for a second step.</span>}</div></section>
    {shownCodes.length > 0 && <RecoveryCodes codes={shownCodes} />}
    {dialog === "setup" && <EnrolmentDialog replacing={state.enrolled} onCancel={() => setDialog(null)} onSaved={(codes) => { setShownCodes(codes); setDialog(null); void load(); }} />}
    {dialog === "codes" && <CodeDialog title="New recovery codes" label="Current authenticator code" action={async (value) => (await secondStepApi.recoveryCodes(value)).recoveryCodes} onCancel={() => setDialog(null)} onSaved={(codes) => { setShownCodes(codes); setDialog(null); void load(); }} />}
    {dialog === "remove" && <CodeDialog title="Turn off second step" label="Current password" action={async (value) => { await secondStepApi.remove(value); return []; }} onCancel={() => setDialog(null)} onSaved={() => { setShownCodes([]); setDialog(null); void load(); }} />}
  </>;
}

function EnrolmentDialog({ replacing, onCancel, onSaved }: { replacing: boolean; onCancel: () => void; onSaved: (codes: string[]) => void }) {
  const [start, setStart] = useState<EnrolmentStart | null>(null); const [code, setCode] = useState(""); const [password, setPassword] = useState(""); const [error, setError] = useState<string | null>(null);
  useEffect(() => { secondStepApi.start().then(setStart).catch(() => setError("Authenticator setup is unavailable.")); }, []);
  const save = async (event: FormEvent) => { event.preventDefault(); if (!start) return; try { onSaved((await secondStepApi.confirm(start.enrolmentId, code, replacing ? password : undefined)).recoveryCodes); } catch (reason) { setError(reason instanceof ApiError ? reason.detail : "The code was not accepted."); } };
  return <ActionDialog title={replacing ? "Replace authenticator" : "Set up authenticator"} onCancel={onCancel} actions={<button className="btn primary" type="submit" form="authenticator-setup" disabled={!start}>Confirm</button>}><form id="authenticator-setup" onSubmit={save} className="stack">{replacing && <div className="note warn">Your old authenticator keeps working until the new code is verified.</div>}{replacing && <label className="field"><span className="lbl">Current password</span><input className="input" type="password" autoComplete="current-password" required value={password} onChange={(event) => setPassword(event.target.value)} /><span className="help">Asked so that a signed-in session alone cannot replace your second step.</span></label>}{start && <><img src={`data:image/svg+xml;charset=utf-8,${encodeURIComponent(start.qrSvg)}`} alt="Authenticator enrolment QR code" style={{ width: 220, height: 220, margin: "0 auto" }} /><div className="field"><span className="lbl">Manual key</span><code>{start.manualKey}</code></div></>}<label className="field"><span className="lbl">Six-digit code</span><input className="input mono" inputMode="numeric" autoComplete="one-time-code" value={code} onChange={(event) => setCode(event.target.value)} /></label>{error && <div className="form-error" role="alert">{error}</div>}</form></ActionDialog>;
}

function PasskeysCard() {
  const [account, setAccount] = useState<PasskeyAccount | null>(null); const [error, setError] = useState<string | null>(null); const [dialog, setDialog] = useState<"add" | { remove: string; name: string } | null>(null);
  const load = () => passkeyApi.account().then((value) => { rememberPasskeyFirst(value.allowed && value.passwordless && value.passkeys.length > 0); setAccount(value); }).catch(() => setError("Passkeys could not be loaded."));
  useEffect(() => { void load(); }, []);
  if (!account) return <section className="card"><div className="card-body">{error ?? "Loading passkeys…"}</div></section>;
  const supported = passkeysSupported();
  return <><section className="card" aria-labelledby="account-passkeys"><div className="card-header"><h2 id="account-passkeys">Passkeys <span className="meta">{account.passkeys.length}</span></h2>{account.allowed && <div className="right"><button type="button" className="btn sm" disabled={!supported} title={supported ? undefined : "This browser cannot create passkeys"} onClick={() => setDialog("add")}><Icon name="plus" />Add a passkey</button></div>}</div>
    {account.passkeys.length > 0 ? <table className="table"><colgroup><col style={{ width: "38%" }} /><col style={{ width: "22%" }} /><col style={{ width: "22%" }} /><col style={{ width: "18%" }} /></colgroup><thead><tr><th>Name</th><th>Added</th><th>Last used</th><th></th></tr></thead><tbody>{account.passkeys.map((passkey) => <tr key={passkey.id}><td>{passkey.name}{passkey.synced && <span className="muted"> · synced</span>}</td><td>{new Date(passkey.createdAt).toLocaleDateString()}</td><td>{passkey.lastUsedAt ? new Date(passkey.lastUsedAt).toLocaleString() : "Never"}</td><td><button type="button" className="btn sm" onClick={() => setDialog({ remove: passkey.id, name: passkey.name })}>Remove</button></td></tr>)}</tbody></table>
      : <div className="card-body"><p className="muted">{account.allowed ? "No passkeys yet. A passkey uses your fingerprint, face or device PIN, and counts as the second step." : "Your company does not allow passkeys."}</p></div>}
    <div className="card-footer"><span className="muted">{account.passwordless ? "Your company lets a passkey sign you in on its own. " : ""}Removing a passkey e-mails you, so you would notice if someone else did it</span></div></section>
    {dialog === "add" && <AddPasskeyDialog onCancel={() => setDialog(null)} onSaved={() => { setDialog(null); void load(); }} />}
    {dialog && dialog !== "add" && <CodeDialog title={`Remove passkey · ${dialog.name}`} label="Current password" action={async (value) => { await passkeyApi.remove(dialog.remove, value); return []; }} onCancel={() => setDialog(null)} onSaved={() => { setDialog(null); void load(); }} />}
  </>;
}

function AddPasskeyDialog({ onCancel, onSaved }: { onCancel: () => void; onSaved: () => void }) {
  const [name, setName] = useState(defaultPasskeyName()); const [password, setPassword] = useState(""); const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  const save = async (event: FormEvent) => { event.preventDefault(); setBusy(true); setError(null); try { await passkeyApi.add(name, password); onSaved(); } catch (reason) { setError(cancelled(reason) ? "Your device did not create a passkey. Try again when you are ready." : reason instanceof ApiError ? reason.detail : "The passkey could not be added."); } finally { setBusy(false); } };
  return <ActionDialog title="Add a passkey" onCancel={onCancel} actions={<button className="btn primary" type="submit" form="passkey-add" disabled={busy}>{busy ? "Waiting for your device…" : "Continue"}</button>}><form id="passkey-add" onSubmit={save} className="stack"><label className="field"><span className="lbl">Name</span><input className="input" maxLength={60} value={name} onChange={(event) => setName(event.target.value)} /><span className="help">So you can tell your passkeys apart, for example "Work laptop".</span></label><label className="field"><span className="lbl">Current password</span><input className="input" type="password" autoComplete="current-password" required value={password} onChange={(event) => setPassword(event.target.value)} /><span className="help">Asked so that a signed-in session alone cannot add a way into your account.</span></label><p className="muted">Next, your browser asks for your fingerprint, face or device PIN.</p>{error && <div className="form-error" role="alert">{error}</div>}</form></ActionDialog>;
}

/** A starting name from the browser's platform; the person can change it. */
function defaultPasskeyName() { const agent = navigator.userAgent; return /iPhone|iPad/.test(agent) ? "iPhone or iPad" : /Android/.test(agent) ? "Android phone" : /Mac/.test(agent) ? "Mac" : /Windows/.test(agent) ? "Windows computer" : "This device"; }

function CodeDialog({ title, label, action, onCancel, onSaved }: { title: string; label: string; action: (value: string) => Promise<string[]>; onCancel: () => void; onSaved: (codes: string[]) => void }) {
  const [value, setValue] = useState(""); const [error, setError] = useState<string | null>(null);
  const save = async (event: FormEvent) => { event.preventDefault(); try { onSaved(await action(value)); } catch (reason) { setError(reason instanceof ApiError ? reason.detail : "The request could not be completed."); } };
  return <ActionDialog title={title} onCancel={onCancel} actions={<button className="btn primary" type="submit" form="second-step-action">Continue</button>}><form id="second-step-action" onSubmit={save}><label className="field"><span className="lbl">{label}</span><input className="input" type={label.includes("password") ? "password" : "text"} autoComplete={label.includes("password") ? "current-password" : "one-time-code"} value={value} onChange={(event) => setValue(event.target.value)} /></label>{error && <div className="form-error" role="alert">{error}</div>}</form></ActionDialog>;
}

function RecoveryCodes({ codes }: { codes: string[] }) { return <section className="card" aria-labelledby="new-recovery-codes"><div className="card-header"><h2 id="new-recovery-codes">Recovery codes <span className="meta">shown once</span></h2></div><div className="card-body"><p className="muted">Each code works once. Keep them somewhere safe, away from this computer.</p><div className="codes">{codes.map((code) => <code key={code}>{code}</code>)}</div></div><div className="card-footer"><button className="btn sm" type="button" onClick={() => void navigator.clipboard.writeText(codes.join("\n"))}>Copy</button><button className="btn sm" type="button" onClick={() => download(codes)}>Download .txt</button></div></section>; }
function download(codes: string[]) { const url = URL.createObjectURL(new Blob([codes.join("\n") + "\n"], { type: "text/plain" })); const link = document.createElement("a"); link.href = url; link.download = "yojana-recovery-codes.txt"; link.click(); URL.revokeObjectURL(url); }
