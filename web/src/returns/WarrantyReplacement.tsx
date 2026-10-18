import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router";
import { StatusBadge } from "../components";
import { UnitField, useReferenceNames } from "../corrections/ReferenceFields";
import { claimsApi } from "../claims/api";
import { usePeople } from "../people/people";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, UnitLink } from "../units/common";
import { fmtDate, fmtDateTime } from "../units/format";
import { replacementApi, returnsApi } from "./api";
import { PRICE_POLICIES, REPLACEMENT_REASONS, humanise, priceLabel, reasonEffect } from "./format";
import type { ReplacementCreateDraft, WarrantyReplacementDetail } from "./types";
import "./returns.css";

const EMPTY_DRAFT: ReplacementCreateDraft = {
  sourceKind: "rma", sourceId: "", sourceEntitlementId: "", replacementUnitId: "", replacementReason: "warranty_defect",
  pricePolicy: "zero", differenceAmount: 0, currency: "", serviceCountry: "", consumerContract: false,
  consentRefurbished: false, replacementPolicyCode: "", returnDue: "", nonReturnPriceBasis: "list",
};

export function Component() {
  const { id } = useParams();
  const [search] = useSearchParams();
  const navigate = useNavigate();
  const creating = !id;
  const [detail, setDetail] = useState<WarrantyReplacementDetail | null>(null);
  const [draft, setDraft] = useState<ReplacementCreateDraft>(() => ({ ...EMPTY_DRAFT, sourceKind: search.get("sourceKind") === "claim" ? "claim" : "rma", sourceId: search.get("sourceId") ?? "", sourceEntitlementId: search.get("sourceEntitlementId") ?? "" }));
  const [sourceLabel, setSourceLabel] = useState("");
  const [sourceSerial, setSourceSerial] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const load = useCallback((signal?: AbortSignal) => id ? replacementApi.detail(id, signal).then(setDetail) : Promise.resolve(), [id]);
  useEffect(() => { const controller = new AbortController(); setError(null); void load(controller.signal).catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); }); return () => controller.abort(); }, [load]);
  useEffect(() => {
    if (!creating || !draft.sourceId) return;
    const controller = new AbortController(); setError(null);
    if (draft.sourceKind === "rma") {
      void returnsApi.detail(draft.sourceId, controller.signal).then((source) => {
        const line = source.lines.find((row) => row.entitlementId === draft.sourceEntitlementId) ?? source.lines.find((row) => row.entitlementId);
        setSourceLabel(source.rmaNumber ?? "Return request"); setSourceSerial(line?.receivedSerial ?? line?.authorisedSerial ?? line?.requestedSerial ?? null);
        setDraft((current) => ({ ...current, sourceEntitlementId: current.sourceEntitlementId || line?.entitlementId || "", currency: source.currency, serviceCountry: source.serviceCountry }));
      }).catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    } else {
      void claimsApi.detail(draft.sourceId, controller.signal).then((source) => {
        const entitlement = source.lines.find((line) => line.entitlementId === draft.sourceEntitlementId)?.entitlementId ?? source.lines.find((line) => line.entitlementId)?.entitlementId ?? "";
        setSourceLabel(source.claimNumber); setSourceSerial(source.serial);
        setDraft((current) => ({ ...current, sourceEntitlementId: current.sourceEntitlementId || entitlement, currency: source.currency, serviceCountry: source.serviceCountry }));
      }).catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    }
    return () => controller.abort();
  }, [creating, draft.sourceId, draft.sourceKind, draft.sourceEntitlementId]);
  const create = async () => {
    setBusy(true); setError(null);
    try { const result = await replacementApi.create(draft); navigate(`/replacements/${result.document.document.warrantyReplacementId}`, { replace: true }); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const ship = async () => {
    if (!id) return; setBusy(true); setError(null);
    try { await replacementApi.ship(id); await load(); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  if (creating) return <ReplacementCreate draft={draft} setDraft={setDraft} sourceLabel={sourceLabel} sourceSerial={sourceSerial} busy={busy} error={error} onCreate={() => void create()} />;
  if (!detail) return <main className="content" id="main" tabIndex={-1}><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Replacements" }, { label: "Document" }]} /><p className="muted">Loading warranty replacement…</p><ErrorNote error={error} /></main>;
  return <ReplacementDocument detail={detail} busy={busy} error={error} onShip={() => void ship()} />;
}

function ReplacementCreate({ draft, setDraft, sourceLabel, sourceSerial, busy, error, onCreate }: { draft: ReplacementCreateDraft; setDraft: (draft: ReplacementCreateDraft) => void; sourceLabel: string; sourceSerial: string | null; busy: boolean; error: unknown; onCreate: () => void }) {
  const ready = !!draft.sourceId && !!draft.sourceEntitlementId && !!draft.replacementUnitId && !!draft.currency && !!draft.serviceCountry
    && (!(draft.replacementReason === "goodwill" || draft.replacementReason === "recall") || !!draft.replacementPolicyCode)
    && (draft.pricePolicy === "zero" || draft.differenceAmount >= 0);
  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: draft.sourceKind === "rma" ? "Returns" : "Claims", to: draft.sourceKind === "rma" ? "/returns" : "/claims" }, { label: "New warranty replacement" }]} />
      <div className="page-header"><div><div className="eyebrow">Warranty replacement · draft · from {sourceLabel || humanise(draft.sourceKind)}</div><h1>{sourceSerial ?? "Original unit"} → choose replacement</h1><p className="sub">A warranty replacement is not a sales order · the server resolves the source unit, sale and stored replacement policy</p></div><div className="actions"><button type="button" className="btn primary" disabled={!ready || busy} onClick={onCreate}><Icon name="check" />Create document</button></div></div>
      {!draft.sourceId && <div className="note warn" style={{ marginBottom: "var(--gap)" }}><strong>Open creation from an RMA or approved claim.</strong>&nbsp;The source document supplies the covered entitlement; it cannot be entered by hand here.</div>}
      <div className="cols-3-2 returns-document-layout">
        <div className="stack">
          <section className="card"><div className="card-header"><h2>Units</h2><span className="meta">the link binds to the selected physical unit</span></div><div className="card-body"><div className="returns-pair"><div className="returns-pair-side"><div className="returns-pair-label">Original · from {humanise(draft.sourceKind)}</div><div className="serial">{sourceSerial ?? "Waiting for source"}</div><div className="sub muted">{sourceLabel || "Launch this screen from the source document"}</div></div><div className="returns-pair-arrow" aria-hidden="true">→</div><div className="returns-pair-side"><UnitField id="replacement-unit" label="Replacement · scan to pick" value={draft.replacementUnitId || null} onChange={(replacementUnitId) => setDraft({ ...draft, replacementUnitId: replacementUnitId ?? "" })} hint="Condition and availability are checked by the document detail" /></div></div></div></section>
          <ReasonCards reason={draft.replacementReason} onReason={(replacementReason) => setDraft({ ...draft, replacementReason, replacementPolicyCode: replacementReason === "goodwill" || replacementReason === "recall" ? draft.replacementPolicyCode : "" })} />
          <section className="card"><div className="card-header"><h2>Source and policy</h2><span className="meta">resolved by the server</span></div><div className="card-body"><div className="returns-source-banner"><span><strong>Source</strong> {sourceLabel || "—"}</span><span><strong>Country</strong> {draft.serviceCountry || "—"}</span><span><strong>Currency</strong> {draft.currency || "—"}</span></div>{(draft.replacementReason === "goodwill" || draft.replacementReason === "recall") && <label className="field" style={{ marginTop: 14 }}><span className="lbl">Approved replacement policy code</span><input className="input serial" value={draft.replacementPolicyCode} onChange={(event) => setDraft({ ...draft, replacementPolicyCode: event.target.value })} placeholder="Policy named on the approval or campaign" /></label>}<label className="returns-option" style={{ marginTop: 14 }}><input type="checkbox" checked={draft.consumerContract} onChange={(event) => setDraft({ ...draft, consumerContract: event.target.checked })} /><span>Consumer contract<span className="s">enables regional refurbished-condition consent rules</span></span></label>{draft.consumerContract && <label className="returns-option" style={{ marginTop: 8 }}><input type="checkbox" checked={draft.consentRefurbished} onChange={(event) => setDraft({ ...draft, consentRefurbished: event.target.checked })} /><span>Consent to a refurbished replacement recorded<span className="s">required for an EU consumer when refurbished stock is selected</span></span></label>}</div></section>
        </div>
        <div className="stack">
          <PriceCard policy={draft.pricePolicy} amount={draft.differenceAmount} currency={draft.currency} onPolicy={(pricePolicy) => setDraft({ ...draft, pricePolicy })} onAmount={(differenceAmount) => setDraft({ ...draft, differenceAmount })} />
          <section className="card"><div className="card-header"><h2>Return</h2><span className="meta">optional advance replacement</span></div><div className="card-body"><label className="field"><span className="lbl">Return due</span><input type="date" className="input" value={draft.returnDue} onChange={(event) => setDraft({ ...draft, returnDue: event.target.value })} /><span className="help">Leave blank when the original is already received</span></label>{draft.returnDue && <label className="field" style={{ marginTop: 12 }}><span className="lbl">If not returned, bill at</span><select className="select" value={draft.nonReturnPriceBasis} onChange={(event) => setDraft({ ...draft, nonReturnPriceBasis: event.target.value })}><option value="list">List price</option><option value="fmv">Fair market value</option><option value="cost">Cost</option></select></label>}</div></section>
          <section className="card"><div className="card-header"><h2>After creation</h2></div><div className="card-body"><p className="muted">The draft document shows the replacement unit’s actual condition checks and a server-produced entitlement transfer preview before anyone ships it.</p></div></section>
          <ErrorNote error={error} />
        </div>
      </div>
    </main>
    <Rail label="Replacement"><Section title="Draft"><p className="muted">Creation records a proposed link. Shipping remains a separate, claim-stamped command.</p></Section></Rail>
  </>;
}

function ReplacementDocument({ detail, busy, error, onShip }: { detail: WarrantyReplacementDetail; busy: boolean; error: unknown; onShip: () => void }) {
  const d = detail.document;
  const names = useReferenceNames({ items: [detail.originalUnit.itemId, detail.replacementUnit.itemId, ...detail.lines.map((line) => line.itemId)], parties: [detail.ownerPartyRef], documents: [detail.originalInvoice], locations: [detail.originalUnit.locationId, detail.replacementUnit.locationId] });
  const people = usePeople([d.createdBy, d.shippedBy]);
  const blocked = detail.checks.some((check) => !check.passed);
  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Replacements" }, { label: d.replacementNumber }]} />
      <div className="page-header"><div><div className="eyebrow">Warranty replacement {d.replacementNumber} · {humanise(d.status)} · from {d.sourceNumber} · not a sales order, no revenue line by default</div><h1>{detail.originalUnit.serial ?? "Original unit"} → {detail.replacementUnit.serial ?? "Replacement unit"}</h1><p className="sub">{names.party(detail.ownerPartyRef)} receives {names.item(detail.replacementUnit.itemId)} · the remaining warranty moves under the stored replacement policy</p></div><div className="actions"><button type="button" className="btn" onClick={() => window.print()}><Icon name="print" />Preview document</button>{d.status === "proposed" && <button type="button" className="btn primary" disabled={busy || blocked} title={blocked ? "Resolve every blocked condition check before shipping" : undefined} onClick={onShip}><Icon name="check" />Ship replacement<kbd className="key">⌘↵</kbd></button>}</div></div>
      <div className="cols-3-2 returns-document-layout">
        <div className="stack">
          <section className="card"><div className="card-header"><h2>Units</h2><span className="meta">the link binds to the unit actually selected</span></div><div className="card-body"><div className="returns-pair"><div className="returns-pair-side"><div className="returns-pair-label">Original</div><UnitLink unitId={detail.originalUnit.unitId}>{detail.originalUnit.serial ?? "No serial recorded"}</UnitLink><div className="sub muted">{names.item(detail.originalUnit.itemId)} · grade {humanise(detail.originalUnit.conditionGrade)} · {detail.originalUnit.locationId ? names.location(detail.originalUnit.locationId) : humanise(detail.originalUnit.locationKind)}</div></div><div className="returns-pair-arrow" aria-hidden="true">→</div><div className="returns-pair-side"><div className="returns-pair-label">Replacement</div><UnitLink unitId={detail.replacementUnit.unitId}>{detail.replacementUnit.serial ?? "No serial recorded"}</UnitLink><div className="sub muted">{names.item(detail.replacementUnit.itemId)} · grade {humanise(detail.replacementUnit.conditionGrade)} · {detail.replacementUnit.locationId ? names.location(detail.replacementUnit.locationId) : humanise(detail.replacementUnit.locationKind)}</div></div></div><ul className="returns-checks" style={{ marginTop: 12 }}>{detail.checks.map((check) => <li key={check.code}><span>{check.label}<span className="s">{check.detail}</span></span><StatusBadge tone={check.passed ? check.outcome === "not_required" ? "neutral" : "ok" : "bad"}>{humanise(check.outcome)}</StatusBadge></li>)}</ul></div></section>
          <ReasonCards reason={d.replacementReason} />
          <section className="card"><div className="card-header"><h2>Warranty transfer <span className="meta">computed by TransferEntitlement, not the screen</span></h2><span className="meta">chain length {detail.chainLength}</span></div><table className="table"><colgroup><col style={{ width: "24%" }} /><col style={{ width: "22%" }} /><col style={{ width: "25%" }} /><col style={{ width: "29%" }} /></colgroup><thead><tr><th>Entitlement</th><th>Unit</th><th>Term</th><th>Effect</th></tr></thead><tbody>{detail.transfers.map((row, index) => <tr key={row.entitlementId ?? `prospective-${index}`}><td className="primary"><span className="t">{humanise(row.coverageFamily)}</span><span className="sub">{humanise(row.entitlementType)}</span></td><td><UnitLink unitId={row.unitId}>{row.unitSerial ?? "No serial recorded"}</UnitLink></td><td>{row.startDate ?? "open"} → {row.endDate ?? "computed on ship"}</td><td><StatusBadge tone={row.effect.includes("open") ? "ok" : "neutral"}>{humanise(row.effect)}</StatusBadge><span className="sub returns-transfer-effect">{row.detail}</span></td></tr>)}</tbody></table><div className="card-footer"><span>Policy <span className="serial">{detail.replacementPolicyCode}</span>{d.computedEnd ? ` · computed end ${fmtDate(d.computedEnd)}` : " · final end is recorded when the replacement ships"}</span></div></section>
          <ErrorNote error={error} />
        </div>
        <div className="stack">
          <PriceCard policy={d.pricePolicy} amount={d.differenceAmount} currency={d.currency} readOnly originalSale={names.document(detail.originalInvoice)} restockingFee={d.restockingFeeAmount} nonReturnCharge={detail.nonReturnCharge} />
          <section className="card"><div className="card-header"><h2>Return</h2><span className="meta">{d.returnDue ? "advance replacement" : "original already received"}</span></div><div className="card-body"><Kv rows={[["Link state", humanise(d.state)], ["Return due", d.returnDue ? fmtDate(d.returnDue) : "not needed · received before replacement"], [detail.nonReturnCharge?.state === "billed" ? "Non-return" : "If not returned", detail.nonReturnCharge ? `${humanise(detail.nonReturnCharge.priceBasis)} · ${money(detail.nonReturnCharge.amount, d.currency)}` : "applies to advance replacements only"]]} /></div></section>
          <section className="card"><div className="card-header"><h2>Customer asset tag</h2></div><div className="card-body"><Kv rows={[["Tag", detail.customerAssetTag ? <span className="serial">{detail.customerAssetTag}</span> : "No active customer tag"], ["Moves to", <span className="serial">{detail.replacementUnit.serial ?? "Replacement unit"}</span>], ["Label", detail.customerAssetTag ? d.status === "shipped" ? "print request raised" : "print request will be raised on ship" : "not required"]]} /></div></section>
          <section className="card"><div className="card-header"><h2>{d.status === "shipped" ? "Recorded on ship" : "Will be recorded on ship"}</h2><span className="meta">one transaction · outbox</span></div><table className="table dense"><tbody><tr><td className="primary">Replacement.Shipped</td><td>{humanise(d.replacementReason)} · {priceLabel(d.pricePolicy).toLowerCase()} · accrual suppressed</td></tr><tr><td className="primary">Stock issue</td><td>{detail.replacementUnit.serial ?? "Replacement unit"} · paired with its serial event</td></tr><tr><td className="primary">Entitlement.Transferred</td><td>{detail.transfers.length - 1} inherited entitlement row · chain length {detail.chainLength}</td></tr><tr><td className="primary">Ownership</td><td>new unit to {names.party(detail.ownerPartyRef)}</td></tr></tbody></table></section>
        </div>
      </div>
    </main>
    <Rail label="Replacement"><Section title="Document"><Kv rows={[["Number", d.replacementNumber], ["Status", humanise(d.status)], ["Created", `${fmtDateTime(d.createdAt)} · ${people(d.createdBy)}`], ["Shipped", d.shippedAt ? `${fmtDateTime(d.shippedAt)} · ${people(d.shippedBy)}` : "—"], ["Posting", detail.stockPostingReference ?? "—"]]} /></Section></Rail>
  </>;
}

function ReasonCards({ reason, onReason }: { reason: string; onReason?: ((reason: string) => void) | undefined }) {
  return <section className="card"><div className="card-header"><h2>Reason</h2><span className="meta">printed on the document · reaches Finance with the shipment</span></div><div className="card-body"><div className="returns-options" role="radiogroup" aria-label="Replacement reason">{REPLACEMENT_REASONS.map((value) => <label className={`returns-option${reason === value ? " on" : ""}${onReason ? "" : " off"}`} key={value}><input type="radio" name="replacement-reason" disabled={!onReason} checked={reason === value} onChange={() => onReason?.(value)} /><span>{humanise(value)}<span className="s">{reasonEffect(value)}</span></span></label>)}</div></div></section>;
}

function PriceCard({ policy, amount, currency, onPolicy, onAmount, readOnly, originalSale, restockingFee, nonReturnCharge }: { policy: string; amount: number; currency: string; onPolicy?: ((policy: string) => void) | undefined; onAmount?: ((amount: number) => void) | undefined; readOnly?: boolean | undefined; originalSale?: string | undefined; restockingFee?: number | undefined; nonReturnCharge?: WarrantyReplacementDetail["nonReturnCharge"] | undefined }) {
  return <section className="card"><div className="card-header"><h2>Price</h2></div><div className="card-body"><div className="returns-segmented" role="group" aria-label="Price policy">{PRICE_POLICIES.map((value) => <button type="button" key={value} aria-pressed={policy === value} disabled={readOnly} onClick={() => onPolicy?.(value)}>{priceLabel(value)}</button>)}</div>{!readOnly && policy !== "zero" && <label className="field" style={{ marginTop: 12 }}><span className="lbl">{priceLabel(policy)} amount · {currency || "currency"}</span><input className="input" type="number" min="0" step="0.01" value={amount} onChange={(event) => onAmount?.(Number(event.target.value))} /></label>}<div className="kv returns-wide-kv" style={{ marginTop: 12 }}><span className="k">Difference</span><span className="v">{money(amount, currency)}{policy === "zero" ? " · no charge" : ""}</span>{readOnly && <>{nonReturnCharge && <><span className="k">{humanise(nonReturnCharge.state)} — non-return</span><span className="v">{money(nonReturnCharge.amount, currency)} · {humanise(nonReturnCharge.priceBasis)}</span></>}<span className="k">Restocking fee</span><span className="v">{restockingFee ? money(restockingFee, currency) : "none"}</span><span className="k">Original sale</span><span className="v">{originalSale ?? "—"}</span></>}</div></div></section>;
}

function money(amount: number, currency: string): string { try { return new Intl.NumberFormat(undefined, { style: "currency", currency: currency || "GBP" }).format(amount); } catch { return `${currency} ${amount.toFixed(2)}`; } }
