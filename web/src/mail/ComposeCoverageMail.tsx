import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useTenantTime } from "../components/tenantTime";
import { useSession } from "../session/session";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { mailApi, type ComposeTemplateChoice, type CoverageMailComposeView, type MailPreviewView, type MailRecipientInput } from "./api";
import { DESIGNER_ROLES, holds, languageName, looksLikeAddress, recipientFor } from "./format";
import "./mail.css";

type Mode = "html" | "text" | "phone";

export function Component() {
  const { unitId = "" } = useParams();
  const navigate = useNavigate();
  const session = useSession();
  const tenantTime = useTenantTime();
  const [view, setView] = useState<CoverageMailComposeView | null>(null);
  const [to, setTo] = useState<MailRecipientInput[]>([]);
  const [cc, setCc] = useState<MailRecipientInput[]>([]);
  const [versionId, setVersionId] = useState("");
  const [language, setLanguage] = useState("en");
  const [note, setNote] = useState("");
  const [signedBy, setSignedBy] = useState<"me" | "team">("me");
  const [preview, setPreview] = useState<MailPreviewView | null>(null);
  const [mode, setMode] = useState<Mode>("html");
  const [commandId, setCommandId] = useState(() => crypto.randomUUID());
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);

  const load = useCallback(() => {
    const controller = new AbortController();
    mailApi.compose(unitId, controller.signal).then((loaded) => {
      setView(loaded);
      const first = loaded.contacts[0];
      setTo(first ? [{ address: first.address, name: first.name }] : []);
      const preferred = loaded.customerLanguage ?? "en";
      const choice = loaded.templates.find((x) => x.defaultFor.includes(preferred)) ?? loaded.templates.find((x) => x.defaultFor.length > 0) ?? loaded.templates[0];
      if (choice) { setVersionId(choice.versionId); setLanguage(choice.languages.includes(preferred) ? preferred : choice.languages[0] ?? "en"); }
    }).catch((reason) => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, [unitId]);
  useEffect(() => load(), [load]);

  const template: ComposeTemplateChoice | undefined = view?.templates.find((x) => x.versionId === versionId);
  const recipientName = to[0]?.name ?? null;
  const sequence = useRef(0);
  useEffect(() => {
    if (!template) return;
    const ticket = ++sequence.current;
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      mailApi.composePreview(unitId, template.versionId, language, recipientName, template.askForNote && note.trim() ? note.trim() : null, signedBy, controller.signal)
        .then((result) => { if (ticket === sequence.current) setPreview(result); })
        .catch((reason) => { if (!controller.signal.aborted && ticket === sequence.current) setError(reason); });
    }, 400);
    return () => { window.clearTimeout(timer); controller.abort(); };
  }, [unitId, template, language, recipientName, note, signedBy]);

  if (!view) return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Unit", to: `/units/${unitId}?tab=whats-covered` }, { label: "E-mail to customer" }]} />
    <ErrorNote error={error} /><div className="empty"><h2>{error ? "This unit cannot be e-mailed" : "Loading…"}</h2></div>
  </main>;

  const back = `/units/${unitId}?tab=whats-covered`;
  const send = async (copyToMeOnly: boolean) => {
    if (!template) return;
    setBusy(true); setError(null); setDone(null);
    try {
      const result = await mailApi.send(unitId, {
        commandId: copyToMeOnly ? crypto.randomUUID() : commandId, templateVersionId: template.versionId, language, to, cc,
        note: template.askForNote && note.trim() ? note.trim() : null, signedBy, copyToMeOnly,
      });
      if (copyToMeOnly) setDone(`A copy is on its way to ${session.user.email}. Nothing was sent to the customer.`);
      else { setCommandId(crypto.randomUUID()); navigate(`/mail/sent?unitId=${unitId}&queued=${result.messageIds.length}`); }
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const tooLong = note.length > 1000;
  const canSend = !!template && to.length > 0 && !busy && !tooLong;

  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: view.serial, to: back }, { label: "E-mail to customer" }]} />
    <PageHeader eyebrow={`What's covered · ${view.itemName}${view.customerName ? ` · owned by ${view.customerName}` : ""}`} title="E-mail what's covered to the customer"
      sub="The message is built on the server from the published template and this unit's record, so what the customer reads is exactly what the sheet says today. You can add a personal note; you cannot change the cover wording."
      actions={<><Link className="btn" to={back}>Cancel</Link>
        <button type="button" className="btn" disabled={!template || busy || tooLong} onClick={() => void send(true)}>Send a copy to me first</button>
        <button type="button" className="btn primary" disabled={!canSend} onClick={() => void send(false)}>{busy ? "Sending…" : to.length > 1 ? `Send ${to.length} messages` : "Send"}</button></>} />
    <div className="stack">
      {view.templates.length === 0 && <div className="note bad"><strong>No published template for this kind of message.</strong>&nbsp;{holds(session.roles, DESIGNER_ROLES) ? <Link to="/mail/templates">Design and publish one first.</Link> : "Ask a service manager to design one and a tenant administrator to publish it."}</div>}
      {done && <div className="note ok" role="status">{done}</div>}
      <ErrorNote error={error} />
      <div className="mail-compose-grid">
        <div className="stack">
          <section className="card" aria-labelledby="mail-recipients-heading"><div className="card-header"><h2 id="mail-recipients-heading">Recipients</h2><span className="meta">{view.customerName ? `from ${view.customerName}'s contacts` : "no customer on the unit"}</span></div><div className="card-body stack">
            <RecipientList label="To" recipients={to} onChange={setTo} contacts={view.contacts} max={10} />
            <RecipientList label="Cc" recipients={cc} onChange={setCc} contacts={view.contacts} max={10} />
            <p className="hint" style={{ margin: 0 }}>Only addresses on the customer's record, or typed and confirmed here. Each recipient in To gets their own copy; nobody sees the others' addresses except those in Cc.</p>
          </div></section>
          <section className="card" aria-labelledby="mail-message-heading"><div className="card-header"><h2 id="mail-message-heading">Message</h2></div><div className="card-body stack">
            <div className="form-grid">
              <label className="field"><span className="lbl">Template</span><select className="select" value={versionId} onChange={(event) => { const next = view.templates.find((x) => x.versionId === event.target.value); setVersionId(event.target.value); if (next && !next.languages.includes(language)) setLanguage(next.languages[0] ?? "en"); }}>
                {view.templates.map((x) => <option key={x.versionId} value={x.versionId}>{x.name} · v{x.version} (published {tenantTime.date(x.publishedAt)})</option>)}
              </select></label>
              <label className="field"><span className="lbl">Language</span><select className="select" value={language} onChange={(event) => setLanguage(event.target.value)}>
                {(template?.languages ?? []).map((code) => <option key={code} value={code}>{languageName(code)}{code === view.customerLanguage ? " (customer's language)" : ""}</option>)}
              </select></label>
            </div>
            <label className="field"><span className="lbl">Subject</span><input className="input" value={preview?.subject ?? ""} readOnly /><span className="help">From the template; it names the unit itself.</span></label>
            {template?.askForNote && <label className="field"><span className="lbl">Personal note <span className="muted">(optional, placed after the greeting)</span></span>
              <textarea className="textarea" rows={3} maxLength={1000} value={note} onChange={(event) => setNote(event.target.value)} placeholder="As requested, here is the warranty cover for your unit." />
              <span className="help">{note.length}/1000 · plain text</span></label>}
            {template?.attachPdf && <div className="field"><span className="lbl">Attachment</span><div className="mail-attachment"><span className="pdf">PDF</span><span><strong>{view.attachmentName}</strong><span className="help" style={{ display: "block" }}>Made at sending time from the same record · customer wording only</span></span><span className="badge neutral">Always attached</span></div></div>}
            <label className="field"><span className="lbl">Signed by</span><select className="select" value={signedBy} onChange={(event) => setSignedBy(event.target.value === "team" ? "team" : "me")}>
              <option value="me">{view.signedByName}{view.signedByTeam ? ` · ${view.signedByTeam}` : ""} (you)</option>
              <option value="team">{view.senderName} (no personal name)</option>
            </select></label>
          </div></section>
        </div>
        <section className="card" aria-labelledby="mail-compose-preview"><div className="card-header"><h2 id="mail-compose-preview">Preview</h2>
          <div className="segmented" role="group" aria-label="Preview">{(["html", "text", "phone"] as Mode[]).map((value) => <button type="button" key={value} aria-pressed={mode === value} onClick={() => setMode(value)}>{value === "html" ? "HTML" : value === "text" ? "Plain text" : "Phone width"}</button>)}</div>
        </div><div className="card-body stack">
          <div className="mail-envelope">
            <span className="k">From</span><span className="v">{view.senderName} &lt;{view.fromAddress}&gt;</span>
            {view.replyTo && <><span className="k">Reply to</span><span className="v">{view.replyTo}</span></>}
            <span className="k">To</span><span className="v">{to.length ? to.map(display).join(", ") : <span className="muted">nobody yet</span>}</span>
            {cc.length > 0 && <><span className="k">Cc</span><span className="v">{cc.map(display).join(", ")}</span></>}
            <span className="k">Subject</span><span className="v"><strong>{preview?.subject ?? ""}</strong></span>
            {template?.attachPdf && <><span className="k">Attached</span><span className="v">{view.attachmentName}</span></>}
          </div>
          {!preview ? <p className="muted">{template ? "Rendering the preview…" : "Pick a template."}</p> : mode === "text" ? <pre className="mail-text">{preview.text}</pre>
            : <iframe className={`mail-frame${mode === "phone" ? " phone" : ""}`} sandbox="" title="E-mail preview" srcDoc={preview.html} />}
          {to.length > 1 && <p className="hint">The preview greets {display(to[0]!)}; each other recipient's copy greets them by name.</p>}
        </div></section>
      </div>
    </div>
  </main>;
}

const display = (recipient: MailRecipientInput) => recipient.name ? `${recipient.name} <${recipient.address}>` : recipient.address;

function RecipientList({ label, recipients, onChange, contacts, max }: {
  label: string; recipients: MailRecipientInput[]; onChange: (next: MailRecipientInput[]) => void;
  contacts: { name: string; address: string; role: string | null }[]; max: number;
}) {
  const [adding, setAdding] = useState(false);
  const [typed, setTyped] = useState("");
  const [confirmed, setConfirmed] = useState(false);
  const chosen = new Set(recipients.map((x) => x.address.toLowerCase()));
  const available = contacts.filter((contact) => !chosen.has(contact.address.toLowerCase()));
  const known = contacts.some((contact) => contact.address.toLowerCase() === typed.trim().toLowerCase());
  const add = (recipient: MailRecipientInput) => { onChange([...recipients, recipient]); setTyped(""); setConfirmed(false); setAdding(false); };
  const id = `mail-${label.toLowerCase()}`;
  return <div className="field">
    <span className="lbl" id={id}>{label}</span>
    <div className="mail-recipients" aria-labelledby={id}>
      {recipients.map((recipient) => <span className="mail-recipient" key={recipient.address}>
        <span>{recipient.name ?? recipient.address}</span>
        {recipient.name && <span className="a">{recipient.address}{contacts.find((x) => x.address === recipient.address)?.role ? ` · ${contacts.find((x) => x.address === recipient.address)!.role}` : ""}</span>}
        {!recipient.name && recipient.confirmed && <span className="a">typed and confirmed</span>}
        <button type="button" aria-label={`Remove ${recipient.address}`} onClick={() => onChange(recipients.filter((x) => x !== recipient))}>×</button>
      </span>)}
      {!adding && recipients.length < max && <button type="button" className="btn sm" onClick={() => setAdding(true)}>Add…</button>}
    </div>
    {adding && <div className="stack" style={{ marginTop: 8 }}>
      {available.length > 0 && <div className="mail-row">{available.map((contact) => <button type="button" className="chip" key={contact.address} onClick={() => add({ address: contact.address, name: contact.name })}>{contact.name}{contact.role ? ` · ${contact.role}` : ""}</button>)}</div>}
      <div className="mail-row">
        <input className="input" style={{ maxWidth: 320 }} type="email" placeholder="Or type an address" value={typed} onChange={(event) => { setTyped(event.target.value); setConfirmed(false); }} aria-label={`Type an address for ${label}`} />
        <button type="button" className="btn sm" disabled={!looksLikeAddress(typed) || chosen.has(typed.trim().toLowerCase()) || (!known && !confirmed)} onClick={() => add(recipientFor(typed, contacts, confirmed))}>Add</button>
        <button type="button" className="btn sm" onClick={() => { setAdding(false); setTyped(""); setConfirmed(false); }}>Close</button>
      </div>
      {looksLikeAddress(typed) && !known && <label className="checkrow"><input type="checkbox" checked={confirmed} onChange={(event) => setConfirmed(event.target.checked)} /><span>{typed.trim()} is not one of the customer's contacts. I have checked it is the right address.</span></label>}
    </div>}
  </div>;
}
