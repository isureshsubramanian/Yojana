import { useEffect, useState } from "react";
import { Link } from "react-router";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { labelsApi } from "../labels/api";
import type { LabelPolicy } from "../labels/types";
import "../labels/labels.css";

export function Component() {
  const session = useSession();
  const permitted = session.roles.includes("tenant_administrator");
  const [policy, setPolicy] = useState<LabelPolicy | null>(null);
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(permitted);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    labelsApi.settings(controller.signal).then(setPolicy).catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); }).finally(() => setLoading(false));
    return () => controller.abort();
  }, [permitted]);

  const save = async () => {
    if (!policy) return;
    setBusy(true); setSaved(false); setError(null);
    try { setPolicy(await labelsApi.saveSettings(policy)); setSaved(true); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };

  if (!permitted) return <main className="content" id="main" tabIndex={-1}><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Labels" }]} /><div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Label settings</h1></div></div><div className="note bad"><strong>Tenant administrator access is required.</strong>&nbsp;These settings affect legal-review gates and the data allowed onto printed labels.</div></main>;

  return <main className="content label-settings" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Labels" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Label settings</h1><p className="sub">Control wording that needs legal review and whether print-time warranty expiry is available to label bindings.</p></div><div className="actions"><Link className="btn" to="/labels/print-jobs">Back to labels</Link><button type="button" className="btn primary" disabled={loading || busy || !policy || policy.legalReviewPhrases.every((phrase) => !phrase.trim()) || !policy.defaultPrinterRef.trim()} onClick={() => void save()}><Icon name="check" />{busy ? "Saving…" : "Save"}</button></div></div>
    <ErrorNote error={error} />{saved && <div className="note ok label-page-note"><strong>Label settings saved.</strong>&nbsp;New drafts and print jobs use these tenant settings.</div>}
    {loading && <p className="muted">Loading label settings…</p>}
    {policy && <div className="cols-3-2 top">
      <section className="card" aria-labelledby="legal-phrases"><div className="card-header"><h2 id="legal-phrases">Legal-review phrase list</h2><button type="button" className="btn sm" onClick={() => setPolicy({ ...policy, legalReviewPhrases: [...policy.legalReviewPhrases, ""] })}><Icon name="plus" />Add phrase</button></div><div className="card-body stack">
        <p className="muted">A case-insensitive match in label static text or layout raises the review flag. The server still requires an independent legal reviewer before activation.</p>
        <div className="label-phrase-list">{policy.legalReviewPhrases.map((phrase, index) => <div className="label-phrase-row" key={index}><label className="field"><span className="lbl">Phrase {index + 1}</span><input className="input" value={phrase} maxLength={100} onChange={(event) => setPolicy({ ...policy, legalReviewPhrases: policy.legalReviewPhrases.map((value, row) => row === index ? event.target.value : value) })} /></label><button type="button" className="btn ghost" disabled={policy.legalReviewPhrases.length === 1} onClick={() => setPolicy({ ...policy, legalReviewPhrases: policy.legalReviewPhrases.filter((_, row) => row !== index) })}>Remove</button></div>)}</div>
      </div></section>
      <div className="stack"><section className="card" aria-labelledby="printed-expiry"><div className="card-header"><h2 id="printed-expiry">Printed expiry</h2><span className="meta">default off</span></div><div className="card-body"><label className="check-setting"><input type="checkbox" checked={policy.printExpiryDates} onChange={(event) => setPolicy({ ...policy, printExpiryDates: event.target.checked })} /><span><strong>Allow warranty expiry on labels</strong><span className="help">Off is the recommended default. A printed date can become stale after an amendment, extension or transfer; the stored print payload preserves what the sticker said.</span></span></label></div></section>
        <section className="card" aria-labelledby="default-printer"><div className="card-header"><h2 id="default-printer">Default printer</h2></div><div className="card-body"><label className="field"><span className="lbl">Printer reference</span><input className="input" value={policy.defaultPrinterRef} maxLength={200} onChange={(event) => setPolicy({ ...policy, defaultPrinterRef: event.target.value })} /><span className="help">Preselected for manual print runs; operators may choose another printer.</span></label></div></section>
      </div>
    </div>}
  </main>;
}
