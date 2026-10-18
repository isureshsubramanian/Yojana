import { useEffect, useState } from "react";
import { Link } from "react-router";
import { ranges, toPage, DEFAULT_LEGAL_ENTITY, newCommandId, now, type AllocateSerialRangeResult, type SerialRangeList, type SerialRangeRow, type SerialSchemeRow } from "../api/units";
import { DataGrid, Shortcut, StatusBadge, useModEnter, type GridColumn } from "../components";
import { DocumentField, ItemField, useReferenceNames, type DocumentValue } from "../corrections/ReferenceFields";
import { openDrawer } from "../shell/drawerStore";
import { Rail } from "../shell/Drawer";
import { Breadcrumbs } from "../shell/PageHeader";
import { usePeople } from "../people/people";
import { ErrorNote, Kv, Section, useActor } from "./common";
import { fmtDate, humanise, shortId } from "./format";

const PAGE = 50;
const STATUSES = ["reserved", "commissioning", "exhausted", "released"];
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function rangeTone(status: string) { return status === "exhausted" ? "ok" : status === "commissioning" ? "warn" : status === "released" ? "bad" : "neutral"; }

export function Component() {
  const actor = useActor();
  const [schemes, setSchemes] = useState<SerialSchemeRow[]>([]);
  const [scheme, setScheme] = useState("");
  const [status, setStatus] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [list, setList] = useState<SerialRangeList | null>(null);
  const person = usePeople(list?.page.items.map((r) => r.reservedBy) ?? []);
  const [loading, setLoading] = useState(false);
  const [version, setVersion] = useState(0);
  const [quantity, setQuantity] = useState(500);
  const [allocatedTo, setAllocatedTo] = useState<DocumentValue | null>(null);
  const [itemId, setItemId] = useState<string | null>(null);
  const [start, setStart] = useState("");
  const [randomised, setRandomised] = useState(false);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [result, setResult] = useState<AllocateSerialRangeResult | null>(null);
  const [active, setActive] = useState<string | null>(null);
  const names = useReferenceNames({
    items: [itemId, ...(list?.page.items.map((row) => row.itemId) ?? [])],
    documents: [allocatedTo, ...(list?.page.items.map((row) => row.allocatedToDocumentId && row.allocatedToDocumentType ? { type: row.allocatedToDocumentType, id: row.allocatedToDocumentId } : null) ?? [])],
  });

  useEffect(() => {
    const controller = new AbortController();
    ranges.schemes(controller.signal).then((s) => { setSchemes(s); setScheme((c) => c || s[0]?.code || ""); }).catch((e) => { if (!(e instanceof DOMException)) setError(e); });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    if (!scheme) return;
    const controller = new AbortController();
    setLoading(true);
    ranges.list(scheme, status, (page - 1) * PAGE, PAGE, controller.signal).then((l) => { setList(l); setLoading(false); }).catch((e) => { if (!(e instanceof DOMException)) { setError(e); setLoading(false); } });
    return () => controller.abort();
  }, [scheme, status, page, version]);

  const definition = schemes.find((s) => s.code === scheme);
  const ready = scheme && quantity > 0 && !!allocatedTo && GUID.test(allocatedTo.id) && !busy;

  const allocate = async () => {
    setBusy(true); setError(null); setResult(null);
    try {
      const r = await ranges.allocate({ commandId: newCommandId(), scheme, quantity, allocatedTo: allocatedTo!, eventTime: now(), actorRef: actor, legalEntityId: DEFAULT_LEGAL_ENTITY, requestedStart: start || null, randomised, itemId, note: note || null });
      setResult(r);
      setVersion((v) => v + 1);
    } catch (e) { setError(e); } finally { setBusy(false); }
  };
  useModEnter(allocate, !ready);

  const columns: GridColumn<SerialRangeRow>[] = [
    { key: "range", label: "Range", width: 23, mono: true, render: (r) => r.randomised ? <>{shortId(r.rangeId)}<span className="sub">randomised list</span></> : <>{r.startValue}<span className="sub serial">→ {r.endValue}</span></> },
    { key: "qty", label: "Qty", width: 10, numeric: true, render: (r) => r.count.toLocaleString() },
    { key: "used", label: "Used", width: 10, numeric: true, render: (r) => r.commissionedCount ? r.commissionedCount.toLocaleString() : <span className="muted">—</span> },
    { key: "to", label: "Allocated to", width: 21, render: (r) => <><span className="primary">{r.allocatedToDocumentType && r.allocatedToDocumentId ? names.document({ type: r.allocatedToDocumentType, id: r.allocatedToDocumentId }) : "—"}</span><span className="sub">{r.itemId ? names.item(r.itemId) : "any item"}</span></> },
    { key: "reserved", label: "Reserved", width: 16, render: (r) => <>{fmtDate(r.reservedAt)}<span className="sub">{person(r.reservedBy)}</span></> },
    { key: "status", label: "Status", width: 20, render: (r) => <StatusBadge tone={rangeTone(r.status)}>{humanise(r.status)}</StatusBadge> },
  ];

  const rowClick = (r: SerialRangeRow) => openDrawer({ eyebrow: `Selected range · ${humanise(r.status)}`, title: r.startValue ?? shortId(r.rangeId), mono: true }, (
    <>
      <h3>Range</h3>
      <Kv rows={[["Start", <span className="serial">{r.startValue ?? "—"}</span>], ["End", <span className="serial">{r.endValue ?? "—"}</span>], ["Count", r.count.toLocaleString()], ["Commissioned", `${r.commissionedCount} of ${r.count}`], ["Allocated to", r.allocatedToDocumentType && r.allocatedToDocumentId ? names.document({ type: r.allocatedToDocumentType, id: r.allocatedToDocumentId }) : "—"], ["Item", r.itemId ? names.item(r.itemId) : "any"], ["Reserved", `${fmtDate(r.reservedAt)} · ${person(r.reservedBy)}`], ["Mode", r.randomised ? "randomised list" : "contiguous block"], ["Note", r.note ?? "—"]]} />
      <div className="act"><Link className="btn block" to={`/units?q=${encodeURIComponent((r.startValue ?? "").slice(0, Math.max(0, (r.startValue ?? "").length - 3)))}`}>Units in this block</Link></div>
    </>
  ));

  return (
    <>
      <main className="content" id="main" tabIndex={-1}>
        <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Units", to: "/units" }, { label: "Serial ranges" }]} />
        <div className="page-header">
          <div>
            <div className="eyebrow">Serial ranges · scheme {scheme || "…"}{list ? ` · ${list.ranges.toLocaleString()} ranges · ${list.serialsIssued.toLocaleString()} serials issued` : ""}</div>
            <h1>Allocate serial range</h1>
            <p className="sub">{definition ? `${definition.code} · ${definition.identifierType} · ${definition.prefix}${definition.blockFormat ? definition.blockFormat : ""}${definition.separator}${"N".repeat(definition.counterDigits)} · example ${definition.example}` : "A reservation of serials for an order; nothing exists in the unit table until commissioning."}</p>
          </div>
          <div className="actions">
            <Link className="btn ghost" to="/units">Cancel</Link>
            <button type="button" className="btn primary" disabled={!ready} aria-busy={busy} onClick={allocate}>Allocate {quantity.toLocaleString()} serials<Shortcut keys="mod+enter" /></button>
          </div>
        </div>

        <div className="cols-2-3">
          <section className="card" aria-labelledby="h-alloc">
            <div className="card-header"><h2 id="h-alloc">Allocation</h2><span className="meta">reservation, not identity</span></div>
            <div className="card-body">
              <div className="form-grid">
                <div className="field"><label htmlFor="a-scheme">Scheme</label><select id="a-scheme" className="select" value={scheme} onChange={(e) => { setScheme(e.target.value); setPage(1); }}>{schemes.map((s) => <option key={s.code} value={s.code}>{s.code} · {s.identifierType} · {s.example}</option>)}</select></div>
                <ItemField id="a-item" label="Item" value={itemId} onChange={setItemId} optional hint="Leave empty when the range may be used across items" />
                <DocumentField id="a-document" label="Allocated to" types={["production_order"]} value={allocatedTo} onChange={setAllocatedTo} hint="Search by production-order number" />
                <div className="field span">
                  <span className="lbl">Mode</span>
                  <div style={{ display: "flex", gap: 20, flexWrap: "wrap" }}>
                    <label className="radiorow"><input type="radio" name="mode" checked={!randomised} onChange={() => setRandomised(false)} /><span>Contiguous block</span></label>
                    <label className="radiorow"><input type="radio" name="mode" checked={randomised} onChange={() => setRandomised(true)} /><span>Randomised list <span className="hint">· non-guessable, customer-visible</span></span></label>
                  </div>
                </div>
                <div className="field"><label htmlFor="a-qty">Quantity <span className="hint">· from the order; count equals quantity</span></label><input id="a-qty" className="input num" type="number" min={1} value={quantity} onChange={(e) => setQuantity(Number(e.target.value) || 0)} /></div>
                <div className="field"><label htmlFor="a-start">Start <span className="hint">· optional; moved to the next free serial on overlap</span></label><input id="a-start" className="input serial" value={start} onChange={(e) => setStart(e.target.value)} placeholder={list?.nextFree ?? ""} disabled={randomised} /></div>
                <div className="field span"><label htmlFor="a-note">Note</label><textarea id="a-note" className="textarea" style={{ minHeight: 64 }} value={note} onChange={(e) => setNote(e.target.value)} /></div>
                {result && (
                  <div className={`span note ${result.startMoved ? "warn" : "ok"}`}>
                    <strong>{result.startMoved ? "Overlap avoided." : "Allocated."}</strong>&nbsp;
                    {result.randomised ? `${result.count} randomised serials reserved.` : <>{result.count} serials <span className="serial">{result.startValue} → {result.endValue}</span>{result.startMoved ? " · the requested start overlapped a reserved range, so the block starts at the next free serial" : ""}.</>}
                  </div>
                )}
                <ErrorNote error={error} />
              </div>
            </div>
            <div className="card-footer"><span>The range is reserved now; units are created only as serials are commissioned.</span></div>
          </section>

          <section className="card" aria-labelledby="h-ranges">
            <div className="card-header">
              <div className="toolbar" style={{ flex: 1 }}>
                <span className="chip on">Scheme · {scheme || "…"}</span>
                {STATUSES.map((s) => <button type="button" key={s} className={`chip${status === s ? " on" : ""}`} aria-pressed={status === s} onClick={() => { setStatus(status === s ? null : s); setPage(1); }}>{humanise(s)}</button>)}
              </div>
              <span className="meta nowrap" id="h-ranges">Newest first</span>
            </div>
            <DataGrid columns={columns} page={list ? toPage(list.page) : { rows: [], total: 0, page: 1, pageSize: PAGE, continuation: null }} rowId={(r) => r.rangeId} onPage={setPage} loading={loading} emptyText="No ranges in this scheme yet." viewportHeight={560} onRowClick={(r) => { setActive(r.rangeId); rowClick(r); }} activeRowId={active} describedBy="h-ranges" />
          </section>
        </div>
      </main>
      <Rail label="Scheme">
        <Section title={`Scheme ${scheme || ""}`}>
          <Kv rows={[["Format", <span className="serial">{definition ? `${definition.prefix}${definition.blockFormat ?? ""}${definition.separator}${"N".repeat(definition.counterDigits)}` : "—"}</span>], ["Identifier type", definition?.identifierType ?? "—"], ["Block", definition?.blockFormat ? "month" : "none"], ["Counter digits", definition?.counterDigits ?? "—"], ["Example", <span className="serial">{definition?.example ?? "—"}</span>]]} />
        </Section>
        <Section title="This block">
          <Kv rows={[["Ranges", list?.ranges.toLocaleString() ?? "—"], ["Serials issued", list?.serialsIssued.toLocaleString() ?? "—"], ["Next free", <span className="serial">{list?.nextFree ?? "—"}</span>]]} />
        </Section>
        <Section title="Rules applied">
          <p className="muted">Ranges cannot overlap. The count matches the order quantity, and reserving a range does not create units. A serial is never re-issued to another physical unit; released ranges remain unused.</p>
        </Section>
      </Rail>
    </>
  );
}
