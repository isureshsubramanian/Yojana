import { useEffect, useState } from "react";
import { useBlocker } from "react-router";
import { Icon } from "../shell/Icons";
import { ErrorNote } from "../units/common";
import { numberingApi, type NumberingMode, type NumberingSeriesSetting } from "./numberingApi";
import { numberingChanges } from "./numberingModel";

const modeGap = (mode: NumberingMode) => mode === "sequence"
  ? "Faster under concurrency; a rolled-back transaction may leave a gap."
  : "Creators queue; committed numbers remain consecutive.";

export function Component() {
  const [saved, setSaved] = useState<NumberingSeriesSetting[]>([]);
  const [draft, setDraft] = useState<NumberingSeriesSetting[]>([]);
  const [loading, setLoading] = useState(true); const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null); const [savedNotice, setSavedNotice] = useState(false);
  const changes = numberingChanges(saved, draft); const dirty = changes.length > 0;
  const blocker = useBlocker(dirty && !busy);

  useEffect(() => {
    const controller = new AbortController();
    numberingApi.settings(controller.signal).then(value => { setSaved(value.series); setDraft(value.series); })
      .catch(reason => { if (!controller.signal.aborted) setError(reason); }).finally(() => setLoading(false));
    return () => controller.abort();
  }, []);
  useEffect(() => {
    if (!dirty) return;
    const guard = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ""; };
    window.addEventListener("beforeunload", guard);
    return () => window.removeEventListener("beforeunload", guard);
  }, [dirty]);

  const setMode = (series: string, mode: NumberingMode) => {
    setSavedNotice(false); setDraft(current => current.map(row => row.series === series ? { ...row, mode } : row));
  };
  const save = async () => {
    setBusy(true); setError(null); setSavedNotice(false);
    const results = await Promise.allSettled(changes.map(change => numberingApi.setMode(change.series, change.mode)));
    const successful = new Map(results.flatMap(result => result.status === "fulfilled" ? [[result.value.setting.series, result.value.setting] as const] : []));
    setSaved(current => current.map(row => successful.get(row.series) ?? row));
    setDraft(current => current.map(row => successful.get(row.series) ?? row));
    const failed = results.find(result => result.status === "rejected");
    if (failed?.status === "rejected") setError(failed.reason); else setSavedNotice(true);
    setBusy(false);
  };

  return <main className="content" id="main" tabIndex={-1}>
    <div className="page-header"><div><div className="eyebrow">Settings · Documents · {draft.length} series</div><h1>Document numbering</h1><p className="sub">How each document series hands out numbers. Saved changes apply to the next number issued and are audited.</p></div><div className="actions"><button type="button" className="btn" disabled={busy || !dirty} onClick={() => setDraft(saved)}>Cancel</button><button type="button" className="btn primary" disabled={busy || !dirty} onClick={() => void save()}><Icon name="check" />{busy ? "Saving…" : "Save"}</button></div></div>
    {blocker.state === "blocked" && <div className="note warn settings-unsaved" role="alert"><strong>Unsaved numbering changes.</strong><span>Save or cancel them before leaving, or discard them now.</span><span className="actions"><button type="button" className="btn" onClick={() => blocker.reset()}>Stay</button><button type="button" className="btn" onClick={() => blocker.proceed()}>Discard and leave</button></span></div>}
    <ErrorNote error={error} />{savedNotice && <div className="note ok"><strong>Document numbering saved.</strong>&nbsp;All changed rows were applied.</div>}
    <div className="note numbering-guidance"><strong>Sequence is the operational default.</strong>&nbsp;It lets creators work in parallel, but a rolled-back transaction may leave a number unused. Choose gap-free only where an audit requirement outweighs the concurrency cost.</div>
    <section className="card top" aria-labelledby="numbering-series"><div className="card-header"><h2 id="numbering-series">Series</h2><span className="meta">{draft.filter(row => row.mode === "sequence").length} sequence · {draft.filter(row => row.mode === "gap_free").length} gap-free</span></div>
      {loading ? <div className="card-body muted">Loading document-number settings…</div> : <div className="grid-viewport"><table className="table numbering"><thead><tr><th>Series</th><th>Example</th><th>Mode</th><th>Gap behaviour</th></tr></thead><tbody>{draft.map(row => <tr key={row.series}><td><span className="primary">{row.label}</span></td><td className="mono">{row.example}</td><td><span className="numbering-segmented" role="group" aria-label={`${row.label} numbering mode`}><button type="button" aria-pressed={row.mode === "sequence"} disabled={busy} onClick={() => setMode(row.series, "sequence")}>Sequence</button><button type="button" aria-pressed={row.mode === "gap_free"} disabled={busy} onClick={() => setMode(row.series, "gap_free")}>Gap-free</button></span></td><td className="muted">{modeGap(row.mode)}</td></tr>)}</tbody></table></div>}
    </section>
  </main>;
}
