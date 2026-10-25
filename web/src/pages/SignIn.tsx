import { useEffect, useState, type FormEvent } from "react";
import { Link, useNavigate, useSearchParams } from "react-router";
import * as z from "zod/mini";
import { apiParsed, ApiError } from "../api/client";
import { session, type Session } from "../api/types";
import { Shortcut } from "../components/Shortcut";
import { passkeyApi } from "../security/passkeyApi";
import { passkeyFirst, rememberPasskeyFirst } from "../security/passkeyHint";
import { pendingSecondStep, secondStepApi, type EnrolmentStart, type PendingSecondStep } from "../security/secondStepApi";
import { cancelled, passkeysSupported } from "../security/webauthn";
import { BareFrame } from "../shell/Frame";
import { Icon } from "../shell/Icons";

const isPending = (value: Session | PendingSecondStep): value is PendingSecondStep => "secondStepRequired" in value;

/** What a failed passkey answer means to the person; a cancelled prompt is not a failure. */
export function passkeyError(reason: unknown): string | null {
  if (cancelled(reason)) return null;
  if (reason instanceof ApiError && reason.title === "passkey_not_allowed") return "Your company does not allow signing in with a passkey alone. Use your password.";
  if (reason instanceof ApiError && reason.title === "passkey_rejected") return "That passkey was not accepted. Try again, or use your password.";
  if (reason instanceof ApiError && reason.title === "second_step_expired") return "Sign in with your password again.";
  if (reason instanceof ApiError && reason.status === 401) return "Sign-in is paused after too many attempts. Wait a minute and try again.";
  if (reason instanceof ApiError && reason.title === "passkey_unavailable") return "Passkeys are not set up on this server.";
  return "The passkey sign-in could not be completed. Try again, or use your password.";
}

export function Component() {
  const [params] = useSearchParams(); const navigate = useNavigate();
  const [email, setEmail] = useState(""); const [password, setPassword] = useState("");
  const [pending, setPending] = useState<PendingSecondStep | null>(null); const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(false); const [waiting, setWaiting] = useState(false);
  const [passwordForm, setPasswordForm] = useState(() => !(passkeyFirst() && passkeysSupported()));
  const finish = (session: Session) => { void passkeyApi.account().then((account) => rememberPasskeyFirst(account.allowed && account.passwordless && account.passkeys.length > 0)).catch(() => undefined); const next = params.get("next"); navigate(session.passwordChangeRequired ? "/change-password" : session.tenant ? (next && next.startsWith("/") ? next : "/") : "/tenants", { replace: true }); };
  const submit = async (e: FormEvent) => { e.preventDefault(); setBusy(true); setError(null); try { const result = await apiParsed("/api/auth/sign-in", z.union([pendingSecondStep, session]), { method: "POST", body: { email, password } }); if (isPending(result)) setPending(result); else finish(result); } catch (err) { setError(err instanceof ApiError && err.status === 401 ? "That email and password do not match an active account." : "Sign-in is unavailable right now. Try again in a moment."); } finally { setBusy(false); } };
  const withPasskey = async () => { setWaiting(true); setError(null); try { const session = await passkeyApi.signIn("passwordless"); rememberPasskeyFirst(true); finish(session); } catch (reason) { if (reason instanceof ApiError && reason.title === "passkey_not_allowed") { rememberPasskeyFirst(false); setPasswordForm(true); } setError(passkeyError(reason)); setWaiting(false); } };
  const usePassword = () => { setPasswordForm(true); setWaiting(false); setError(null); };
  return <BareFrame><div className="auth">{pending ? <SecondStep pending={pending} onComplete={finish} /> : waiting ? <PasskeyWaiting onCancel={usePassword} /> : <section className="card" aria-labelledby="h-signin"><div className="card-body">
    <h1 id="h-signin">Sign in</h1><p className="lead">Use your work account. Your tenant and roles are applied after sign-in.</p>
    {!passwordForm ? <div style={{ display: "grid", gap: 16, marginTop: 24 }}>
      {error && <div className="form-error" role="alert">{error}</div>}
      <button type="button" className="btn primary block" autoFocus onClick={() => void withPasskey()}><Icon name="key" />Sign in with a passkey</button>
      <button type="button" className="btn block" onClick={usePassword}>Use my password instead</button>
      <p className="hint" style={{ textAlign: "center" }}>This browser remembers you last signed in with a passkey.</p>
    </div> : <form style={{ display: "grid", gap: 16, marginTop: 24 }} onSubmit={submit} noValidate>
      <div className="field"><label htmlFor="s-email">Email</label><input id="s-email" className="input" type="email" autoComplete="username webauthn" required autoFocus value={email} onChange={(e) => setEmail(e.target.value)} /></div>
      <div style={{ display: "flex", justifyContent: "flex-end" }}><a href="/forgot-password" style={{ fontSize: 13 }}>Forgot password?</a></div>
      <div className="field"><label htmlFor="s-pass">Password</label><input id="s-pass" className="input" type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} /></div>
      {error && <div className="form-error" role="alert">{error}</div>}<button type="submit" className="btn primary block" aria-busy={busy}>Sign in<Shortcut keys="enter" /></button>
      <div className="divider">or</div>
      {passkeysSupported() && <button type="button" className="btn block" onClick={() => void withPasskey()}><Icon name="key" />Sign in with a passkey</button>}
      <button type="button" className="btn block" disabled title="Single sign-on is configured per tenant">Continue with single sign-on</button>
      <div className="scanbox"><Icon name="scan" /><input className="input" type="password" aria-label="Scan operator badge" placeholder="Scan operator badge at a station" disabled /></div>
    </form>}</div><div className="card-footer" style={{ justifyContent: "center", gap: 20 }}><Link to="/legal/third-party-notices" className="muted">Third-party notices</Link><a href="/health" className="muted" target="_blank" rel="noopener">Status</a></div></section>}
    <p className="hint" style={{ textAlign: "center", marginTop: 16 }}>Yojana 0.1 · session cookie only · no tracking</p></div></BareFrame>;
}

/** Shown behind the browser's own prompt while it asks the device; cancelling there brings the person back here. */
function PasskeyWaiting({ onCancel }: { onCancel: () => void }) {
  return <section className="card" aria-labelledby="h-passkey"><div className="card-body" style={{ textAlign: "center" }}>
    <Icon name="key" /><h1 id="h-passkey" style={{ marginTop: 12 }}>Confirm on your device</h1>
    <p className="lead">Your browser is asking for your fingerprint, face or device PIN. Nothing is sent to us but a signed answer.</p>
    <div className="note" style={{ marginTop: 20, textAlign: "left" }}>A passkey counts as both steps, so you will not be asked for a code.</div>
    <div style={{ display: "grid", gap: 10, marginTop: 20 }}><button type="button" className="btn block" disabled>Waiting for your device…</button><button type="button" className="btn block" onClick={onCancel}>Use my password instead</button></div>
  </div><div className="card-footer" style={{ justifyContent: "center" }}><span className="muted">Your company decides whether a passkey alone may sign you in</span></div></section>;
}

function SecondStep({ pending, onComplete }: { pending: PendingSecondStep; onComplete: (session: Session) => void }) {
  const codesOffered = pending.enrolmentRequired || pending.methods.includes("authenticator"); const passkeyOffered = !pending.enrolmentRequired && pending.methods.includes("passkey") && passkeysSupported();
  const [method, setMethod] = useState<"authenticator" | "recovery_code">("authenticator"); const [code, setCode] = useState("");
  const [enrolment, setEnrolment] = useState<EnrolmentStart | null>(null); const [codes, setCodes] = useState<string[]>([]); const [saved, setSaved] = useState(false); const [session, setSession] = useState<Session | null>(null);
  const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  useEffect(() => { if (pending.enrolmentRequired) secondStepApi.start().then(setEnrolment).catch(() => setError("Authenticator setup is unavailable right now.")); }, [pending.enrolmentRequired]);
  const submit = async (event: FormEvent) => { event.preventDefault(); setBusy(true); setError(null); try { if (pending.enrolmentRequired) { if (!enrolment) return; const result = await secondStepApi.confirm(enrolment.enrolmentId, code); setCodes(result.recoveryCodes); setSession(result.session); } else onComplete(await secondStepApi.complete(method, code)); } catch (reason) { setError(reason instanceof ApiError && reason.title === "second_step_code_wrong" ? "That code was not accepted. Check it and try again." : "The second step could not be completed."); } finally { setBusy(false); } };
  const withPasskey = async () => { setBusy(true); setError(null); try { onComplete(await passkeyApi.signIn("second_step")); } catch (reason) { setError(passkeyError(reason)); } finally { setBusy(false); } };
  if (codes.length) return <section className="card" aria-labelledby="h-recovery"><div className="card-body"><p className="stepno">2 · Save recovery codes</p><h1 id="h-recovery">Keep these codes somewhere safe</h1><p className="lead">Each code works once if you lose your phone. They will not be shown again.</p><div className="codes" aria-label="Recovery codes">{codes.map((value) => <code key={value}>{value}</code>)}</div><div className="cluster" style={{ marginTop: 18 }}><button className="btn" type="button" onClick={() => void navigator.clipboard.writeText(codes.join("\n"))}>Copy</button><button className="btn" type="button" onClick={() => downloadCodes(codes)}>Download .txt</button></div><label className="checkrow" style={{ marginTop: 18 }}><input type="checkbox" checked={saved} onChange={(event) => setSaved(event.target.checked)} /><span>I have saved these codes</span></label><button className="btn primary block" type="button" disabled={!saved || !session} onClick={() => session && onComplete(session)}>Continue</button></div></section>;
  if (!codesOffered) return <section className="card" aria-labelledby="h-second-step"><div className="card-body"><p className="stepno">2 · Second step</p><h1 id="h-second-step">Confirm with your passkey</h1><p className="lead">Your browser will ask for your fingerprint, face or device PIN.</p>{error && <div className="form-error" role="alert">{error}</div>}<button className="btn primary block" type="button" style={{ marginTop: 20 }} disabled={busy || !passkeyOffered} onClick={() => void withPasskey()}><Icon name="key" />Use my passkey</button>{!passkeyOffered && <p className="muted">This browser cannot use passkeys. Sign in on a device that holds your passkey, or ask your administrator to reset your second step.</p>}</div></section>;
  return <section className="card" aria-labelledby="h-second-step"><div className="card-body"><p className="stepno">2 · Second step {pending.enrolmentRequired ? "(required for your role)" : ""}</p><h1 id="h-second-step">{pending.enrolmentRequired ? "Set up an authenticator app" : method === "authenticator" ? "Enter the code from your authenticator app" : "Use a recovery code"}</h1>
    {pending.enrolmentRequired && enrolment && <><p className="lead">Scan this QR code, or enter the manual key, then type the six-digit code.</p><img src={`data:image/svg+xml;charset=utf-8,${encodeURIComponent(enrolment.qrSvg)}`} alt="Authenticator enrolment QR code" style={{ width: 220, height: 220, display: "block", margin: "18px auto" }} /><div className="field"><span className="lbl">Manual key</span><code>{enrolment.manualKey}</code></div></>}{!pending.enrolmentRequired && <p className="lead">Open your authenticator app and enter its current six-digit code.</p>}
    <form onSubmit={submit} style={{ display: "grid", gap: 16, marginTop: 20 }}><label className="field"><span className="lbl">{method === "authenticator" ? "Six-digit code" : "Recovery code"}</span><input className="input mono" inputMode={method === "authenticator" ? "numeric" : "text"} autoComplete="one-time-code" autoFocus value={code} onChange={(event) => setCode(event.target.value)} /></label>{error && <div className="form-error" role="alert">{error}</div>}<button className="btn primary block" type="submit" disabled={busy || (pending.enrolmentRequired && !enrolment)}>Continue</button></form>
    {(passkeyOffered || (!pending.enrolmentRequired && pending.methods.includes("recovery_code"))) && <div className="divider" style={{ marginTop: 16 }}>other ways</div>}
    {passkeyOffered && <button className="btn block" type="button" style={{ marginTop: 12 }} disabled={busy} onClick={() => void withPasskey()}><Icon name="key" />Use a passkey instead</button>}
    {!pending.enrolmentRequired && pending.methods.includes("recovery_code") && <button className="btn block" type="button" style={{ marginTop: 12 }} onClick={() => { setMethod(method === "authenticator" ? "recovery_code" : "authenticator"); setCode(""); setError(null); }}>{method === "authenticator" ? "Use a recovery code" : "Use authenticator app"}</button>}
  </div><div className="card-footer" style={{ justifyContent: "center" }}><span className="muted">Lost your phone? Use a recovery code, or ask your administrator to reset your second step.</span></div></section>;
}

function downloadCodes(codes: string[]) { const url = URL.createObjectURL(new Blob([codes.join("\n") + "\n"], { type: "text/plain" })); const link = document.createElement("a"); link.href = url; link.download = "yojana-recovery-codes.txt"; link.click(); URL.revokeObjectURL(url); }
