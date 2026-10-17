import { useEffect, useState } from "react";
import { Link } from "react-router";
import { ApiError } from "../api/client";
import { LINE_KINDS, humanise } from "../claims/format";
import { recoveryApi } from "../recovery/api";
import type { RecoveryPolicy, RecoveryWholeUnitRule } from "../recovery/types";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import "../claims/claims.css";

export function Component() {
  const session = useSession();
  const permitted = session.roles.includes("tenant_administrator");
  const [policy, setPolicy] = useState<RecoveryPolicy | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    const controller = new AbortController();
    recoveryApi.policy(controller.signal)
      .then(setPolicy)
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); })
      .finally(() => setLoading(false));
    return () => controller.abort();
  }, []);

  const updateRule = (wholeUnitLines: RecoveryWholeUnitRule) => policy && setPolicy({ ...policy, wholeUnitLines });
  const toggleKind = (kind: string) => policy && setPolicy({ ...policy, lineKinds: policy.lineKinds.includes(kind) ? policy.lineKinds.filter((value) => value !== kind) : [...policy.lineKinds, kind] });
  const save = async () => {
    if (!policy || policy.lineKinds.length === 0 || !Number.isInteger(policy.vendorAnswerDueDays) || policy.vendorAnswerDueDays < 1 || policy.vendorAnswerDueDays > 90) return;
    setBusy(true); setSaved(false); setError(null);
    try { setPolicy(await recoveryApi.savePolicy(policy)); setSaved(true); }
    catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };

  const invalidKinds = policy !== null && policy.lineKinds.length === 0;
  const invalidDays = policy !== null && (!Number.isInteger(policy.vendorAnswerDueDays) || policy.vendorAnswerDueDays < 1 || policy.vendorAnswerDueDays > 90);
  const example = policy?.wholeUnitLines === "named_part_only"
    ? "Example · A unit contains one covered drive. A parts line on the whole unit raises no recovery; name that drive on the line to recover from its vendor."
    : "Example · A unit contains one covered drive, so a whole-unit parts line recovers from that drive’s vendor. If two parts carry live terms, recovery is not raised and the claim is listed with the reason.";

  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Supplier recovery" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · {permitted ? "tenant administrator" : "read only"}</div><h1>Supplier recovery</h1><p className="sub">Choose when approved customer-claim lines raise a recovery claim against a live vendor term. Changes apply to later approvals only.</p></div><div className="actions"><Link className="btn" to="/settings">Cancel</Link>{permitted && <button type="button" className="btn primary" disabled={loading || busy || !policy || invalidKinds || invalidDays} aria-busy={busy} onClick={() => void save()}><Icon name="check" />Save</button>}</div></div>
    {!permitted && <div className="note" style={{ marginBottom: 16 }}><strong>Read only.</strong>&nbsp;A tenant administrator can change this policy.</div>}
    <PolicyError error={error} />
    {saved && <div className="note ok" style={{ marginBottom: 16 }}><strong>Supplier recovery policy saved.</strong>&nbsp;New claim approvals now use these rules.</div>}
    {loading && <p className="muted">Loading supplier recovery policy…</p>}
    {policy && <div className="cols-3-2 top">
      <section className="card" aria-labelledby="whole-unit-lines"><div className="card-header"><h2 id="whole-unit-lines">Whole-unit claim lines</h2><span className="meta">when the line does not name a part</span></div><div className="card-body stack">
        <div className="field"><label className="radiorow"><input type="radio" name="whole-unit-lines" checked={policy.wholeUnitLines === "single_covered_part"} disabled={!permitted} onChange={() => updateRule("single_covered_part")} /><span><strong>Recover from the one covered part</strong><span className="help">When exactly one part inside the unit has a live vendor term, recover from that vendor. With several, raise nothing and list the claim with the reason.</span></span></label></div>
        <div className="field"><label className="radiorow"><input type="radio" name="whole-unit-lines" checked={policy.wholeUnitLines === "named_part_only"} disabled={!permitted} onChange={() => updateRule("named_part_only")} /><span><strong>Recover only when the line names the part</strong><span className="help">A whole-unit line raises no recovery from parts inside it. The claim line must identify the covered part.</span></span></label></div>
        <div className="note">{example}</div>
      </div></section>
      <div className="stack">
        <section className="card" aria-labelledby="recovery-line-kinds"><div className="card-header"><h2 id="recovery-line-kinds">Line kinds that raise recovery</h2><span className="meta">at least one</span></div><div className="card-body stack">
          {LINE_KINDS.map((kind) => <div className="field" key={kind}><label className="radiorow"><input type="checkbox" checked={policy.lineKinds.includes(kind)} disabled={!permitted} onChange={() => toggleKind(kind)} /><span><strong>{humanise(kind)}</strong><span className="help">Approved {kind} lines may recover against a live vendor term.</span></span></label></div>)}
          {invalidKinds && <div className="form-error" role="alert">Choose at least one claim line kind.</div>}
        </div></section>
        <section className="card" aria-labelledby="vendor-answer-due"><div className="card-header"><h2 id="vendor-answer-due">Vendor answer due</h2><span className="meta">waiting threshold</span></div><div className="card-body stack"><div className="field"><label htmlFor="vendor-answer-days">Vendor answer due after N days</label><input id="vendor-answer-days" className="input" type="number" min="1" max="90" step="1" value={policy.vendorAnswerDueDays} disabled={!permitted} aria-invalid={invalidDays} onChange={(event) => setPolicy({ ...policy, vendorAnswerDueDays: Number(event.target.value) })} /><span className="help">From 1 to 90 days. An unanswered recovery claim changes from Raised to Waiting at this age.</span></div>{invalidDays && <div className="form-error" role="alert">Enter a whole number from 1 to 90 days.</div>}</div></section>
      </div>
    </div>}
  </main>;
}

function PolicyError({ error }: { error: unknown }) {
  if (!error) return null;
  const title = error instanceof ApiError ? error.rule : null;
  const detail = error instanceof Error ? error.message : String(error);
  return <div className="form-error" role="alert" style={{ marginBottom: 16 }}>{title && <><strong>{humanise(title)}</strong> · </>}{detail}</div>;
}
