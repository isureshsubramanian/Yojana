import { useEffect, useState } from "react";
import { Link } from "react-router";
import { ApiError } from "../api/client";
import { passwordApi, type PasswordSettings } from "../security/api";
import { useSession } from "../session/session";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  const [settings, setSettings] = useState<PasswordSettings | null>(null);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    passwordApi.settings(controller.signal).then(setSettings).catch((reason) => {
      if (!(reason instanceof DOMException && reason.name === "AbortError")) setError("Sign-in policy could not be loaded.");
    });
    return () => controller.abort();
  }, [permitted]);
  const save = async () => {
    if (!settings) return; setBusy(true); setSaved(false); setError(null);
    try { setSettings(await passwordApi.saveSettings(settings)); setSaved(true); }
    catch (reason) { setError(reason instanceof ApiError && reason.status === 409 ? "Someone else changed this policy. Reload before saving." : reason instanceof ApiError ? reason.detail : "Sign-in policy could not be saved."); }
    finally { setBusy(false); }
  };
  const linkInvalid = settings !== null && (settings.resetLinkMinutes < 10 || settings.resetLinkMinutes > 1440);
  const daysInvalid = settings !== null && (!Number.isInteger(settings.invitationValidDays) || settings.invitationValidDays < 1 || settings.invitationValidDays > 30);
  const invalid = linkInvalid || daysInvalid;
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Sign-in policy" }]} />
    <PageHeader eyebrow="Settings · tenant administrator" title="Sign-in policy" sub="Password recovery and invitation rules for people in this company. Every change is recorded in Security events." actions={<><Link className="btn" to="/settings">Back to settings</Link><button className="btn primary" type="button" disabled={busy || !settings || invalid} onClick={() => void save()}>{busy ? "Saving…" : "Save"}</button></>} />
    {!permitted && <div className="note bad"><strong>Tenant administrator access is required.</strong></div>}
    {error && <div className="note bad" role="alert">{error}</div>}
    {saved && <div className="note ok"><strong>Sign-in policy saved.</strong></div>}
    {permitted && !settings && !error && <p className="muted">Loading sign-in policy…</p>}
    {settings && <div className="stack top" style={{ maxWidth: 900 }}>
      <section className="card" aria-labelledby="password-policy"><div className="card-header"><h2 id="password-policy">Passwords</h2></div><div className="card-body"><div className="form-grid">
        <label className="field"><span className="lbl">Shortest password</span><input className="input" value="12 characters" readOnly /><span className="help">Longer beats complicated; no forced symbols or expiry.</span></label>
        <label className="field"><span className="lbl">Reset link works for</span><span className="input-unit"><input className="input num" type="number" min={10} max={1440} value={settings.resetLinkMinutes} onChange={(event) => setSettings({ ...settings, resetLinkMinutes: Number(event.target.value) })} /><span className="u">minutes</span></span><span className="help">10 to 1,440 minutes. Single use; a new link cancels the old one.</span></label>
        <label className="field"><span className="lbl">Invitations work for</span><span className="input-unit"><input className="input num" type="number" min={1} max={30} value={settings.invitationValidDays} onChange={(event) => setSettings({ ...settings, invitationValidDays: Number(event.target.value) })} /><span className="u">days</span></span><span className="help">1 to 30 days; resending starts the period again.</span></label>
      </div>{linkInvalid && <div className="note bad">Choose from 10 to 1,440 minutes.</div>}{daysInvalid && <div className="note bad">Choose from 1 to 30 days.</div>}</div></section>
      <section className="card" aria-labelledby="temporary-password-policy"><div className="card-header"><h2 id="temporary-password-policy">Temporary passwords</h2></div><div className="card-body">
        <label className="checkrow"><input type="checkbox" checked={settings.adminTemporaryPasswordAllowed} onChange={(event) => setSettings({ ...settings, adminTemporaryPasswordAllowed: event.target.checked })} /><span>Administrators may set a temporary password<span className="hint" style={{ display: "block" }}>The person must choose a new password at next sign-in. Off is safer: the reset link goes only to the person.</span></span></label>
      </div></section>
    </div>}
  </main>;
}
