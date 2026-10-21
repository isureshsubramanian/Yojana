import { useEffect, useState, type FormEvent } from "react";
import { useNavigate, useSearchParams } from "react-router";
import { api, ApiError } from "../api/client";
import type { Session } from "../api/types";
import { Shortcut } from "../components/Shortcut";
import { secondStepApi, type EnrolmentStart, type PendingSecondStep } from "../security/secondStepApi";
import { BareFrame } from "../shell/Frame";
import { Icon } from "../shell/Icons";

const isPending = (value: Session | PendingSecondStep): value is PendingSecondStep => "secondStepRequired" in value;

export function Component() {
  const [params] = useSearchParams(); const navigate = useNavigate();
  const [email, setEmail] = useState(""); const [password, setPassword] = useState("");
  const [pending, setPending] = useState<PendingSecondStep | null>(null); const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  const finish = (session: Session) => { const next = params.get("next"); navigate(session.passwordChangeRequired ? "/change-password" : session.tenant ? (next && next.startsWith("/") ? next : "/") : "/tenants", { replace: true }); };
  const submit = async (e: FormEvent) => { e.preventDefault(); setBusy(true); setError(null); try { const result = await api<Session | PendingSecondStep>("/api/auth/sign-in", { method: "POST", body: { email, password } }); if (isPending(result)) setPending(result); else finish(result); } catch (err) { setError(err instanceof ApiError && err.status === 401 ? "That email and password do not match an active account." : "Sign-in is unavailable right now. Try again in a moment."); } finally { setBusy(false); } };
  return <BareFrame><div className="auth">{pending ? <SecondStep pending={pending} onComplete={finish} /> : <section className="card" aria-labelledby="h-signin"><div className="card-body">
    <h1 id="h-signin">Sign in</h1><p className="lead">Use your work account. Your tenant and roles are applied after sign-in.</p>
    <form style={{ display: "grid", gap: 16, marginTop: 24 }} onSubmit={submit} noValidate>
      <div className="field"><label htmlFor="s-email">Email</label><input id="s-email" className="input" type="email" autoComplete="username" required autoFocus value={email} onChange={(e) => setEmail(e.target.value)} /></div>
      <div style={{ display: "flex", justifyContent: "flex-end" }}><a href="/forgot-password" style={{ fontSize: 13 }}>Forgot password?</a></div>
      <div className="field"><label htmlFor="s-pass">Password</label><input id="s-pass" className="input" type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} /></div>
      {error && <div className="form-error" role="alert">{error}</div>}<button type="submit" className="btn primary block" aria-busy={busy}>Sign in<Shortcut keys="enter" /></button>
      <div className="divider">or</div><button type="button" className="btn block" disabled title="Single sign-on is configured per tenant">Continue with single sign-on</button>
      <div className="scanbox"><Icon name="scan" /><input className="input" type="password" aria-label="Scan operator badge" placeholder="Scan operator badge at a station" disabled /></div>
    </form></div><div className="card-footer" style={{ justifyContent: "center", gap: 20 }}><a href="/notices" className="muted" target="_blank" rel="noopener">Third-party notices</a><a href="/health" className="muted" target="_blank" rel="noopener">Status</a></div></section>}
    <p className="hint" style={{ textAlign: "center", marginTop: 16 }}>Yojana 0.1 · session cookie only · no tracking</p></div></BareFrame>;
}

function SecondStep({ pending, onComplete }: { pending: PendingSecondStep; onComplete: (session: Session) => void }) {
  const [method, setMethod] = useState<"authenticator" | "recovery_code">("authenticator"); const [code, setCode] = useState("");
  const [enrolment, setEnrolment] = useState<EnrolmentStart | null>(null); const [codes, setCodes] = useState<string[]>([]); const [saved, setSaved] = useState(false); const [session, setSession] = useState<Session | null>(null);
  const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  useEffect(() => { if (pending.enrolmentRequired) secondStepApi.start().then(setEnrolment).catch(() => setError("Authenticator setup is unavailable right now.")); }, [pending.enrolmentRequired]);
  const submit = async (event: FormEvent) => { event.preventDefault(); setBusy(true); setError(null); try { if (pending.enrolmentRequired) { if (!enrolment) return; const result = await secondStepApi.confirm(enrolment.enrolmentId, code); setCodes(result.recoveryCodes); setSession(result.session); } else onComplete(await secondStepApi.complete(method, code)); } catch (reason) { setError(reason instanceof ApiError && reason.title === "second_step_code_wrong" ? "That code was not accepted. Check it and try again." : "The second step could not be completed."); } finally { setBusy(false); } };
  if (codes.length) return <section className="card" aria-labelledby="h-recovery"><div className="card-body"><p className="stepno">2 · Save recovery codes</p><h1 id="h-recovery">Keep these codes somewhere safe</h1><p className="lead">Each code works once if you lose your phone. They will not be shown again.</p><div className="codes" aria-label="Recovery codes">{codes.map((value) => <code key={value}>{value}</code>)}</div><div className="cluster" style={{ marginTop: 18 }}><button className="btn" type="button" onClick={() => void navigator.clipboard.writeText(codes.join("\n"))}>Copy</button><button className="btn" type="button" onClick={() => downloadCodes(codes)}>Download .txt</button></div><label className="checkrow" style={{ marginTop: 18 }}><input type="checkbox" checked={saved} onChange={(event) => setSaved(event.target.checked)} /><span>I have saved these codes</span></label><button className="btn primary block" type="button" disabled={!saved || !session} onClick={() => session && onComplete(session)}>Continue</button></div></section>;
  return <section className="card" aria-labelledby="h-second-step"><div className="card-body"><p className="stepno">2 · Second step {pending.enrolmentRequired ? "(required for your role)" : ""}</p><h1 id="h-second-step">{pending.enrolmentRequired ? "Set up an authenticator app" : method === "authenticator" ? "Enter the code from your authenticator app" : "Use a recovery code"}</h1>
    {pending.enrolmentRequired && enrolment && <><p className="lead">Scan this QR code, or enter the manual key, then type the six-digit code.</p><img src={`data:image/svg+xml;charset=utf-8,${encodeURIComponent(enrolment.qrSvg)}`} alt="Authenticator enrolment QR code" style={{ width: 220, height: 220, display: "block", margin: "18px auto" }} /><div className="field"><span className="lbl">Manual key</span><code>{enrolment.manualKey}</code></div></>}{!pending.enrolmentRequired && <p className="lead">Open your authenticator app and enter its current six-digit code.</p>}
    <form onSubmit={submit} style={{ display: "grid", gap: 16, marginTop: 20 }}><label className="field"><span className="lbl">{method === "authenticator" ? "Six-digit code" : "Recovery code"}</span><input className="input mono" inputMode={method === "authenticator" ? "numeric" : "text"} autoComplete="one-time-code" autoFocus value={code} onChange={(event) => setCode(event.target.value)} /></label>{error && <div className="form-error" role="alert">{error}</div>}<button className="btn primary block" type="submit" disabled={busy || (pending.enrolmentRequired && !enrolment)}>Continue</button></form>
    {!pending.enrolmentRequired && pending.methods.includes("recovery_code") && <button className="btn block" type="button" style={{ marginTop: 12 }} onClick={() => { setMethod(method === "authenticator" ? "recovery_code" : "authenticator"); setCode(""); setError(null); }}>{method === "authenticator" ? "Use a recovery code" : "Use authenticator app"}</button>}
  </div><div className="card-footer" style={{ justifyContent: "center" }}><span className="muted">Lost your phone? Use a recovery code, or ask your administrator to reset your second step.</span></div></section>;
}

function downloadCodes(codes: string[]) { const url = URL.createObjectURL(new Blob([codes.join("\n") + "\n"], { type: "text/plain" })); const link = document.createElement("a"); link.href = url; link.download = "yojana-recovery-codes.txt"; link.click(); URL.revokeObjectURL(url); }
