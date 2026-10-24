import { useEffect, useState } from "react";
import { ApiError } from "../api/client";
import { FormActions } from "../components";
import { useSession } from "../session/session";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { useLegalEntities, legalEntitiesApi } from "../api/legalEntities";
import { LegalEntityField } from "../corrections/ReferenceFields";
import { companyApi, companyLine, type CompanyMailSettings } from "./companyApi";

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  const { value: legalEntities, error: legalEntityError, setValue: setLegalEntities } = useLegalEntities(permitted);
  const [defaultLegalEntityId, setDefaultLegalEntityId] = useState("");
  useEffect(() => { if (legalEntities?.defaultId) setDefaultLegalEntityId((current) => current || legalEntities.defaultId!); }, [legalEntities?.defaultId]);
  const [settings, setSettings] = useState<CompanyMailSettings | null>(null);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [test, setTest] = useState<string | null>(null);
  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    companyApi.mail(controller.signal).then(setSettings).catch((reason) => {
      if (!(reason instanceof DOMException && reason.name === "AbortError")) setError("Company settings could not be loaded.");
    });
    return () => controller.abort();
  }, [permitted]);
  const save = async () => {
    if (!settings) return; setBusy(true); setSaved(false); setError(null);
    try {
      const [mail, entities] = await Promise.all([companyApi.saveMail(settings), legalEntitiesApi.setDefault(defaultLegalEntityId)]);
      setSettings(mail); setLegalEntities(entities); setSaved(true);
    }
    catch (reason) { setError(reason instanceof ApiError && reason.status === 409 ? "Someone else changed these settings. Reload before saving." : reason instanceof ApiError ? reason.detail : "Company settings could not be saved."); }
    finally { setBusy(false); }
  };
  const sendTest = async () => {
    setTest("Sending a test message to you…");
    try {
      await companyApi.sendTest();
      setTest("Test message on its way to your address; it normally arrives within a minute.");
    } catch (reason) { setTest(reason instanceof ApiError ? reason.detail : "The test message could not be sent."); }
  };
  const tooLong = settings !== null && settings.footerLine.trim().length > 120;
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Company" }]} />
    <PageHeader eyebrow="Settings · tenant administrator" title="Company" sub="How the company appears on every e-mail it sends, account mail and customer mail alike. Every change is recorded in Security events." />
    {!permitted && <div className="note bad"><strong>Tenant administrator access is required.</strong></div>}
    {!!(error || legalEntityError) && <div className="note bad" role="alert">{error ?? "Legal entities could not be loaded."}</div>}
    {permitted && !settings && !error && <p className="muted">Loading company settings…</p>}
    {settings && <div className="stack top" style={{ maxWidth: 900 }}>
      <section className="card" aria-labelledby="company-legal-entity"><div className="card-header"><h2 id="company-legal-entity">Legal entity</h2><span className="meta">used when a screen does not offer a choice</span></div><div className="card-body"><div className="form-grid">
        <div className="span"><LegalEntityField id="company-default-legal-entity" label="Default legal entity" value={defaultLegalEntityId || null} onChange={(value) => setDefaultLegalEntityId(value ?? "")} hint="Every screen command is stamped on the server; tenants with one legal entity use it automatically." /></div>
      </div></div></section>
      <section className="card" aria-labelledby="company-name"><div className="card-header"><h2 id="company-name">Name and footer</h2></div><div className="card-body"><div className="form-grid">
        <div className="field span"><span className="lbl">Company name</span><span className="fixed-value">{settings.companyName}</span><span className="help">Set when the company was created; the e-mail header shows it beside its initials.</span></div>
        <label className="field span"><span className="lbl">Footer line</span><input className="input" maxLength={120} value={settings.footerLine} placeholder="Service operations · Bengaluru, India" onChange={(event) => setSettings({ ...settings, footerLine: event.target.value })} /><span className="help">Shown after the company name at the foot of every e-mail; up to 120 characters, plain text. Leave empty to show the name alone.</span></label>
      </div>
        <p className="hint" style={{ margin: "12px 0 0" }}>Every e-mail ends with: <strong>{companyLine(settings)}</strong></p>
        {tooLong && <div className="note bad">Keep the footer line to 120 characters.</div>}
      </div></section>
      <section className="card" aria-labelledby="company-sender"><div className="card-header"><h2 id="company-sender">Sender</h2><span className="meta">the address itself is the service's</span></div><div className="card-body"><div className="form-grid">
        <label className="field"><span className="lbl">Sender name</span><input className="input" maxLength={100} value={settings.senderName} placeholder="The service's default name" onChange={(event) => setSettings({ ...settings, senderName: event.target.value })} /><span className="help">Appears as the From name. Leave empty for the service's default.</span></label>
        <label className="field"><span className="lbl">Replies go to</span><input className="input" type="email" value={settings.replyTo} placeholder="service@example.com" onChange={(event) => setSettings({ ...settings, replyTo: event.target.value })} /><span className="help">One address the company reads. Leave empty for no reply-to.</span></label>
      </div></div>
        <div className="card-footer"><button className="btn sm" type="button" onClick={() => void sendTest()}>Send a test message to me</button><span className="grow" />{test && <span className="muted" role="status">{test}</span>}</div>
      </section>
      <p className="hint">No logo and no pictures in e-mail: the header shows the company's initials, so every mail program displays it.</p>
      <FormActions status={saved ? "Saved. New commands and e-mails use these settings from now on." : null}><button className="btn primary" type="button" disabled={busy || !settings || !defaultLegalEntityId || tooLong} onClick={() => void save()}>{busy ? "Saving…" : "Save"}</button></FormActions>
    </div>}
  </main>;
}
