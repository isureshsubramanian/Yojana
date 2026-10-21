import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { Link, useParams } from "react-router";
import { ApiError } from "../api/client";
import { StatusBadge } from "../components/StatusBadge";
import { useTenantTime } from "../components/tenantTime";
import { useSession } from "../session/session";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { mailApi, type MailBlock, type MailField, type MailLanguageContent, type MailPreviewView, type MailTemplateCheck, type MailTemplateContent, type MailTemplateDetail, type MailTemplateVersionView } from "./api";
import {
  BLOCK_TYPES, DESIGNER_ROLES, LANGUAGE_CHOICES, addLanguage, blockLabel, fieldGroups, fieldsMentioned, holds, insertField, itemToken,
  languageName, listOf, moveBlock, newBlock, pieces, readText, versionTone, writeText, type TextKey,
} from "./format";
import "./mail.css";

type PreviewMode = "design" | "text" | "phone";
interface Editing { versionId: string | null; expectedVersion: string | null; status: string; version: number }

export function Component() {
  const { id = "" } = useParams();
  const session = useSession();
  const tenantTime = useTenantTime();
  const designer = holds(session.roles, DESIGNER_ROLES);
  const [template, setTemplate] = useState<MailTemplateDetail | null>(null);
  const [editing, setEditing] = useState<Editing | null>(null);
  const [content, setContent] = useState<MailTemplateContent | null>(null);
  const [language, setLanguage] = useState("en");
  const [selected, setSelected] = useState<number | null>(null);
  const [focus, setFocus] = useState<{ key: TextKey; start: number; end: number } | null>(null);
  const [sample, setSample] = useState<string | null>(null);
  const [mode, setMode] = useState<PreviewMode>("design");
  const [preview, setPreview] = useState<MailPreviewView | null>(null);
  const [check, setCheck] = useState<MailTemplateCheck | null>(null);
  const [dirty, setDirty] = useState(false);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const boxes = useRef(new Map<TextKey, HTMLInputElement | HTMLTextAreaElement>());

  const show = useCallback((detail: MailTemplateDetail) => {
    const version = detail.versions.find((x) => x.status === "draft") ?? detail.versions.find((x) => x.status === "published") ?? detail.versions[0];
    setTemplate(detail);
    if (!version) return;
    setEditing({ versionId: version.versionId, expectedVersion: version.expectedVersion, status: version.status, version: version.version });
    setContent(version.content);
    setLanguage((current) => version.content.languages.some((x) => x.language === current) ? current : version.content.languages[0]?.language ?? "en");
    setSample((current) => current ?? detail.sampleRecords[0]?.recordId ?? null);
    setDirty(false);
  }, []);
  const load = useCallback(() => {
    const controller = new AbortController();
    mailApi.template(id, controller.signal).then(show).catch((reason) => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, [id, show]);
  useEffect(() => load(), [load]);

  // The server renders the preview and runs the checks on every change, a moment after typing stops.
  const sequence = useRef(0);
  useEffect(() => {
    if (!template || !content) return;
    const ticket = ++sequence.current;
    const timer = window.setTimeout(() => {
      mailApi.preview(template.templateId, sample, language, content, content.askForNote ? "As requested, here is the cover for your unit." : null)
        .then((result) => { if (ticket === sequence.current) { setPreview(result); setCheck(result.check); } })
        .catch((reason) => { if (ticket === sequence.current) setError(reason); });
    }, 500);
    return () => window.clearTimeout(timer);
  }, [template, content, language, sample]);

  if (!template || !content || !editing) return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Mail templates", to: "/mail/templates" }, { label: "Template" }]} />
    <ErrorNote error={error} /><div className="empty"><h2>{error ? "Template unavailable" : "Loading template…"}</h2></div>
  </main>;

  const readOnly = !designer || editing.status !== "draft" && editing.versionId !== null;
  const current = content.languages.find((x) => x.language === language) ?? content.languages[0]!;
  const fields = template.kindView.fields;
  const used = fieldsMentioned(current, fields);
  const published = template.versions.find((x) => x.status === "published");
  const draft = template.versions.find((x) => x.status === "draft");

  const change = (next: MailTemplateContent) => { setContent(next); setDirty(true); setNotice(null); };
  const changeLanguage = (next: MailLanguageContent) => change({ ...content, languages: content.languages.map((x) => x.language === current.language ? next : x) });
  const setText = (key: TextKey, value: string) => changeLanguage(writeText(current, key, value));
  const changeBlocks = (blocks: MailBlock[]) => changeLanguage({ ...current, blocks });
  const changeBlock = (index: number, block: MailBlock) => changeBlocks(current.blocks.map((x, at) => at === index ? block : x));
  const box = (key: TextKey): BoxProps => ({
    ref: (element: HTMLInputElement | HTMLTextAreaElement | null) => { if (element) boxes.current.set(key, element); else boxes.current.delete(key); },
    value: readText(current, key),
    readOnly,
    onChange: (event: { target: { value: string } }) => setText(key, event.target.value),
    onSelect: (event: { currentTarget: HTMLInputElement | HTMLTextAreaElement }) => setFocus({ key, start: event.currentTarget.selectionStart ?? 0, end: event.currentTarget.selectionEnd ?? 0 }),
  });
  const insert = (field: MailField) => {
    if (readOnly || !focus) return;
    const list = listOf(current, focus.key);
    const token = list && field.itemOf === list ? itemToken(field) : field.path;
    const result = insertField(readText(current, focus.key), token, focus.start, focus.end);
    setText(focus.key, result.text);
    setFocus({ key: focus.key, start: result.caret, end: result.caret });
    requestAnimationFrame(() => { const element = boxes.current.get(focus.key); element?.focus(); element?.setSelectionRange(result.caret, result.caret); });
  };

  const act = async (work: () => Promise<void>) => {
    setBusy(true); setError(null); setNotice(null);
    try { await work(); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const save = (submitForReview = false) => act(async () => {
    const result = await mailApi.saveDraft(template.templateId, editing.versionId, editing.versionId ? editing.expectedVersion : null, content, submitForReview);
    setCheck(result.check);
    if (result.template) { show(result.template); setNotice(submitForReview ? "Draft saved and sent for review; an administrator publishes it." : result.check.passed ? "Draft saved." : "Draft saved. It cannot be published until the checks pass."); }
  });
  const publish = () => act(async () => {
    let versionId = editing.versionId;
    if (dirty || !versionId) {
      const saved = await mailApi.saveDraft(template.templateId, editing.versionId, editing.versionId ? editing.expectedVersion : null, content);
      setCheck(saved.check);
      if (!saved.template || !saved.check.passed) return;
      versionId = saved.template.versions.find((x) => x.status === "draft")?.versionId ?? null;
    }
    if (!versionId) return;
    const result = await mailApi.publish(template.templateId, versionId);
    if (result.template) { show(result.template); setNotice(`Published. New messages use v${result.template.versions.find((x) => x.status === "published")?.version}; sent ones keep their own.`); }
  });
  const sendTest = () => act(async () => {
    await mailApi.testSend(template.templateId, sample, current.language, content);
    setNotice(`A test of the ${languageName(current.language)} version is on its way to ${session.user.email}.`);
  });
  const startDraft = () => { setEditing({ versionId: null, expectedVersion: null, status: "draft", version: (template.versions[0]?.version ?? 0) + 1 }); setDirty(true); setNotice(null); };
  const makeDefault = () => act(async () => { const detail = await mailApi.setDefault(template.templateId, [current.language]); show(detail); setNotice(`Default for ${languageName(current.language)} messages of this kind.`); });
  const retire = () => act(async () => { show(await mailApi.retire(template.templateId)); setNotice("Retired. Nobody can pick it when sending; sent messages are unchanged."); });

  const isAdmin = template.canPublish;
  const checkNote = check === null ? null : check.passed
    ? <div className="note ok"><strong>Checks pass.</strong>&nbsp;{check.fieldsUsed.length} fields used, all known for this kind of message · every repeating block has an empty-list text · subject {check.subjectLength} characters · {content.languages.map((x) => languageName(x.language)).join(", ")} · no images · links go into the product only.{check.warnings.length > 0 && <> {check.warnings.join(" ")}</>}</div>
    : <div className="note bad" role="status"><strong>Checks need attention.</strong><ul className="mail-checks">{check.errors.map((message) => <li key={message}>{message}</li>)}</ul></div>;

  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Mail templates", to: "/mail/templates" }, { label: template.name }]} />
    <PageHeader eyebrow={`Mail template · ${template.kindView.name}${published ? ` · v${published.version} published ${tenantTime.date(published.publishedAt)}` : ""}${editing.status === "draft" ? ` · editing v${editing.version} draft` : ""}`}
      title={template.name}
      sub="Your wording around the record's own facts. Fields come from the typed list on the left only, so every value is the one on the record; the layout is made e-mail-safe on the server and a plain-text part is written from it. Published versions are never edited."
      actions={readOnly ? <>{designer && <button type="button" className="btn primary" disabled={busy || !!draft} onClick={startDraft}>Edit as v{(template.versions[0]?.version ?? 0) + 1} draft</button>}</> : <>
        <button type="button" className="btn" disabled={busy} onClick={() => void sendTest()}>Send a test to me</button>
        <button type="button" className="btn" disabled={busy || !dirty && editing.versionId !== null} onClick={() => void save()}>Save draft</button>
        {isAdmin ? <button type="button" className="btn primary" disabled={busy || check?.passed === false} onClick={() => void publish()}>Publish v{editing.version}</button>
          : <button type="button" className="btn primary" disabled={busy || check?.passed === false || draft?.submittedForReview === true && !dirty} onClick={() => void save(true)}>{draft?.submittedForReview && !dirty ? "Waiting for review" : "Ask an administrator to publish"}</button>}
      </>} />
    <div className="stack">
      {!isAdmin && designer && <div className="note info"><strong>Publishing is for tenant administrators.</strong> You can design, test and save drafts; an administrator publishes v{editing.version}, and the template list shows it as waiting for review until then.</div>}
      {readOnly && designer && <div className="note">v{editing.version} is {editing.status}; published versions are never edited. {draft ? "A draft already exists for the next version." : "Start the next version as a draft to change the wording."}</div>}
      {notice && <div className="note ok" role="status">{notice}</div>}
      {checkNote}
      <ErrorNote error={error} onReload={load} />
      {error instanceof ApiError && error.status === 422 && <p className="hint">Fix the points above, then try again; the draft is kept as it is.</p>}
      <div className="mail-designer-grid">
        <div className="stack">
          <section className="card" aria-labelledby="mail-shell-heading"><div className="card-header"><h2 id="mail-shell-heading">Header and footer</h2></div><div className="card-body stack">
            <p className="muted" style={{ margin: 0 }}>Every message uses the company's monogram header and footer line, the same as account mail; there is no logo and no pictures. The footer line, sender name and reply-to come from {session.roles.includes("tenant_administrator") ? <Link to="/settings/company">Settings › Company</Link> : "Settings › Company"}.</p>
            {template.kindView.hasPdf && <label className="checkrow mail-option"><input type="checkbox" checked={content.attachPdf} disabled={readOnly} onChange={(event) => change({ ...content, attachPdf: event.target.checked })} /><span>Attach the customer's {template.kindView.name.toLowerCase().replace(/ sheet$/, "")} PDF<span className="help">Made at sending time from the same record; the sender cannot remove it.</span></span></label>}
            <label className="checkrow mail-option"><input type="checkbox" checked={content.askForNote} disabled={readOnly} onChange={(event) => change({ ...content, askForNote: event.target.checked })} /><span>Offer a personal note<span className="help">The sender may type one; place it with a paragraph shown when <code>message.note</code> has a value.</span></span></label>
          </div></section>
          <section className="card" aria-labelledby="mail-fields-heading"><div className="card-header"><h2 id="mail-fields-heading">Fields</h2><span className="meta">{readOnly ? "read only" : focus ? "click to insert" : "pick a box first"}</span></div><div className="card-body mail-fields">
            {fieldGroups(fields).map(([group, list]) => <div key={group}><h3>{group}</h3>{list.map((field) => <button type="button" className="mail-field" key={field.path} disabled={readOnly || !focus} onClick={() => insert(field)} title={field.repeats ? "A list: use it in a table, or {{#each}} … {{else}} … {{/each}}" : undefined}>
              <span><span className="t">{field.label}{field.repeats ? " (list)" : ""}</span><span className="p">{field.itemOf ? `${field.itemOf} › ${itemToken(field)}` : field.path}</span></span>
              {used.has(field.path) ? <StatusBadge tone="ok">Used</StatusBadge> : <span />}
            </button>)}</div>)}
            <p className="hint">Helpers: <code>{"{{date path}}"}</code>, <code>{"{{datetime path}}"}</code>, <code>{"{{money path}}"}</code>, <code>{"{{label path}}"}</code>.</p>
          </div></section>
          <VersionsCard versions={template.versions} defaultFor={template.defaultFor} date={tenantTime.date}
            admin={isAdmin} busy={busy} language={current.language} published={!!published}
            onDefault={() => void makeDefault()} onRetire={() => void retire()} />
        </div>

        <div className="stack">
          <section className="card" aria-labelledby="mail-opening-heading"><div className="card-header"><h2 id="mail-opening-heading">Subject and opening</h2>
            <div className="segmented" role="group" aria-label="Language">{content.languages.map((x) => <button type="button" key={x.language} aria-pressed={x.language === current.language} onClick={() => { setLanguage(x.language); setSelected(null); setFocus(null); }}>{languageName(x.language)}</button>)}</div>
          </div><div className="card-body stack">
            <label className="field"><span className="lbl">Subject</span><input className="input" maxLength={200} {...box("subject")} /></label>
            <label className="field"><span className="lbl">Preview line <span className="muted">(shown next to the subject in the inbox)</span></span><input className="input" maxLength={200} {...box("preheader")} /></label>
            <div className="form-grid">
              <label className="field"><span className="lbl">Band label</span><input className="input" maxLength={40} {...box("bandLabel")} /></label>
              <label className="field"><span className="lbl">Heading</span><input className="input" {...box("heading")} /></label>
            </div>
            {!readOnly && <div className="mail-row">
              <select className="select" aria-label="Add a language" value="" onChange={(event) => { if (!event.target.value) return; change({ ...content, languages: addLanguage(content.languages, event.target.value, current) }); setLanguage(event.target.value); }}>
                <option value="">Add a language…</option>
                {LANGUAGE_CHOICES.filter((code) => !content.languages.some((x) => x.language === code)).map((code) => <option key={code} value={code}>{languageName(code)} (copy of {languageName(current.language)})</option>)}
              </select>
              {content.languages.length > 1 && <button type="button" className="btn sm" onClick={() => { const rest = content.languages.filter((x) => x.language !== current.language); change({ ...content, languages: rest }); setLanguage(rest[0]!.language); setSelected(null); }}>Remove {languageName(current.language)}</button>}
            </div>}
          </div></section>

          <section className="card" aria-labelledby="mail-layout-heading"><div className="card-header"><h2 id="mail-layout-heading">Layout</h2>
            {!readOnly && <select className="select" style={{ width: "auto" }} aria-label="Add block" value="" onChange={(event) => { if (!event.target.value) return; const blocks = [...current.blocks, newBlock(event.target.value, fields)]; changeBlocks(blocks); setSelected(blocks.length - 1); }}>
              <option value="">Add block…</option>{BLOCK_TYPES.map((entry) => <option key={entry.type} value={entry.type}>{entry.label} · {entry.hint}</option>)}
            </select>}
          </div><div className="card-body mail-blocks">
            {current.blocks.map((block, index) => <div key={index}>
              <button type="button" className="mail-block" aria-pressed={selected === index} onClick={() => setSelected(selected === index ? null : index)}>
                <span className="kind">{blockLabel(block)}{block.when && block.when !== "message.note" ? ` · shown when ${block.when} has a value` : ""}</span>
                <span className="kind">{block.type === "table" ? `each ${block.each ?? ""}` : ""}</span>
                <span className="body">{summary(block)}</span>
              </button>
              {selected === index && <BlockEditor block={block} index={index} fields={fields} readOnly={readOnly} box={box}
                onChange={(next) => changeBlock(index, next)}
                onMove={(delta) => { changeBlocks(moveBlock(current.blocks, index, delta)); setSelected(index + delta); setFocus(null); }}
                onRemove={() => { changeBlocks(current.blocks.filter((_, at) => at !== index)); setSelected(null); setFocus(null); }}
                first={index === 0} last={index === current.blocks.length - 1} />}
            </div>)}
            {current.blocks.length === 0 && <p className="muted">No blocks yet. Add a paragraph to begin.</p>}
          </div></section>
        </div>

        <section className="card mail-preview" aria-labelledby="mail-preview-heading"><div className="card-header"><h2 id="mail-preview-heading">Preview</h2>
          <div className="segmented" role="group" aria-label="Preview">{(["design", "text", "phone"] as PreviewMode[]).map((value) => <button type="button" key={value} aria-pressed={mode === value} onClick={() => setMode(value)}>{value === "design" ? "Design" : value === "text" ? "Plain text" : "Phone"}</button>)}</div>
        </div><div className="card-body stack">
          <label className="field"><span className="lbl">Fill from a real record</span><select className="select" value={sample ?? ""} onChange={(event) => setSample(event.target.value || null)}>
            {template.sampleRecords.length === 0 && <option value="">No {template.kindView.recordLabel.toLowerCase()} with live cover yet</option>}
            {template.sampleRecords.map((record) => <option key={record.recordId} value={record.recordId}>{record.label}</option>)}
          </select></label>
          {preview && <div className="mail-envelope"><span className="k">Subject</span><span className="v"><strong>{preview.subject}</strong></span>
            {preview.preheader && <><span className="k">Preview</span><span className="v">{preview.preheader}</span></>}
            {preview.attachmentName && <><span className="k">Attached</span><span className="v">{preview.attachmentName}</span></>}</div>}
          {!preview ? <p className="muted">Rendering the preview…</p> : mode === "text" ? <pre className="mail-text">{preview.text}</pre>
            : <iframe className={`mail-frame${mode === "phone" ? " phone" : ""}`} sandbox="" title="E-mail preview" srcDoc={preview.html} />}
          {content.askForNote && <p className="hint">The preview shows a sample personal note; the sender writes their own.</p>}
        </div></section>
      </div>
    </div>
  </main>;
}

function summary(block: MailBlock): ReactNode {
  const text = block.type === "table" ? (block.columns ?? []).map((column) => `${column.header}: ${column.text}`).join(" · ")
    : block.type === "panel" ? (block.facts ?? []).map((fact) => `${fact.label}: ${fact.text}`).join("\n")
      : block.type === "button" ? `${block.label ?? ""} → ${block.link ?? ""}` : block.text ?? "";
  if (!text.trim()) return <span className="muted">Empty — click to write it.</span>;
  return pieces(text).map((piece, index) => piece.kind === "text" ? <span key={index}>{piece.value}</span>
    : piece.kind === "field" ? <span key={index} className="mail-token">{piece.value}</span> : <span key={index} className="mail-logic">{`{{${piece.value}}}`}</span>);
}

interface BoxProps {
  ref: (element: HTMLInputElement | HTMLTextAreaElement | null) => void;
  value: string;
  readOnly: boolean;
  onChange: (event: { target: { value: string } }) => void;
  onSelect: (event: { currentTarget: HTMLInputElement | HTMLTextAreaElement }) => void;
}
type Box = (key: TextKey) => BoxProps;

function BlockEditor({ block, index, fields, readOnly, box, onChange, onMove, onRemove, first, last }: {
  block: MailBlock; index: number; fields: MailField[]; readOnly: boolean; box: Box;
  onChange: (block: MailBlock) => void; onMove: (delta: -1 | 1) => void; onRemove: () => void; first: boolean; last: boolean;
}) {
  const key = (rest: string) => `b${index}.${rest}`;
  const plain = fields.filter((field) => !field.repeats && !field.itemOf);
  return <div className="mail-block-editor">
    {(block.type === "heading" || block.type === "paragraph" || block.type === "sign_off") && <>
      <label className="field"><span className="lbl">Text</span><textarea className="textarea" rows={block.type === "heading" ? 1 : 4} {...box(key("text"))} /><span className="help">**bold**, *italic*, [label]({"{{links.…}}"}); a new line starts a new line.</span></label>
      <label className="field"><span className="lbl">Show</span><select className="select" value={block.when ?? ""} disabled={readOnly} onChange={(event) => onChange({ ...block, when: event.target.value || null })}>
        <option value="">Always</option>{plain.map((field) => <option key={field.path} value={field.path}>Only when {field.label.toLowerCase()} has a value</option>)}
      </select></label>
    </>}
    {block.type === "table" && <>
      <div className="form-grid">
        <label className="field"><span className="lbl">One row for each</span><select className="select" value={block.each ?? ""} disabled={readOnly} onChange={(event) => onChange({ ...block, each: event.target.value })}>
          <option value="">Choose a list…</option>{fields.filter((field) => field.repeats).map((field) => <option key={field.path} value={field.path}>{field.label}</option>)}
        </select></label>
        <label className="field"><span className="lbl">When the list is empty</span><input className="input" {...box(key("emptyText"))} /></label>
      </div>
      {(block.columns ?? []).map((_, column) => <div className="row" key={column}>
        <label className="field"><span className="lbl">Column {column + 1}</span><input className="input" {...box(key(`c${column}.header`))} /></label>
        <label className="field"><span className="lbl">Value</span><input className="input" {...box(key(`c${column}.text`))} /></label>
        <button type="button" className="btn sm" disabled={readOnly || (block.columns?.length ?? 0) <= 1} onClick={() => onChange({ ...block, columns: (block.columns ?? []).filter((__, at) => at !== column) })}>Remove</button>
      </div>)}
      {!readOnly && (block.columns?.length ?? 0) < 4 && <button type="button" className="btn sm" onClick={() => onChange({ ...block, columns: [...(block.columns ?? []), { header: "Column", text: "" }] })}>Add column</button>}
    </>}
    {block.type === "panel" && <>
      {(block.facts ?? []).map((_, fact) => <div className="row" key={fact}>
        <label className="field"><span className="lbl">Label</span><input className="input" {...box(key(`f${fact}.label`))} /></label>
        <label className="field"><span className="lbl">Value</span><input className="input" {...box(key(`f${fact}.text`))} /></label>
        <button type="button" className="btn sm" disabled={readOnly || (block.facts?.length ?? 0) <= 1} onClick={() => onChange({ ...block, facts: (block.facts ?? []).filter((__, at) => at !== fact) })}>Remove</button>
      </div>)}
      {!readOnly && (block.facts?.length ?? 0) < 8 && <button type="button" className="btn sm" onClick={() => onChange({ ...block, facts: [...(block.facts ?? []), { label: "Label", text: "" }] })}>Add row</button>}
    </>}
    {block.type === "button" && <div className="form-grid">
      <label className="field"><span className="lbl">Button label</span><input className="input" {...box(key("label"))} /></label>
      <label className="field"><span className="lbl">Opens</span><select className="select" value={block.link ?? ""} disabled={readOnly} onChange={(event) => onChange({ ...block, link: event.target.value })}>
        {fields.filter((field) => field.path.startsWith("links.")).map((field) => <option key={field.path} value={field.path}>{field.label}</option>)}
      </select><span className="help">Buttons open the product's own pages only.</span></label>
    </div>}
    {!readOnly && <div className="mail-row">
      <button type="button" className="btn sm" disabled={first} onClick={() => onMove(-1)}>Move up</button>
      <button type="button" className="btn sm" disabled={last} onClick={() => onMove(1)}>Move down</button>
      <span className="grow" />
      <button type="button" className="btn sm" onClick={onRemove}>Remove block</button>
    </div>}
  </div>;
}

function VersionsCard({ versions, defaultFor, date, admin, busy, language, published, onDefault, onRetire }: {
  versions: MailTemplateVersionView[]; defaultFor: string[]; date: (value: string | null | undefined) => string; admin: boolean; busy: boolean;
  language: string; published: boolean; onDefault: () => void; onRetire: () => void;
}) {
  return <section className="card" aria-labelledby="mail-versions-heading"><div className="card-header"><h2 id="mail-versions-heading">Versions</h2><span className="meta">{defaultFor.length ? `default for ${defaultFor.map(languageName).join(", ")}` : "not a default"}</span></div><div className="card-body stack">
    {versions.map((version) => <div className="mail-row" key={version.versionId}>
      <StatusBadge tone={versionTone(version.status)}>v{version.version} {version.status}{version.submittedForReview && version.status === "draft" ? " · for review" : ""}</StatusBadge>
      <span className="muted">{version.status === "published" || version.status === "retired" ? `${date(version.publishedAt)} · ${version.publishedBy ?? ""}` : `${date(version.updatedAt)} · ${version.updatedBy}`}</span>
    </div>)}
    {admin && published && <div className="mail-row">
      {!defaultFor.includes(language) && <button type="button" className="btn sm" disabled={busy} onClick={onDefault}>Make default for {languageName(language)}</button>}
      <button type="button" className="btn sm" disabled={busy} onClick={onRetire}>Retire template</button>
    </div>}
  </div></section>;
}
