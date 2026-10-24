import { useEffect, useState } from "react";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { ErrorNote } from "../units/common";
import { scanningApi, type ScanningPolicy } from "./scanningApi";

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  const [policy, setPolicy] = useState<ScanningPolicy | null>(null);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    if (!permitted) return;
    let current = true;
    void scanningApi.get().then(value => { if (current) setPolicy(value); })
      .catch(reason => { if (current) setError(reason); });
    return () => { current = false; };
  }, [permitted]);
  const save = async () => {
    if (!policy) return;
    setBusy(true); setSaved(false); setError(null);
    try { setPolicy(await scanningApi.save(policy)); setSaved(true); }
    catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };
  if (!permitted) return <div className="note bad"><strong>Tenant administrator access is required.</strong></div>;
  return <>
    <div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Scanning</h1><p className="sub">Tune keyboard-wedge completion and repeat protection for this organisation.</p></div><div className="actions"><button type="button" className="btn primary" disabled={busy || !policy} onClick={() => void save()}><Icon name="check" />{busy ? "Saving…" : "Save"}</button></div></div>
    <ErrorNote error={error} />
    {saved && <div className="note ok"><strong>Scanning settings saved.</strong>&nbsp;New scan boxes use them after the next sign-in session.</div>}
    {!policy && !error && <p className="muted">Loading scanning settings…</p>}
    {policy && <section className="card top" aria-labelledby="scan-timing"><div className="card-header"><h2 id="scan-timing">Keyboard-wedge timing</h2></div><div className="card-body stack">
      <label className="field"><span className="lbl">End pause</span><span className="input-unit"><input className="input num" type="number" min="150" max="1000" value={policy.endPauseMs} onChange={event => setPolicy({ ...policy, endPauseMs: Number(event.target.value) })} /><span>ms</span></span><span className="help">A scanner-speed value submits after this quiet period. Typed text never submits itself.</span></label>
      <label className="field"><span className="lbl">Repeat window</span><span className="input-unit"><input className="input num" type="number" min="0" max="10000" value={policy.repeatWindowMs} onChange={event => setPolicy({ ...policy, repeatWindowMs: Number(event.target.value) })} /><span>ms</span></span><span className="help">The same label read again in this period is ignored. Set 0 to turn repeat suppression off.</span></label>
    </div></section>}
  </>;
}
