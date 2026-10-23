import { useEffect, useState } from "react";
import { Link } from "react-router";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { useSession } from "../session/session";
import { ErrorNote } from "../units/common";
import { refurbishmentsApi } from "../refurbishments/api";
import { identityTitle } from "../refurbishments/format";
import type { RefurbishmentItemClassPolicy, RefurbishmentPolicy } from "../refurbishments/types";
import "../refurbishments/refurbishments.css";
import { duplicateRows, policyProblems } from "./refurbishmentPolicyModel";
import { ItemFamilyField } from "../corrections/ReferenceFields";

const IDENTITIES = [
  { value: "same_unit_same_serial", detail: "Keep the physical unit and its current serial." },
  { value: "same_unit_new_serial", detail: "Keep the physical unit; issue a new serial linked to its predecessor." },
  { value: "new_unit", detail: "Create a derived unit and apply the configured replacement policy." },
] as const;

export function Component() {
  const session = useSession();
  const permitted = session.roles.includes("tenant_administrator");
  const [policy, setPolicy] = useState<RefurbishmentPolicy | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { const controller = new AbortController(); refurbishmentsApi.policy(controller.signal).then(setPolicy).catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); }).finally(() => setLoading(false)); return () => controller.abort(); }, []);
  const updateOverride = (index: number, patch: Partial<RefurbishmentItemClassPolicy>) => policy && setPolicy({ ...policy, itemClasses: policy.itemClasses.map((row, rowIndex) => rowIndex === index ? { ...row, ...patch } : row) });
  const duplicates = policy ? duplicateRows(policy) : new Set<number>();
  const problems = policy ? policyProblems(policy, duplicates) : [];
  const save = async () => {
    if (!policy || invalid(policy)) return; setBusy(true); setSaved(false); setError(null);
    try { setPolicy(await refurbishmentsApi.savePolicy(policy)); setSaved(true); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Refurbishment policy" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · {permitted ? "tenant administrator" : "read only"}</div><h1>Refurbishment policy</h1><p className="sub">Set the default identity outcome and cycle limit, then override them for named item classes. A work order stores the resolved policy when it opens.</p></div><div className="actions"><Link className="btn" to="/refurbishments">Back to work orders</Link>{permitted && <button type="button" className="btn primary" disabled={loading || busy || !policy || invalid(policy)} onClick={() => void save()}><Icon name="check" />Save</button>}</div></div>
    {!permitted && <div className="note" style={{ marginBottom: 16 }}><strong>Read only.</strong>&nbsp;A tenant administrator can change this policy.</div>}
    <ErrorNote error={error} />{saved && <div className="note ok" style={{ marginBottom: 16 }}><strong>Refurbishment policy saved.</strong>&nbsp;New work orders now resolve against these defaults and overrides.</div>}
    {loading && <p className="muted">Loading refurbishment policy…</p>}
    {policy && <div className="stack">
      <section className="card" aria-labelledby="refurb-default-identity"><div className="card-header"><h2 id="refurb-default-identity">Default identity</h2><span className="meta">used when no item-class override matches</span></div><div className="card-body"><div className="refurb-identities" role="radiogroup" aria-label="Default refurbishment identity">{IDENTITIES.map((option) => <label key={option.value} className={policy.defaultIdentity === option.value ? "on" : ""}><input type="radio" name="default-refurb-identity" checked={policy.defaultIdentity === option.value} disabled={!permitted} onChange={() => setPolicy({ ...policy, defaultIdentity: option.value })} /><span>{identityTitle(option.value)}<span className="s">{option.detail}</span></span></label>)}</div></div></section>
      <div className="stack"><section className="card" aria-labelledby="refurb-overrides"><div className="card-header"><h2 id="refurb-overrides">Item-class overrides</h2>{permitted && <button type="button" className="btn sm" onClick={() => setPolicy({ ...policy, itemClasses: [...policy.itemClasses, { itemClass: "", identity: policy.defaultIdentity, maxCycles: policy.defaultMaxCycles, replacementPolicyCode: null }] })}><Icon name="plus" />Add override</button>}</div><div className="card-body">{policy.itemClasses.length === 0 ? <p className="muted">No item-class overrides. Every class uses the tenant default.</p> : policy.itemClasses.map((row, index) => <div className="refurb-policy-row" key={index}><ItemFamilyField id={`refurb-class-${index}`} label="Item class" value={row.itemClass || null} disabled={!permitted} invalid={duplicates.has(index)} hint={duplicates.has(index) ? "Already overridden above" : undefined} onChange={(itemClass) => updateOverride(index, { itemClass: itemClass ?? "" })} /><div className="field"><label htmlFor={`refurb-identity-${index}`}>Identity</label><select id={`refurb-identity-${index}`} className="select" value={row.identity} disabled={!permitted} onChange={(event) => updateOverride(index, { identity: event.target.value })}>{IDENTITIES.map((option) => <option key={option.value} value={option.value}>{identityTitle(option.value)}</option>)}</select></div><div className="field"><label htmlFor={`refurb-cycles-${index}`}>Max cycles</label><input id={`refurb-cycles-${index}`} className="input" type="number" min="1" max="100" value={row.maxCycles} disabled={!permitted} onChange={(event) => updateOverride(index, { maxCycles: Number(event.target.value) })} /></div><div className="field"><label htmlFor={`refurb-replacement-${index}`}>Replacement policy <span className="hint">· policy (c)</span></label><input id={`refurb-replacement-${index}`} className="input" value={row.replacementPolicyCode ?? ""} disabled={!permitted} onChange={(event) => updateOverride(index, { replacementPolicyCode: event.target.value.trim() || null })} /></div>{permitted && <button type="button" className="icon-btn row-remove" title="Remove override" aria-label={`Remove override ${row.itemClass || index + 1}`} onClick={() => setPolicy({ ...policy, itemClasses: policy.itemClasses.filter((_, rowIndex) => rowIndex !== index) })}><Icon name="x" /></button>}</div>)}{problems.length > 0 && <div className="note bad" role="alert">{problems.map((problem) => <div key={problem}>{problem}</div>)}</div>}</div></section>
        <div className="cols-2 policy-pair"><section className="card" aria-labelledby="refurb-default-cycles"><div className="card-header"><h2 id="refurb-default-cycles">Default maximum cycles</h2></div><div className="card-body"><div className="field"><label htmlFor="default-refurb-cycles">Cycles</label><input id="default-refurb-cycles" className="input" type="number" min="1" max="100" value={policy.defaultMaxCycles} disabled={!permitted} onChange={(event) => setPolicy({ ...policy, defaultMaxCycles: Number(event.target.value) })} /><span className="help">Opening another refurbishment is refused after this limit.</span></div></div></section><section className="card" aria-labelledby="refurb-default-replacement"><div className="card-header"><h2 id="refurb-default-replacement">Default replacement policy</h2><span className="meta">new derived unit only</span></div><div className="card-body"><div className="field"><label htmlFor="default-refurb-replacement">Policy code <span className="hint">· optional</span></label><input id="default-refurb-replacement" className="input" value={policy.defaultReplacementPolicyCode ?? ""} disabled={!permitted} onChange={(event) => setPolicy({ ...policy, defaultReplacementPolicyCode: event.target.value.trim() || null })} /><span className="help">Required when policy (c) creates a new unit that must receive live entitlements.</span></div></div></section></div>
      </div>
    </div>}
  </main>;
}

function invalid(policy: RefurbishmentPolicy): boolean {
  if (!Number.isInteger(policy.defaultMaxCycles) || policy.defaultMaxCycles < 1 || policy.defaultMaxCycles > 100) return true;
  const classes = policy.itemClasses.map((row) => row.itemClass.trim().toLowerCase());
  return classes.some((value) => !value) || new Set(classes).size !== classes.length || policy.itemClasses.some((row) => !Number.isInteger(row.maxCycles) || row.maxCycles < 1 || row.maxCycles > 100 || row.identity === "new_unit" && !row.replacementPolicyCode?.trim());
}

