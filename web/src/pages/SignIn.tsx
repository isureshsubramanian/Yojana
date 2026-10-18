import { useState, type FormEvent } from "react";
import { useNavigate, useSearchParams } from "react-router";
import { api, ApiError } from "../api/client";
import type { Session } from "../api/types";
import { Shortcut } from "../components/Shortcut";
import { BareFrame } from "../shell/Frame";
import { Icon } from "../shell/Icons";

export function Component() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const session = await api<Session>("/api/auth/sign-in", { method: "POST", body: { email, password } });
      const next = params.get("next");
      navigate(session.tenant ? (next && next.startsWith("/") ? next : "/") : "/tenants", { replace: true });
    } catch (err) {
      setError(err instanceof ApiError && err.status === 401 ? "That email and password do not match an active account." : "Sign-in is unavailable right now. Try again in a moment.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <BareFrame>
      <div className="auth">
        <section className="card" aria-labelledby="h-signin">
          <div className="card-body">
            <h1 id="h-signin">Sign in</h1>
            <p className="lead">Use your work account. Your tenant and roles are applied after sign-in.</p>
            <form style={{ display: "grid", gap: 16, marginTop: 24 }} onSubmit={submit} noValidate>
              <div className="field">
                <label htmlFor="s-email">Email</label>
                <input id="s-email" className="input" type="email" autoComplete="username" required autoFocus value={email} onChange={(e) => setEmail(e.target.value)} />
              </div>
              <div className="field">
                <label htmlFor="s-pass">Password</label>
                <input id="s-pass" className="input" type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
              </div>
              {error && <div className="form-error" role="alert">{error}</div>}
              <button type="submit" className="btn primary block" aria-busy={busy}>Sign in<Shortcut keys="enter" /></button>
              <div className="divider">or</div>
              <button type="button" className="btn block" disabled title="Single sign-on is configured per tenant">Continue with single sign-on</button>
              <div className="scanbox">
                <Icon name="scan" />
                <input className="input" type="password" aria-label="Scan operator badge" placeholder="Scan operator badge at a station" disabled />
              </div>
            </form>
          </div>
          <div className="card-footer" style={{ justifyContent: "center", gap: 20 }}>
            <a href="/notices" className="muted" target="_blank" rel="noopener">Third-party notices</a>
            <a href="/health" className="muted" target="_blank" rel="noopener">Status</a>
          </div>
        </section>
        <p className="hint" style={{ textAlign: "center", marginTop: 16 }}>Yojana 0.1 · session cookie only · no tracking</p>
      </div>
    </BareFrame>
  );
}
