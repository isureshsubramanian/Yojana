import { useEffect, useState } from "react";
import { ErrorNote, Section } from "../units/common";
import { Icon } from "../shell/Icons";
import { downloadLabelFile, labelsApi } from "./api";
import type { LabelPolicy, LabelPrintLayout, LabelPrintOptions, LabelTemplateCatalogueRow } from "./types";

export function PrintLabelDrawer({ serial }: { serial: string }) {
  const [policy, setPolicy] = useState<LabelPolicy | null>(null);
  const [options, setOptions] = useState<LabelPrintOptions | null>(null);
  const [templates, setTemplates] = useState<LabelTemplateCatalogueRow[]>([]);
  const [templateId, setTemplateId] = useState("");
  const [format, setFormat] = useState<LabelPrintLayout["format"]>("sheet");
  const [profileId, setProfileId] = useState("");
  const [startRow, setStartRow] = useState(1);
  const [startColumn, setStartColumn] = useState(1);
  const [copies, setCopies] = useState(1);
  const [busy, setBusy] = useState(false);
  const [downloaded, setDownloaded] = useState(false);
  const [fallback, setFallback] = useState(false);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    const controller = new AbortController();
    Promise.all([labelsApi.settings(controller.signal), labelsApi.printOptions(controller.signal), labelsApi.templates(controller.signal)])
      .then(([nextPolicy, nextOptions, nextTemplates]) => {
        setPolicy(nextPolicy); setOptions(nextOptions); setTemplates(nextTemplates);
        setFormat(nextPolicy.defaultFormat); setProfileId(nextOptions.defaultSheetProfileId);
      }).catch((reason) => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, []);

  if (!policy || !options) return <Section title="Print label"><p className="muted">Loading label choices…</p><ErrorNote error={error} /></Section>;
  const profile = options.sheetProfiles.find((value) => value.profileId === profileId) ?? options.sheetProfiles[0];
  const submit = async () => {
    setBusy(true); setDownloaded(false); setFallback(false); setError(null);
    try {
      const file = await labelsApi.printBatch([serial], templateId || null, {
        format, sheetProfileId: format === "sheet" ? profileId : null, startRow, startColumn, copies,
      });
      downloadLabelFile(file); setFallback(file.htmlFallback); setDownloaded(true);
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };

  return <>
    <Section title="Label"><div className="kv wide"><span className="k">Serial</span><span className="v serial">{serial}</span></div><label className="field"><span className="lbl">Template</span><select className="select" value={templateId} onChange={(event) => setTemplateId(event.target.value)}><option value="">Choose active template by unit</option>{templates.filter((template) => template.activeTemplateVersionId).map((template) => <option key={template.templateId} value={template.activeTemplateVersionId ?? ""}>{template.name} · {template.activeWidthMm ?? "—"} × {template.activeHeightMm ?? "—"} mm (v{template.activeVersion})</option>)}</select></label></Section>
    <Section title="Format"><div className="stack"><div className="segmented plain" role="group" aria-label="Label format"><button type="button" aria-pressed={format === "sheet"} onClick={() => setFormat("sheet")}>Sheet</button><button type="button" aria-pressed={format === "one_per_page"} onClick={() => setFormat("one_per_page")}>One label per page</button></div>
      {format === "sheet" && profile && <><label className="field"><span className="lbl">Sheet profile</span><select className="select" value={profileId} onChange={(event) => { setProfileId(event.target.value); setStartRow(1); setStartColumn(1); }}>{options.sheetProfiles.map((value) => <option key={value.profileId} value={value.profileId}>{value.name}{value.builtIn ? " · built in" : ""}</option>)}</select></label><div className="form-grid"><label className="field"><span className="lbl">Start row</span><input className="input" type="number" min={1} max={profile.rows} value={startRow} onChange={(event) => setStartRow(Number(event.target.value))} /></label><label className="field"><span className="lbl">Start column</span><input className="input" type="number" min={1} max={profile.columns} value={startColumn} onChange={(event) => setStartColumn(Number(event.target.value))} /></label></div></>}
      <label className="field"><span className="lbl">Copies</span><input className="input" type="number" min={1} max={100} value={copies} onChange={(event) => setCopies(Number(event.target.value))} /></label></div></Section>
    <Section title="Download"><div className="note"><strong>Downloaded labels are logged.</strong>&nbsp;Printing them is not confirmed.</div>{downloaded && <div className="note ok"><strong>Downloaded as PDF — printing is not confirmed.</strong></div>}{fallback && <div className="note warn"><strong>PDF worker unavailable in this installation.</strong>&nbsp;An HTML print fallback was downloaded.</div>}<ErrorNote error={error} /><div className="act"><button type="button" className="btn primary block" disabled={busy || copies < 1 || (format === "sheet" && !profile)} onClick={() => void submit()}><Icon name="down" />{busy ? "Preparing…" : "Download PDF"}</button></div></Section>
  </>;
}
