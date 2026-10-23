import { useEffect, useState, type ReactNode } from "react";
import { Link } from "react-router";
import { claimsApi } from "../claims/api";
import { claimRiskRuleLabel } from "../claims/format";
import type { ClaimRiskRulesPolicy } from "../claims/types";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { STAFF_ROLES, roleLabel } from "./roles";
import "../claims/claims.css";

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  const [policy, setPolicy] = useState<ClaimRiskRulesPolicy | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    const controller = new AbortController();
    claimsApi.riskRules(controller.signal).then(setPolicy)
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); })
      .finally(() => setLoading(false));
    return () => controller.abort();
  }, []);
  const save = async () => {
    if (!policy) return;
    setBusy(true); setSaved(false); setError(null);
    try { setPolicy(await claimsApi.saveRiskRules(policy)); setSaved(true); }
    catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };

  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Claims risk rules" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · Claims · {permitted ? "tenant administrator" : "read only"}</div><h1>Claims risk rules</h1><p className="sub">Choose which deterministic checks hold a claim for a second review. A flag never denies a claim; thresholds apply to later evaluations.</p></div><div className="actions"><Link className="btn" to="/claims/risk-review">Risk review</Link>{permitted && <button type="button" className="btn primary" disabled={loading || busy || !policy} aria-busy={busy} onClick={() => void save()}><Icon name="check" />Save</button>}</div></div>
    {!permitted && <div className="note" style={{ marginBottom: 16 }}><strong>Read only.</strong>&nbsp;A tenant administrator can change claim risk rules.</div>}
    <ErrorNote error={error} />
    {saved && <div className="note ok" role="status" style={{ marginBottom: 16 }}><strong>Claim risk rules saved.</strong>&nbsp;Later claim evaluations now use these settings.</div>}
    {loading && <p className="muted">Loading claim risk rules…</p>}
    {policy && <div className="stack">
      <section className="card" aria-labelledby="claim-risk-rules"><div className="card-header"><h2 id="claim-risk-rules">Rules</h2><span className="meta">rules only · no scoring model</span></div><div className="card-body claim-risk-rule-settings">
        <RuleRow label={claimRiskRuleLabel("recent_cover_purchase")} detail="Covering add-on bought shortly before the incident" enabled={policy.recentCoverPurchase.enabled} disabled={!permitted} onEnabled={(enabled) => setPolicy({ ...policy, recentCoverPurchase: { ...policy.recentCoverPurchase, enabled } })}><NumberField id="risk-recent-days" label="Window" suffix="days" min={1} max={3650} value={policy.recentCoverPurchase.days} disabled={!permitted || !policy.recentCoverPurchase.enabled} onChange={(days) => setPolicy({ ...policy, recentCoverPurchase: { ...policy.recentCoverPurchase, days } })} /></RuleRow>
        <RuleRow label={claimRiskRuleLabel("repeat_unit_claims")} detail="Several earlier claims on the same unit" enabled={policy.repeatUnitClaims.enabled} disabled={!permitted} onEnabled={(enabled) => setPolicy({ ...policy, repeatUnitClaims: { ...policy.repeatUnitClaims, enabled } })}><NumberField id="risk-repeat-count" label="Earlier claims" min={1} max={100} value={policy.repeatUnitClaims.minimumEarlierClaims} disabled={!permitted || !policy.repeatUnitClaims.enabled} onChange={(minimumEarlierClaims) => setPolicy({ ...policy, repeatUnitClaims: { ...policy.repeatUnitClaims, minimumEarlierClaims } })} /><NumberField id="risk-repeat-days" label="Within" suffix="days" min={1} max={3650} value={policy.repeatUnitClaims.days} disabled={!permitted || !policy.repeatUnitClaims.enabled} onChange={(days) => setPolicy({ ...policy, repeatUnitClaims: { ...policy.repeatUnitClaims, days } })} /></RuleRow>
        <RuleRow label={claimRiskRuleLabel("repeat_fault")} detail="The same reported cause appeared before" enabled={policy.repeatFault.enabled} disabled={!permitted} onEnabled={(enabled) => setPolicy({ ...policy, repeatFault: { ...policy.repeatFault, enabled } })}><NumberField id="risk-fault-days" label="Window" suffix="days" min={1} max={3650} value={policy.repeatFault.days} disabled={!permitted || !policy.repeatFault.enabled} onChange={(days) => setPolicy({ ...policy, repeatFault: { ...policy.repeatFault, days } })} /></RuleRow>
        <RuleRow label={claimRiskRuleLabel("claimant_rate")} detail="Claims per installed unit exceed the tenant baseline" enabled={policy.claimantRate.enabled} disabled={!permitted} onEnabled={(enabled) => setPolicy({ ...policy, claimantRate: { ...policy.claimantRate, enabled } })}><NumberField id="risk-rate-window" label="Window" suffix="days" min={1} max={3650} value={policy.claimantRate.days} disabled={!permitted || !policy.claimantRate.enabled} onChange={(days) => setPolicy({ ...policy, claimantRate: { ...policy.claimantRate, days } })} /><NumberField id="risk-rate-multiplier" label="Over tenant rate" suffix="×" min={1.01} max={100} step={0.01} value={policy.claimantRate.rateMultiplier} disabled={!permitted || !policy.claimantRate.enabled} onChange={(rateMultiplier) => setPolicy({ ...policy, claimantRate: { ...policy.claimantRate, rateMultiplier } })} /><NumberField id="risk-rate-claims" label="Minimum claims" min={1} max={10000} value={policy.claimantRate.minimumClaims} disabled={!permitted || !policy.claimantRate.enabled} onChange={(minimumClaims) => setPolicy({ ...policy, claimantRate: { ...policy.claimantRate, minimumClaims } })} /><NumberField id="risk-rate-units" label="Minimum units" min={1} max={1000000} value={policy.claimantRate.minimumUnits} disabled={!permitted || !policy.claimantRate.enabled} onChange={(minimumUnits) => setPolicy({ ...policy, claimantRate: { ...policy.claimantRate, minimumUnits } })} /></RuleRow>
        <RuleRow label={claimRiskRuleLabel("channel_mismatch")} detail="Claimant is outside the unit’s recorded sale, ownership or custody chain" enabled={policy.channelMismatch.enabled} disabled={!permitted} onEnabled={(enabled) => setPolicy({ ...policy, channelMismatch: { enabled } })} />
        <RuleRow label={claimRiskRuleLabel("recent_ownership_change")} detail="Ownership started shortly before the incident" enabled={policy.recentOwnershipChange.enabled} disabled={!permitted} onEnabled={(enabled) => setPolicy({ ...policy, recentOwnershipChange: { ...policy.recentOwnershipChange, enabled } })}><NumberField id="risk-owner-days" label="Window" suffix="days" min={1} max={3650} value={policy.recentOwnershipChange.days} disabled={!permitted || !policy.recentOwnershipChange.enabled} onChange={(days) => setPolicy({ ...policy, recentOwnershipChange: { ...policy.recentOwnershipChange, days } })} /></RuleRow>
        <RuleRow label={claimRiskRuleLabel("part_not_returned")} detail="An earlier replacement part was overdue at the incident date" enabled={policy.partNotReturned.enabled} disabled={!permitted} onEnabled={(enabled) => setPolicy({ ...policy, partNotReturned: { enabled } })} />
        <RuleRow label={claimRiskRuleLabel("duplicate_open_claim")} detail="The same unit or part serial is already on an open claim" enabled={policy.duplicateOpenClaim.enabled} disabled={!permitted} onEnabled={(enabled) => setPolicy({ ...policy, duplicateOpenClaim: { enabled } })} />
      </div></section>
      <section className="card" aria-labelledby="claim-risk-reviewer"><div className="card-header"><h2 id="claim-risk-reviewer">Second reviewer</h2><span className="meta">applies to every enabled rule</span></div><div className="card-body form-grid">
        <div className="field"><label htmlFor="claim-risk-reviewer-role">Reviewer role</label><select id="claim-risk-reviewer-role" className="select" style={{ maxWidth: 320 }} value={policy.reviewerRole} disabled={!permitted} onChange={(event) => setPolicy({ ...policy, reviewerRole: event.target.value })}>{STAFF_ROLES.map((role) => <option value={role} key={role}>{roleLabel(role)}</option>)}</select><span className="help">A reviewer must hold this tenant role when the decision is saved.</span></div>
        <label className="checkrow claim-risk-reviewer-switch"><input type="checkbox" checked={policy.requireDifferentReviewer} disabled={!permitted} onChange={(event) => setPolicy({ ...policy, requireDifferentReviewer: event.target.checked })} /><span><strong>The person who submitted a claim cannot review it</strong><span className="help">Require another person to make every flag decision.</span></span></label>
      </div></section>
    </div>}
  </main>;
}

function RuleRow({ label, detail, enabled, disabled, onEnabled, children }: { label: string; detail: string; enabled: boolean; disabled: boolean; onEnabled: (enabled: boolean) => void; children?: ReactNode | undefined }) {
  return <div className="claim-risk-rule-row"><label className="checkrow"><input type="checkbox" checked={enabled} disabled={disabled} onChange={(event) => onEnabled(event.target.checked)} /><span><strong>{label}</strong><span className="help">{detail}</span></span></label><div className="claim-risk-thresholds">{children ?? <span className="muted">No numeric threshold</span>}</div></div>;
}

function NumberField({ id, label, suffix, value, min, max, step = 1, disabled, onChange }: { id: string; label: string; suffix?: string | undefined; value: number; min: number; max: number; step?: number | undefined; disabled: boolean; onChange: (value: number) => void }) {
  return <div className="field"><label htmlFor={id}>{label}</label><div className="claim-risk-number"><input id={id} className="input num" type="number" min={min} max={max} step={step} value={value} disabled={disabled} onChange={(event) => onChange(Number(event.target.value))} />{suffix && <span>{suffix}</span>}</div></div>;
}
