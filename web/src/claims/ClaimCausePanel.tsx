import { useEffect, useState } from "react";
import { StatusBadge } from "../components";
import { ErrorNote } from "../units/common";
import { fmtDate, humanise } from "../units/format";
import { coverItemsApi, coverLabel, type ClaimCause, type ClaimGuidance } from "../entitlements/coverItems";

export function ClaimCausePanel({ unitId, cause, claimDate, onCause, description, onDescription, occurredDate, onOccurredDate, onGuidance }: {
  unitId: string | null; cause: string; onCause: (v: string) => void; description: string; onDescription: (v: string) => void;
  claimDate: string; occurredDate: string; onOccurredDate: (v: string) => void; onGuidance: (g: ClaimGuidance | null) => void;
}) {
  const [causes, setCauses] = useState<ClaimCause[]>([]);
  const [guidance, setGuidance] = useState<ClaimGuidance | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { const c = new AbortController(); coverItemsApi.causes(c.signal).then((p) => setCauses(p.items)).catch((e) => { if (!c.signal.aborted) setError(e); }); return () => c.abort(); }, []);
  useEffect(() => {
    setGuidance(null); onGuidance(null); setError(null);
    if (!unitId || !cause) return;
    const c = new AbortController();
    coverItemsApi.guidance(unitId, cause, claimDate, occurredDate, c.signal).then((g) => { setGuidance(g); onGuidance(g); }).catch((e) => { if (!c.signal.aborted) setError(e); });
    return () => c.abort();
  }, [unitId, cause, claimDate, occurredDate]); // guidance is an advisory snapshot; changing the desk's family never reselects it
  return <div className="cols-3-2" style={{ alignItems: "start" }}><section className="card"><div className="card-header"><h2>What happened? <span className="meta">the customer’s words</span></h2></div><div className="card-body">
    <CauseChips causes={causes} selected={cause} onChange={onCause} />
    <div className="form-grid" style={{ marginTop: 16 }}><label className="field">Customer’s description<textarea className="textarea" maxLength={4000} value={description} onChange={(e) => onDescription(e.target.value)} /></label><label className="field">When did it happen?<input className="input" type="date" value={occurredDate} onChange={(e) => onOccurredDate(e.target.value)} /></label></div>
  </div></section><div className="stack"><ErrorNote error={error} />
    {guidance ? <ClaimGuidanceCard guidance={guidance} /> : <section className="card"><div className="card-body"><p className="muted">{unitId ? "Pick a cause to see the live covers that answer it." : "Resolve a unit to see guidance."}</p><p className="hint">Guidance only · adjudication confirms the cover</p></div></section>}
  </div></div>;
}
export function CauseChips({ causes, selected, onChange }: { causes: ClaimCause[]; selected: string; onChange: (v: string) => void }) {
  return <div className="cause-options" role="group" aria-label="What happened?">{causes.filter((c) => c.active).map((c) => <button type="button" className="cause-option" aria-pressed={selected === c.code} key={c.code} onClick={() => onChange(c.code)}>{c.customerWording}</button>)}</div>;
}
export function ClaimGuidanceCard({ guidance }: { guidance: ClaimGuidance }) {
  return <section className="card"><div className="card-header"><h2>Which cover applies</h2><StatusBadge tone={guidance.covered ? "ok" : "warn"}>{guidance.covered ? "Covered" : "No granting cover recorded"}</StatusBadge></div><div className="card-body stack">
    {guidance.grants.map((g) => <div className="note" key={g.cover.entitlementId}><strong>{coverLabel(g.cover)}</strong><p>Until {fmtDate(g.cover.until)}</p>{g.waitingPeriodUntil && <p>Inside the waiting period until {fmtDate(g.waitingPeriodUntil)}</p>}{g.usage.map((u) => <p key={u.meterType}>{u.now} {humanise(u.meterType)} remaining now · {u.afterThisClaim ?? "—"} after this claim · {u.period === "contract_year" ? "contract year" : "term"}</p>)}{g.items.map((i) => <p key={i.code}>{i.customerWording}{i.partsScope.partCategories.length || i.partsScope.itemFamilies.length ? ` · applies to ${i.partsScopeDescription ?? "the recorded parts scope"}` : ""}</p>)}</div>)}
    {guidance.excludes.map((x) => <p key={`${x.cover.entitlementId}:${x.code}`}>{coverLabel(x.cover)} excludes it · {x.customerWording}</p>)}
    <div className="note warn"><strong>Say to the customer</strong><p>{guidance.sayToCustomer}</p></div></div><div className="card-footer"><span className="muted">{guidance.notice}</span></div></section>;
}
