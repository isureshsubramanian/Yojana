// ADR-0023 drawers on the certificate viewer: Correct and reissue (preview first, nothing is signed until the user
// confirms) and Print copy (one logged Copy). Rules live in correction.ts; the server checks every one again.
import { useEffect, useId, useState } from "react";
import { useNavigate } from "react-router";
import { FormActions } from "../components/FormActions";
import { closeDrawer } from "../shell/drawerStore";
import { Icon } from "../shell/Icons";
import { ErrorNote, Section } from "../units/common";
import { certificateCorrectionApi } from "./api";
import { changeLabel, factChanges, REASON_MAXIMUM, reasonProblem } from "./correction";
import type { CertificateCorrectionPreview } from "./types";

export function CorrectionPanel({ certificateId, onIssued }: { certificateId: string; onIssued: () => void }) {
  const navigate = useNavigate();
  const reasonId = useId();
  const [preview, setPreview] = useState<CertificateCorrectionPreview | null>(null);
  const [reason, setReason] = useState("");
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    const controller = new AbortController();
    certificateCorrectionApi.preview(certificateId, controller.signal).then(setPreview)
      .catch((reason: unknown) => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, [certificateId]);

  if (!preview) return <><ErrorNote error={error} />{!error && <p className="muted" role="status">Comparing the signed version with today's data…</p>}</>;
  const facts = factChanges(preview.changes);
  const derived = preview.changes.filter((change) => change.derived);
  const problem = reasonProblem(reason);

  async function issue() {
    setTouched(true);
    if (problem || busy) return;
    setBusy(true); setError(null);
    try {
      const result = await certificateCorrectionApi.reissue(certificateId, reason);
      closeDrawer(); onIssued();
      void navigate(`/certificates/${result.certificateId}`);
    } catch (reason) { setError(reason); setBusy(false); }
  }

  return <>
    {!preview.canCorrect ? <div className="note" role="status">{preview.refusalDetail ?? "Nothing to correct: the record matches today's data."}</div> : <>
      <Section title={`What changes · v${preview.fromVersion} → v${preview.toVersion}`}>
        <table className="table dense correction-changes"><thead><tr><th>Field</th><th>Signed</th><th>Today</th></tr></thead><tbody>
          {facts.map((change) => <tr key={`${change.path}|${change.before ?? ""}`}><td>{changeLabel(change)}</td><td className="mono">{change.before ?? "—"}</td><td>{change.after ?? "—"}</td></tr>)}
          {derived.map((change) => <tr key={`${change.path}|derived`} className="muted"><td>{changeLabel(change)}</td><td colSpan={2}>derived summary, not a certificate fact</td></tr>)}
        </tbody></table>
      </Section>
      <p className="muted">v{preview.fromVersion} stays on file as Superseded. v{preview.toVersion} prints "Verified record correction; unit condition was not re-verified." with your reason. Subscribers receive Certificate.Superseded.</p>
      <label className="field" htmlFor={reasonId}><span className="lbl">Reason (required)</span>
        <textarea id={reasonId} className="textarea" rows={3} maxLength={REASON_MAXIMUM} value={reason} placeholder="Part names corrected: ERP item descriptions"
          aria-invalid={touched && !!problem} aria-describedby={touched && problem ? `${reasonId}-problem` : undefined}
          onChange={(event) => setReason(event.target.value)} onBlur={() => setTouched(true)} />
        {touched && problem && <span className="help bad" id={`${reasonId}-problem`} role="alert">{problem}</span>}</label>
    </>}
    <ErrorNote error={error} />
    <FormActions>
      <button type="button" className="btn" onClick={closeDrawer}>Cancel</button>
      {preview.canCorrect && <button type="button" className="btn primary" disabled={busy} aria-busy={busy} onClick={() => void issue()}><Icon name="check" />Issue v{preview.toVersion}</button>}
    </FormActions>
  </>;
}

export function CopyPanel({ certificateId, fallbackName, onPrinted }: { certificateId: string; fallbackName: string; onPrinted: () => void }) {
  const reasonId = useId(); const recipientId = useId();
  const [reason, setReason] = useState("");
  const [recipient, setRecipient] = useState("");
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [done, setDone] = useState<{ fileName: string; htmlFallback: boolean } | null>(null);
  const problem = reasonProblem(reason);

  async function print() {
    setTouched(true);
    if (problem || busy) return;
    setBusy(true); setError(null);
    try { setDone(await certificateCorrectionApi.printCopy(certificateId, reason, recipient, fallbackName)); onPrinted(); }
    catch (reason) { setError(reason); }
    finally { setBusy(false); }
  }

  if (done) return <>
    <div className="note ok" role="status">Downloaded {done.fileName}. It is marked COPY and logged in the reprint log.</div>
    {done.htmlFallback && <div className="note warn" role="status">The PDF service did not answer, so this copy is an HTML page. Open it and print it from the browser.</div>}
    <FormActions><button type="button" className="btn" onClick={closeDrawer}>Close</button></FormActions>
  </>;
  return <>
    <p className="muted">A copy never creates a version. Each one is marked COPY and listed in the reprint log with your reason.</p>
    <label className="field" htmlFor={reasonId}><span className="lbl">Reason (required)</span>
      <textarea id={reasonId} className="textarea" rows={2} maxLength={REASON_MAXIMUM} value={reason} placeholder="Customer asked for the release record"
        aria-invalid={touched && !!problem} aria-describedby={touched && problem ? `${reasonId}-problem` : undefined}
        onChange={(event) => setReason(event.target.value)} onBlur={() => setTouched(true)} />
      {touched && problem && <span className="help bad" id={`${reasonId}-problem`} role="alert">{problem}</span>}</label>
    <label className="field" htmlFor={recipientId}><span className="lbl">Recipient · optional</span>
      <input id={recipientId} className="input" maxLength={200} value={recipient} onChange={(event) => setRecipient(event.target.value)} /></label>
    <ErrorNote error={error} />
    <FormActions>
      <button type="button" className="btn" onClick={closeDrawer}>Cancel</button>
      <button type="button" className="btn primary" disabled={busy} aria-busy={busy} onClick={() => void print()}><Icon name="print" />Print copy</button>
    </FormActions>
  </>;
}
