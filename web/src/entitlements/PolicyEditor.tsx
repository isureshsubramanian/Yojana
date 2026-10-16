import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { StatusBadge } from "../components";
import { Rail } from "../shell/Drawer";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, useActor } from "../units/common";
import { fmtDate, humanise, shortId } from "../units/format";
import { entitlementApi } from "./api";
import { commaList, issueTone, nullableNumber } from "./format";
import type { AmendmentApprovalPolicy, CoverageFamily, PolicyDetail, PolicySummary, PolicyVersion, RefurbishedGoodsPolicy, ReplacementPolicy, TenantCurrency, WarrantyReferencePolicy } from "./types";

export function Component() {
  const actor = useActor();
  const [summaries, setSummaries] = useState<PolicySummary[]>([]);
  const [policyCode, setPolicyCode] = useState("");
  const [versions, setVersions] = useState<PolicyVersion[]>([]);
  const [selectedId, setSelectedId] = useState("");
  const [detail, setDetail] = useState<PolicyDetail | null>(null);
  const [families, setFamilies] = useState<CoverageFamily[]>([]);
  const [replacements, setReplacements] = useState<ReplacementPolicy[]>([]);
  const [approval, setApproval] = useState<AmendmentApprovalPolicy | null>(null);
  const [refurbished, setRefurbished] = useState<RefurbishedGoodsPolicy | null>(null);
  const [serviceRules, setServiceRules] = useState<WarrantyReferencePolicy | null>(null);
  const [currency, setCurrency] = useState<TenantCurrency | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [message, setMessage] = useState("");

  const loadList = async () => {
    const [policies, familyRows, replacementRows, approvalPolicy, refurb, rules, tenantCurrency] = await Promise.all([entitlementApi.policies(), entitlementApi.families(), entitlementApi.replacementPolicies(), entitlementApi.approvalPolicy(), entitlementApi.refurbishedPolicy(), entitlementApi.serviceContractRules(), entitlementApi.currency()]);
    setSummaries(policies); setFamilies(familyRows); setReplacements(replacementRows); setApproval(approvalPolicy); setRefurbished(refurb); setServiceRules(rules); setCurrency(tenantCurrency);
    if (!policyCode && policies[0]) setPolicyCode(policies[0].policyCode);
  };
  useEffect(() => { setLoading(true); loadList().catch(setError).finally(() => setLoading(false)); }, []); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!policyCode) return;
    const controller = new AbortController();
    entitlementApi.policyVersions(policyCode, controller.signal).then((rows) => { setVersions(rows); const preferred = rows.find((row) => row.status === "draft") ?? rows[0]; setSelectedId(preferred?.policyVersionId ?? ""); }).catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    return () => controller.abort();
  }, [policyCode]);
  useEffect(() => {
    if (!selectedId) { setDetail(null); return; }
    const controller = new AbortController();
    entitlementApi.policyDetail(selectedId, controller.signal).then(setDetail).catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    return () => controller.abort();
  }, [selectedId]);

  const selectedSummary = summaries.find((row) => row.policyCode === policyCode);
  const editable = detail?.status === "draft";
  const setTerms = (next: PolicyDetail["terms"]) => detail && setDetail({ ...detail, terms: next });
  const save = async () => {
    if (!detail || !editable) return;
    setBusy(true); setError(null); setMessage("");
    try { const result = await entitlementApi.updatePolicy(detail, actor); const fresh = await entitlementApi.policyDetail(result.policyVersionId); setDetail(fresh); setMessage("Draft saved and validated."); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const validate = async () => {
    if (!detail) return;
    setBusy(true); setError(null);
    try { const validation = await entitlementApi.validatePolicy(detail.policyVersionId); setDetail({ ...detail, validation }); setMessage("Validation refreshed."); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const publish = async () => {
    if (!detail || !editable) return;
    setBusy(true); setError(null);
    try { await entitlementApi.publishPolicy(detail.policyVersionId, actor); setMessage("Policy version published."); await loadList(); setDetail(await entitlementApi.policyDetail(detail.policyVersionId)); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const newVersion = async () => {
    if (!selectedSummary) return;
    setBusy(true); setError(null);
    try {
      const result = await entitlementApi.createPolicyDraft({ commandId: crypto.randomUUID(), policyCode: selectedSummary.policyCode, name: selectedSummary.name, coverageFamily: selectedSummary.coverageFamily, entitlementType: detail?.entitlementType ?? "manufacturer", appliesToContractsFrom: new Date().toISOString().slice(0, 10), terms: null, actorRef: actor });
      setVersions(await entitlementApi.policyVersions(selectedSummary.policyCode)); setSelectedId(result.policyVersionId); setMessage("New draft opened from the newest version.");
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const errors = detail?.validation.issues.filter((issue) => issue.severity === "error").length ?? 0;
  const warnings = detail?.validation.issues.filter((issue) => issue.severity !== "error").length ?? 0;
  const replacement = replacements.find((row) => row.code === detail?.terms.replacementPolicyCode);
  const policyOptions = useMemo(() => summaries.map((row) => <option key={row.policyCode} value={row.policyCode}>{row.name} · {row.policyCode}</option>), [summaries]);

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Entitlements", to: "/entitlements/gaps" }, { label: "Warranty policies" }]} />
      <div className="page-header"><div><div className="eyebrow">Warranty policy · versions by contract date · {detail?.coverageFamily ?? "…"}</div><h1>{detail?.name ?? (loading ? "Loading policy…" : "Warranty policies")}</h1><p className="sub">Published versions are immutable · draft changes are validated before publication · {detail ? `${selectedSummary?.liveEntitlements ?? 0} live entitlements remain on their recorded version` : "select a policy"}</p></div><div className="actions"><button type="button" className="btn" disabled={!selectedSummary || !!selectedSummary.draftVersion || busy} onClick={newVersion}>New version</button><button type="button" className="btn" disabled={!detail || busy} onClick={validate}>Validate</button><button type="button" className="btn primary" disabled={!editable || errors > 0 || busy} aria-busy={busy} onClick={publish}>Publish version<kbd className="key">⌘↵</kbd></button></div></div>
      <ErrorNote error={error} />{message && <div className="note ok" role="status" style={{ marginBottom: "var(--gap)" }}>{message}</div>}
      <div className="cols-2-3">
        <div className="stack">
          <section className="card"><div className="card-header"><h2>Policies</h2><span className="meta">{summaries.length}</span></div><div className="card-body"><label className="lbl" htmlFor="policy-select">Policy</label><select id="policy-select" className="select" value={policyCode} onChange={(event) => setPolicyCode(event.target.value)}>{policyOptions}</select></div></section>
          <section className="card" aria-labelledby="policy-versions"><div className="card-header"><h2 id="policy-versions">Versions <span className="meta">effective by contract date</span></h2><span className="meta">{versions.length}</span></div><table className="table dense"><thead><tr><th>Version</th><th>Contracts from</th><th>Status</th></tr></thead><tbody>{versions.map((row) => <tr key={row.policyVersionId} className={`clickable${selectedId === row.policyVersionId ? " sel" : ""}`} tabIndex={0} onKeyDown={(event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); setSelectedId(row.policyVersionId); } }} onClick={() => setSelectedId(row.policyVersionId)}><td className="primary">v{row.version}<span className="sub">{row.liveEntitlements} live · {row.ruleVersion}</span></td><td>{fmtDate(row.appliesToContractsFrom)}</td><td><StatusBadge tone={row.status === "draft" ? "warn" : "ok"}>{humanise(row.status)}</StatusBadge></td></tr>)}</tbody></table></section>
          <section className="card" aria-labelledby="policy-validation"><div className="card-header"><h2 id="policy-validation">Validation <span className="meta">{detail ? `v${detail.version}` : "—"}</span></h2><StatusBadge tone={errors ? "bad" : warnings ? "warn" : "ok"}>{errors ? `${errors} errors` : warnings ? `${warnings} warnings` : "Ready"}</StatusBadge></div><div className="card-body stack">{detail?.validation.issues.length ? detail.validation.issues.map((issue) => <div className={`note ${issueTone(issue)}`} key={`${issue.code}:${issue.field}`}><strong>{humanise(issue.field)}</strong> · {issue.message}<span className="serial">{issue.code}</span></div>) : <div className="note ok"><strong>Validation passed.</strong>&nbsp;This version can be published.</div>}</div></section>
          <section className="card"><div className="card-header"><h2>Coverage families <span className="meta">tenant reference data</span></h2></div><table className="table dense"><thead><tr><th>Family</th><th>Stacking</th></tr></thead><tbody>{families.map((row) => <tr key={row.familyCode}><td className="primary">{row.name}<span className="sub serial">{row.familyCode}</span></td><td><StatusBadge tone={row.stacking ? "info" : "neutral"}>{row.stacking ? "Stacks" : "One live"}</StatusBadge></td></tr>)}</tbody></table></section>
        </div>
        {detail ? <div className="stack">
          <section className="card"><div className="card-header"><h2>Term and start</h2><span className="meta">{editable ? "draft · editable" : "published · read only"}</span></div><div className="card-body"><div className="form-grid">
            <Field label="Name" id="policy-name" value={detail.name} disabled={!editable} onChange={(value) => setDetail({ ...detail, name: value })} span />
            <Field label="Applies to contracts from" id="policy-from" value={detail.appliesToContractsFrom} type="date" disabled={!editable} onChange={(value) => setDetail({ ...detail, appliesToContractsFrom: value })} />
            <Field label="Duration" id="policy-duration" value={String(detail.terms.duration.value)} type="number" disabled={!editable} onChange={(value) => setTerms({ ...detail.terms, duration: { ...detail.terms.duration, value: Number(value) } })} />
            <Select label="Duration unit" id="policy-duration-unit" value={detail.terms.duration.unit} disabled={!editable} options={["months", "days"]} onChange={(value) => setTerms({ ...detail.terms, duration: { ...detail.terms.duration, unit: value } })} />
            <Select label="End of month" id="policy-eom" value={detail.terms.duration.endOfMonth} disabled={!editable} options={["clamp", "roll"]} onChange={(value) => setTerms({ ...detail.terms, duration: { ...detail.terms.duration, endOfMonth: value } })} />
            <label className="radiorow"><input type="checkbox" checked={detail.terms.duration.endInclusive} disabled={!editable} onChange={(event) => setTerms({ ...detail.terms, duration: { ...detail.terms.duration, endInclusive: event.target.checked } })} /><span><strong>End day is covered</strong><span className="sub">Inclusive means start + term − 1 day.</span></span></label>
            <Select label="Duration basis" id="policy-basis" value={detail.terms.durationBasis} disabled={!editable} options={["time", "usage", "both"]} onChange={(value) => setTerms({ ...detail.terms, durationBasis: value })} />
            <Select label="Reference time zone" id="policy-timezone" value={detail.terms.referenceTimeZoneRule} disabled={!editable} options={["customer_site", "obligor_entity", "utc"]} onChange={(value) => setTerms({ ...detail.terms, referenceTimeZoneRule: value })} />
            <Field label="Start trigger" id="policy-trigger" value={detail.terms.start.trigger} disabled={!editable} onChange={(value) => setTerms({ ...detail.terms, start: { ...detail.terms.start, trigger: value } })} />
            <Field label="Start cap (days)" id="policy-cap" value={detail.terms.start.capDays?.toString() ?? ""} type="number" disabled={!editable} onChange={(value) => setTerms({ ...detail.terms, start: { ...detail.terms.start, capDays: nullableNumber(value) } })} />
            <Field label="Fallback chain" id="policy-fallbacks" value={detail.terms.start.fallbacks.map((row) => `${row.trigger}${row.offsetDays ? `+${row.offsetDays}` : ""}`).join(", ")} disabled={!editable} span onChange={(value) => setTerms({ ...detail.terms, start: { ...detail.terms.start, fallbacks: commaList(value).map((part) => { const [trigger = "", offset = "0"] = part.split("+"); return { trigger, offsetDays: Number(offset) || 0 }; }) } })} />
          </div></div></section>
          <section className="card"><div className="card-header"><h2>Coverage, region and transfer</h2><span className="meta">stored on the version</span></div><div className="card-body"><div className="form-grid">
            <Field label="Parts %" id="policy-parts" value={String(detail.terms.coverage.partsPct)} type="number" disabled={!editable} onChange={(value) => setTerms({ ...detail.terms, coverage: { ...detail.terms.coverage, partsPct: Number(value) } })} />
            <Field label="Labour %" id="policy-labour" value={String(detail.terms.coverage.labourPct)} type="number" disabled={!editable} onChange={(value) => setTerms({ ...detail.terms, coverage: { ...detail.terms.coverage, labourPct: Number(value) } })} />
            <Field label="Travel %" id="policy-travel" value={String(detail.terms.coverage.travelPct)} type="number" disabled={!editable} onChange={(value) => setTerms({ ...detail.terms, coverage: { ...detail.terms.coverage, travelPct: Number(value) } })} />
            <Field label="Service mode" id="policy-service" value={detail.terms.coverage.serviceMode} disabled={!editable} onChange={(value) => setTerms({ ...detail.terms, coverage: { ...detail.terms.coverage, serviceMode: value } })} />
            <Field label="Region scope" id="policy-regions" value={detail.terms.regionScope.join(", ")} disabled={!editable} onChange={(value) => setTerms({ ...detail.terms, regionScope: commaList(value) })} />
            <Select label="Transferable" id="policy-transfer" value={detail.terms.transferable} disabled={!editable} options={["yes", "no", "fee", "registration_required"]} onChange={(value) => setTerms({ ...detail.terms, transferable: value })} />
            <Field label="Exclusions" id="policy-exclusions" value={detail.terms.exclusions.join(", ")} disabled={!editable} span onChange={(value) => setTerms({ ...detail.terms, exclusions: commaList(value) })} />
          </div></div></section>
          <section className="card"><div className="card-header"><h2>Replacement policy <span className="meta">what the replacement unit inherits</span></h2></div><div className="card-body"><div className="form-grid"><div className="field span"><label htmlFor="policy-replacement">Policy</label><select id="policy-replacement" className="select" disabled={!editable} value={detail.terms.replacementPolicyCode ?? ""} onChange={(event) => setTerms({ ...detail.terms, replacementPolicyCode: event.target.value || null })}><option value="">None</option>{replacements.map((row) => <option key={row.code} value={row.code}>{row.code} · {humanise(row.rule)}</option>)}</select></div>{replacement && <div className="span note"><strong>{humanise(replacement.rule)}</strong>&nbsp;{replacement.minDays ? `minimum ${replacement.minDays} days` : replacement.extensionMonths ? `${replacement.extensionMonths} month extension` : replacement.description ?? "Configured replacement rule"}.</div>}</div></div></section>
          <section className="card"><div className="card-header"><h2>Refurbished policies <span className="meta">by item, grade, channel and region</span></h2>{editable && <button type="button" className="btn ghost sm" onClick={() => setTerms({ ...detail.terms, refurbished: [...detail.terms.refurbished, { itemId: null, grade: "refurbished_a", channel: "consumer", region: "EU", durationMonths: 12, statutoryFloorMonths: 12, statementKind: "seller_refurbished" }] })}>Add row</button>}</div><table className="table"><thead><tr><th>Item · grade</th><th>Channel · region</th><th className="num">Term</th><th className="num">Floor</th><th>Statement</th></tr></thead><tbody>{detail.terms.refurbished.map((row, index) => editable ? <tr key={index}>
            <td><input className="input serial" aria-label={`Refurbished row ${index + 1} item id`} value={row.itemId ?? ""} placeholder="Any item" onChange={(event) => setTerms({ ...detail.terms, refurbished: detail.terms.refurbished.map((value, i) => i === index ? { ...value, itemId: event.target.value || null } : value) })} /><input className="input" aria-label={`Refurbished row ${index + 1} grade`} value={row.grade} onChange={(event) => setTerms({ ...detail.terms, refurbished: detail.terms.refurbished.map((value, i) => i === index ? { ...value, grade: event.target.value } : value) })} /></td>
            <td><input className="input" aria-label={`Refurbished row ${index + 1} channel`} value={row.channel} onChange={(event) => setTerms({ ...detail.terms, refurbished: detail.terms.refurbished.map((value, i) => i === index ? { ...value, channel: event.target.value } : value) })} /><input className="input" aria-label={`Refurbished row ${index + 1} region`} value={row.region} onChange={(event) => setTerms({ ...detail.terms, refurbished: detail.terms.refurbished.map((value, i) => i === index ? { ...value, region: event.target.value } : value) })} /></td>
            <td><input className="input" aria-label={`Refurbished row ${index + 1} duration months`} type="number" value={row.durationMonths} onChange={(event) => setTerms({ ...detail.terms, refurbished: detail.terms.refurbished.map((value, i) => i === index ? { ...value, durationMonths: Number(event.target.value) } : value) })} /></td>
            <td><input className="input" aria-label={`Refurbished row ${index + 1} statutory floor months`} type="number" value={row.statutoryFloorMonths ?? ""} onChange={(event) => setTerms({ ...detail.terms, refurbished: detail.terms.refurbished.map((value, i) => i === index ? { ...value, statutoryFloorMonths: nullableNumber(event.target.value) } : value) })} /></td>
            <td><select className="select" aria-label={`Refurbished row ${index + 1} statement`} value={row.statementKind} onChange={(event) => setTerms({ ...detail.terms, refurbished: detail.terms.refurbished.map((value, i) => i === index ? { ...value, statementKind: event.target.value } : value) })}><option value="no_oem_warranty">No OEM warranty</option><option value="oem_refurbished">OEM refurbished</option><option value="seller_refurbished">Seller refurbished</option></select><button type="button" className="btn ghost sm" onClick={() => setTerms({ ...detail.terms, refurbished: detail.terms.refurbished.filter((_, i) => i !== index) })}>Remove</button></td>
          </tr> : <tr key={`${row.itemId}:${row.grade}:${row.channel}:${row.region}:${index}`}><td className="primary">{row.itemId ? shortId(row.itemId) : "Any item"}<span className="sub">{humanise(row.grade)}</span></td><td>{humanise(row.channel)}<span className="sub">{row.region}</span></td><td className="num">{row.durationMonths} m</td><td className="num">{row.statutoryFloorMonths ? `${row.statutoryFloorMonths} m` : "—"}</td><td>{humanise(row.statementKind)}</td></tr>)}{detail.terms.refurbished.length === 0 && <tr><td colSpan={5} className="muted" style={{ textAlign: "center", height: 72 }}>No refurbished rows on this version.</td></tr>}</tbody></table></section>
          {editable && <div className="actions"><button type="button" className="btn primary" disabled={busy} aria-busy={busy} onClick={save}>Save draft</button></div>}
        </div> : <section className="card"><div className="empty"><h2>No policy version selected</h2><p className="hint">Choose a policy and version to inspect its terms.</p></div></section>}
      </div>
    </main>
    <Rail label="Policy reference"><Section title="Selected version"><Kv rows={[["Policy", detail?.policyCode ?? "—"], ["Version", detail ? `v${detail.version}` : "—"], ["Status", humanise(detail?.status)], ["Rule version", detail?.ruleVersion ?? "—"], ["Tenant currency", currency?.currency ?? "not configured"], ["Live entitlements", selectedSummary?.liveEntitlements.toLocaleString() ?? "—"]]} /></Section><Section title="Tenant rules"><Kv rows={[["Approval reasons", approval?.reasons.length.toLocaleString() ?? "—"], ["Reduction role", humanise(approval?.reductionRole)], ["Second-hand floors", refurbished?.floors.length.toLocaleString() ?? "—"], ["Service-contract jurisdictions", serviceRules?.jurisdictions.length.toLocaleString() ?? "—"]]} /></Section><Section title="Versioning"><p className="muted">A published version stays immutable. Existing entitlements retain it; publication changes resolution only for contracts on or after the new effective date.</p></Section></Rail>
  </>;
}

function Field({ label, id, value, onChange, type = "text", disabled, span }: { label: string; id: string; value: string; onChange: (value: string) => void; type?: string; disabled: boolean; span?: boolean }) { return <div className={`field${span ? " span" : ""}`}><label htmlFor={id}>{label}</label><input id={id} className="input" type={type} value={value} disabled={disabled} onChange={(event) => onChange(event.target.value)} /></div>; }
function Select({ label, id, value, options, onChange, disabled }: { label: string; id: string; value: string; options: string[]; onChange: (value: string) => void; disabled: boolean }) { return <div className="field"><label htmlFor={id}>{label}</label><select id={id} className="select" value={value} disabled={disabled} onChange={(event) => onChange(event.target.value)}>{options.map((option) => <option value={option} key={option}>{humanise(option)}</option>)}</select></div>; }
