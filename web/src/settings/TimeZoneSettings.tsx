import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { usePeople } from "../people/people";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { timeZoneSettingsApi, type TenantTimeZoneSettings } from "./timeZoneSettingsApi";

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  const [settings, setSettings] = useState<TenantTimeZoneSettings | null>(null);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const actors = usePeople([settings?.updatedBy, ...(settings?.history.map((row) => row.updatedBy) ?? [])]);

  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    timeZoneSettingsApi.get(controller.signal).then(setSettings)
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    return () => controller.abort();
  }, [permitted]);

  const zoneIds = useMemo(() => new Set(settings?.timeZones.map((row) => row.id) ?? []), [settings?.timeZones]);
  const offsetFor = (zoneId: string | null) => settings?.timeZones.find((row) => row.id === zoneId)?.offset;
  const invalid = settings !== null && ((settings.tenantTimeZone !== null && !zoneIds.has(settings.tenantTimeZone))
    || settings.legalEntityOverrides.some((row) => !zoneIds.has(row.timeZone)));

  const save = async () => {
    if (!settings) return;
    setBusy(true); setSaved(false); setError(null);
    try { setSettings(await timeZoneSettingsApi.save(settings)); setSaved(true); }
    catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };

  if (!permitted) return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Time zone" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Time zone</h1></div></div>
    <div className="note bad"><strong>Tenant administrator access is required.</strong>&nbsp;This setting controls document time presentation.</div>
  </main>;

  const unusedEntities = settings?.legalEntities.filter((entity) =>
    !settings.legalEntityOverrides.some((row) => row.legalEntityId === entity.legalEntityId)) ?? [];

  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Time zone" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Time zone</h1><p className="sub">UTC remains the storage format. New documents convert date-times to this tenant zone, or to the issuing entity’s override, and snapshot the choice when issued.</p></div><div className="actions"><Link className="btn" to="/settings">Back to settings</Link><button type="button" className="btn primary" disabled={busy || !settings || invalid} aria-busy={busy} onClick={() => void save()}><Icon name="check" />{busy ? "Saving…" : "Save"}</button></div></div>
    <ErrorNote error={error} />
    {saved && <div className="note ok"><strong>Time-zone settings saved.</strong>&nbsp;Already-issued certificates and stored label payloads remain unchanged.</div>}
    {!settings && !error && <p className="muted">Loading time-zone settings…</p>}
    {settings && <div className="stack top">
      {!settings.tenantTimeZone && <div className="note warn"><strong>No tenant time zone set — documents print UTC.</strong>&nbsp;Choose an IANA time zone below for future certificates and time-bearing labels.</div>}
      <datalist id="tenant-time-zones">{settings.timeZones.map((zone) => <option value={zone.id} label={zone.label} key={zone.id} />)}</datalist>
      <div className="cols-3-2 top">
        <section className="card" aria-labelledby="tenant-time-zone"><div className="card-header"><h2 id="tenant-time-zone">Tenant time zone</h2><span className="meta">document default</span></div><div className="card-body stack">
          <label className="field"><span className="lbl">IANA time zone</span><input className="input" list="tenant-time-zones" value={settings.tenantTimeZone ?? ""} placeholder="Search, for example Asia/Kolkata" onChange={(event) => setSettings({ ...settings, tenantTimeZone: event.target.value || null })} /><span className="help">{offsetFor(settings.tenantTimeZone) ? `Current offset ${offsetFor(settings.tenantTimeZone)}. ` : ""}Search results show the current UTC offset. Daylight-saving offsets are resolved again when each document is issued.</span></label>
          {settings.tenantTimeZone && !zoneIds.has(settings.tenantTimeZone) && <div className="note bad">Choose a time zone from the IANA list.</div>}
          {settings.updatedBy && <p className="muted">Last changed by {actors(settings.updatedBy)}{settings.updatedAt ? ` · ${new Date(settings.updatedAt).toLocaleString()}` : ""}</p>}
        </div></section>
        <section className="card" aria-labelledby="time-zone-resolution"><div className="card-header"><h2 id="time-zone-resolution">Resolution order</h2></div><div className="card-body"><ol className="stack"><li>Issuing legal entity override</li><li>Tenant time zone</li><li>UTC when neither is set</li></ol><p className="muted">Warranty calendar maths keeps its existing policy-selected site or obligor zone and does not inherit the tenant document default.</p></div></section>
      </div>
      <section className="card" aria-labelledby="entity-time-zones"><div className="card-header"><div><h2 id="entity-time-zones">Issuing legal entities</h2><span className="meta">optional overrides · names from the party directory</span></div><button type="button" className="btn sm" disabled={unusedEntities.length === 0 || busy} onClick={() => { const entity = unusedEntities[0]; if (entity) setSettings({ ...settings, legalEntityOverrides: [...settings.legalEntityOverrides, { legalEntityId: entity.legalEntityId, timeZone: settings.tenantTimeZone ?? "Etc/UTC" }] }); }}><Icon name="plus" />Add override</button></div><div className="card-body stack">
        {settings.legalEntityOverrides.length === 0 && <p className="muted">No overrides. Every issuing entity uses the tenant document time zone.</p>}
        {settings.legalEntityOverrides.map((row, index) => <div className="form-grid" key={row.legalEntityId}>
          <label className="field"><span className="lbl">Legal entity</span><select className="select" value={row.legalEntityId} onChange={(event) => setSettings({ ...settings, legalEntityOverrides: settings.legalEntityOverrides.map((value, rowIndex) => rowIndex === index ? { ...value, legalEntityId: event.target.value } : value) })}>{settings.legalEntities.filter((entity) => entity.legalEntityId === row.legalEntityId || unusedEntities.some((unused) => unused.legalEntityId === entity.legalEntityId)).map((entity) => <option value={entity.legalEntityId} key={entity.legalEntityId}>{entity.name} · {entity.code}</option>)}</select></label>
          <label className="field"><span className="lbl">IANA time zone</span><input className="input" list="tenant-time-zones" value={row.timeZone} onChange={(event) => setSettings({ ...settings, legalEntityOverrides: settings.legalEntityOverrides.map((value, rowIndex) => rowIndex === index ? { ...value, timeZone: event.target.value } : value) })} />{offsetFor(row.timeZone) && <span className="help">Current offset {offsetFor(row.timeZone)}</span>}</label>
          <button type="button" className="btn ghost" disabled={busy} onClick={() => setSettings({ ...settings, legalEntityOverrides: settings.legalEntityOverrides.filter((_, rowIndex) => rowIndex !== index) })}>Remove</button>
        </div>)}
        {invalid && <div className="note bad">Choose every configured time zone from the IANA list.</div>}
      </div></section>
      {settings.history.length > 0 && <section className="card" aria-labelledby="time-zone-audit"><div className="card-header"><h2 id="time-zone-audit">Audit history</h2><span className="meta">temporal tenant setting</span></div><table className="table dense"><thead><tr><th>Changed by</th><th>Tenant zone</th><th>Overrides</th><th>Changed</th></tr></thead><tbody>{settings.history.map((row) => <tr key={`${row.validFrom}-${row.updatedBy}`}><td className="primary">{actors(row.updatedBy)}</td><td className="mono">{row.tenantTimeZone ?? "UTC fallback"}</td><td>{row.legalEntityOverrides.length}</td><td>{new Date(row.updatedAt).toLocaleString()}</td></tr>)}</tbody></table></section>}
    </div>}
  </main>;
}
