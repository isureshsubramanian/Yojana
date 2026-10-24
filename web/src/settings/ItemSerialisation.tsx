import { useEffect, useState } from "react";
import { ItemField, useReferenceNames } from "../corrections/ReferenceFields";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { ErrorNote } from "../units/common";
import { itemSerialisationApi } from "./itemSerialisationApi";

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  const [itemId, setItemId] = useState<string | null>(null);
  const [modelNumbers, setModelNumbers] = useState("");
  const [loaded, setLoaded] = useState(false);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const names = useReferenceNames({ items: [itemId] });
  useEffect(() => {
    setLoaded(false); setSaved(false); setError(null);
    if (!permitted || !itemId) { setModelNumbers(""); return; }
    const controller = new AbortController();
    void itemSerialisationApi.get(itemId, controller.signal)
      .then(settings => { setModelNumbers(settings.modelNumbers.join("\n")); setLoaded(true); })
      .catch(reason => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    return () => controller.abort();
  }, [itemId, permitted]);
  const values = modelNumbers.split(/\r?\n/).map(value => value.trim()).filter(Boolean);
  const save = async () => {
    if (!itemId) return;
    setBusy(true); setSaved(false); setError(null);
    try {
      const settings = await itemSerialisationApi.save(itemId, values);
      setModelNumbers(settings.modelNumbers.join("\n")); setSaved(true);
    } catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };
  if (!permitted) return <div className="note bad"><strong>Tenant administrator access is required.</strong></div>;
  return <>
    <div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Item serialisation</h1><p className="sub">Record the model and part numbers printed on an item’s labels so they cannot be captured as unit serials by mistake.</p></div><div className="actions"><button type="button" className="btn primary" disabled={busy || !itemId || !loaded || values.length > 20} onClick={() => void save()}><Icon name="check" />{busy ? "Saving…" : "Save"}</button></div></div>
    <ErrorNote error={error} />
    {saved && <div className="note ok" role="status"><strong>Item serialisation settings saved.</strong>&nbsp;Later serial captures use the updated list.</div>}
    <section className="card top" aria-labelledby="item-model-numbers"><div className="card-header"><h2 id="item-model-numbers">Model numbers</h2><span className="meta">up to 20 per item</span></div><div className="card-body stack">
      <ItemField id="serialisation-item" label="Item" value={itemId} onChange={setItemId} hint="Choose by item code or name" />
      {itemId && !loaded && !error && <p className="muted">Loading {names.item(itemId)}…</p>}
      {itemId && loaded && <label className="field"><span className="lbl">Model and part numbers <span className="hint">· one per line</span></span><textarea className="textarea serial" value={modelNumbers} maxLength={1300} onChange={event => { setModelNumbers(event.target.value); setSaved(false); }} placeholder={"HX20PR-64P6ZY0\nST-4200"} /><span className={values.length > 20 ? "help bad" : "help"}>{values.length} of 20 · each may contain up to 64 characters · matching ignores case and spaces</span></label>}
      {!itemId && <p className="muted">Choose an item to view or edit the model numbers printed on its labels.</p>}
    </div></section>
  </>;
}
