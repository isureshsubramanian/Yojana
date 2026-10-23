import { useEffect, useState } from "react";
import { Link } from "react-router";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { importFileSettingsApi, type ImportFileStorageSettings } from "./importFileSettingsApi";

const AGE_UNITS: [ImportFileStorageSettings["archiveAgeUnit"], string][] = [["days", "Days"], ["months", "Months"], ["years", "Years"]];

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  const [settings, setSettings] = useState<ImportFileStorageSettings | null>(null);
  const [busy, setBusy] = useState(false); const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    importFileSettingsApi.get(controller.signal).then(setSettings)
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    return () => controller.abort();
  }, [permitted]);
  const save = async () => {
    if (!settings) return; setBusy(true); setSaved(false); setError(null);
    try { setSettings(await importFileSettingsApi.save(settings)); setSaved(true); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  if (!permitted) return <main className="content" id="main" tabIndex={-1}><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Import files" }]} /><div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Import files</h1></div></div><div className="note bad"><strong>Tenant administrator access is required.</strong></div></main>;
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Import files" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Import files</h1><p className="sub">Choose where new uploads are kept and when older files move to the archive.</p></div><div className="actions"><Link className="btn" to="/settings">Back to settings</Link><button type="button" className="btn primary" disabled={busy || !settings || settings.archiveAge < 0 || !settings.archiveStore} onClick={() => void save()}><Icon name="check" />{busy ? "Saving…" : "Save"}</button></div></div>
    <ErrorNote error={error} />{saved && <div className="note ok"><strong>Import-file settings saved.</strong>&nbsp;Existing archived files stay in their named archive store.</div>}
    {!settings && !error && <p className="muted">Loading import-file settings…</p>}
    {settings && <div className="cols-3-2 top">
      <section className="card" aria-labelledby="active-import-storage"><div className="card-header"><h2 id="active-import-storage">Active storage</h2></div><div className="card-body stack"><p className="muted">This choice applies to new uploads. Changing it does not move existing active files.</p><div className="cluster">
        <label className="radiorow"><input type="radio" name="import-storage" checked={settings.storage === "database"} onChange={() => setSettings({ ...settings, storage: "database" })} /><span><strong>Database</strong><span className="help">New uploads are kept in the separate files database.</span></span></label>
        <label className="radiorow"><input type="radio" name="import-storage" checked={settings.storage === "file"} onChange={() => setSettings({ ...settings, storage: "file" })} /><span><strong>Folder</strong><span className="help">New uploads are kept in the files folder set up for this installation.</span></span></label>
      </div></div></section>
      <section className="card" aria-labelledby="archive-import-storage"><div className="card-header"><h2 id="archive-import-storage">Archive</h2></div><div className="card-body stack">
        <label className="field"><span className="lbl">Archive to</span><select className="select" value={settings.archiveStore} onChange={(event) => setSettings({ ...settings, archiveStore: event.target.value })}>{settings.archiveStores.map((store) => <option value={store.name} key={store.name}>{store.name} · {store.kind === "folder" ? "Folder" : "Database"}</option>)}</select><span className="help">Files already archived stay where they are when you change this.</span></label>
        <div className="field"><label className="lbl" htmlFor="archive-age">Archive after</label><span className="input-unit"><input id="archive-age" className="input num" type="number" min="0" max="36500" value={settings.archiveAge} onChange={(event) => setSettings({ ...settings, archiveAge: Number(event.target.value) })} /><span className="segmented" role="radiogroup" aria-label="Archive age unit">{AGE_UNITS.map(([unit, label]) => <button type="button" key={unit} role="radio" aria-checked={settings.archiveAgeUnit === unit} aria-pressed={settings.archiveAgeUnit === unit} onClick={() => setSettings({ ...settings, archiveAgeUnit: unit })}>{label}</button>)}</span></span></div>
        {settings.archiveAge === 0 && <div className="note warn"><strong>Automatic archiving is off.</strong>&nbsp;Active storage will continue to grow until an age is set.</div>}
      </div></section>
    </div>}
  </main>;
}
