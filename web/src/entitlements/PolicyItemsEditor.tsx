import { ClaimCausesEditor } from "./ClaimCausesEditor";
import { useSession } from "../session/session";
import { useItemFamilies } from "../corrections/ReferenceFields";
import { useEffect, useState } from "react";
import { DataGrid, StatusBadge, type GridColumn } from "../components";
import { openDrawer, closeDrawer } from "../shell/drawerStore";
import { ErrorNote } from "../units/common";
import { coverItemsApi, type ClaimCause, type PolicyCoverItem, type PolicyItemPage } from "./coverItems";

export function PolicyItemsEditor({ versionId, items, editable, dirty, onChange }: { versionId: string; items: PolicyCoverItem[]; editable: boolean; dirty: boolean; onChange: (update: (items: PolicyCoverItem[]) => PolicyCoverItem[]) => void }) {
  const itemFamilies = useItemFamilies();
  const manageCauses = useSession().roles.includes("service_manager");
  const [type, setType] = useState(""); const [page, setPage] = useState(1);
  const [remote, setRemote] = useState<PolicyItemPage | null>(null); const [causes, setCauses] = useState<ClaimCause[]>([]);
  const [causeVersion, setCauseVersion] = useState("");
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    const c = new AbortController();
    Promise.all([coverItemsApi.items(versionId, type, (page - 1) * 25, c.signal), coverItemsApi.causes(c.signal)])
      .then(([data, catalogue]) => { setRemote(data); setCauses(catalogue.items); setCauseVersion(catalogue.version); }).catch((e) => { if (!c.signal.aborted) setError(e); });
    return () => c.abort();
  }, [versionId, type, page, dirty]);
  const filtered = items.filter((i) => !type || i.type === type);
  const open = (item: PolicyCoverItem, fresh = false) => openDrawer({ eyebrow: "Policy item", title: fresh ? "Add item" : item.code }, <ItemForm item={item} causes={causes} editable={editable} fresh={fresh}
    onSave={(updated) => { onChange((current) => fresh ? [...current, updated] : current.map((i) => i.code === item.code ? updated : i)); closeDrawer(); }}
    onRemove={() => { onChange((current) => current.filter((i) => i.code !== item.code)); closeDrawer(); }} />);
  const columns: GridColumn<PolicyCoverItem>[] = [
    { key: "code", label: "Code", width: 12, mono: true, render: (r) => r.code },
    { key: "type", label: "Type", width: 12, render: (r) => <StatusBadge tone={r.type === "covered" ? "ok" : "warn"}>{r.type === "covered" ? "Covered" : "Excluded"}</StatusBadge> },
    { key: "wording", label: "Customer wording", width: 35, render: (r) => <span className="wrap">{r.customerWording}</span> },
    { key: "category", label: "Category", width: 15, render: (r) => <>{r.category ?? <StatusBadge tone="neutral">Needs category</StatusBadge>}{r.customerWording.length > 500 && <StatusBadge tone="warn">Too long for the customer sheet</StatusBadge>}</> },
    { key: "scope", label: "Applies to", width: 19, render: (r) => [...r.partsScope.partCategories, ...r.partsScope.itemFamilies.map((code) => itemFamilies.name(code))].join(", ") || "all parts" },
    { key: "since", label: "Since", width: 7, render: (r) => r.sinceVersion ? `v${r.sinceVersion}` : "This draft" },
  ];
  // Persisted lists use the server page. Unsaved draft edits are staged with the other policy terms until Save draft.
  const rows = dirty ? filtered.slice((page - 1) * 25, page * 25) : remote?.items ?? [];
  const total = dirty ? filtered.length : remote?.total ?? 0;
  return <section className="card"><div className="card-header"><h2>Covered and excluded items <span className="meta">{items.length} items · {items.filter((i) => !i.category).length} need a category</span></h2><div className="right"><div className="segmented" role="group" aria-label="Item type">{["", "covered", "excluded"].map((v) => <button type="button" key={v} aria-pressed={type === v} onClick={() => { setType(v); setPage(1); }}>{v === "covered" ? "Covered" : v === "excluded" ? "Excluded" : "All"}<span className="n">{items.filter((i) => !v || i.type === v).length}</span></button>)}</div><button type="button" className="btn sm" disabled={!editable || !causes.length} onClick={() => open({ code: "", type: "covered", customerWording: "", internalNote: null, category: null, partsScope: { partCategories: [], itemFamilies: [] }, causeCodes: [] }, true)}>Add item</button>{manageCauses && <button type="button" className="btn sm" disabled={!causes.length} onClick={() => openDrawer({ eyebrow: "Tenant reference data", title: "Claim causes" }, <ClaimCausesEditor causes={causes} version={causeVersion} onSaved={(updated, version) => { setCauses(updated); setCauseVersion(version); closeDrawer(); }} />)}>Claim causes…</button>}<button type="button" className="btn sm" onClick={() => openDrawer({ eyebrow: "Customer wording preview", title: "Preview customer sheet" }, <PolicyCustomerPreview items={items} />)}>Preview customer sheet</button></div></div><ErrorNote error={error} />
    <DataGrid columns={columns} rowId={(i) => i.code} page={{ rows, page, pageSize: 25, total, continuation: null }} onPage={setPage} onRowClick={(i) => open(i)} footerNote="Save draft to keep item changes" />
    <div className="card-footer">Items from the old free-text list arrive as “Needs category”. Publishing makes them immutable; units keep their sold version.</div>
  </section>;
}
export function PolicyCustomerPreview({ items }: { items: PolicyCoverItem[] }) {
  return <>{["covered", "excluded"].map((type) => <section key={type}><h3>{type === "covered" ? "Covered" : "Not covered"}</h3><ul>{items.filter((i) => i.type === type).map((i) => <li key={i.code}>{i.customerWording}</li>)}</ul></section>)}<p className="hint">Policy wording preview. A unit’s customer sheet combines all its live covers and dates.</p></>;
}
export function ItemForm({ item, causes, editable, fresh, onSave, onRemove }: { item: PolicyCoverItem; causes: ClaimCause[]; editable: boolean; fresh: boolean; onSave: (i: PolicyCoverItem) => void; onRemove: () => void }) {
  const [draft, setDraft] = useState(item);
  const itemFamilies = useItemFamilies();
  const list = (s: string) => s.split(",").map((x) => x.trim()).filter(Boolean);
  return <div className="form-grid one"><label className="field">Code<input className="input" maxLength={24} disabled={!editable || !fresh} value={draft.code} onChange={(e) => setDraft({ ...draft, code: e.target.value.toUpperCase() })} /></label>
    <label className="field">Type<select className="select" disabled={!editable} value={draft.type} onChange={(e) => setDraft({ ...draft, type: e.target.value as PolicyCoverItem["type"] })}><option value="covered">Covered</option><option value="excluded">Excluded</option></select></label>
    <label className="field">Customer wording<textarea className="textarea" maxLength={500} required disabled={!editable} value={draft.customerWording} onChange={(e) => setDraft({ ...draft, customerWording: e.target.value })} /></label>
    <label className="field">Internal note<textarea className="textarea" disabled={!editable} value={draft.internalNote ?? ""} onChange={(e) => setDraft({ ...draft, internalNote: e.target.value || null })} /></label>
    <label className="field">Category<select className="select" disabled={!editable} value={draft.category ?? ""} onChange={(e) => setDraft({ ...draft, category: e.target.value || null })}><option value="">Needs category</option>{["cause", "part", "condition", "service"].map((v) => <option key={v}>{v}</option>)}</select></label>
    <label className="field">Part categories<input className="input" disabled={!editable} value={draft.partsScope.partCategories.join(", ")} onChange={(e) => setDraft({ ...draft, partsScope: { ...draft.partsScope, partCategories: list(e.target.value) } })} /></label>
    <fieldset disabled={!editable}><legend>Item-family references</legend>{itemFamilies.options.map((family) => <label className="field" key={family.code}><span><input type="checkbox" checked={draft.partsScope.itemFamilies.includes(family.code)} onChange={(e) => setDraft({ ...draft, partsScope: { ...draft.partsScope, itemFamilies: e.target.checked ? [...draft.partsScope.itemFamilies, family.code] : draft.partsScope.itemFamilies.filter((v) => v !== family.code) } })} /> {family.name}</span></label>)}<p className="hint">Empty scope applies to all parts.</p></fieldset>
    <fieldset disabled={!editable}><legend>Claim causes it answers</legend>{causes.map((c) => <label className="field" key={c.code}><span><input type="checkbox" checked={draft.causeCodes.includes(c.code)} onChange={(e) => setDraft({ ...draft, causeCodes: e.target.checked ? [...draft.causeCodes, c.code] : draft.causeCodes.filter((v) => v !== c.code) })} /> {c.customerWording}{c.active ? "" : " · inactive"}</span></label>)}</fieldset>
    {editable && <div className="actions"><button type="button" className="btn primary" disabled={!draft.code || !draft.customerWording.trim() || (draft.customerWording.length > 500 && (fresh || draft.customerWording !== item.customerWording))} onClick={() => onSave(draft)}>Keep in draft</button>{!fresh && <button type="button" className="btn" onClick={onRemove}>Remove item</button>}</div>}
  </div>;
}
