import { useEffect, useMemo, useState } from "react";
import { Link, useNavigate } from "react-router";
import { ApiError } from "../api/client";
import { FormActions } from "../components";
import { CustomerField } from "../corrections/ReferenceFields";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { PageHeader } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { blockerPath, canConfirm, storeLabel } from "./privacyModel";
import { privacyApi, type ErasureBlocker, type ErasurePreview, type ErasureRequestInput, type ErasureSubjectKind, type ReviewAction } from "./privacyApi";
import { userManagementApi, type TenantUserRow } from "./userManagementApi";

type Refusal = { message: string; unblockingStep: string | null; blockers: readonly ErasureBlocker[] };

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  const navigate = useNavigate();
  const [kind, setKind] = useState<ErasureSubjectKind>("Party");
  const [subjectId, setSubjectId] = useState<string | null>(null);
  const [reference, setReference] = useState("");
  const [received, setReceived] = useState("");
  const [reason, setReason] = useState("");
  const [naturalPerson, setNaturalPerson] = useState(false);
  const [preview, setPreview] = useState<ErasurePreview | null>(null);
  const [decisions, setDecisions] = useState<Record<string, ReviewAction | undefined>>({});
  const [confirmation, setConfirmation] = useState("");
  const [refusal, setRefusal] = useState<Refusal | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const invalidate = () => { setPreview(null); setDecisions({}); setConfirmation(""); setRefusal(null); setError(null); };
  const request = (): ErasureRequestInput => ({ subjectKind: kind, subjectId: subjectId ?? "", requestReference: reference.trim(), requestReceivedOn: received, reason: reason.trim(), subjectIsNaturalPerson: kind === "User" || naturalPerson });
  const previewReady = !!subjectId && !!reference.trim() && !!received && !!reason.trim() && (kind === "User" || naturalPerson);

  const runPreview = async () => {
    setBusy(true); setError(null); setRefusal(null);
    try {
      const result = await privacyApi.preview(request());
      setPreview(result); setDecisions({}); setConfirmation("");
    } catch (reason) {
      if (reason instanceof ApiError && reason.status === 409) {
        setPreview(null); setRefusal({ message: reason.detail ?? "The erasure request was refused.", unblockingStep: reason.unblockingStep, blockers: reason.blockers });
      } else setError(reason);
    } finally { setBusy(false); }
  };
  const confirm = async () => {
    if (!preview || !canConfirm(preview, decisions, reference.trim(), confirmation)) return;
    setBusy(true); setError(null);
    try {
      await privacyApi.confirm({ ...request(), confirmation, reviewDecisions: preview.reviewItems.map(item => ({ itemKey: item.itemKey, action: decisions[item.itemKey]! })) });
      navigate("/settings/privacy");
    } catch (reason) {
      if (reason instanceof ApiError && reason.status === 409) {
        setPreview(null); setRefusal({ message: reason.detail ?? "The erasure request was refused.", unblockingStep: reason.unblockingStep, blockers: reason.blockers });
      } else setError(reason);
    } finally { setBusy(false); }
  };

  return <main className="content" id="main" tabIndex={-1}>
    <PageHeader eyebrow="Settings · Privacy" title="New erasure" sub="Choose the person, record the request, preview what changes, then confirm. The preview changes nothing unless it finds a reason to save the request as refused." />
    {!permitted ? <div className="note bad"><strong>Tenant administrator access is required.</strong></div> : refusal ?
      <RefusedPreview refusal={refusal} reference={reference} onBack={() => navigate("/settings/privacy")} onAgain={() => void runPreview()} busy={busy} /> :
      <div className="stack">
        <ErrorNote error={error} />
        <section className="card" aria-labelledby="privacy-who"><div className="card-header"><h2 id="privacy-who">1 · Who and why</h2></div>
          <div className="card-body"><div className="form-grid">
            <div className="field"><span className="lbl">Person</span><div className="segmented plain" role="group" aria-label="Kind of person">
              {(["Party", "User"] as const).map(value => <button type="button" key={value} aria-pressed={kind === value} onClick={() => { setKind(value); setSubjectId(null); invalidate(); }}>{value === "Party" ? "Customer" : "User"}</button>)}</div><span className="help">A user is a staff member or a customer portal user.</span></div>
            {kind === "Party" ? <CustomerField id="privacy-customer" label="Customer" value={subjectId} onChange={value => { setSubjectId(value); invalidate(); }} />
              : <UserPicker value={subjectId} onChange={value => { setSubjectId(value); invalidate(); }} />}
            <label className="field"><span className="lbl">Request reference</span><input className="input serial" value={reference} maxLength={120} onChange={event => { setReference(event.target.value); invalidate(); }} /><span className="help">Your privacy desk's reference. Required.</span></label>
            <label className="field"><span className="lbl">Request received</span><input className="input" type="date" value={received} onChange={event => { setReceived(event.target.value); invalidate(); }} /><span className="help">Answer within one month of this date.</span></label>
            <label className="field span"><span className="lbl">Reason</span><textarea className="textarea" maxLength={1000} value={reason} onChange={event => { setReason(event.target.value); invalidate(); }} /></label>
            {kind === "Party" && <label className="checkrow span"><input type="checkbox" checked={naturalPerson} onChange={event => { setNaturalPerson(event.target.checked); invalidate(); }} /><span><strong>This customer is an individual</strong> (a consumer or sole trader), not a company. A company's own data is not personal data. Its contact people are erased as users.</span></label>}
          </div></div>
          <div className="card-footer"><span>Nothing changes until you confirm in step 3. A refusal is saved with its reason.</span><button type="button" className="btn" disabled={!previewReady || busy} onClick={() => void runPreview()}><Icon name="eye" />Preview</button></div>
        </section>
        {preview && <PreviewSections preview={preview} decisions={decisions} onDecision={(key, action) => setDecisions(value => ({ ...value, [key]: action }))} />}
        {preview && <section className="card" aria-labelledby="privacy-confirm"><div className="card-header"><h2 id="privacy-confirm">3 · Confirm</h2></div>
          <div className="card-body"><div className="note warn"><strong>This cannot be undone.</strong><span>The person's name, contact details and codes are removed from Yojana. Backups keep them until they rotate out; restored backups reapply completed erasures. Erase the person in your source system as well.</span></div>
            <label className="field privacy-confirm"><span className="lbl">Type the request reference to confirm</span><input className="input serial" value={confirmation} onChange={event => setConfirmation(event.target.value)} />
              {confirmation && confirmation !== reference.trim() && <span className="help bad" role="alert">Request reference does not match.</span>}</label>
          </div>
          <div className="card-body"><FormActions><Link className="btn" to="/settings/privacy">Cancel</Link><button type="button" className="btn danger" disabled={busy || !canConfirm(preview, decisions, reference.trim(), confirmation)} onClick={() => void confirm()}><Icon name="shield" />Erase personal data</button></FormActions></div>
        </section>}
      </div>}
  </main>;
}

function PreviewSections({ preview, decisions, onDecision }: { preview: ErasurePreview; decisions: Record<string, ReviewAction | undefined>; onDecision: (key: string, action: ReviewAction) => void }) {
  return <>
    <section className="card" aria-labelledby="privacy-preview"><div className="card-header"><h2 id="privacy-preview">2 · Preview</h2><span className="meta">no changes made</span></div>
      <table className="table"><thead><tr><th>Where</th><th className="num" style={{ width: 90 }}>Found</th><th>What happens</th></tr></thead><tbody>{preview.counts.map(row => <tr key={`${row.store}:${row.kind}`}><td className="primary">{storeLabel(row.store, row.kind)}</td><td className="num">{row.count.toLocaleString()}</td><td>{row.action}</td></tr>)}</tbody></table>
      <div className="card-body"><div className="note"><strong>Kept as anonymous references:</strong><span>Units with their ownership history, warranties, closed claims and closed RMAs. The warranty history carries on. Basis recorded: legal obligation and legal claims.</span></div></div>
    </section>
    <section className="card" aria-labelledby="privacy-review"><div className="card-header"><h2 id="privacy-review">Free text to review</h2><span className="meta">{preview.reviewItems.length} · may mention the person in other words</span></div>
      {preview.reviewItems.length === 0 ? <div className="card-body"><p className="muted">No linked free-text items need a decision.</p></div> : <table className="table"><thead><tr><th style={{ width: 150 }}>Record</th><th>Text</th><th style={{ width: 220 }}>Decision</th></tr></thead><tbody>{preview.reviewItems.map(item => <tr key={item.itemKey}><td className="serial primary">{item.recordReference}</td><td>{item.excerpt ?? item.field}</td><td><div className="segmented plain" role="group" aria-label={`Decision for ${item.recordReference}`}><button type="button" aria-pressed={decisions[item.itemKey] === "Redact"} onClick={() => onDecision(item.itemKey, "Redact")}>Redact</button><button type="button" aria-pressed={decisions[item.itemKey] === "Keep"} onClick={() => onDecision(item.itemKey, "Keep")}>Keep</button></div></td></tr>)}</tbody></table>}
      <div className="card-body"><p className="muted">{preview.accuracyNote} Redacted text reads "[Personal data redacted]".</p></div>
    </section>
  </>;
}

function RefusedPreview({ refusal, reference, onBack, onAgain, busy }: { refusal: Refusal; reference: string; onBack: () => void; onAgain: () => void; busy: boolean }) {
  return <section className="card" aria-labelledby="privacy-refused"><div className="card-header"><h2 id="privacy-refused">2 · Preview</h2><span className="meta">{reference}</span></div>
    <div className="card-body"><div className="note bad"><strong>Can't erase yet.</strong><span>{refusal.message} {refusal.unblockingStep ? `${refusal.unblockingStep.charAt(0).toUpperCase()}${refusal.unblockingStep.slice(1)}.` : ""} The request is saved as refused, with this reason.</span></div></div>
    {refusal.blockers.length > 0 && <table className="table"><thead><tr><th style={{ width: 150 }}>Record</th><th>What is open</th><th style={{ width: 180 }}>Status</th><th style={{ width: 100 }} /></tr></thead><tbody>{refusal.blockers.map(blocker => { const path = blockerPath(blocker); return <tr key={`${blocker.kind}:${blocker.recordId ?? blocker.reference}`}><td className="serial primary">{blocker.reference}</td><td>{blocker.detail}</td><td><span className="badge warn">{blocker.status.replace(/_/g, " ")}</span></td><td>{path && <Link className="btn sm" to={path}>Open</Link>}</td></tr>; })}</tbody></table>}
    <div className="card-body"><p className="muted">Live warranties do not block an erasure. Only open claims, RMAs, replacement holds and unreturned replacements do.</p></div>
    <div className="card-footer"><button type="button" className="btn ghost" onClick={onBack}>Back to Privacy</button><button type="button" className="btn" disabled={busy} onClick={onAgain}><Icon name="cycle" />Preview again</button></div>
  </section>;
}

function UserPicker({ value, onChange }: { value: string | null; onChange: (value: string | null) => void }) {
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<TenantUserRow[]>([]);
  const selected = useMemo(() => results.find(row => row.userId === value), [results, value]);
  useEffect(() => {
    if (value || query.trim().length < 2) { if (!value) setResults([]); return; }
    const controller = new AbortController();
    const timer = window.setTimeout(() => void userManagementApi.page({ status: "all", q: query.trim(), take: 12 }, controller.signal)
      .then(page => setResults(page.items.filter(row => row.userId !== null))).catch(reason => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setResults([]); }), 180);
    return () => { window.clearTimeout(timer); controller.abort(); };
  }, [query, value]);
  return <div className="field reference-field"><label htmlFor="privacy-user">User</label>{value ? <div className="reference-selected"><span>{selected ? `${selected.displayName} · ${selected.email}` : "Selected user"}</span><button type="button" className="reference-clear" onClick={() => { onChange(null); setQuery(""); }}>Clear</button></div> : <div className="reference-combobox"><input id="privacy-user" className="input" value={query} placeholder="Search user name or e-mail" onChange={event => setQuery(event.target.value)} />{results.length > 0 && <div className="reference-options" role="listbox">{results.map(row => <button type="button" role="option" aria-selected="false" className="reference-option" key={row.userId!} onClick={() => onChange(row.userId)}><span>{row.displayName}</span><span>{row.email} · {row.status}</span></button>)}</div>}</div>}<span className="help">The user must be deactivated before erasure.</span></div>;
}
