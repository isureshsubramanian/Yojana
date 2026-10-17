import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import { Link, useParams } from "react-router";
import { StatusBadge } from "../components";
import { useReferenceNames } from "../corrections/ReferenceFields";
import { usePeople } from "../people/people";
import { noRecoveryExplanation } from "../recovery/format";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { openDrawer } from "../shell/drawerStore";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, UnitLink } from "../units/common";
import { fmtDate, fmtDateTime } from "../units/format";
import { claimsApi } from "./api";
import { checkLabel, checkValue, claimTone, humanise, money, stackOutcome } from "./format";
import type { ClaimAdjudicationPreview, ClaimDetail, ClaimLinePreview, CurrencyInfo } from "./types";
import "./claims.css";

type DialogState = "evidence" | "deny" | null;
const OPEN = new Set(["submitted", "adjudicating"]);

export function Component() {
  const { claimId = "" } = useParams();
  const [detail, setDetail] = useState<ClaimDetail | null>(null);
  const [preview, setPreview] = useState<ClaimAdjudicationPreview | null>(null);
  const [catalogue, setCatalogue] = useState<CurrencyInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [dialog, setDialog] = useState<DialogState>(null);
  const [note, setNote] = useState("");
  const [denialReason, setDenialReason] = useState("");

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoading(true); setError(null);
    try {
      const [nextDetail, nextPreview, nextCatalogue] = await Promise.all([claimsApi.detail(claimId, signal), claimsApi.preview(claimId, signal), claimsApi.currencyCatalogue(signal)]);
      setDetail(nextDetail); setPreview(nextPreview); setCatalogue(nextCatalogue);
      setDenialReason(nextPreview.manualDenialReasons[0] ?? "");
    } catch (reason) { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); } finally { setLoading(false); }
  }, [claimId]);
  useEffect(() => { const controller = new AbortController(); void load(controller.signal); return () => controller.abort(); }, [load]);
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => { if ((event.metaKey || event.ctrlKey) && event.key === "Enter" && detail && preview && OPEN.has(detail.status) && !preview.refusal) { event.preventDefault(); void approve(); } };
    window.addEventListener("keydown", onKey); return () => window.removeEventListener("keydown", onKey);
  });

  const partyRefs = detail ? [detail.claimantPartyRef, detail.settlementPartyRef, detail.serviceEntityId, detail.obligorEntityId, ...(detail.recoveries ?? []).map((row) => row.vendorPartyRef), ...(preview?.recoveries ?? []).map((row) => row.vendorPartyRef)] : [];
  const itemRefs = detail ? [detail.itemId, ...(preview?.lines ?? []).map((line) => line.itemId), preview?.successorItemId] : [];
  const names = useReferenceNames({ parties: partyRefs, items: itemRefs });
  const actorRefs = detail ? [detail.submittedBy, detail.adjudicatedBy, ...detail.evidence.map((row) => row.actorRef)] : [];
  const person = usePeople(actorRefs);
  const decided = detail ? !OPEN.has(detail.status) : false;
  const approvedLines = preview?.lines.filter((line) => line.approvedAmount > 0).length ?? 0;
  const firstStack = preview?.lines.find((line) => line.stack.length)?.stack ?? [];

  const act = async (action: () => Promise<unknown>) => {
    setBusy(true); setError(null);
    try { await action(); setDialog(null); setNote(""); await load(); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const approve = () => preview?.refusal ? Promise.resolve() : act(() => claimsApi.adjudicate(claimId));
  const requestEvidence = () => act(() => claimsApi.requestEvidence(claimId, note.trim()));
  const deny = () => act(() => claimsApi.adjudicate(claimId, denialReason, note.trim()));
  const change = (kind: "settle" | "close") => act(() => kind === "settle" ? claimsApi.settle(claimId) : claimsApi.close(claimId));

  if (loading && !detail) return <main className="content"><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Claims", to: "/claims" }, { label: "Loading…" }]} /><p className="muted">Loading claim…</p></main>;
  if (!detail || !preview) return <main className="content"><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Claims", to: "/claims" }, { label: "Claim" }]} /><ErrorNote error={error ?? new Error("Claim not found.")} /></main>;

  const fault = preview.lines[0]?.description ?? humanise(preview.lines[0]?.kind) ?? "Claim";
  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Claims", to: "/claims" }, { label: detail.claimNumber }]} />
      <div className="page-header"><div><div className="eyebrow">Claim {detail.claimNumber} · {humanise(decided ? detail.coverageSource : preview.coverageSource)} · {humanise(detail.status)} · submitted {fmtDate(detail.submittedAt)} by {person(detail.submittedBy)}</div><h1>{detail.serial ?? "No serial recorded"} · {fault}</h1><p className="sub">{names.party(detail.claimantPartyRef)} claims, {names.party(detail.settlementPartyRef)} settles · each line is decided against the policy stack on the claim date{decided ? " · recorded decisions are read-only" : " · approving publishes Claim.Adjudicated"}</p></div><div className="actions">
        {!decided && <><button type="button" className="btn" onClick={() => { setNote(""); setDialog("evidence"); }}>Ask for evidence</button><button type="button" className="btn" onClick={() => { setNote(""); setDialog("deny"); }}>Deny…</button><button type="button" className="btn primary" disabled={busy || !!preview.refusal} title={preview.refusal ? humanise(preview.refusal) : undefined} onClick={() => void approve()}><Icon name="check" />Approve {approvedLines} of {preview.lines.length} lines<kbd className="key">⌘↵</kbd></button></>}
        {["approved", "partially_approved", "denied"].includes(detail.status) && <button type="button" className="btn primary" disabled={busy} onClick={() => void change("settle")}>Settle claim</button>}
        {detail.status === "settled" && <button type="button" className="btn primary" disabled={busy} onClick={() => void change("close")}>Close claim</button>}
      </div></div>
      <ErrorNote error={error} />
      {preview.refusal && <div className="note bad" style={{ marginBottom: 16 }}><strong>Approval refused · {humanise(preview.refusal)}.</strong>&nbsp;{preview.refusalStep ? `To unblock: ${preview.refusalStep}.` : "Review the claim lines before approving."}</div>}
      {preview.requiredReviewerRole && !decided && <div className="note warn" style={{ marginBottom: 16 }}><strong>Reviewer role required.</strong>&nbsp;Approval must be made by a user with the {humanise(preview.requiredReviewerRole).toLowerCase()} role because this claim uses low-confidence cover.</div>}
      <div className="cols-3-2">
        <div className="stack">
          <section className="card" aria-labelledby="decision-lines"><div className="card-header"><h2 id="decision-lines">Decision per line <span className="meta">{humanise(preview.outcome).toLowerCase()} · {money(preview.approvedAmount, preview.currency, catalogue)} of {money(preview.requestedAmount, preview.currency, catalogue)}</span></h2></div><div className="grid-viewport"><table className="table"><colgroup><col style={{ width: "23%" }} /><col style={{ width: "20%" }} /><col style={{ width: "12%" }} /><col style={{ width: "14%" }} /><col style={{ width: "14%" }} /><col style={{ width: "17%" }} /></colgroup><thead><tr><th>Line</th><th>Covered by</th><th className="num">Coverage</th><th className="num">Requested</th><th className="num">Approved</th><th>Decision</th></tr></thead><tbody>{preview.lines.map((line) => <tr key={line.claimLineId} className="clickable" tabIndex={0} onClick={() => openLine(line, names.item(line.itemId))} onKeyDown={(event) => { if (event.key === "Enter") openLine(line, names.item(line.itemId)); }}>
            <td className="primary"><span className="t">{line.description ?? humanise(line.kind)}</span><span className="sub">{humanise(line.kind)}{line.isChild ? ` · child ${line.serial ?? names.item(line.itemId)}` : " · root unit"}</span></td>
            <td>{line.policyCode ? `${line.policyCode}${line.policyVersion == null ? "" : ` v${line.policyVersion}`}` : "No policy matched"}<span className="sub">{humanise(line.level)} · {humanise(line.coverageFamily)}</span></td>
            <td className="num">{line.coveragePct} %</td><td className="num">{money(line.requestedAmount, preview.currency, catalogue)}</td><td className="num">{money(line.approvedAmount, preview.currency, catalogue)}</td>
            <td><StatusBadge tone={line.approvedAmount > 0 ? "ok" : line.denialReason ? "warn" : "neutral"}>{line.approvedAmount > 0 ? "Covered" : line.coverageSource === "chargeable" ? "Chargeable" : "Not covered"}</StatusBadge>{line.denialReason && <span className="sub">{humanise(line.denialReason)}</span>}</td>
          </tr>)}</tbody></table></div><div className="card-footer"><span>Settlement formula <span className="serial">full</span> · pro-rata formulas arrive later · coverage percentages come from the policy version effective for the claim</span></div></section>
          <section className="card claims-stack" aria-labelledby="policy-stack"><div className="card-header"><h2 id="policy-stack">Policy stack on {detail.claimDate}</h2><span className="meta">first match wins · ties by priority, then latest start</span></div><div className="card-body">{firstStack.length ? firstStack.map((step) => <div className="job" key={`${step.level}-${step.outcome}`}><div><div className="t">{humanise(step.level)}</div><div className="s">{step.detail || humanise(step.outcome)}</div></div><StatusBadge tone={claimTone(step.outcome)}>{stackOutcome(step)}</StatusBadge></div>) : <p className="muted">No policy-stack trail was recorded for the first line.</p>}</div></section>
          <section className="card" aria-labelledby="claim-record"><div className="card-header"><h2 id="claim-record">{decided ? "Recorded on adjudication" : "Will be recorded on approve"}</h2><span className="meta">one transaction · outbox</span></div><table className="table dense"><colgroup><col style={{ width: "30%" }} /><col style={{ width: "58%" }} /><col style={{ width: "12%" }} /></colgroup><tbody>
            <tr><td className="primary"><span className="t">Claim.Adjudicated</span></td><td>{preview.cohortId ? "one accrual cohort" : "no accrual cohort"} · {humanise(preview.coverageSource).toLowerCase()} · {money(preview.approvedAmount, preview.currency, catalogue)} by line · service {names.party(detail.serviceEntityId)} · obligor {names.party(preview.obligorEntityId)}</td><td className="num">1</td></tr>
            {preview.recoveries.map((recovery) => <tr key={`${recovery.childUnitId}-${recovery.vendorPartyRef}`}><td className="primary"><span className="t">RecoveryClaim.Raised</span></td><td>{names.party(recovery.vendorPartyRef)} · child {recovery.childSerial ?? "serial unavailable"} · requested {money(recovery.requestedAmount, preview.currency, catalogue)}{recovery.monthOfTerm && recovery.termMonths ? ` · month ${recovery.monthOfTerm} of ${recovery.termMonths}` : ""}</td><td className="num">1</td></tr>)}
            {preview.recoveryNotRaisedReason && <tr><td className="primary"><span className="t">No recovery</span></td><td title={noRecoveryExplanation(preview.recoveryNotRaisedReason)}>{noRecoveryExplanation(preview.recoveryNotRaisedReason)}</td><td className="num">—</td></tr>}
            {preview.chargeableAmount > 0 && <tr><td className="primary"><span className="t">Chargeable quote request</span></td><td>{money(preview.chargeableAmount, preview.currency, catalogue)} · to {names.party(detail.settlementPartyRef)}</td><td className="num">1</td></tr>}
          </tbody></table></section>
        </div>
        <div className="stack">
          <section className="card" aria-labelledby="claim-checks"><div className="card-header"><h2 id="claim-checks">Checks</h2></div><table className="table dense"><colgroup><col style={{ width: "58%" }} /><col style={{ width: "42%" }} /></colgroup><tbody>{preview.checks.map((check) => <tr key={check.key}><td>{checkLabel(check.key, check.value)}</td><td><StatusBadge tone={claimTone(check.outcome)}>{checkValue(check.key, check.value)}</StatusBadge></td></tr>)}</tbody></table></section>
          <section className="card" aria-labelledby="claim-remedy"><div className="card-header"><h2 id="claim-remedy">Remedy</h2><span className="meta">consumer’s informed choice recorded</span></div><div className="card-body"><div className="claims-remedy">{["repair", "replacement"].map((value) => <label key={value} className={detail.remedy === value ? "on" : ""}><input type="radio" disabled checked={detail.remedy === value} readOnly /><span>{humanise(value)}<span className="help">{value === "repair" ? "Repair the claimed unit" : "Replace the claimed unit"}</span></span></label>)}</div></div></section>
          <section className="card" aria-labelledby="claim-entities"><div className="card-header"><h2 id="claim-entities">Entities</h2></div><div className="card-body"><Kv rows={[["Service entity", names.party(detail.serviceEntityId)], ["Obligor entity", names.party(detail.obligorEntityId)], ["Recharge", detail.serviceEntityId === detail.obligorEntityId ? "Same entity" : "Entities differ · Finance recharges from the event"]]} /></div></section>
          <section className="card" aria-labelledby="claim-evidence"><div className="card-header"><h2 id="claim-evidence">Evidence</h2><span className="meta">{detail.evidence.length} items</span></div><div className="card-body">{detail.evidence.length ? detail.evidence.map((item) => <div className="job" key={item.evidenceId}><div><div className="t">{item.reference}</div><div className="s">{humanise(item.kind)}{item.description ? ` · ${item.description}` : ""} · {fmtDateTime(item.recordedAt)} by {person(item.actorRef)}</div></div>{item.kind === "evidence_requested" ? <StatusBadge tone="warn">Requested</StatusBadge> : <StatusBadge tone="ok">Recorded</StatusBadge>}</div>) : <p className="muted">No evidence is recorded.</p>}</div></section>
          {detail.recoveries?.length ? <section className="card" aria-labelledby="claim-recovery"><div className="card-header"><h2 id="claim-recovery">Recovery claims</h2></div><div className="card-body">{detail.recoveries.map((row) => <div className="job" key={row.recoveryClaimId}><div><div className="t"><Link to={`/claims/recovery?claim=${encodeURIComponent(row.recoveryNumber)}`}>{row.recoveryNumber}</Link></div><div className="s">{names.party(row.vendorPartyRef)}</div></div><StatusBadge tone={claimTone(row.status)}>{humanise(row.status)}</StatusBadge></div>)}</div></section> : null}
        </div>
      </div>
    </main>
    <Rail label="Claim"><Section title="Claim"><Kv rows={[["Number", detail.claimNumber], ["Status", humanise(detail.status)], ["Submitted", fmtDateTime(detail.submittedAt)], ["Adjudicated", fmtDateTime(detail.adjudicatedAt)], ["Reviewer", person(detail.adjudicatedBy)], ["Reviewer role", humanise(detail.reviewerRole)]]} /></Section></Rail>
    {dialog === "evidence" && <DecisionDialog title="Ask for evidence" onCancel={() => setDialog(null)} actions={<button type="button" className="btn primary" disabled={busy || !note.trim()} onClick={() => void requestEvidence()}>Send request</button>}><div className="field"><label htmlFor="evidence-note">What evidence is needed?</label><textarea id="evidence-note" className="textarea" autoFocus value={note} onChange={(event) => setNote(event.target.value)} /><span className="help">The note is recorded on the claim and shown as evidence requested.</span></div></DecisionDialog>}
    {dialog === "deny" && <DecisionDialog title="Deny claim" onCancel={() => setDialog(null)} actions={<button type="button" className="btn primary" disabled={busy || !denialReason || !note.trim()} onClick={() => void deny()}>Deny claim</button>}><div className="field"><label htmlFor="denial-reason">Manual denial reason</label><select id="denial-reason" className="select" value={denialReason} onChange={(event) => setDenialReason(event.target.value)}>{preview.manualDenialReasons.map((reason) => <option key={reason} value={reason}>{humanise(reason)}</option>)}</select></div><div className="field"><label htmlFor="denial-note">Decision note</label><textarea id="denial-note" className="textarea" autoFocus value={note} onChange={(event) => setNote(event.target.value)} /><span className="help">Required. Policy-stack reasons are decided by the server and cannot be selected manually.</span></div></DecisionDialog>}
  </>;
}

function DecisionDialog({ title, children, actions, onCancel }: { title: string; children: ReactNode; actions: ReactNode; onCancel: () => void }) {
  return <div className="claims-modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onCancel(); }}><section className="claims-modal" role="dialog" aria-modal="true" aria-labelledby="claim-dialog-title"><div className="card-header"><h2 id="claim-dialog-title">{title}</h2><button type="button" className="icon-btn" aria-label="Close" onClick={onCancel}><Icon name="x" /></button></div><div className="card-body">{children}<div className="actions"><button type="button" className="btn" onClick={onCancel}>Cancel</button>{actions}</div></div></section></div>;
}

function openLine(line: ClaimLinePreview, itemName: string) {
  openDrawer({ eyebrow: "Line decision", title: line.description ?? humanise(line.kind) }, <><h3>Why this decision</h3><Kv rows={[
    ["Unit", <><UnitLink unitId={line.unitId}>{line.serial ?? "Open unit"}</UnitLink> · {itemName}</>], ["Level", humanise(line.level)], ["Policy", line.policyCode ? `${line.policyCode}${line.policyVersion == null ? "" : ` v${line.policyVersion}`}` : "No match"],
    ["Term", line.coverStart || line.coverEnd ? `${line.coverStart ?? "open"} → ${line.coverEnd ?? "open"}` : "—"], ["Coverage", `${line.coveragePct} %`], ["Denial reason", humanise(line.denialReason)],
  ]} /><h3 style={{ marginTop: 24 }}>Policy trail</h3>{line.stack.map((step) => <div className="job" key={`${step.level}-${step.outcome}`}><div><div className="t">{humanise(step.level)}</div><div className="s">{step.detail || humanise(step.outcome)}</div></div><StatusBadge tone={claimTone(step.outcome)}>{stackOutcome(step)}</StatusBadge></div>)}</>);
}
