import { useEffect, useState } from "react";
import { Link } from "react-router";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import {
  certificateSettingsApi,
  type ReleaseRecordIssuePoint,
  type ReleaseRecordIssuePointSettings,
} from "./certificateSettingsApi";

const options: ReadonlyArray<{ value: ReleaseRecordIssuePoint; label: string; help: string }> = [
  { value: "at_build_release", label: "At build release", help: "Built units receive version 1 when quality releases the build; bought-in units wait for their first customer shipment." },
  { value: "at_first_shipment", label: "At first customer shipment", help: "Version 1 is created when the unit first enters customer custody, including drop-ship ownership." },
  { value: "on_request", label: "Only on request", help: "No automatic version 1 is created; an authorised certificate issue run creates it." },
];

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  const [settings, setSettings] = useState<ReleaseRecordIssuePointSettings | null>(null);
  const [loading, setLoading] = useState(permitted);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    certificateSettingsApi.get(controller.signal).then(setSettings)
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); })
      .finally(() => setLoading(false));
    return () => controller.abort();
  }, [permitted]);

  const save = async () => {
    if (!settings) return;
    setBusy(true); setSaved(false); setError(null);
    try { setSettings(await certificateSettingsApi.save(settings)); setSaved(true); }
    catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };

  if (!permitted) return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Certificates" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Certificate settings</h1></div></div>
    <div className="note bad"><strong>Tenant administrator access is required.</strong>&nbsp;This setting controls when release records are first issued.</div>
  </main>;

  const invalid = settings?.itemClassOverrides.some((row, index, rows) => !row.itemClass.trim()
    || rows.some((other, otherIndex) => otherIndex !== index && other.itemClass.trim().toLocaleLowerCase() === row.itemClass.trim().toLocaleLowerCase())) ?? false;

  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Certificates" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Certificate settings</h1><p className="sub">Choose when a unit receives its first release record. Changes apply only when a unit reaches a later trigger; existing certificates are never reissued or withdrawn by this edit.</p></div><div className="actions"><Link className="btn" to="/settings">Cancel</Link><button type="button" className="btn primary" disabled={loading || busy || !settings || invalid} aria-busy={busy} onClick={() => void save()}><Icon name="check" />{busy ? "Saving…" : "Save"}</button></div></div>
    <ErrorNote error={error} />
    {saved && <div className="note ok"><strong>Certificate settings saved.</strong>&nbsp;Future issue-point triggers use this configuration.</div>}
    {loading && <p className="muted">Loading certificate settings…</p>}
    {settings && <div className="stack top">
      <section className="card" aria-labelledby="release-record-default"><div className="card-header"><h2 id="release-record-default">Release record issue point</h2><span className="meta">tenant default</span></div><div className="card-body stack">
        {options.map((option) => <label className="radiorow" key={option.value}><input type="radio" name="release-record-default" checked={settings.defaultIssuePoint === option.value} disabled={busy} onChange={() => setSettings({ ...settings, defaultIssuePoint: option.value })} /><span><strong>{option.label}</strong><span className="help">{option.help}</span></span></label>)}
      </div></section>
      <section className="card" aria-labelledby="release-record-overrides"><div className="card-header"><h2 id="release-record-overrides">Item-class overrides</h2><button type="button" className="btn sm" disabled={busy} onClick={() => setSettings({ ...settings, itemClassOverrides: [...settings.itemClassOverrides, { itemClass: "", issuePoint: settings.defaultIssuePoint }] })}><Icon name="plus" />Add override</button></div><div className="card-body stack">
        <p className="muted">An override wins over the tenant default for units in that item class.</p>
        {settings.itemClassOverrides.length === 0 && <p className="muted">No overrides. Every item class uses the tenant default.</p>}
        {settings.itemClassOverrides.map((row, index) => <div className="cluster" key={index}>
          <label className="field"><span className="lbl">Item class</span><input className="input" value={row.itemClass} maxLength={100} onChange={(event) => setSettings({ ...settings, itemClassOverrides: settings.itemClassOverrides.map((value, rowIndex) => rowIndex === index ? { ...value, itemClass: event.target.value } : value) })} /></label>
          <label className="field"><span className="lbl">Issue point</span><select className="select" value={row.issuePoint} onChange={(event) => setSettings({ ...settings, itemClassOverrides: settings.itemClassOverrides.map((value, rowIndex) => rowIndex === index ? { ...value, issuePoint: event.target.value as ReleaseRecordIssuePoint } : value) })}>{options.map((option) => <option value={option.value} key={option.value}>{option.label}</option>)}</select></label>
          <button type="button" className="btn ghost" disabled={busy} onClick={() => setSettings({ ...settings, itemClassOverrides: settings.itemClassOverrides.filter((_, rowIndex) => rowIndex !== index) })}>Remove</button>
        </div>)}
        {invalid && <div className="note bad">Enter a distinct item class for every override.</div>}
      </div></section>
    </div>}
  </main>;
}
