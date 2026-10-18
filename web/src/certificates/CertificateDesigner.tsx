import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { PresenceAdvisory, PresenceChips, useDocumentPresence } from "../components/Presence";
import { StatusBadge } from "../components/StatusBadge";
import { PartyField, UnitField, useReferenceNames } from "../corrections/ReferenceFields";
import { usePeople } from "../people/people";
import { useSession } from "../session/session";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { Rail } from "../shell/Drawer";
import { openDrawer } from "../shell/drawerStore";
import { ErrorNote, Kv, Section } from "../units/common";
import { certificateApi } from "./api";
import { assetPreviewSource, designPreviewHtml, insertPlaceholder } from "./designerCore";
import { fmtDateTime, humanise, statusTone } from "./format";
import type {
  CertificateTemplateDetail, CertificateTemplateSummary, CertificateTemplateVersion, FieldCatalogueEntry,
  TemplateValidation, TenantAsset,
} from "./types";
import "./certificates.css";

type EditTarget = "layout" | "header" | "footer";
interface Draft {
  templateVersionId: string | null; language: string; payloadSchemaVersion: string; layoutBody: string;
  pageSize: string; orientation: string; marginTopMm: number; marginRightMm: number; marginBottomMm: number;
  marginLeftMm: number; headerBody: string; footerBody: string; brandingAssetId: string | null;
  brandingAssets: Record<string, string>;
  staticText: Record<string, string>; fields: string[];
}

export function Component() {
  const { id = "" } = useParams();
  if (id === "new") return <NewCertificateTemplate />;
  return <ExistingCertificateTemplate id={id} />;
}

function ExistingCertificateTemplate({ id }: { id: string }) {
  const navigate = useNavigate();
  const session = useSession();
  const canWrite = session.roles.includes("certificate_administrator");
  const [template, setTemplate] = useState<CertificateTemplateDetail | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [catalogue, setCatalogue] = useState<FieldCatalogueEntry[]>([]);
  const [assets, setAssets] = useState<TenantAsset[]>([]);
  const [validation, setValidation] = useState<TemplateValidation | null>(null);
  const [sampleUnitId, setSampleUnitId] = useState<string | null>(null);
  const [sampleHtml, setSampleHtml] = useState<string | null>(null);
  const [issuerEntityId, setIssuerEntityId] = useState<string | null>(null);
  const [view, setView] = useState<"design" | "html">("design");
  const [target, setTarget] = useState<EditTarget>("layout");
  const [selection, setSelection] = useState({ start: 0, end: 0 });
  const [dirty, setDirty] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const editor = useRef<HTMLTextAreaElement | null>(null);
  const presence = useDocumentPresence("certificate_template", id, dirty);
  const people = usePeople([draftVersion(template)?.approvedBy, draftVersion(template)?.updatedBy]);

  const load = useCallback(() => {
    const controller = new AbortController(); setError(null);
    certificateApi.template(id, controller.signal).then(async (detail) => {
      const [fields, logos] = await Promise.all([certificateApi.fields(detail.documentKind, controller.signal), certificateApi.assets(controller.signal)]);
      setTemplate(detail); setCatalogue(fields); setAssets(logos);
      const version = draftVersion(detail); setDraft(toDraft(version)); setValidation(null); setDirty(false); setSampleHtml(null);
      const assignedIssuer = Object.keys(version?.brandingAssets ?? {}).find((key) => key !== "default")
        ?? logos.find((asset) => asset.legalEntityId)?.legalEntityId ?? null;
      setIssuerEntityId(assignedIssuer);
    }).catch((reason) => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, [id]);
  useEffect(() => load(), [load]);

  // API contract: branding maps use "default" or the issuing legal entity id as their key.
  const brandingKey = issuerEntityId ?? "default";
  const selectedAssetId = draft?.brandingAssets[brandingKey] ?? (brandingKey === "default" ? draft?.brandingAssetId : null);
  const selectedAsset = assets.find((asset) => asset.assetId === selectedAssetId) ?? null;
  const issuerAssignments = Object.entries(draft?.brandingAssets ?? {}).filter(([key]) => key !== "default");
  const issuerNames = useReferenceNames({ parties: issuerAssignments.map(([key]) => key) });
  const current = template ? draftVersion(template) : null;
  const update = (change: Partial<Draft>) => { setDraft((value) => value ? { ...value, ...change } : value); setDirty(true); setValidation(null); setSampleHtml(null); };
  const request = () => draft ? {
    ...draft, headerBody: draft.headerBody || null, footerBody: draft.footerBody || null,
  } : null;
  const save = async () => {
    if (!template || !draft) return; setBusy(true); setError(null);
    try {
      const result = await certificateApi.saveDraft(template.templateId, request());
      setDraft(toDraft(result.version)); setValidation(result.validation); setDirty(false);
      setTemplate(await certificateApi.template(template.templateId));
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const validate = async () => {
    if (!template || !draft) return; setBusy(true); setError(null);
    try { setValidation(await certificateApi.validate(template.templateId, request())); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const activate = async () => {
    if (!template || !draft?.templateVersionId) return; setBusy(true); setError(null);
    try { await certificateApi.activate(template.templateId, draft.templateVersionId); load(); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const retire = async () => {
    if (!template || !draft?.templateVersionId) return; setBusy(true); setError(null);
    try { await certificateApi.retire(template.templateId, draft.templateVersionId); load(); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const preview = async () => {
    if (!template || !draft?.templateVersionId || !sampleUnitId || dirty) return; setBusy(true); setError(null);
    try {
      const result = await certificateApi.preview(template.templateId, draft.templateVersionId, sampleUnitId);
      setValidation(result.validation); setSampleHtml(decodeBase64(result.bytes)); setView("design");
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const copyStarter = async () => {
    if (!template) return; setBusy(true); setError(null);
    try {
      const created = await certificateApi.createTemplate({ code: `${template.code}-copy-${Date.now()}`, name: `${template.name} · custom`, documentKind: template.documentKind, formType: template.formType, appliesToItemClass: template.appliesToItemClass, copyFromTemplateId: template.templateId });
      navigate(`/templates/certificates/${created.templateId}`);
    } catch (reason) { setError(reason); setBusy(false); }
  };
  const upload = async (file: File) => {
    setBusy(true); setError(null);
    try {
      const asset = await certificateApi.uploadAsset(file, issuerEntityId); setAssets((rows) => [asset, ...rows]);
      update({ brandingAssetId: issuerEntityId ? draft?.brandingAssetId ?? null : asset.assetId,
        brandingAssets: { ...(draft?.brandingAssets ?? {}), [brandingKey]: asset.assetId } });
    }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const insert = (field: string) => {
    if (!draft || !canWrite || template?.builtIn) return;
    const value = target === "layout" ? draft.layoutBody : target === "header" ? draft.headerBody : draft.footerBody;
    const result = insertPlaceholder(value, field, selection.start, selection.end);
    update(target === "layout" ? { layoutBody: result.text, fields: [...new Set([...draft.fields, field])] }
      : target === "header" ? { headerBody: result.text, fields: [...new Set([...draft.fields, field])] }
        : { footerBody: result.text, fields: [...new Set([...draft.fields, field])] });
    requestAnimationFrame(() => { editor.current?.focus(); editor.current?.setSelectionRange(result.caret, result.caret); });
  };

  if (!template || !draft || !current) return <main className="content" id="main" tabIndex={-1}><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Templates", to: "/templates" }, { label: "Certificate designer" }]} /><ErrorNote error={error} /><div className="empty"><h2>{error ? "Template unavailable" : "Loading designer…"}</h2></div></main>;
  const readOnly = template.builtIn || !canWrite || draft.templateVersionId !== null && current.status !== "draft";
  const previewDocument = sampleHtml ?? designPreviewHtml(draft.headerBody + draft.layoutBody + draft.footerBody, {
    top: draft.marginTopMm, right: draft.marginRightMm, bottom: draft.marginBottomMm, left: draft.marginLeftMm,
  });
  const checks = validation ? validation.valid ? `Checks pass. ${draft.fields.length} declared fields; all placeholders are in the certificate payload catalogue.`
    : validation.errors.join(" ") : "Run validation before activation. Unknown placeholders and party fields on a release record are refused.";

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Templates", to: "/templates" }, { label: template.name }]} />
      <PageHeader title={template.name} sub={`${humanise(template.documentKind)} · ${template.builtIn ? "built-in starter" : `version ${current.version} ${current.status}`} · active versions are immutable`}
        actions={<><PresenceChips users={presence} />{template.builtIn ? <button type="button" className="btn primary" disabled={!canWrite || busy} onClick={() => void copyStarter()}>Copy to customise</button> : <>
          {current.status !== "draft" && draft.templateVersionId !== null && <button type="button" className="btn" disabled={!canWrite || busy} onClick={() => update({ templateVersionId: null })}>New draft</button>}
          <button type="button" className="btn" disabled={!canWrite || busy || !dirty} onClick={() => void save()}>Save draft</button>
          {current.status === "draft" && <button type="button" className="btn primary" disabled={!canWrite || busy || dirty || validation?.valid !== true} onClick={() => void activate()}>Activate</button>}
          {current.status === "active" && <button type="button" className="btn" disabled={!canWrite || busy} onClick={() => void retire()}>Retire</button>}
        </>}</>} />
      <div className="stack">
        <PresenceAdvisory users={presence} editing={dirty} documentLabel="certificate template" />
        <div className={`note ${validation?.valid ? "ok" : validation ? "bad" : ""}`}><strong>{validation?.valid ? "Checks pass." : validation ? "Checks need attention." : "Not checked."}</strong>&nbsp;{checks}</div>
        <ErrorNote error={error} onReload={load} />
        <div className="certificate-designer-grid">
          <div className="stack">
            <section className="card" aria-labelledby="branding-heading"><div className="card-header"><h2 id="branding-heading">Logo and branding</h2></div><div className="card-body stack certificate-branding-panel">
              <div className="logo-drop">{assetPreviewSource(selectedAsset) ? <img src={assetPreviewSource(selectedAsset) ?? undefined} alt="Current certificate logo" /> : <span className="logo-empty">No logo</span>}<span>{selectedAsset ? <><strong>{selectedAsset.originalFileName ?? `Logo · ${assetFormat(selectedAsset.mediaType)}`}</strong><span className="hint">{formatBytes(selectedAsset.byteLength)} · {selectedAsset.width ?? "?"} × {selectedAsset.height ?? "?"}<br />SHA-256 recorded at issue</span></> : <span className="hint">Tenant default branding</span>}</span></div>
              <PartyField id="certificate-issuer" label="Logo for issuing legal entity" value={issuerEntityId} onChange={setIssuerEntityId} optional hint="Leave empty for a tenant-wide logo" />
              {issuerAssignments.length > 0 && <div className="certificate-issuer-assignments"><span className="lbl">Per-issuer assignments</span>{issuerAssignments.map(([legalEntityId, assetId]) => <button type="button" className="field-insert" key={legalEntityId} onClick={() => setIssuerEntityId(legalEntityId)}><span><span className="t">{issuerNames.party(legalEntityId)}</span><span className="p">{assets.find((asset) => asset.assetId === assetId)?.mediaType ?? "Logo asset"}</span></span><StatusBadge tone={issuerEntityId === legalEntityId ? "ok" : "neutral"}>{issuerEntityId === legalEntityId ? "Selected" : "View"}</StatusBadge></button>)}</div>}
              <label className="field"><span className="lbl">Upload replacement</span><input type="file" accept="image/png,image/jpeg,image/svg+xml" disabled={readOnly || busy} onChange={(event) => { const file = event.target.files?.[0]; if (file) void upload(file); }} /><span className="help">PNG, JPEG or SVG · maximum 1 MB and 4096 × 4096</span></label>
              {selectedAsset && <button type="button" className="btn sm" disabled={readOnly} onClick={() => { const next = { ...draft.brandingAssets }; delete next[brandingKey]; update({ brandingAssetId: brandingKey === "default" ? null : draft.brandingAssetId, brandingAssets: next }); }}>Remove for this issuer</button>}
              <label className="field"><span className="lbl">Issuer display name</span><input className="input" value={draft.staticText.issuer_display_name ?? ""} disabled={readOnly} onChange={(event) => update({ staticText: { ...draft.staticText, issuer_display_name: event.target.value } })} /></label>
              <label className="field"><span className="lbl">Issuer address</span><textarea className="textarea" rows={2} value={draft.staticText.issuer_address ?? ""} disabled={readOnly} onChange={(event) => update({ staticText: { ...draft.staticText, issuer_address: event.target.value } })} /></label>
              <label className="field"><span className="lbl">Issuer registration code</span><input className="input" value={draft.staticText.issuer_registration_code ?? ""} disabled={readOnly} onChange={(event) => update({ staticText: { ...draft.staticText, issuer_registration_code: event.target.value } })} /></label>
              <label className="field"><span className="lbl">Accent colour token</span><input className="input" value={draft.staticText.accent_colour ?? ""} disabled={readOnly} onChange={(event) => update({ staticText: { ...draft.staticText, accent_colour: event.target.value } })} placeholder="Tenant design token or print colour" /></label>
              <p className="hint">Assets are append-only. The certificate snapshots this asset id and SHA-256 when issued.</p>
            </div></section>
            <section className="card" aria-labelledby="field-catalogue-heading"><div className="card-header"><h2 id="field-catalogue-heading">Fields</h2><span className="meta">insert into {target}</span></div><div className="card-body certificate-fields">
              {groups(catalogue).map(([group, fields]) => <div key={group}><h3>{humanise(group)}</h3>{fields.map((field) => <button type="button" className="field-insert" key={field.path} disabled={readOnly} onClick={() => { insert(field.path); openDrawer({ eyebrow: "Certificate payload field", title: field.path, mono: true }, <Section title="Field"><Kv rows={[["Type", field.type], ["Repeats", field.repeating ? "yes" : "no"], ["If empty", "wrap in an if or unless block"]]} /></Section>); }}><span><span className="t">{humanise(field.path.split(".").at(-1))}</span><span className="p">{field.path}</span></span><StatusBadge tone={draft.fields.includes(field.path) ? "ok" : "neutral"}>{draft.fields.includes(field.path) ? "Used" : "Add"}</StatusBadge></button>)}</div>)}
            </div></section>
          </div>
          <section className="card certificate-layout-card" aria-labelledby="layout-heading"><div className="card-header"><h2 id="layout-heading">Layout</h2><div className="segmented" role="group" aria-label="Layout view"><button type="button" aria-pressed={view === "design"} onClick={() => setView("design")}>Design</button><button type="button" aria-pressed={view === "html"} onClick={() => setView("html")}>HTML</button></div></div><div className="card-body stack">
            <div className="certificate-layout-settings">
              <label className="field"><span className="lbl">Page</span><select className="select" value={draft.pageSize} disabled={readOnly} onChange={(event) => update({ pageSize: event.target.value })}><option>A4</option><option>Letter</option><option>A5</option></select></label>
              <label className="field"><span className="lbl">Orientation</span><select className="select" value={draft.orientation} disabled={readOnly} onChange={(event) => update({ orientation: event.target.value })}><option value="portrait">Portrait</option><option value="landscape">Landscape</option></select></label>
              <label className="field"><span className="lbl">Language</span><input className="input" value={draft.language} disabled={readOnly} onChange={(event) => update({ language: event.target.value })} /></label>
              <label className="field"><span className="lbl">Applies to</span><input className="input" value={template.appliesToItemClass ?? "All items"} readOnly /></label>
            </div>
            <div className="certificate-margin-grid">{(["Top", "Right", "Bottom", "Left"] as const).map((side) => { const key = `margin${side}Mm` as keyof Draft; return <label className="field" key={side}><span className="lbl">{side} margin · mm</span><input className="input" type="number" min="0" max="100" step="0.5" value={draft[key] as number} disabled={readOnly} onChange={(event) => update({ [key]: Number(event.target.value) })} /></label>; })}</div>
            <label className="field"><span className="lbl">Static text · {draft.language}</span><textarea className="textarea" value={draft.staticText[draft.language] ?? ""} disabled={readOnly} onChange={(event) => update({ staticText: { ...draft.staticText, [draft.language]: event.target.value } })} placeholder="Language-specific wording stored with this template version" /></label>
            {view === "design" ? <iframe className="certificate-design-frame" sandbox="" title="Certificate layout design preview" srcDoc={previewDocument} /> : <>
              <div className="segmented certificate-editor-target" role="group" aria-label="HTML section">{(["layout", "header", "footer"] as EditTarget[]).map((value) => <button type="button" key={value} aria-pressed={target === value} onClick={() => { setTarget(value); setSelection({ start: 0, end: 0 }); }}>{humanise(value)}</button>)}</div>
              <textarea ref={editor} className="textarea certificate-html-editor" spellCheck={false} disabled={readOnly} value={target === "layout" ? draft.layoutBody : target === "header" ? draft.headerBody : draft.footerBody}
                onSelect={(event) => setSelection({ start: event.currentTarget.selectionStart, end: event.currentTarget.selectionEnd })}
                onChange={(event) => update(target === "layout" ? { layoutBody: event.target.value } : target === "header" ? { headerBody: event.target.value } : { footerBody: event.target.value })} />
            </>}
            <div className="certificate-preview-controls"><UnitField id="certificate-sample-unit" label="Sample unit" value={sampleUnitId} onChange={setSampleUnitId} hint="Search by serial or another identifier" /><button type="button" className="btn" disabled={!sampleUnitId || !draft.templateVersionId || dirty || busy} onClick={() => void preview()}>Preview saved draft</button><button type="button" className="btn" disabled={readOnly || busy} onClick={() => void validate()}>Validate</button></div>
          </div></section>
        </div>
      </div>
    </main>
    <Rail label="Template"><Section title="Version"><Kv rows={[["Version", `v${current.version}`], ["State", humanise(current.status)], ["Effective", current.effectiveFrom ? fmtDateTime(current.effectiveFrom) : "not active"], ["Approved by", current.approvedBy ? people(current.approvedBy) : "not approved"], ["Last changed by", people(current.updatedBy)]]} /></Section><Section title="Editing boundary"><p className="muted">Only fixed certificate-payload fields may be inserted. Release-record layouts cannot bind an owner, customer, party or address field.</p></Section></Rail>
  </>;
}

function NewCertificateTemplate() {
  const navigate = useNavigate(); const session = useSession(); const canWrite = session.roles.includes("certificate_administrator");
  const [starters, setStarters] = useState<CertificateTemplateSummary[]>([]); const [name, setName] = useState("");
  const [code, setCode] = useState(""); const [kind, setKind] = useState("release_record"); const [formType, setFormType] = useState("generic_release");
  const [scope, setScope] = useState(""); const [source, setSource] = useState(""); const [error, setError] = useState<unknown>(null); const [busy, setBusy] = useState(false);
  useEffect(() => { const controller = new AbortController(); certificateApi.templates(controller.signal).then(setStarters).catch(setError); return () => controller.abort(); }, []);
  const candidates = starters.filter((row) => row.builtIn && row.documentKind === kind);
  const create = async () => { setBusy(true); setError(null); try { const row = await certificateApi.createTemplate({ code: code.trim(), name: name.trim(), documentKind: kind, formType, appliesToItemClass: scope.trim() || null, copyFromTemplateId: source || null }); navigate(`/templates/certificates/${row.templateId}`); } catch (reason) { setError(reason); setBusy(false); } };
  return <main className="content" id="main" tabIndex={-1}><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Templates", to: "/templates" }, { label: "New certificate template" }]} /><PageHeader title="New certificate template" sub="Start blank or copy one of the built-in layouts; the new template begins as draft." /><div className="cols-3-2"><section className="card"><div className="card-header"><h2>Template identity</h2></div><div className="card-body form-grid">
    <label className="field"><span className="lbl">Name</span><input className="input" value={name} onChange={(event) => { setName(event.target.value); if (!code) setCode(slug(event.target.value)); }} /></label><label className="field"><span className="lbl">Code</span><input className="input" value={code} onChange={(event) => setCode(event.target.value)} /></label>
    <label className="field"><span className="lbl">Document</span><select className="select" value={kind} onChange={(event) => { const next = event.target.value; setKind(next); setFormType(next === "warranty_certificate" ? "warranty_certificate" : "generic_release"); setSource(""); }}><option value="release_record">Release record</option><option value="warranty_certificate">Warranty certificate</option></select></label><label className="field"><span className="lbl">Form</span><select className="select" value={formType} onChange={(event) => setFormType(event.target.value)}>{kind === "warranty_certificate" ? <option value="warranty_certificate">Warranty certificate</option> : <><option value="generic_release">Generic release</option><option value="coc">Certificate of conformity</option><option value="dhr_extract">Production-history extract</option><option value="release_8130_3_style">8130-3-style release</option><option value="easa_form_1_style">EASA Form 1-style release</option><option value="battery_passport">Battery passport</option><option value="custom">Custom</option></>}</select></label>
    <label className="field"><span className="lbl">Applies to item class</span><input className="input" value={scope} onChange={(event) => setScope(event.target.value)} placeholder="All items when empty" /></label><label className="field"><span className="lbl">Starting layout</span><select className="select" value={source} onChange={(event) => setSource(event.target.value)}><option value="">Blank</option>{candidates.map((row) => <option key={row.templateId} value={row.templateId}>{row.name}</option>)}</select></label>
    <div className="span"><ErrorNote error={error} /></div><div className="actions span"><Link className="btn" to="/templates">Cancel</Link><button type="button" className="btn primary" disabled={!canWrite || !name.trim() || !code.trim() || busy} onClick={() => void create()}>Create draft</button></div>
  </div></section><section className="card"><div className="card-header"><h2>Immutable boundary</h2></div><div className="card-body"><p className="muted">The template owns presentation only. Placeholders are restricted to fields already present in the signed certificate payload, and issued records keep their original template version and logo hash.</p></div></section></div></main>;
}

function draftVersion(template: CertificateTemplateDetail | null): CertificateTemplateVersion | null {
  if (!template?.versions.length) return null;
  return template.versions.find((version) => version.status === "draft") ?? template.versions[0] ?? null;
}
function toDraft(version: CertificateTemplateVersion | null): Draft | null { return version ? {
  templateVersionId: version.templateVersionId, language: version.language, payloadSchemaVersion: version.payloadSchemaVersion,
  layoutBody: version.layoutBody, pageSize: version.pageSize, orientation: version.orientation, marginTopMm: version.marginTopMm,
  marginRightMm: version.marginRightMm, marginBottomMm: version.marginBottomMm, marginLeftMm: version.marginLeftMm,
  headerBody: version.headerBody ?? "", footerBody: version.footerBody ?? "", brandingAssetId: version.brandingAssetId,
  brandingAssets: { ...version.brandingAssets },
  staticText: { ...version.staticText }, fields: [...version.fields],
} : null; }
function groups(fields: FieldCatalogueEntry[]): [string, FieldCatalogueEntry[]][] {
  const grouped = new Map<string, FieldCatalogueEntry[]>();
  for (const field of fields) { const group = field.path.split(".")[0] ?? "other"; grouped.set(group, [...(grouped.get(group) ?? []), field]); }
  return [...grouped.entries()];
}
function decodeBase64(value: string): string { const binary = atob(value); const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0)); return new TextDecoder().decode(bytes); }
function formatBytes(value: number): string { return value < 1024 ? `${value} B` : `${Math.ceil(value / 1024)} KB`; }
function assetFormat(mediaType: string): string { return mediaType === "image/svg+xml" ? "SVG" : mediaType === "image/jpeg" ? "JPEG" : mediaType === "image/png" ? "PNG" : "image"; }
function slug(value: string): string { return value.toLocaleLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 80); }
