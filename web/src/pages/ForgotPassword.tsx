import { useState, type FormEvent } from "react";
import { Link } from "react-router";
import { BareFrame } from "../shell/Frame";
import { passwordApi } from "../security/api";

export function Component() {
  const [email, setEmail] = useState("");
  const [sentTo, setSentTo] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);
  const submit = async (event: FormEvent) => {
    event.preventDefault(); setBusy(true); setError(false);
    try { await passwordApi.request(email); setSentTo(email); }
    catch { setError(true); }
    finally { setBusy(false); }
  };
  return <BareFrame><div className="auth"><section className="card" aria-labelledby="h-forgot"><div className="card-body">
    {sentTo === null ? <><h1 id="h-forgot">Forgot your password?</h1><p className="lead">Enter your work e-mail and we will send you a link to choose a new one.</p><form onSubmit={submit} noValidate>
      <label className="field"><span className="lbl">E-mail</span><input className="input" type="email" autoComplete="username" required autoFocus value={email} onChange={(event) => setEmail(event.target.value)} /></label>
      {error && <div className="form-error" role="alert">Reset is unavailable right now. Try again in a moment.</div>}
      <button className="btn primary block" type="submit" disabled={busy}>{busy ? "Sending…" : "Send reset link"}</button><Link className="btn block" to="/sign-in">Back to sign in</Link>
    </form></> : <><h1 id="h-forgot">Check your e-mail</h1><p className="lead">If an account exists for {sentTo}, we sent a link. It works once and for the time chosen by your company.</p><div className="note" style={{ marginTop: 20 }}>Nothing arrived after a few minutes? Look in junk mail, or ask your administrator to send the link for you.</div><div className="stack" style={{ marginTop: 20 }}><button className="btn block" type="button" onClick={() => setSentTo(null)}>Send it again</button><Link className="btn block" to="/sign-in">Back to sign in</Link></div></>}
  </div></section></div></BareFrame>;
}
