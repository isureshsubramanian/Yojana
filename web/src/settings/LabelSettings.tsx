import { useEffect, useState } from "react";
import { Link } from "react-router";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { labelsApi } from "../labels/api";
import { pageDimensions, sheetFit, sheetName } from "../labels/sheetModel";
import type { LabelPolicy, LabelPrintOptions, LabelSheetProfile } from "../labels/types";
import "../labels/labels.css";

export function Component() {
  const session = useSession();
  const permitted = session.roles.includes("tenant_administrator");
  const [policy, setPolicy] = useState<LabelPolicy | null>(null);
  const [options, setOptions] = useState<LabelPrintOptions | null>(null);
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(permitted);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    Promise.all([labelsApi.settings(controller.signal), labelsApi.printOptions(controller.signal)])
      .then(([nextPolicy, nextOptions]) => { setPolicy(nextPolicy); setOptions(nextOptions); })
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); })
      .finally(() => setLoading(false));
    return () => controller.abort();
  }, [permitted]);

  const save = async () => {
    if (!policy) return;
    setBusy(true); setSaved(false); setError(null);
    try {
      const next = await labelsApi.saveSettings(policy); setPolicy(next); setSaved(true);
      setOptions(await labelsApi.printOptions());
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };

  if (!permitted) return <main className="content" id="main" tabIndex={-1}><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Labels" }]} /><div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Label settings</h1></div></div><div className="note bad"><strong>Tenant administrator access is required.</strong>&nbsp;These settings affect legal-review gates and the data allowed onto printed labels.</div></main>;

  return <main className="content label-settings" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Labels" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Label settings</h1><p className="sub">Control label review, PDF output and the physical sticker sheets used by this tenant.</p></div><div className="actions"><Link className="btn" to="/labels/print-jobs">Back to labels</Link><button type="button" className="btn primary" disabled={loading || busy || !policy || policy.legalReviewPhrases.every((phrase) => !phrase.trim())} onClick={() => void save()}><Icon name="check" />{busy ? "Saving…" : "Save"}</button></div></div>
    <ErrorNote error={error} />{saved && <div className="note ok label-page-note"><strong>Label settings saved.</strong>&nbsp;New downloads use these tenant settings.</div>}
    {loading && <p className="muted">Loading label settings…</p>}
    {policy && <div className="stack">
      <div className="cols-3-2 top">
        <section className="card" aria-labelledby="legal-phrases"><div className="card-header"><h2 id="legal-phrases">Legal-review phrase list</h2><button type="button" className="btn sm" onClick={() => setPolicy({ ...policy, legalReviewPhrases: [...policy.legalReviewPhrases, ""] })}><Icon name="plus" />Add phrase</button></div><div className="card-body stack">
          <p className="muted">A case-insensitive whole-word or phrase-boundary match in label static text or layout raises the review flag.</p>
          <div className="label-phrase-list">{policy.legalReviewPhrases.map((phrase, index) => <div className="label-phrase-row" key={index}><label className="field"><span className="lbl">Phrase {index + 1}</span><input className="input" value={phrase} maxLength={100} onChange={(event) => setPolicy({ ...policy, legalReviewPhrases: policy.legalReviewPhrases.map((value, row) => row === index ? event.target.value : value) })} /></label><button type="button" className="btn ghost" disabled={policy.legalReviewPhrases.length === 1} onClick={() => setPolicy({ ...policy, legalReviewPhrases: policy.legalReviewPhrases.filter((_, row) => row !== index) })}>Remove</button></div>)}</div>
        </div></section>
        <div className="stack"><section className="card" aria-labelledby="pdf-output"><div className="card-header"><h2 id="pdf-output">Print output</h2></div><div className="card-body stack">
          <label className="field"><span className="lbl">Printer adapter</span><select className="select" value={policy.printerAdapter} onChange={(event) => setPolicy({ ...policy, printerAdapter: event.target.value as LabelPolicy["printerAdapter"] })}><option value="pdf_download">PDF download</option><option value="unavailable">Unavailable</option></select><span className="help">PDF download records that the file was downloaded; it never claims the labels were physically printed.</span></label>
          <label className="field"><span className="lbl">Default format</span><select className="select" value={policy.defaultFormat} onChange={(event) => setPolicy({ ...policy, defaultFormat: event.target.value as LabelPolicy["defaultFormat"] })}><option value="sheet">Sticker sheet</option><option value="one_per_page">One label per page</option></select></label>
        </div></section>
          <section className="card" aria-labelledby="printed-expiry"><div className="card-header"><h2 id="printed-expiry">Printed expiry</h2><span className="meta">default off</span></div><div className="card-body"><label className="check-setting"><input type="checkbox" checked={policy.printExpiryDates} onChange={(event) => setPolicy({ ...policy, printExpiryDates: event.target.checked })} /><span><strong>Allow warranty expiry on labels</strong><span className="help">A printed date can become stale after an amendment, extension or transfer; the stored payload preserves what the sticker said.</span></span></label></div></section>
        </div>
      </div>

      <section className="card" aria-labelledby="sticker-sheet"><div className="card-header"><h2 id="sticker-sheet">Sticker sheet</h2><span className="meta">tenant default · dimensions in millimetres</span></div><div className="card-body label-sheet-settings">
        <SheetEditor profile={policy.defaultSheet} onChange={(defaultSheet) => setPolicy({ ...policy, defaultSheet })} />
      </div></section>

      <section className="card" aria-labelledby="extra-sheets"><div className="card-header"><h2 id="extra-sheets">Extra sheet profiles</h2><button className="btn sm" type="button" onClick={() => setPolicy({ ...policy, extraSheetProfiles: [...policy.extraSheetProfiles, newProfile()] })}><Icon name="plus" />Add profile</button></div><div className="card-body stack">
        <p className="muted">Profile names are generated from dimensions. The built-in profiles stay read-only.</p>
        <div className="label-profile-list">{options?.sheetProfiles.filter((profile) => profile.builtIn).map((profile) => <div className="label-profile-summary" key={profile.profileId}><strong>{profile.name}</strong><span className="help">Built in · read-only · margins {profile.marginTopMm}/{profile.marginRightMm}/{profile.marginBottomMm}/{profile.marginLeftMm} mm</span></div>)}</div>
        {policy.extraSheetProfiles.map((profile, index) => <div className="label-extra-profile" key={profile.profileId}><div className="card-header"><h3>{sheetName(profile)}</h3><button type="button" className="btn ghost sm" onClick={() => setPolicy({ ...policy, extraSheetProfiles: policy.extraSheetProfiles.filter((_, row) => row !== index) })}>Remove</button></div><SheetEditor profile={profile} compact onChange={(next) => setPolicy({ ...policy, extraSheetProfiles: policy.extraSheetProfiles.map((value, row) => row === index ? next : value) })} /></div>)}
        {policy.extraSheetProfiles.length === 0 && <p className="muted">No tenant-added profiles.</p>}
      </div></section>
    </div>}
  </main>;
}

function SheetEditor({ profile, onChange, compact = false }: { profile: LabelSheetProfile; onChange: (profile: LabelSheetProfile) => void; compact?: boolean }) {
  const fit = sheetFit(profile);
  const set = (patch: Partial<LabelSheetProfile>) => onChange({ ...profile, ...patch, name: sheetName({ ...profile, ...patch }) });
  return <div className={compact ? "label-sheet-editor compact" : "label-sheet-editor"}>
    <div className="form-grid label-sheet-fields">
      <label className="field"><span className="lbl">Page size</span><select className="select" value={profile.pageSize} onChange={(event) => set({ pageSize: event.target.value as "A4" | "Letter" })}><option>A4</option><option>Letter</option></select></label>
      <NumberField label="Columns" value={profile.columns} min={1} max={50} unit="" onChange={(columns) => set({ columns })} />
      <NumberField label="Rows" value={profile.rows} min={1} max={50} unit="" onChange={(rows) => set({ rows })} />
      <NumberField label="Label width" value={profile.labelWidthMm} min={10} max={200} onChange={(labelWidthMm) => set({ labelWidthMm })} />
      <NumberField label="Label height" value={profile.labelHeightMm} min={10} max={200} onChange={(labelHeightMm) => set({ labelHeightMm })} />
      <NumberField label="Top margin" value={profile.marginTopMm} min={0} max={200} onChange={(marginTopMm) => set({ marginTopMm })} />
      <NumberField label="Right margin" value={profile.marginRightMm} min={0} max={200} onChange={(marginRightMm) => set({ marginRightMm })} />
      <NumberField label="Bottom margin" value={profile.marginBottomMm} min={0} max={200} onChange={(marginBottomMm) => set({ marginBottomMm })} />
      <NumberField label="Left margin" value={profile.marginLeftMm} min={0} max={200} onChange={(marginLeftMm) => set({ marginLeftMm })} />
      <NumberField label="Horizontal gap" value={profile.horizontalGapMm} min={0} max={200} onChange={(horizontalGapMm) => set({ horizontalGapMm })} />
      <NumberField label="Vertical gap" value={profile.verticalGapMm} min={0} max={200} onChange={(verticalGapMm) => set({ verticalGapMm })} />
    </div>
    <div><SheetPreview profile={profile} /><div className={fit.fits ? "note ok" : "note bad"}>{fit.fits ? <><strong>Grid fits.</strong>&nbsp;The preview keeps the page and stickers in true proportion.</> : <><strong>Grid does not fit.</strong>&nbsp;Overflow: {fit.horizontalOverflowMm ? `${fit.horizontalOverflowMm.toFixed(2)} mm horizontally` : ""}{fit.horizontalOverflowMm && fit.verticalOverflowMm ? " and " : ""}{fit.verticalOverflowMm ? `${fit.verticalOverflowMm.toFixed(2)} mm vertically` : ""}.</>}</div></div>
  </div>;
}

function NumberField({ label, value, min, max, unit = "mm", onChange }: { label: string; value: number; min: number; max: number; unit?: string; onChange: (value: number) => void }) {
  return <label className="field"><span className="lbl">{label}{unit ? ` · ${unit}` : ""}</span><input className="input" type="number" step="0.1" min={min} max={max} value={value} onChange={(event) => onChange(Number(event.target.value))} /></label>;
}

function SheetPreview({ profile }: { profile: LabelSheetProfile }) {
  const page = pageDimensions(profile.pageSize);
  const cells = Array.from({ length: Math.min(profile.columns * profile.rows, 500) });
  return <div className="label-sheet-preview" style={{ aspectRatio: `${page.width} / ${page.height}` }} aria-label={`${profile.columns} by ${profile.rows} sticker sheet preview`}>
    {cells.map((_, index) => { const row = Math.floor(index / profile.columns); const column = index % profile.columns; return <span key={index} style={{ left: `${100 * (profile.marginLeftMm + column * (profile.labelWidthMm + profile.horizontalGapMm)) / page.width}%`, top: `${100 * (profile.marginTopMm + row * (profile.labelHeightMm + profile.verticalGapMm)) / page.height}%`, width: `${100 * profile.labelWidthMm / page.width}%`, height: `${100 * profile.labelHeightMm / page.height}%` }} />; })}
  </div>;
}

function newProfile(): LabelSheetProfile {
  return { profileId: `sheet-${crypto.randomUUID()}`, name: "A4 · 2 × 7 · 99.1 × 38.1 mm", pageSize: "A4", columns: 2, rows: 7, labelWidthMm: 99.1, labelHeightMm: 38.1, marginTopMm: 15.15, marginRightMm: 5.9, marginBottomMm: 15.15, marginLeftMm: 5.9, horizontalGapMm: 0, verticalGapMm: 0, builtIn: false };
}
