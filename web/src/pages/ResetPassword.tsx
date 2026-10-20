import { useMemo, useState, type FormEvent } from "react";
import { Link, useNavigate } from "react-router";
import { ApiError } from "../api/client";
import { passwordApi } from "../security/api";
import { PasswordRules } from "../security/PasswordRules";
import { BareFrame } from "../shell/Frame";
import { tokenFromHash } from "../security/resetModel";

export function Component() {
  const navigate = useNavigate();
  const token = useMemo(() => tokenFromHash(window.location.hash), []);
  const [password, setPassword] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [invalid, setInvalid] = useState(token === null);
  const [busy, setBusy] = useState(false);
  const [reasons, setReasons] = useState<readonly { code: string; message: string }[]>([]);
  const [error, setError] = useState<string | null>(null);
  const submit = async (event: FormEvent) => {
    event.preventDefault(); setError(null); setReasons([]);
    if (!token || password !== confirmation) { setError("Type the same new password twice."); return; }
    setBusy(true);
    try {
      const session = await passwordApi.complete(token, password);
      window.history.replaceState(null, "", window.location.pathname);
      navigate(session.tenant ? "/" : "/tenants", { replace: true });
    } catch (reason) {
      if (reason instanceof ApiError && reason.title === "reset_link_invalid") setInvalid(true);
      else if (reason instanceof ApiError) { setReasons(reason.reasons); setError(reason.detail); }
      else setError("The password could not be saved.");
    } finally { setBusy(false); }
  };
  return <BareFrame><div className="auth"><section className="card" aria-labelledby="h-reset"><div className="card-body">
    {invalid ? <><h1 id="h-reset">This link has expired</h1><p className="lead">Reset links work once. A newer request also cancels the earlier link.</p><Link className="btn primary block" to="/forgot-password" style={{ marginTop: 20 }}>Request a new link</Link><Link className="btn block" to="/sign-in" style={{ marginTop: 10 }}>Back to sign in</Link></> : <><h1 id="h-reset">Choose a new password</h1><p className="lead">Saving signs you out on every other device and signs you in here.</p><form onSubmit={submit} noValidate>
      <label className="field"><span className="lbl">New password</span><input className="input" type="password" autoComplete="new-password" required autoFocus value={password} onChange={(event) => setPassword(event.target.value)} /></label>
      <label className="field"><span className="lbl">Type it again</span><input className="input" type="password" autoComplete="new-password" required value={confirmation} onChange={(event) => setConfirmation(event.target.value)} /></label>
      <PasswordRules password={password} confirmation={confirmation} />
      {reasons.length > 0 && <ul className="note bad">{reasons.map((reason) => <li key={reason.code}>{reason.message}</li>)}</ul>}
      {error && <div className="form-error" role="alert">{error}</div>}
      <button className="btn primary block" type="submit" disabled={busy || password !== confirmation || password.length === 0}>{busy ? "Saving…" : "Save and sign in"}</button>
    </form></>}
  </div></section></div></BareFrame>;
}
