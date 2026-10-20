import { useState } from "react";
import { ErrorNote } from "../units/common";
import { coverItemsApi, type ClaimCause } from "./coverItems";

export function ClaimCausesEditor({ causes, onSaved }: { causes: ClaimCause[]; onSaved: (items: ClaimCause[]) => void }) {
  const [rows, setRows] = useState(() => causes.map((c) => ({ ...c, rowId: c.code, fresh: false })));
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);
  const save = async () => {
    setBusy(true); setError(null);
    try {
      const items = rows.map(({ code, customerWording, active }) => ({ code, customerWording, active }));
      await coverItemsApi.saveCauses(items); onSaved(items);
    } catch (e) { setError(e); } finally { setBusy(false); }
  };
  return <div className="stack"><h3>Tenant claim causes</h3><p className="hint">Use your customers’ words. A referenced code cannot be removed; deactivate it to keep it off new intake screens. Existing items and claims keep their reference.</p>
    {rows.map((row, index) => <div className="form-grid one" key={row.rowId}>
      <label className="field">Cause code<input className="input" maxLength={40} disabled={!row.fresh || busy} value={row.code} onChange={(e) => setRows((all) => all.map((r, i) => i === index ? { ...r, code: e.target.value.toUpperCase() } : r))} /></label>
      <label className="field">Customer wording<input className="input" maxLength={200} disabled={busy} value={row.customerWording} onChange={(e) => setRows((all) => all.map((r, i) => i === index ? { ...r, customerWording: e.target.value } : r))} /></label>
      <label><input type="checkbox" checked={row.active} disabled={busy} onChange={(e) => setRows((all) => all.map((r, i) => i === index ? { ...r, active: e.target.checked } : r))} /> Active</label>
      <button type="button" className="btn ghost sm" disabled={busy} onClick={() => setRows((all) => all.filter((_, i) => i !== index))}>Remove unreferenced cause</button>
    </div>)}
    <ErrorNote error={error} /><div className="actions"><button type="button" className="btn" disabled={busy} onClick={() => setRows((all) => [...all, { rowId: crypto.randomUUID(), fresh: true, code: "", customerWording: "", active: true }])}>Add cause</button><button type="button" className="btn primary" disabled={busy || rows.length === 0 || rows.some((r) => !r.code.trim() || !r.customerWording.trim())} aria-busy={busy} onClick={() => void save()}>Save causes</button></div>
  </div>;
}
