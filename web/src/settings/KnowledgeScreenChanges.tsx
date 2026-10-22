import { useEffect, useState } from "react";
import { ErrorNote } from "../units/common";
import { knowledgeApi } from "../questions/api";
import type { KnowledgeScreenReference } from "../questions/types";

const when = (value: string | null) => value
  ? new Date(value).toLocaleString([], { dateStyle: "medium", timeStyle: "short" })
  : "Never marked";

export function KnowledgeScreenChanges() {
  const [screens, setScreens] = useState<KnowledgeScreenReference[]>([]);
  const [selected, setSelected] = useState(""); const [reason, setReason] = useState("");
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);

  const load = async (cursor?: string) => {
    const page = await knowledgeApi.screens(cursor);
    setScreens(current => cursor ? [...current, ...page.items] : page.items);
    setNextCursor(page.nextCursor);
    setSelected(current => current || page.items[0]?.reference || "");
  };
  useEffect(() => { void load().catch(setError); }, []);

  const markChanged = async () => {
    setBusy(true); setError(null);
    try {
      await knowledgeApi.markScreenChanged(selected, reason.trim());
      setReason(""); setScreens([]); setSelected(""); await load();
    } catch (problem) { setError(problem); }
    finally { setBusy(false); }
  };
  const current = screens.find(screen => screen.reference === selected);
  return <section className="card" aria-labelledby="knowledge-screen-changes"><div className="card-header"><h2 id="knowledge-screen-changes">Knowledge screen changes</h2><span className="meta">recorded and audited</span></div><div className="card-body stack">
    <p className="muted">Mark a screen when its behaviour or wording changes. Published entries written against it stay readable with a warning until a reviewer checks them.</p>
    <ErrorNote error={error} />
    {screens.length === 0 ? <p className="muted">No published knowledge entry references a screen yet.</p> : <>
      <label className="field"><span className="lbl">Screen</span><select className="select" value={selected} onChange={event => setSelected(event.target.value)}>{screens.map(screen => <option value={screen.reference} key={screen.reference}>{screen.label} · {screen.publishedEntries} {screen.publishedEntries === 1 ? "entry" : "entries"}</option>)}</select></label>
      {current && <p className="help">Last marked: {when(current.lastChangedAt)}{current.lastChangedByName ? ` · ${current.lastChangedByName}` : ""}</p>}
      <label className="field"><span className="lbl">What changed</span><textarea className="textarea" value={reason} maxLength={500} onChange={event => setReason(event.target.value)} placeholder="Describe the changed behaviour reviewers should check." /></label>
      <div className="cluster"><button className="btn primary" type="button" disabled={busy || !selected || !reason.trim()} onClick={() => void markChanged()}>{busy ? "Marking…" : "Mark screen changed"}</button>{nextCursor && <button className="btn" type="button" disabled={busy} onClick={() => void load(nextCursor).catch(setError)}>Load more screens</button>}</div>
    </>}
  </div></section>;
}
