import { useEffect, useState } from "react";
import { Link } from "react-router";
import { ApiError } from "../api/client";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { ItemFamilyField } from "../corrections/ReferenceFields";
import {
  certificateSettingsApi,
  type CertificateDateFormatSettings,
  type ReleaseRecordBomContent,
  type ReleaseRecordIssuePoint,
  type ReleaseRecordIssuePointSettings,
} from "./certificateSettingsApi";
import {
  certificateDatePatterns,
  certificateDateTimeExample,
  certificateLanguageName,
  certificateTimePatterns,
  type CertificateDatePattern,
  type CertificateTimePattern,
} from "./certificateDateFormats";
import { timeZoneSettingsApi, type TenantTimeZoneSettings } from "./timeZoneSettingsApi";

const options: ReadonlyArray<{ value: ReleaseRecordIssuePoint; label: string; help: string }> = [
  { value: "at_build_release", label: "At build release", help: "Built units receive version 1 when quality releases the build; bought-in units wait for their first customer shipment." },
  { value: "at_first_shipment", label: "At first customer shipment", help: "Version 1 is created when the unit first enters customer custody, including drop-ship ownership." },
  { value: "on_request", label: "Only on request", help: "No automatic version 1 is created; an authorised certificate issue run creates it." },
];

const bomOptions: ReadonlyArray<{ value: ReleaseRecordBomContent; label: string; help: string }> = [
  { value: "serialised_only", label: "Serialised components only", help: "Print the traceable serialised components already captured in the as-built record." },
  { value: "full_bom", label: "Full bill of materials", help: "Also snapshot and print the other inventory item lines supplied by the ERP." },
];

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  const [settings, setSettings] = useState<ReleaseRecordIssuePointSettings | null>(null);
  const [dateFormats, setDateFormats] = useState<CertificateDateFormatSettings | null>(null);
  const [timeZone, setTimeZone] = useState<TenantTimeZoneSettings | null>(null);
  const [loading, setLoading] = useState(permitted);
  const [busy, setBusy] = useState(false);
  const [dateBusy, setDateBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [dateSaved, setDateSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    Promise.all([certificateSettingsApi.get(controller.signal), certificateSettingsApi.getDateFormats(controller.signal), timeZoneSettingsApi.get(controller.signal)])
      .then(([issuePoint, formats, zone]) => { setSettings(issuePoint); setDateFormats(formats); setTimeZone(zone); })
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

  const saveDateFormats = async () => {
    if (!dateFormats) return;
    setDateBusy(true); setDateSaved(false); setError(null);
    try { setDateFormats(await certificateSettingsApi.saveDateFormats(dateFormats)); setDateSaved(true); }
    catch (reason) { setError(reason); }
    finally { setDateBusy(false); }
  };

  const changeDateFormat = (language: string, field: "datePattern" | "timePattern",
    value: CertificateDatePattern | CertificateTimePattern) => {
    if (!dateFormats) return;
    setDateSaved(false);
    setDateFormats({ ...dateFormats, languages: dateFormats.languages.map((row) => row.language === language
      ? { ...row, [field]: value } : row) });
  };

  if (!permitted) return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Certificates" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Certificate settings</h1></div></div>
    <div className="note bad"><strong>Tenant administrator access is required.</strong>&nbsp;This setting controls when release records are first issued.</div>
  </main>;

  const invalid = settings?.itemClassOverrides.some((row, index, rows) => !row.itemClass.trim()
    || rows.some((other, otherIndex) => otherIndex !== index && other.itemClass.trim().toLocaleLowerCase() === row.itemClass.trim().toLocaleLowerCase())) ?? false;
  const bomError = error instanceof ApiError && error.rule === "full_bom_needs_erp_bom";

  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Certificates" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Certificate settings</h1><p className="sub">Choose when release records begin and how dates appear on newly issued certificates. Existing certificate presentation snapshots never change.</p></div><div className="actions"><Link className="btn" to="/settings">Back to settings</Link><button type="button" className="btn primary" disabled={loading || busy || !settings || invalid} aria-busy={busy} onClick={() => void save()}><Icon name="check" />{busy ? "Saving…" : "Save release settings"}</button></div></div>
    {!bomError && <ErrorNote error={error} />}
    {timeZone && !timeZone.tenantTimeZone && <div className="note warn"><strong>No tenant time zone set — documents print UTC.</strong>&nbsp;<Link to="/settings/time-zone">Set the tenant time zone</Link> for future certificate versions.</div>}
    {saved && <div className="note ok"><strong>Certificate settings saved.</strong>&nbsp;Future issue-point triggers use this configuration.</div>}
    {loading && <p className="muted">Loading certificate settings…</p>}
    {settings && <div className="stack top">
      <section className="card" aria-labelledby="release-record-default"><div className="card-header"><h2 id="release-record-default">Release record issue point</h2><span className="meta">tenant default</span></div><div className="card-body stack">
        {options.map((option) => <label className="radiorow" key={option.value}><input type="radio" name="release-record-default" checked={settings.defaultIssuePoint === option.value} disabled={busy} onChange={() => setSettings({ ...settings, defaultIssuePoint: option.value })} /><span><strong>{option.label}</strong><span className="help">{option.help}</span></span></label>)}
      </div></section>
      <section className="card" aria-labelledby="release-record-bom"><div className="card-header"><h2 id="release-record-bom">Bill of materials content</h2><span className="meta">tenant default</span></div><div className="card-body stack">
        {bomOptions.map((option) => <label className="radiorow" key={option.value}><input type="radio" name="release-record-bom" checked={settings.defaultBomContent === option.value} disabled={busy} onChange={() => { setError(null); setSettings({ ...settings, defaultBomContent: option.value }); }} /><span><strong>{option.label}</strong><span className="help">{option.help}</span></span></label>)}
        {bomError && <div className="note bad" role="alert">Your ERP connection does not supply bills of materials, so only serialised components can be printed.</div>}
      </div></section>
      <section className="card" aria-labelledby="release-record-overrides"><div className="card-header"><h2 id="release-record-overrides">Item-class overrides</h2><button type="button" className="btn sm" disabled={busy} onClick={() => setSettings({ ...settings, itemClassOverrides: [...settings.itemClassOverrides, { itemClass: "", issuePoint: settings.defaultIssuePoint, bomContent: settings.defaultBomContent }] })}><Icon name="plus" />Add override</button></div><div className="card-body stack">
        <p className="muted">An override wins over the tenant default for units in that item class.</p>
        {settings.itemClassOverrides.length === 0 && <p className="muted">No overrides. Every item class uses the tenant default.</p>}
        {settings.itemClassOverrides.map((row, index) => <div className="settings-override" key={index}>
          <ItemFamilyField id={`certificate-item-class-${index}`} label="Item class" value={row.itemClass || null} onChange={(itemClass) => setSettings({ ...settings, itemClassOverrides: settings.itemClassOverrides.map((value, rowIndex) => rowIndex === index ? { ...value, itemClass: itemClass ?? "" } : value) })} />
          <label className="field"><span className="lbl">Issue point</span><select className="select" value={row.issuePoint} onChange={(event) => setSettings({ ...settings, itemClassOverrides: settings.itemClassOverrides.map((value, rowIndex) => rowIndex === index ? { ...value, issuePoint: event.target.value as ReleaseRecordIssuePoint } : value) })}>{options.map((option) => <option value={option.value} key={option.value}>{option.label}</option>)}</select></label>
          <label className="field"><span className="lbl">BOM content</span><select className="select" value={row.bomContent} onChange={(event) => { setError(null); setSettings({ ...settings, itemClassOverrides: settings.itemClassOverrides.map((value, rowIndex) => rowIndex === index ? { ...value, bomContent: event.target.value as ReleaseRecordBomContent } : value) }); }}>{bomOptions.map((option) => <option value={option.value} key={option.value}>{option.label}</option>)}</select></label>
          <button type="button" className="btn ghost settings-override-remove" disabled={busy} onClick={() => setSettings({ ...settings, itemClassOverrides: settings.itemClassOverrides.filter((_, rowIndex) => rowIndex !== index) })}>Remove</button>
        </div>)}
        {invalid && <div className="note bad">Enter a distinct item class for every override.</div>}
      </div></section>
      {dateFormats && <section className="card" aria-labelledby="certificate-date-time"><div className="card-header"><div><h2 id="certificate-date-time">Date and time</h2><span className="meta">per certificate language · snapshotted at issue</span></div><button type="button" className="btn primary sm" disabled={dateBusy} aria-busy={dateBusy} onClick={() => void saveDateFormats()}><Icon name="check" />{dateBusy ? "Saving…" : "Save date and time"}</button></div><div className="card-body stack">
        <p className="muted">Active certificate layout languages appear here automatically. A saved language remains available after its last layout is retired. These choices apply to future versions only. Date-times use the issuing entity’s time zone and print its abbreviation; UTC is used when no zone is configured.</p>
        {dateSaved && <div className="note ok"><strong>Date and time settings saved.</strong>&nbsp;The next certificate snapshots these patterns.</div>}
        {dateFormats.languages.length === 0 && <p className="muted">No active certificate layouts or saved language formats.</p>}
        {dateFormats.languages.map((row) => <div className="card" key={row.language}>
          <div className="card-header"><h3>{certificateLanguageName(row.language)}</h3><span className="meta mono">{row.language}</span></div>
          <div className="card-body cols-2">
            <fieldset className="stack"><legend className="lbl">Date format</legend>{certificateDatePatterns.map((pattern) => <label className="radiorow" key={pattern}><input type="radio" name={`certificate-date-${row.language}`} checked={row.datePattern === pattern} disabled={dateBusy} onChange={() => changeDateFormat(row.language, "datePattern", pattern)} /><span><strong className="mono">{pattern}</strong><span className="help">{certificateDateTimeExample(row.language, pattern, row.timePattern, timeZone?.tenantTimeZone ?? "UTC")}</span></span></label>)}</fieldset>
            <fieldset className="stack"><legend className="lbl">Time format</legend>{certificateTimePatterns.map((pattern) => <label className="radiorow" key={pattern.value}><input type="radio" name={`certificate-time-${row.language}`} checked={row.timePattern === pattern.value} disabled={dateBusy} onChange={() => changeDateFormat(row.language, "timePattern", pattern.value)} /><span><strong>{pattern.label}</strong><span className="help">{certificateDateTimeExample(row.language, row.datePattern, pattern.value, timeZone?.tenantTimeZone ?? "UTC")}</span></span></label>)}</fieldset>
          </div>
        </div>)}
      </div></section>}
    </div>}
  </main>;
}
