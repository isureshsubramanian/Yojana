import { useState, type KeyboardEvent } from "react";
import { useNavigate, useRevalidator } from "react-router";
import { api } from "../api/client";
import { initials, roleLabel, useSession } from "../session/session";
import { BareFrame } from "../shell/Frame";

export function Component() {
  const session = useSession();
  const navigate = useNavigate();
  const revalidator = useRevalidator();
  const [selected, setSelected] = useState(() => Math.max(0, session.tenants.findIndex((t) => t.code === session.tenant?.code)));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const open = async (code: string) => {
    setBusy(true);
    setError(null);
    try {
      await api("/api/auth/select-tenant", { method: "POST", body: { tenantCode: code } });
      await revalidator.revalidate();
      navigate("/", { replace: true });
    } catch {
      setError("That tenant could not be opened. Your membership may have changed; sign in again if this persists.");
      setBusy(false);
    }
  };

  const onKey = (e: KeyboardEvent) => {
    if (e.key === "ArrowDown") { e.preventDefault(); setSelected((i) => Math.min(session.tenants.length - 1, i + 1)); }
    if (e.key === "ArrowUp") { e.preventDefault(); setSelected((i) => Math.max(0, i - 1)); }
    if (e.key === "Enter") { const t = session.tenants[selected]; if (t) void open(t.code); }
  };

  const current = session.tenants[selected];
  return (
    <BareFrame>
      <div className="auth" style={{ maxWidth: 600 }}>
        <section className="card" aria-labelledby="h-tenant">
          <div className="card-body" style={{ paddingBottom: 20 }}>
            <h1 id="h-tenant">Choose a tenant</h1>
            <p className="lead">Everything you see and change is scoped to the tenant you pick. You can switch later from the header.</p>
          </div>
          {session.tenants.length === 0 ? (
            <div className="empty"><h2>No tenant membership</h2><p className="hint">Your account exists but belongs to no tenant. Ask a tenant administrator to add you.</p></div>
          ) : (
            <ul className="tenant-list" role="listbox" aria-label="Tenants" aria-activedescendant={current ? `tenant-${current.code}` : undefined} tabIndex={0} onKeyDown={onKey}>
              {session.tenants.map((t, i) => (
                <li key={t.code} id={`tenant-${t.code}`} role="option" aria-selected={i === selected} className="link" onClick={() => { setSelected(i); void open(t.code); }}>
                  <span className="glyph" aria-hidden="true">{initials(t.name)}</span>
                  <span>
                    <div className="name">{t.name}</div>
                    <div className="meta">{t.code} · {t.roles.map(roleLabel).join(", ") || "No roles"}</div>
                  </span>
                  {t.code === session.tenant?.code ? <span className="badge info">Current</span> : <span />}
                </li>
              ))}
            </ul>
          )}
          {error && <div className="card-body" style={{ paddingTop: 0 }}><div className="form-error" role="alert">{error}</div></div>}
          <div className="card-footer">
            <span><kbd className="key">↑</kbd> <kbd className="key">↓</kbd> move · <kbd className="key">↵</kbd> open</span>
            {current && <button type="button" className="btn primary sm" aria-busy={busy} onClick={() => open(current.code)}>Open {current.name}</button>}
          </div>
        </section>
      </div>
    </BareFrame>
  );
}
