import { ClaimCausePanel } from "./ClaimCausePanel";
import type { ClaimGuidance } from "../entitlements/coverItems";
import { useEffect, useRef, useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router";
import { DEFAULT_LEGAL_ENTITY, units, type AsBuiltRow, type LookupHit, type UnitDetail } from "../api/units";
import { ScanInput, Shortcut, useModEnter } from "../components";
import { ItemField, PartyField, useReferenceNames } from "../corrections/ReferenceFields";
import { entitlementApi } from "../entitlements/api";
import type { UnitEntitlementDetail } from "../entitlements/types";
import { Icon } from "../shell/Icons";
import { Rail } from "../shell/Drawer";
import { openDrawer } from "../shell/drawerStore";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section } from "../units/common";
import { claimsApi } from "./api";
import { COUNTRY_CODES, countryName } from "../reference/countries";
import { LINE_KINDS, REMEDIES, campaignCoverTerms, campaignOptionHint, humanise, money, overlayFlagsText } from "./format";
import type { CampaignCoverageSource, ClaimCampaignOption, ClaimEvidenceInput, CoverageFamily, CurrencyInfo, SubmitClaim, TenantCurrency } from "./types";
import "./claims.css";

type IntakeMode = "unit" | "proof" | "no_record";
type DraftLine = { id: string; kind: string; description: string; childUnitId: string; coverageFamily: string; requestedAmount: string; measurements: string };
type DraftEvidence = { id: string; kind: string; reference: string; description: string };
type AskedSource = "Warranty" | "Recall" | "Goodwill" | "Contract" | "Chargeable";
const ASKED_SOURCES: AskedSource[] = ["Warranty", "Recall", "Goodwill", "Contract", "Chargeable"];
const TODAY = new Date().toISOString().slice(0, 10);

// Base warranty is what most claims are made under; other families are a deliberate choice on the line.
function defaultFamily(families: { familyCode: string; active: boolean }[]): string {
  const active = families.filter((family) => family.active);
  return (active.find((family) => family.familyCode === "base_warranty") ?? active[0])?.familyCode ?? "";
}
function draftLine(family = ""): DraftLine { return { id: crypto.randomUUID(), kind: "parts", description: "", childUnitId: "", coverageFamily: family, requestedAmount: "", measurements: "" }; }
function draftEvidence(): DraftEvidence { return { id: crypto.randomUUID(), kind: "attachment", reference: "", description: "" }; }

export function Component() {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  useEffect(() => {
    const id = searchParams.get("unit"); if (!id) return;
    const c = new AbortController();
    Promise.all([units.detail(id, c.signal), entitlementApi.detailForUnit(id, c.signal)]).then(([d, e]) => {
      setUnit(d); setEntitlements(e); setClaimant(d.ownerPartyRef); setSettlement(d.ownerPartyRef);
      setIdentifier(d.identifiers.find((i) => !i.validTo)?.rawValue ?? "");
    }).catch((e) => { if (!c.signal.aborted) setError(e); });
    return () => c.abort();
  }, [searchParams]);
  const familyEdited = useRef(false);
  const [cause, setCause] = useState(() => searchParams.get("cause") ?? "");
  const [reportedDescription, setReportedDescription] = useState("");
  const [occurredDate, setOccurredDate] = useState("");
  const [guidance, setGuidance] = useState<ClaimGuidance | null>(null);
  const [guidedFamily, setGuidedFamily] = useState("");
  const [mode, setMode] = useState<IntakeMode>("unit");
  const [identifier, setIdentifier] = useState("");
  const [hit, setHit] = useState<LookupHit | null>(null);
  const [unit, setUnit] = useState<UnitDetail | null>(null);
  const [entitlements, setEntitlements] = useState<UnitEntitlementDetail | null>(null);
  const [itemId, setItemId] = useState<string | null>(null);
  const [proofRef, setProofRef] = useState("");
  const [proofDate, setProofDate] = useState("");
  const [claimant, setClaimant] = useState<string | null>(null);
  const [settlement, setSettlement] = useState<string | null>(null);
  const [serviceCountry, setServiceCountry] = useState("GB");
  const [claimDate, setClaimDate] = useState(TODAY);
  const [currency, setCurrency] = useState("");
  const [currencySettings, setCurrencySettings] = useState<TenantCurrency | null>(null);
  const [catalogue, setCatalogue] = useState<CurrencyInfo[]>([]);
  const [families, setFamilies] = useState<CoverageFamily[]>([]);
  const [lines, setLines] = useState<DraftLine[]>([draftLine()]);
  const [evidence, setEvidence] = useState<DraftEvidence[]>([draftEvidence()]);
  const [remedy, setRemedy] = useState("repair");
  const [choiceEvidence, setChoiceEvidence] = useState("");
  const [askedSource, setAskedSource] = useState<AskedSource>("Warranty");
  const [campaignOptions, setCampaignOptions] = useState<ClaimCampaignOption[]>([]);
  const [selectedCampaignId, setSelectedCampaignId] = useState("");
  const [campaignsLoading, setCampaignsLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [lookupBusy, setLookupBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    const controller = new AbortController();
    Promise.all([claimsApi.currency(controller.signal), claimsApi.currencyCatalogue(controller.signal), claimsApi.coverageFamilies(controller.signal)])
      .then(([settings, nextCatalogue, nextFamilies]) => {
        setCurrencySettings(settings); setCatalogue(nextCatalogue); setFamilies(nextFamilies.filter((family) => family.active));
        setCurrency(settings.currency ?? settings.accepted[0] ?? "");
        setLines((current) => current.map((line, index) => index === 0 && !line.coverageFamily ? { ...line, coverageFamily: defaultFamily(nextFamilies) } : line));
      }).catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    if (!unit?.unitId || !claimDate) { setCampaignOptions([]); setSelectedCampaignId(""); setCampaignsLoading(false); return; }
    const controller = new AbortController();
    setCampaignOptions([]); setSelectedCampaignId(""); setCampaignsLoading(true);
    claimsApi.campaignOptions(unit.unitId, claimDate, controller.signal)
      .then(setCampaignOptions)
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); })
      .finally(() => { if (!controller.signal.aborted) setCampaignsLoading(false); });
    return () => controller.abort();
  }, [unit?.unitId, claimDate]);

  useEffect(() => {
    if (askedSource !== "Recall" && askedSource !== "Goodwill") { setSelectedCampaignId(""); return; }
    const source = askedSource.toLowerCase() as CampaignCoverageSource;
    const covering = campaignOptions.filter((option) => option.coverageSource === source && option.coversOnDate);
    if (!campaignsLoading && covering.length === 0) { setSelectedCampaignId(""); setAskedSource("Warranty"); return; }
    setSelectedCampaignId((current) => covering.some((option) => option.campaignId === current) ? current : covering.length === 1 ? covering[0]!.campaignId : "");
  }, [askedSource, campaignOptions, campaignsLoading]);

  const names = useReferenceNames({ items: [unit?.itemId, itemId, ...(unit?.asBuilt ?? []).map((child) => child.childItemId)], parties: [unit?.ownerPartyRef, unit?.custodyPartyRef, claimant, settlement, DEFAULT_LEGAL_ENTITY] });
  const resolve = async (value: string) => {
    setIdentifier(value); setLookupBusy(true); setError(null); setHit(null); setUnit(null); setEntitlements(null);
    try {
      const lookup = await units.lookup(value);
      if (lookup.hits.length !== 1 || !["exact", "superseded"].includes(lookup.resolution)) throw new Error(lookup.hits.length ? "This identifier is ambiguous. Open Unit search and choose the unit before claiming." : "No unit resolves from this identifier. Use Proof of purchase or No record to reconstruct it.");
      const match = lookup.hits[0]!;
      const [detail, cover] = await Promise.all([units.detail(match.unitId), entitlementApi.detailForUnit(match.unitId)]);
      setHit(match); setUnit(detail); setEntitlements(cover); setItemId(detail.itemId);
      setClaimant(detail.ownerPartyRef); setSettlement(detail.ownerPartyRef);
      setLines((current) => current.map((line) => ({ ...line, childUnitId: "" })));
    } catch (reason) { setError(reason); } finally { setLookupBusy(false); }
  };
  const switchMode = (next: IntakeMode) => {
    setMode(next); setError(null);
    if (next !== "unit") { setHit(null); setUnit(null); setEntitlements(null); setClaimant(null); setSettlement(null); setItemId(null); setLines((current) => current.map((line) => ({ ...line, childUnitId: "" }))); }
  };
  const updateLine = (id: string, patch: Partial<DraftLine>) => setLines((current) => current.map((line) => line.id === id ? { ...line, ...patch } : line));
  const updateEvidence = (id: string, patch: Partial<DraftEvidence>) => setEvidence((current) => current.map((row) => row.id === id ? { ...row, ...patch } : row));
  const showChild = (child: AsBuiltRow) => openDrawer({ eyebrow: "Claim line · child unit", title: child.childSerial ?? child.slot ?? "Child unit", mono: !!child.childSerial }, <><h3>As-built child</h3><Kv rows={[["Serial", child.childSerial ?? "No serial recorded"], ["Item", names.item(child.childItemId)], ["Slot", child.slot ?? "—"], ["Relation", humanise(child.relation)], ["Installed", child.installedAt.slice(0, 10)], ["Release status", humanise(child.releaseStatus)]]} /><p className="muted" style={{ marginTop: 16 }}>Adjudication checks inbound cover on this child. If it is live on the claim date, approval raises supplier recovery automatically.</p></>);
  const total = lines.reduce((sum, line) => sum + (Number(line.requestedAmount) || 0), 0);
  const children = unit?.asBuilt.filter((child) => child.childUnitId && !child.removedAt) ?? [];
  const activeCover = entitlements?.entitlements.filter((cover) => cover.effectiveStatus === "active") ?? [];
  // What the live cover suggests; adjudication still decides per line. No unit yet → nothing highlighted.
  const customerCover = activeCover.filter((cover) => cover.direction === "outbound");
  const coverageExplanation = !unit ? null : customerCover.length === 0 ? "Chargeable" : suggestedSource(customerCover.map((cover) => cover.type));
  const coveringCampaigns = (source: CampaignCoverageSource) => campaignOptions.filter((option) => option.coverageSource === source && option.coversOnDate);
  const selectedCampaigns = askedSource === "Recall" || askedSource === "Goodwill" ? coveringCampaigns(askedSource.toLowerCase() as CampaignCoverageSource) : [];
  const selectedCampaign = selectedCampaigns.find((option) => option.campaignId === selectedCampaignId) ?? null;
  const campaignChosen = askedSource !== "Recall" && askedSource !== "Goodwill" || !!selectedCampaign;
  const currencyChoices = currencySettings?.accepted ?? [];
  const canSubmit = !!claimDate && !!currency && !!claimant && !!settlement && !!serviceCountry && !!choiceEvidence.trim() && lines.length > 0 && lines.every((line) => line.description.trim() && line.coverageFamily && line.requestedAmount !== "" && Number(line.requestedAmount) >= 0)
    && campaignChosen && (mode === "unit" ? !!unit : !!identifier.trim() && !!itemId && !!proofRef.trim() && !!proofDate);

  const submit = async () => {
    if (!canSubmit || busy) return;
    setBusy(true); setError(null);
    const body: SubmitClaim = {
      reportedCauseCode: cause || null, reportedDescription: reportedDescription || null, occurredDate: occurredDate || null,
      commandId: crypto.randomUUID(), unitId: mode === "unit" ? unit!.unitId : null,
      identifier: mode === "unit" ? null : { value: identifier.trim(), issuerPartyRef: null, identifierType: null, erpSerialId: null },
      claimDate, claimantPartyRef: claimant!, settlementPartyRef: settlement!,
      serviceEntityId: DEFAULT_LEGAL_ENTITY, obligorEntityId: DEFAULT_LEGAL_ENTITY, serviceCountry, currency,
      lines: lines.map((line) => ({ kind: line.kind, coverageFamily: line.coverageFamily, requestedAmount: Number(line.requestedAmount), unitId: line.childUnitId || null, description: line.description.trim(), measurements: parseMeasurements(line.measurements) })),
      evidence: evidence.filter((row) => row.reference.trim()).map<ClaimEvidenceInput>((row) => ({ kind: row.kind, reference: row.reference.trim(), description: row.description.trim() || null })),
      remedy, consumerChoiceEvidenceRef: choiceEvidence.trim(), eventTime: new Date().toISOString(),
      proofOfPurchaseRef: mode === "unit" ? null : proofRef.trim(), proofOfPurchaseDate: mode === "unit" ? null : proofDate, itemId: mode === "unit" ? null : itemId,
      ...(selectedCampaign ? { campaignId: selectedCampaign.campaignId } : {}),
    };
    try { const result = await claimsApi.submit(body); navigate(`/claims/${result.claimId}`); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  useModEnter(submit, !canSubmit || busy);

  return <><main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Claims", to: "/claims" }, { label: "New claim" }]} />
    <div className="page-header"><div><h1>New claim</h1><p className="sub">Find the unit by any identifier or by proof of purchase · the claimant may differ from the party that settles · nothing is decided here; adjudication runs next</p></div><div className="actions"><Link className="btn" to="/claims">Cancel</Link><button type="button" className="btn primary" disabled={!canSubmit || busy} aria-busy={busy} onClick={() => void submit()}><Icon name="check" />{guidedFamily ? `Submit under ${families.find((f) => f.familyCode === guidedFamily)?.name ?? "selected cover"}` : "Submit and adjudicate"}<Shortcut keys="mod+enter" /></button></div></div>
    <div className="stack">
      <section className="card" aria-labelledby="claim-find"><div className="card-body"><div className="toolbar">
        <ScanInput size="lg" primary value={identifier} onChange={(value) => { setIdentifier(value); if (unit) { setUnit(null); setHit(null); setEntitlements(null); } }} onSubmit={(scan) => { if (mode === "unit") void resolve(scan.value); }} ariaLabel="Scan or enter a serial, asset tag or licence id" placeholder={mode === "unit" ? "Scan or enter any unit identifier" : "Identifier on the product or proof"} hint={lookupBusy ? <span>…</span> : <Shortcut keys="/" />} />
        <div className="segmented plain" role="group" aria-label="Find by">{([['unit', 'Unit identifier'], ['proof', 'Proof of purchase'], ['no_record', 'No record']] as const).map(([value, label]) => <button key={value} type="button" aria-pressed={mode === value} onClick={() => switchMode(value)}>{label}</button>)}</div>
        <span className="grow" /><span className="hint" id="claim-find">{mode === "unit" ? "Superseded serials and asset tags resolve to the same unit." : "A low-confidence unit and entitlement are reconstructed from the proof supplied."}</span>
      </div>{mode === "unit" && identifier && !unit && <div style={{ marginTop: 10 }}><button type="button" className="btn sm" disabled={lookupBusy} onClick={() => void resolve(identifier)}>Resolve unit</button></div>}
      {mode !== "unit" && <div className="form-grid" style={{ marginTop: 16 }}><ItemField id="claim-item" label="Item" value={itemId} onChange={setItemId} hint="Required to reconstruct the unit without an existing record" /><div className="field"><label htmlFor="claim-proof-ref">Proof-of-purchase reference</label><input id="claim-proof-ref" className="input" value={proofRef} onChange={(event) => setProofRef(event.target.value)} /></div><div className="field"><label htmlFor="claim-proof-date">Proof-of-purchase date</label><input id="claim-proof-date" className="input" type="date" value={proofDate} onChange={(event) => setProofDate(event.target.value)} /></div></div>}
      </div></section>
      <ClaimCausePanel unitId={unit?.unitId ?? null} cause={cause} onCause={(value) => { familyEdited.current = false; setGuidedFamily(""); setLines((current) => current.map((line, index) => index === 0 ? { ...line, coverageFamily: defaultFamily(families) } : line)); setCause(value); }} description={reportedDescription} onDescription={setReportedDescription} occurredDate={occurredDate} onOccurredDate={setOccurredDate} onGuidance={(g) => {
        setGuidance(g);
        const family = g?.grants.length === 1 ? g.grants[0]!.cover.familyCode : "";
        if (familyEdited.current) return;
        setGuidedFamily(family);
        if (family) setLines((current) => current.map((line, index) => index === 0 ? { ...line, coverageFamily: family } : line));
      }} />
      {guidance && guidance.grants.length > 1 && <p className="hint">Several covers grant this cause. Choose the coverage family on each line.</p>}
      <ErrorNote error={error} />
      {!currencyChoices.length && <div className="note warn"><strong>No accepted currencies are configured.</strong>&nbsp;Set a working currency and accepted claim currencies in <Link to="/settings/currencies">Currencies settings</Link>. Claim submission is disabled until then.</div>}
      <div className="cols-3-2">
        <div className="stack">
          <section className="card" aria-labelledby="claim-unit"><div className="card-header"><h2 id="claim-unit">Unit <span className="meta">{unit ? `resolved from ${hit?.identifierType ?? "identifier"}` : mode === "unit" ? "waiting for an identifier" : "will be reconstructed"}</span></h2>{unit && <Link className="btn ghost sm" to={`/units/${unit.unitId}`}>Open unit</Link>}</div><div className="card-body">
            {unit ? <Kv rows={[
              ["Serial", <span className="serial">{hit?.rawValue ?? identifier}</span>], ["Item", names.item(unit.itemId)], ["Owner now", names.party(unit.ownerPartyRef)],
              ["Custody", unit.custodyPartyRef ? names.party(unit.custodyPartyRef) : unit.locationKind ? `${humanise(unit.locationKind)}${unit.locationId ? " · location recorded" : ""}` : "—"],
              ["Live coverage", activeCover.length ? activeCover.map((cover) => `${cover.coverageFamilyName} · ${cover.startDate ?? "open"} → ${cover.endDate ?? "open"} · region ${cover.regionScope.join(", ") || "any"}`).join("; ") : "No active cover returned"],
              ["Overlay flags", overlayFlagsText(unit.overlayFlags)], ["As-built children", children.length.toLocaleString()],
            ]} /> : <p className="muted">{mode === "unit" ? "Scan or enter an identifier above, then resolve it. Only facts returned by the unit and entitlement APIs appear here." : "The submitted identifier, item and proof of purchase let the server reconstruct a low-confidence record. No sale, shipment or delivery facts are inferred."}</p>}
          </div></section>
          <section className="card" aria-labelledby="claim-lines"><div className="card-header"><h2 id="claim-lines">What is claimed <span className="meta">{lines.length} {lines.length === 1 ? "line" : "lines"} · coverage is decided per line</span></h2><button type="button" className="btn sm" onClick={() => setLines((current) => [...current, draftLine(defaultFamily(families))])}><Icon name="plus" />Add line</button></div>
            <div className="grid-viewport"><table className="table claims-lines"><colgroup><col style={{ width: "12%" }} /><col style={{ width: "25%" }} /><col style={{ width: "15%" }} /><col style={{ width: "17%" }} /><col style={{ width: "12%" }} /><col style={{ width: "14%" }} /><col style={{ width: "5%" }} /></colgroup><thead><tr><th>Kind</th><th>Description</th><th>Child unit</th><th>Coverage family</th><th className="num">Requested</th><th>Measured</th><th /></tr></thead><tbody>{lines.map((line, index) => <tr key={line.id}>
              <td><select className="select" aria-label={`Line ${index + 1} kind`} value={line.kind} onChange={(event) => updateLine(line.id, { kind: event.target.value })}>{LINE_KINDS.map((kind) => <option key={kind} value={kind}>{humanise(kind)}</option>)}</select></td>
              <td><input className="input" aria-label={index === 0 ? "Fault as reported" : `Line ${index + 1} description`} placeholder={index === 0 ? "Fault as reported" : "Work or item claimed"} value={line.description} onChange={(event) => updateLine(line.id, { description: event.target.value })} /></td>
              <td><select className="select" aria-label={`Line ${index + 1} child unit`} value={line.childUnitId} onChange={(event) => { updateLine(line.id, { childUnitId: event.target.value }); const child = children.find((candidate) => candidate.childUnitId === event.target.value); if (child) showChild(child); }}><option value="">Root unit</option>{children.map((child) => <option key={child.childUnitId!} value={child.childUnitId!}>{child.childSerial ?? child.slot ?? "Child unit"}</option>)}</select></td>
              <td><select className="select" aria-label={`Line ${index + 1} coverage family`} value={line.coverageFamily} onChange={(event) => { updateLine(line.id, { coverageFamily: event.target.value }); if (index === 0) { familyEdited.current = true; setGuidedFamily(event.target.value); } }}><option value="">Choose…</option>{families.map((family) => <option key={family.familyCode} value={family.familyCode}>{family.name}</option>)}</select></td>
              <td><input className="input num" aria-label={`Line ${index + 1} requested amount`} type="number" min="0" step="0.01" value={line.requestedAmount} onChange={(event) => updateLine(line.id, { requestedAmount: event.target.value })} /></td>
              <td><input className="input" aria-label={`Line ${index + 1} measurements`} placeholder="print_hours=1840" value={line.measurements} onChange={(event) => updateLine(line.id, { measurements: event.target.value })} /></td>
              <td><button type="button" className="btn sm" aria-label={`Remove line ${index + 1}`} disabled={lines.length === 1} onClick={() => setLines((current) => current.filter((candidate) => candidate.id !== line.id))}>×</button></td>
            </tr>)}</tbody></table></div><div className="card-footer"><span>Requested {currency ? money(total, currency, catalogue) : "—"} · enter the fault as reported in the first line’s description</span></div>
          </section>
        </div>
        <div className="stack">
          <section className="card" aria-labelledby="claim-parties"><div className="card-header"><h2 id="claim-parties">Parties</h2></div><div className="card-body"><div className="form-grid">
            <div className="span"><PartyField id="claim-claimant" label="Claimant" value={claimant} onChange={setClaimant} hint="Who reported it · defaults to the current owner" /></div>
            <div className="span"><PartyField id="claim-settlement" label="Settles with" value={settlement} onChange={setSettlement} hint="Who is credited; this may differ from the claimant" /></div>
            <div className="field"><label htmlFor="claim-country">Service country</label><select id="claim-country" className="select" value={serviceCountry} onChange={(event) => setServiceCountry(event.target.value)}>{COUNTRY_CODES.map((code) => <option key={code} value={code}>{code} · {countryName(code)}</option>)}</select></div>
            <div className="field"><label htmlFor="claim-currency">Currency</label><select id="claim-currency" className="select" value={currency} disabled={!currencyChoices.length} onChange={(event) => setCurrency(event.target.value)}>{currencyChoices.map((code) => <option key={code} value={code}>{code}{code === currencySettings?.currency ? " · working currency" : ""}</option>)}</select></div>
            <div className="field"><label htmlFor="claim-date">Claim date</label><input id="claim-date" className="input" type="date" value={claimDate} onChange={(event) => setClaimDate(event.target.value)} /></div>
            <div className="field span"><label>Service entity</label><div className="reference-selected"><span>{names.party(DEFAULT_LEGAL_ENTITY)}</span></div><span className="help">The tenant’s legal entity; read-only.</span></div>
            <div className="field span"><label>Obligor entity</label><div className="reference-selected"><span>{names.party(DEFAULT_LEGAL_ENTITY)}</span></div><span className="help">Adjudication records the obligor on the event.</span></div>
          </div></div></section>
          <section className="card" aria-labelledby="claim-source"><div className="card-header"><h2 id="claim-source">Coverage source asked for</h2><span className="meta">adjudication may change it</span></div><div className="card-body"><div className="claims-segmented" role="group" aria-label="Coverage source">{ASKED_SOURCES.map((source) => {
            const campaignSource = source === "Recall" || source === "Goodwill" ? source.toLowerCase() as CampaignCoverageSource : null;
            const disabled = campaignSource !== null && coveringCampaigns(campaignSource).length === 0;
            return <button type="button" key={source} className={`chip${source === askedSource ? " on" : ""}`} aria-pressed={source === askedSource} disabled={disabled || campaignSource !== null && campaignsLoading} onClick={() => { setAskedSource(source); if (campaignSource) { const choices = coveringCampaigns(campaignSource); setSelectedCampaignId(choices.length === 1 ? choices[0]!.campaignId : ""); } }}>{source}</button>;
          })}</div>
            {(askedSource === "Recall" || askedSource === "Goodwill") && selectedCampaigns.length > 0 && <div className="field claims-campaign-picker"><label htmlFor="claim-campaign">Campaign</label><select id="claim-campaign" className="select" value={selectedCampaignId} onChange={(event) => setSelectedCampaignId(event.target.value)}><option value="">Choose a campaign…</option>{selectedCampaigns.map((option) => <option key={option.campaignId} value={option.campaignId}>{option.code} · {option.name}</option>)}</select>{selectedCampaign && <span className="help">{[selectedCampaign.runsUntil ? `runs until ${selectedCampaign.runsUntil}` : null, `reserve ${selectedCampaign.reserveTag}`, campaignCoverTerms(selectedCampaign)].filter(Boolean).join(" · ")}</span>}</div>}
            {unit && campaignsLoading && <p className="hint claims-campaign-hint">Checking campaigns for this unit and claim date…</p>}
            {unit && !campaignsLoading && (["recall", "goodwill"] as const).map((source) => coveringCampaigns(source).length === 0 ? <p className="hint claims-campaign-hint" key={source}>{campaignOptionHint(campaignOptions, source)}</p> : null)}
            {!unit && <p className="hint claims-campaign-hint">Resolve a unit to check recall and goodwill campaigns. Warranty, contract and chargeable claims are decided by adjudication.</p>}
            {unit && !campaignsLoading && coveringCampaigns("recall").length > 0 && coveringCampaigns("goodwill").length > 0 && askedSource !== "Recall" && askedSource !== "Goodwill" && <p className="hint claims-campaign-hint">Recall and goodwill are available through campaigns selecting this unit; choose one to name its campaign.</p>}
            {coverageExplanation && askedSource !== coverageExplanation && <p className="hint claims-campaign-hint">The unit’s live cover suggests {coverageExplanation.toLowerCase()}; adjudication still decides each line.</p>}
          </div></section>
          <section className="card" aria-labelledby="claim-remedy"><div className="card-header"><h2 id="claim-remedy">Remedy and informed choice</h2></div><div className="card-body"><div className="claims-remedy">{REMEDIES.map((value) => <label key={value} className={remedy === value ? "on" : ""}><input type="radio" name="claim-remedy" value={value} checked={remedy === value} onChange={() => setRemedy(value)} /><span>{humanise(value)}<span className="help">The consumer’s requested outcome; coverage is still adjudicated.</span></span></label>)}</div><div className="field" style={{ marginTop: 12 }}><label htmlFor="claim-choice">Consumer’s informed choice evidence reference</label><input id="claim-choice" className="input" value={choiceEvidence} onChange={(event) => setChoiceEvidence(event.target.value)} placeholder="Call, e-mail or signed choice reference" /><span className="help">Required and stored as consumer remedy-choice evidence.</span></div></div></section>
          <section className="card" aria-labelledby="claim-evidence"><div className="card-header"><h2 id="claim-evidence">Evidence <span className="meta">{evidence.filter((row) => row.reference.trim()).length} items</span></h2><button type="button" className="btn sm" onClick={() => setEvidence((current) => [...current, draftEvidence()])}><Icon name="plus" />Add</button></div><div className="card-body stack">{evidence.map((row, index) => <div className="form-grid" key={row.id}>
            <div className="field"><label htmlFor={`evidence-kind-${row.id}`}>Kind</label><select id={`evidence-kind-${row.id}`} className="select" value={row.kind} onChange={(event) => updateEvidence(row.id, { kind: event.target.value })}><option value="attachment">Attachment</option><option value="proof_of_purchase">Proof of purchase</option></select></div>
            <div className="field"><label htmlFor={`evidence-ref-${row.id}`}>Reference</label><input id={`evidence-ref-${row.id}`} className="input" value={row.reference} onChange={(event) => updateEvidence(row.id, { reference: event.target.value })} placeholder="File or document reference" /></div>
            <div className="field span"><label htmlFor={`evidence-desc-${row.id}`}>Description</label><input id={`evidence-desc-${row.id}`} className="input" value={row.description} onChange={(event) => updateEvidence(row.id, { description: event.target.value })} /><button type="button" className="btn ghost sm" disabled={evidence.length === 1} onClick={() => setEvidence((current) => current.filter((candidate) => candidate.id !== row.id))}>Remove evidence {index + 1}</button></div>
          </div>)}</div></section>
        </div>
      </div>
    </div>
  </main><Rail label="Claim intake"><Section title="Submission boundary"><p className="muted">This form records the facts and evidence supplied. Coverage source, percentages, obligor and recovery are determined by adjudication after submission.</p></Section></Rail></>;
}

function parseMeasurements(text: string): Record<string, number> | null {
  const entries = text.split(",").map((entry) => entry.trim()).filter(Boolean).map((entry) => { const [key, raw] = entry.split("=").map((part) => part.trim()); return key && raw !== undefined && Number.isFinite(Number(raw)) ? [key, Number(raw)] as const : null; }).filter((entry): entry is readonly [string, number] => entry !== null);
  return entries.length ? Object.fromEntries(entries) : null;
}

/** The coverage source an entitlement type usually leads to (mirrors the server's mapping; display only). */
function suggestedSource(types: string[]): string {
  if (types.includes("recall_campaign")) return "Recall";
  if (types.some((type) => type === "contract" || type === "extended")) return "Contract";
  if (types.includes("goodwill")) return "Goodwill";
  return "Warranty";
}
