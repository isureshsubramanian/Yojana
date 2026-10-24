import { useEffect, useState, type FormEvent } from "react";
import { Link } from "react-router";
import { ApiError } from "../api/client";
import { api } from "../api/client";
import type { Session } from "../api/types";
import { PasswordRules, usePasswordRules } from "../security/PasswordRules";
import { BareFrame } from "../shell/Frame";
import { roleLabel } from "../settings/roles";
import { userManagementApi, type InvitationInspection } from "../settings/userManagementApi";

const tokenFromHash = () => new URLSearchParams(window.location.hash.replace(/^#/, "")).get("t") ?? "";

export function Component() {
  const [token] = useState(tokenFromHash);
  const [invitation, setInvitation] = useState<InvitationInspection | null>(null);
  const [invalid, setInvalid] = useState(false);
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [needsSignIn, setNeedsSignIn] = useState(false);
  const [reasons, setReasons] = useState<readonly { code: string; message: string }[]>([]);
  const rules = usePasswordRules(token ? { invitation: token } : {});
  const [signedIn, setSignedIn] = useState(false);
  useEffect(() => {
    if (!token) { setInvalid(true); return; }
    const controller = new AbortController();
    userManagementApi.inspect(token).then(setInvitation).catch(() => setInvalid(true));
    return () => controller.abort();
  }, [token]);
  useEffect(() => {
    if (!invitation?.accountExists) return;
    void api<Session>("/api/auth/me").then(() => setSignedIn(true)).catch(() => setSignedIn(false));
  }, [invitation]);

  const choosePassword = async (event: FormEvent) => {
    event.preventDefault(); setError(null); setReasons([]);
    if (password !== confirm) { setError("The passwords do not match."); return; }
    setBusy(true);
    try { await userManagementApi.accept(token, password); window.location.assign("/"); }
    catch (reason) {
      if (reason instanceof ApiError && reason.title === "password_policy") { setReasons(reason.reasons); setError("Choose a password that meets every rule."); }
      else if (reason instanceof ApiError && reason.title === "invitation_invalid") setInvalid(true);
      else setError("The invitation could not be accepted. Try again.");
    } finally { setBusy(false); }
  };
  const acceptSignedIn = async () => {
    setBusy(true); setError(null);
    try { await userManagementApi.acceptSignedIn(token); window.location.assign("/tenants"); }
    catch (reason) {
      if (reason instanceof ApiError && reason.status === 401) setNeedsSignIn(true);
      else if (reason instanceof ApiError && reason.title === "invitation_other_account") setError("Sign in with the invited e-mail address before accepting.");
      else if (reason instanceof ApiError && reason.title === "invitation_invalid") setInvalid(true);
      else setError("The invitation could not be accepted. Try again.");
    } finally { setBusy(false); }
  };
  const next = `/accept-invitation#t=${encodeURIComponent(token)}`;

  return <BareFrame><div className="auth"><section className="card" aria-labelledby="accept-heading"><div className="card-body">
    <h1 id="accept-heading">{invalid ? "This invitation is no longer available" : invitation ? `Join ${invitation.tenantName}` : "Checking invitation…"}</h1>
    {invalid && <><p className="lead">The link is invalid, expired, already used or revoked.</p><Link className="btn primary block" to="/sign-in">Go to sign in</Link></>}
    {invitation && !invalid && <>
      <p className="lead">Invited as {invitation.roles.map(roleLabel).join(", ")} · {invitation.email}</p>
      {invitation.accountExists ? <div className="stack" style={{ marginTop: 24 }}><p>This address already has an account. Sign in with it, then accept the new company membership.</p>{needsSignIn || !signedIn ? <Link className="btn primary block" to={`/sign-in?next=${encodeURIComponent(next)}`}>Sign in to accept</Link> : <button type="button" className="btn primary block" disabled={busy} onClick={() => void acceptSignedIn()}>{busy ? "Accepting…" : "Accept invitation"}</button>}</div> :
        <form className="stack" style={{ marginTop: 24 }} onSubmit={choosePassword}>
          <div className="field"><label htmlFor="new-password">Choose a password</label><input id="new-password" className="input" type="password" autoComplete="new-password" maxLength={128} required value={password} onChange={(event) => setPassword(event.target.value)} /></div>
          <div className="field"><label htmlFor="confirm-password">Confirm password</label><input id="confirm-password" className="input" type="password" autoComplete="new-password" required value={confirm} onChange={(event) => setConfirm(event.target.value)} /></div>
          <PasswordRules password={password} confirmation={confirm} rules={rules} />
          {reasons.length > 0 && <ul className="note bad" aria-label="Password policy reasons">{reasons.map((reason) => <li key={reason.code}>{reason.message}</li>)}</ul>}
          <button type="submit" className="btn primary block" disabled={busy || [...password].length < rules.minimumLength || password !== confirm}>{busy ? "Setting up…" : "Set up my account"}</button>
        </form>}
      {error && <div className="form-error" role="alert">{error}</div>}
    </>}
  </div></section></div></BareFrame>;
}

export { tokenFromHash };
