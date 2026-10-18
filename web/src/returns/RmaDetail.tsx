import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router";
import { ActionDialog, PresenceAdvisory, PresenceChips, StatusBadge, useDocumentPresence } from "../components";
import { PartyField, useReferenceNames } from "../corrections/ReferenceFields";
import { usePeople } from "../people/people";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, UnitLink } from "../units/common";
import { fmtDate, fmtDateTime } from "../units/format";
import { returnsApi } from "./api";
import { dispositionOptionState, humanise, receiptTone, stageNumber, stageTone } from "./format";
import type { RmaDetail, RmaDetailLine } from "./types";
import "./returns.css";

const DISPOSITIONS = [
  ["refurbish", "opens a work order against this unit"], ["repair_and_return", "entitlement stays · parts get their own cover"],
  ["return_to_stock", "choose a grade · inherited cover closes"], ["return_to_vendor", "ownership and custody pass to the vendor"],
  ["credit", "closes the entitlement refunded · no link"], ["scrap", "sanitise first where the item class requires it"],
  ["replace", "creates the replacement intent"], ["restock_as_new", "seller error and unused stock only"],
  ["reject", "return to the customer as received"],
] as const;

export function Component() {
  const { rmaId = "" } = useParams();
  const [detail, setDetail] = useState<RmaDetail | null>(null);
  const [choices, setChoices] = useState<Record<string, string>>({});
  const [vendors, setVendors] = useState<Record<string, string | null>>({});
  const [dialog, setDialog] = useState<"dispatch" | "close" | null>(null);
  const [carrierName, setCarrierName] = useState("");
  const [trackingReference, setTrackingReference] = useState("");
  const [dispatchedAt, setDispatchedAt] = useState(toLocalInput(new Date()));
  const [closeFollowUps, setCloseFollowUps] = useState<RmaDetail["openFollowUps"]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const load = useCallback((signal?: AbortSignal) => returnsApi.detail(rmaId, signal).then((next) => {
    setDetail(next); setChoices(Object.fromEntries(next.lines.map((line) => [line.rmaLineId, line.disposition ?? defaultDisposition(line)]))); setVendors({});
  }), [rmaId]);
  useEffect(() => { const controller = new AbortController(); setError(null); void load(controller.signal).catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); }); return () => controller.abort(); }, [load]);
  const authorise = async () => { setBusy(true); setError(null); try { await returnsApi.authorise(rmaId); await load(); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  const dispatch = async () => {
    if (!dispatchedAt) return;
    setBusy(true); setError(null);
    try {
      await returnsApi.dispatch(rmaId, { dispatchedAt: new Date(dispatchedAt).toISOString(), carrierName: carrierName.trim() || null, trackingReference: trackingReference.trim() || null });
      setDialog(null); await load();
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const disposition = async () => {
    if (!detail) return;
    setBusy(true); setError(null);
    try {
      await returnsApi.disposition(rmaId, detail.lines.map((line) => ({ rmaLineId: line.rmaLineId, disposition: choices[line.rmaLineId] ?? defaultDisposition(line), vendorPartyRef: choices[line.rmaLineId] === "return_to_vendor" ? vendors[line.rmaLineId] ?? null : null })));
      await load();
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const close = async () => {
    setBusy(true); setError(null);
    try {
      const result = await returnsApi.close(rmaId);
      setCloseFollowUps(result.openFollowUps);
      if (result.closed) setDialog(null);
      await load();
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const names = useReferenceNames({
    items: detail?.lines.flatMap((line) => [line.requestedItemId, line.receivedItemId]) ?? [], parties: detail ? [detail.returningPartyRef, detail.settlementPartyRef, detail.serviceEntityId] : [],
    documents: detail?.lines.map((line) => line.originalSaleRef) ?? [],
    locations: detail?.lines.map((line) => line.receivingLocationId) ?? [],
  });
  const people = usePeople(detail ? [detail.requestedBy, detail.lastActorRef, detail.dispatch?.dispatchedBy, ...detail.lines.map((line) => line.receivedBy)] : []);
  const canDisposition = useMemo(() => !!detail && detail.lines.every((line) => {
    const choice = choices[line.rmaLineId] ?? defaultDisposition(line);
    return dispositionOptionState(choice, line, detail.stage).enabled && (choice !== "return_to_vendor" || !!vendors[line.rmaLineId]);
  }), [choices, detail, vendors]);
  const rmaEditing = dialog !== null || (!!detail && detail.lines.some((line) =>
    (choices[line.rmaLineId] ?? defaultDisposition(line)) !== (line.disposition ?? defaultDisposition(line))))
    || Object.values(vendors).some((vendor) => vendor !== null);
  const presence = useDocumentPresence("rma", rmaId, rmaEditing);
  if (!detail) return <main className="content" id="main" tabIndex={-1}><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Returns", to: "/returns" }, { label: "Return" }]} /><p className="muted">Loading return…</p><ErrorNote error={error} /></main>;
  const primary = detail.lines[0];
  const exceptions = detail.lines.filter((line) => ["mismatch", "unauthorised", "counterfeit"].includes(line.receiptOutcome ?? ""));
  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Returns", to: "/returns" }, { label: detail.rmaNumber ?? "Return request" }]} />
      <div className="page-header"><div><div className="eyebrow">{detail.rmaNumber ?? "Return request"} · stage {stageNumber(detail.stage)} of 6 · {humanise(detail.stage)} · requested {fmtDate(detail.requestedAt)} by {people(detail.requestedBy)}</div><h1>{primary?.authorisedSerial ?? primary?.requestedSerial ?? "Return awaiting unit"}{primary?.description ? ` · ${primary.description}` : ""}</h1><p className="sub">{names.party(detail.returningPartyRef)} owns the returned unit · the original sale is resolved from its ownership episode and latest shipment, never chosen by hand</p><PresenceChips users={presence} /></div><div className="actions">
        {detail.stage === "requested" && <button type="button" className="btn primary" disabled={busy} onClick={() => void authorise()}><Icon name="check" />Authorise return</button>}
        {detail.stage === "authorised" && <button type="button" className="btn" disabled={busy} onClick={() => { setCloseFollowUps([]); setDialog("dispatch"); }}><Icon name="returns" />Record dispatch</button>}
        {["authorised", "in_transit"].includes(detail.stage) && <Link className="btn primary" to={`/returns/receipt?rmaId=${detail.rmaId}`}><Icon name="scan" />Receive return</Link>}
        {detail.stage === "received" && <button type="button" className="btn primary" disabled={busy || !canDisposition} title={!canDisposition ? "Choose an enabled disposition for every line and any required vendor" : undefined} onClick={() => void disposition()}><Icon name="check" />Disposition {detail.lines.length === 1 ? "line" : `${detail.lines.length} lines`}</button>}
        {detail.stage === "dispositioned" && <button type="button" className="btn primary" disabled={busy} onClick={() => { setCloseFollowUps(detail.openFollowUps); setDialog("close"); }}><Icon name="check" />Close RMA</button>}
      </div></div>
      <PresenceAdvisory users={presence} editing={rmaEditing} documentLabel="RMA" />
      <div className="cols-3-2 returns-document-layout">
        <div className="stack">
          <section className="card" aria-labelledby="rma-lines-heading"><div className="card-header"><h2 id="rma-lines-heading">Lines <span className="meta">{detail.lines.length} · {detail.lines.filter((line) => line.receiptOutcome === "accepted").length} matched · {exceptions.length} held</span></h2></div><table className="table"><colgroup><col style={{ width: "7%" }} /><col style={{ width: "27%" }} /><col style={{ width: "27%" }} /><col style={{ width: "21%" }} /><col style={{ width: "18%" }} /></colgroup><thead><tr><th>#</th><th>Authorised unit</th><th>Coverage decision</th><th>Received</th><th>Disposition</th></tr></thead><tbody>{detail.lines.map((line) => <tr key={line.rmaLineId}><td>{line.sequence}</td><td>{line.authorisedUnitId ? <UnitLink unitId={line.authorisedUnitId}>{line.authorisedSerial ?? "No serial recorded"}</UnitLink> : <span className="muted">Awaiting authorisation</span>}<span className="sub">{names.item(line.requestedItemId)}</span></td><td>{line.coveragePct == null ? <span className="muted">Pending</span> : <>{line.coveragePct}% · {humanise(line.coverageSource)}<span className="sub">{line.denialReason ? humanise(line.denialReason) : `${detail.currency} ${(line.approvedAmount ?? 0).toLocaleString()}`}</span></>}</td><td>{line.receiptOutcome ? <><StatusBadge tone={receiptTone(line.receiptOutcome)}>{humanise(line.receiptOutcome)}</StatusBadge><span className="sub serial">{line.receivedSerial ?? (line.provisionalUnitId ? "Provisional identity" : "No serial")}</span></> : <span className="muted">Not received</span>}</td><td>{line.disposition ? <StatusBadge tone="ok">{humanise(line.disposition)}</StatusBadge> : detail.stage === "received" ? <StatusBadge tone="info">Choosing</StatusBadge> : <span className="muted">Not ready</span>}</td></tr>)}</tbody></table><div className="card-footer"><span>Original sale {primary ? names.document(primary.originalSaleRef) : "—"} · {names.party(detail.returningPartyRef)}</span></div></section>
          {exceptions.map((line) => <MismatchPanel key={line.rmaLineId} line={line} names={names} />)}
          {detail.lines.map((line) => <section className="card" key={line.rmaLineId} aria-labelledby={`disposition-${line.rmaLineId}`}><div className="card-header"><h2 id={`disposition-${line.rmaLineId}`}>Line {line.sequence} · disposition</h2><span className="meta">grade {humanise(line.conditionGrade)} · custody quarantine</span></div><div className="card-body"><div className="returns-options" role="radiogroup" aria-label={`Disposition for line ${line.sequence}`}>
            {DISPOSITIONS.map(([value, effect]) => { const state = dispositionOptionState(value, line, detail.stage); const selected = choices[line.rmaLineId] === value; return <label className={`returns-option${selected ? " on" : ""}${state.enabled ? "" : " off"}`} key={value} title={state.reason ?? undefined}><input type="radio" name={`disposition-${line.rmaLineId}`} value={value} disabled={!state.enabled || !!line.disposition} checked={selected} onChange={() => setChoices((current) => ({ ...current, [line.rmaLineId]: value }))} /><span><span className="returns-option-title">{humanise(value)}</span><span className="s">{state.reason ?? effect}</span></span></label>; })}
          </div>{choices[line.rmaLineId] === "return_to_vendor" && !line.disposition && <div style={{ marginTop: 14 }}><PartyField id={`vendor-${line.rmaLineId}`} label="Vendor" value={vendors[line.rmaLineId] ?? null} onChange={(vendor) => setVendors((current) => ({ ...current, [line.rmaLineId]: vendor }))} hint="Required for the vendor custody and ownership move" /></div>}
          {line.replacementIntentId && line.entitlementId && <div className="note info returns-card-note"><strong>Replacement intent recorded.</strong>&nbsp;Create the warranty replacement document to pick the new unit and preview the entitlement transfer. <Link to={`/replacements/new?sourceKind=rma&sourceId=${detail.rmaId}&sourceEntitlementId=${line.entitlementId}`}>Create replacement</Link></div>}</div></section>)}
          <ErrorNote error={error} onReload={() => { void load(); }} />
        </div>
        <div className="stack">
          <section className="card"><div className="card-header"><h2>Stages</h2><StatusBadge tone={stageTone(detail.stage)}>{stageNumber(detail.stage)} of 6</StatusBadge></div><div className="card-body"><ol className="returns-checks">{[
            ["Requested", detail.requestedAt], ["Authorised", detail.authorisedAt], ["In transit", detail.inTransitAt], ["Received", detail.receivedAt], ["Dispositioned", detail.dispositionedAt], ["Closed", detail.closedAt],
          ].map(([label, at], index) => <li key={label}><span>{label}<span className="s">{at ? fmtDateTime(at) : "waiting"}</span></span><StatusBadge tone={at ? "ok" : index + 1 === stageNumber(detail.stage) ? "info" : "neutral"}>{at ? "Recorded" : index + 1 === stageNumber(detail.stage) ? "Current" : "Pending"}</StatusBadge></li>)}</ol>{detail.dispatch && <div className="note info returns-card-note"><strong>Dispatch recorded.</strong>&nbsp;{detail.dispatch.carrierName ?? "Carrier not recorded"}{detail.dispatch.trackingReference ? ` · ${detail.dispatch.trackingReference}` : ""} · by {people(detail.dispatch.dispatchedBy)}</div>}</div></section>
          {detail.lines.filter((line) => line.receiptBusinessTime).map((line) => <section className="card" key={line.rmaLineId}><div className="card-header"><h2>Receipt · line {line.sequence}</h2></div><div className="card-body"><Kv rows={[["Received unit", line.receivedUnitId ? <UnitLink unitId={line.receivedUnitId}>{line.receivedSerial ?? "Open unit"}</UnitLink> : "Provisional identity"], ["Grade", `${humanise(line.conditionGrade)} · ${humanise(line.valuationRule)}`], ["Outcome", humanise(line.receiptOutcome)], ["Location", names.location(line.receivingLocationId)], ["Business time", fmtDateTime(line.receiptBusinessTime)], ["Posting period", line.postingPeriod ?? "—"], ["Received by", people(line.receivedBy)]]} /></div></section>)}
        </div>
      </div>
    </main>
    <Rail label="Return"><Section title="Return"><Kv rows={[["Number", detail.rmaNumber ?? "Awaiting authorisation"], ["Customer", names.party(detail.returningPartyRef)], ["Stage", humanise(detail.stage)], ["Country", detail.serviceCountry], ["Currency", detail.currency]]} /></Section></Rail>
    {dialog === "dispatch" && <ActionDialog title="Record return dispatch" onCancel={() => setDialog(null)} actions={<button type="button" className="btn primary" disabled={busy || !dispatchedAt} onClick={() => void dispatch()}>{busy ? "Recording…" : "Record dispatch"}</button>}><div className="returns-inline-fields"><label className="field"><span className="lbl">Carrier · optional</span><input className="input" value={carrierName} maxLength={160} onChange={(event) => setCarrierName(event.target.value)} /></label><label className="field"><span className="lbl">Tracking reference · optional</span><input className="input" value={trackingReference} maxLength={160} onChange={(event) => setTrackingReference(event.target.value)} /></label><label className="field"><span className="lbl">Dispatched at</span><input className="input" type="datetime-local" value={dispatchedAt} onChange={(event) => setDispatchedAt(event.target.value)} /></label></div><p className="muted">Custody moves to returning at this business time. The customer remains the owner.</p><ErrorNote error={error} /></ActionDialog>}
    {dialog === "close" && <ActionDialog title="Close RMA" onCancel={() => setDialog(null)} actions={<button type="button" className="btn primary" disabled={busy} onClick={() => void close()}>{busy ? "Checking…" : "Close RMA"}</button>}>{closeFollowUps.length ? <><p>The RMA cannot close until every follow-up is complete.</p><ol className="returns-checks">{closeFollowUps.map((followUp) => <li key={followUp.rmaLineId}><span>Line {followUp.sequence} · {humanise(followUp.disposition)}<span className="s">{followUp.detail}</span></span><StatusBadge tone="warn">Open</StatusBadge></li>)}</ol></> : <p>Every recorded follow-up appears complete. The server will verify the current replacement, refurbishment, entitlement and ownership facts before closing.</p>}<ErrorNote error={error} /></ActionDialog>}
  </>;
}

function defaultDisposition(line: RmaDetailLine): string { return line.conditionGrade === "defective" ? "refurbish" : "return_to_stock"; }

function MismatchPanel({ line, names }: { line: RmaDetailLine; names: ReturnType<typeof useReferenceNames> }) {
  return <section className="card" aria-labelledby={`mismatch-${line.rmaLineId}`}><div className="card-header"><h2 id={`mismatch-${line.rmaLineId}`}>Line {line.sequence} · received unit is not the authorised unit</h2><StatusBadge tone="bad">Held in quarantine</StatusBadge></div><div className="card-body"><div className="returns-pair"><div className="returns-pair-side"><div className="returns-pair-label">Authorised</div><div className="serial">{line.authorisedSerial ?? "No serial recorded"}</div><div className="sub muted">{names.item(line.requestedItemId)}</div></div><div className="returns-pair-arrow" aria-hidden="true">≠</div><div className="returns-pair-side bad"><div className="returns-pair-label">Received · {fmtDateTime(line.receiptBusinessTime)}</div><div className="serial">{line.receivedSerial ?? "Provisional identity"}</div><div className="sub muted">{names.item(line.receivedItemId)}{line.exceptionNote ? ` · ${line.exceptionNote}` : ""}</div></div></div><div className="returns-options" style={{ marginTop: 12 }} aria-label="Mismatch resolution choices">{[["Amend to the received unit", "confirm ownership and cover before binding"], ["Reject and return", "ship it back and request the authorised unit"], ["Treat as unauthorised", "no claim attaches · separate receipt"]].map(([label, reason]) => <div className="returns-option off" key={label}><span><span className="returns-option-title">{label}</span><span className="s">{reason} · use the correction workbench</span></span></div>)}</div></div></section>;
}

function toLocalInput(value: Date): string {
  const local = new Date(value.getTime() - value.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}
