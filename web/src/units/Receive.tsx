import { useMemo, useState } from "react";
import { Link } from "react-router";
import { units, DEFAULT_LEGAL_ENTITY, newCommandId, now, type CommissionLineResult, type CommissionUnitsResult, type ScanResult } from "../api/units";
import { DataGrid, ScanInput, StatusBadge, type GridColumn } from "../components";
import { openDrawer } from "../shell/drawerStore";
import { Rail } from "../shell/Drawer";
import { Breadcrumbs } from "../shell/PageHeader";
import { usePeople } from "../people/people";
import { ErrorNote, Figure, Kv, Section, useActor } from "./common";
import { outcomeTone, pct, serialFromScan, splitSerials } from "./format";

interface Captured { n: number; raw: string; serial: string; vendor: string | null; lot: string | null; scan: ScanResult | null; source: "scan" | "typed" | "list"; at: string; errors: string[]; result?: CommissionLineResult | undefined }

const ORIGINS = ["purchased", "manufactured", "vendor_asn", "trade_in", "customer_supplied", "legacy_unknown"];
const LOCATION_KINDS = ["warehouse", "bin", "quarantine", "in_transit", "at_subcontractor", "at_3pl"];
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Receive and commission (P1 screen 3): scan-first capture, GS1 parsing on the server, vendor serial pairing, one set-based commit. */
export function Component() {
  const actor = useActor();
  const person = usePeople([actor]);
  const [mode, setMode] = useState<"scan" | "paste">("scan");
  const [itemId, setItemId] = useState("");
  const [origin, setOrigin] = useState("purchased");
  const [docType, setDocType] = useState("receipt");
  const [docId, setDocId] = useState("");
  const [docLine, setDocLine] = useState("");
  const [locationId, setLocationId] = useState("");
  const [locationKind, setLocationKind] = useState("warehouse");
  const [vendorRequired, setVendorRequired] = useState(false);
  const [pending, setPending] = useState(0);
  const [expected, setExpected] = useState(0);
  const [rows, setRows] = useState<Captured[]>([]);
  const [last, setLast] = useState<Captured | null>(null);
  const [pasted, setPasted] = useState("");
  const [facet, setFacet] = useState<"all" | "valid" | "decide" | "pending">("all");
  const [page, setPage] = useState(1);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [result, setResult] = useState<CommissionUnitsResult | null>(null);

  const add = async (raw: string, source: "scan" | "typed" | "list") => {
    let scan: ScanResult | null = null;
    try { scan = await units.scan(raw); } catch { scan = null; }
    const serial = scan ? serialFromScan(scan.fields, scan.cleaned) : raw.trim();
    const lot = scan?.fields.find((f) => f.key === "10")?.value ?? null;
    const errors = scan ? scan.fields.flatMap((f) => f.errors) : [];
    const duplicate = rows.find((r) => r.serial.toUpperCase() === serial.toUpperCase());
    if (duplicate) errors.push(`duplicate in session · first at #${duplicate.n}`);
    const captured: Captured = { n: rows.length + 1, raw, serial, vendor: null, lot, scan, source, at: new Date().toISOString(), errors };
    setRows((r) => [captured, ...r]);
    setLast(captured);
    setResult(null);
  };

  const pairVendor = (value: string) => {
    setRows((r) => {
      const i = r.findIndex((x) => x.vendor === null);
      if (i < 0) return r;
      const next = [...r];
      next[i] = { ...next[i]!, vendor: value.trim() };
      return next;
    });
  };

  const addPasted = async () => {
    for (const s of splitSerials(pasted)) await add(s, "list");
    setPasted("");
  };

  const valid = rows.filter((r) => r.errors.length === 0 && (!vendorRequired || r.vendor));
  const decide = rows.filter((r) => r.errors.length > 0 || (vendorRequired && !r.vendor));
  const shown = facet === "valid" ? valid : facet === "decide" ? decide : rows;
  const PAGE = 50;
  const grid = { rows: shown.slice((page - 1) * PAGE, page * PAGE), total: shown.length, page, pageSize: PAGE, continuation: null };
  const ready = GUID.test(itemId) && (valid.length + pending) > 0 && !busy;

  const commit = async () => {
    setBusy(true); setError(null);
    try {
      const ordered = [...rows].reverse().filter((r) => r.errors.length === 0 && (!vendorRequired || r.vendor));
      const lines = ordered.map((r) => ({ identifiers: [{ type: "internal_serial", rawValue: r.serial }, ...(r.vendor ? [{ type: "vendor_serial", rawValue: r.vendor }] : [])], clientReference: String(r.n), lotId: GUID.test(r.lot ?? "") ? r.lot : null }));
      for (let i = 0; i < pending; i++) lines.push({ identifiers: [], clientReference: `pending-${i + 1}`, lotId: null });
      const r = await units.commission({
        commandId: newCommandId(), itemId, origin, lines, eventTime: now(), actorRef: actor, legalEntityId: DEFAULT_LEGAL_ENTITY,
        document: GUID.test(docId) ? { type: docType, id: docId, line: docLine ? Number(docLine) : null } : null,
        locationId: GUID.test(locationId) ? locationId : null, locationKind: locationKind || null, skipInvalidLines: true,
      });
      setResult(r);
      setRows((current) => current.map((row) => ({ ...row, result: r.lines.find((l) => l.clientReference === String(row.n)) })));
    } catch (e) { setError(e); } finally { setBusy(false); }
  };

  const columns: GridColumn<Captured>[] = [
    { key: "n", label: "#", width: 8, numeric: true, muted: true, render: (r) => r.n },
    { key: "serial", label: "Internal", width: 22, mono: true, render: (r) => r.serial },
    { key: "vendor", label: "Vendor", width: 20, mono: true, render: (r) => r.vendor ?? <span className="muted">—</span> },
    { key: "lot", label: "Lot", width: 14, render: (r) => r.lot ?? <span className="muted">—</span> },
    { key: "validation", label: "Validation", width: 36, render: (r) => r.result
      ? <StatusBadge tone={outcomeTone(r.result.outcome)}>{r.result.outcome}{r.result.reasons.length ? ` · ${r.result.reasons.join(", ")}` : ""}</StatusBadge>
      : r.errors.length ? <StatusBadge tone="bad">{r.errors.join(" · ")}</StatusBadge>
      : vendorRequired && !r.vendor ? <StatusBadge tone="warn">Valid · vendor serial pending</StatusBadge>
      : <StatusBadge tone="ok">Valid{r.scan && r.scan.fields.length > 0 ? " · parsed" : ""}{r.scan && r.scan.separatorsRecovered ? ` · FNC1 ×${r.scan.separatorsRecovered}` : ""}</StatusBadge> },
  ];

  const rowClick = (r: Captured) => openDrawer({ eyebrow: `Selected scan · #${r.n}`, title: r.serial, mono: true }, (
    <>
      <h3>Scan</h3>
      <Kv rows={[["Validation", r.errors.length ? <span className="bad-text">{r.errors.join("; ")}</span> : <span className="ok-text">valid</span>], ["Raw", <span className="serial" title={r.raw}>{r.raw.length > 28 ? r.raw.slice(0, 28) + "…" : r.raw}</span>], ["Internal serial", <span className="serial">{r.serial}</span>], ["Vendor serial", r.vendor ? <span className="serial">{r.vendor}</span> : "—"], ["Lot", r.lot ?? "—"], ["Source", r.source], ["Captured", r.at.slice(11, 19)], ["Operator", person(actor)], ...(r.result ? [["Outcome", `${r.result.outcome}${r.result.unitId ? ` · ${r.result.unitId.slice(0, 8)}` : ""}`] as [string, string]] : [])]} />
      {r.scan && r.scan.fields.length > 0 && <div className="parsed" style={{ marginTop: 12 }}>{r.scan.fields.map((f, i) => <span className={`tok${f.errors.length ? " bad" : ""}`} key={i}><b>{f.key}{f.description ? ` ${f.description}` : ""}</b><code>{f.value}</code></span>)}</div>}
      <div className="act">
        {r.result?.unitId ? <Link className="btn primary block" to={`/units/${r.result.unitId}`}>Open unit</Link> : <button type="button" className="btn block" onClick={() => { setRows((x) => x.filter((y) => y.n !== r.n)); }}>Remove from session</button>}
      </div>
    </>
  ));

  return (
    <>
      <main className="content" id="main" tabIndex={-1}>
        <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Units", to: "/units" }, { label: "Receive and commission" }]} />
        <div className="page-header">
          <div>
            <div className="eyebrow">{origin === "manufactured" ? "Production receipt" : "Purchase receipt"} · {docType}{docId ? ` ${docId.slice(0, 8)}` : ""}</div>
            <h1>Receive and commission</h1>
            <p className="sub">{expected ? `${expected} expected · ` : ""}origin {origin} · internal serial{vendorRequired ? " plus vendor serial" : ""} · opening custody {locationKind}</p>
          </div>
          <div className="actions">
            <Link className="btn ghost" to="/units">Discard session</Link>
            <button type="button" className="btn primary" disabled={!ready} aria-busy={busy} onClick={commit}>Commission {valid.length + pending} units<kbd className="key">⌘↵</kbd></button>
          </div>
        </div>

        <div className="cols-2-3">
          <section className="card" aria-labelledby="h-capture">
            <div className="card-header"><h2 id="h-capture">Capture</h2><span className="meta">session {new Date().toISOString().slice(0, 10)}</span></div>
            <div className="card-body">
              <div className="stack" style={{ gap: 16 }}>
                <div className="form-grid">
                  <div className="field"><label htmlFor="r-item">Item id <span className="hint">· from the host's item master</span></label><input id="r-item" className="input serial" value={itemId} onChange={(e) => setItemId(e.target.value)} placeholder="00000000-0000-0000-0000-000000000000" /></div>
                  <div className="field"><label htmlFor="r-origin">Origin</label><select id="r-origin" className="select" value={origin} onChange={(e) => setOrigin(e.target.value)}>{ORIGINS.map((o) => <option key={o} value={o}>{o}</option>)}</select></div>
                  <div className="field"><label htmlFor="r-doctype">Document type</label><input id="r-doctype" className="input" value={docType} onChange={(e) => setDocType(e.target.value)} /></div>
                  <div className="field"><label htmlFor="r-docid">Document id <span className="hint">· line optional</span></label><div style={{ display: "grid", gridTemplateColumns: "minmax(0,1fr) 64px", gap: 8 }}><input id="r-docid" className="input serial" value={docId} onChange={(e) => setDocId(e.target.value)} placeholder="guid" /><input className="input num" value={docLine} onChange={(e) => setDocLine(e.target.value)} placeholder="line" aria-label="Document line" /></div></div>
                  <div className="field"><label htmlFor="r-loc">Put away to <span className="hint">· opening custody</span></label><div style={{ display: "grid", gridTemplateColumns: "minmax(0,1fr) 150px", gap: 8 }}><input id="r-loc" className="input serial" value={locationId} onChange={(e) => setLocationId(e.target.value)} placeholder="location id" /><select className="select" value={locationKind} onChange={(e) => setLocationKind(e.target.value)} aria-label="Location kind">{LOCATION_KINDS.map((k) => <option key={k} value={k}>{k}</option>)}</select></div></div>
                  <div className="field"><label htmlFor="r-expected">Expected <span className="hint">· from the document</span></label><input id="r-expected" className="input num" type="number" min={0} value={expected || ""} onChange={(e) => setExpected(Number(e.target.value) || 0)} /></div>
                </div>
                <div className="segmented plain" role="group" aria-label="Capture mode"><button type="button" aria-pressed={mode === "scan"} onClick={() => setMode("scan")}>Scan</button><button type="button" aria-pressed={mode === "paste"} onClick={() => setMode("paste")}>Paste list</button></div>
                {mode === "scan" ? (
                  <div className="field">
                    <label htmlFor="r-scan">Scan the next label <span className="hint">· focus stays here · scanner or keyboard</span></label>
                    <ScanInput id="r-scan" size="lg" primary autoFocus ariaLabel="Scan the next label" placeholder={expected ? `Waiting for scan ${rows.length + 1} of ${expected}` : "Waiting for a scan"} hint={<kbd className="key">↵</kbd>} describedBy="r-scan-help" onSubmit={(s) => add(s.value, s.source)} />
                    <span className="help" id="r-scan-help">GS1 element strings and MH10.8.2 data identifiers are parsed on the server; the wedge's Ctrl+] is restored to a group separator before parsing.</span>
                  </div>
                ) : (
                  <div className="field">
                    <label htmlFor="r-paste">Pasted list <span className="hint">· one serial per line, comma or space separated</span></label>
                    <textarea id="r-paste" className="textarea serial" value={pasted} onChange={(e) => setPasted(e.target.value)} />
                    <div><button type="button" className="btn" onClick={addPasted} disabled={!pasted.trim()}>Add {splitSerials(pasted).length} serials</button></div>
                  </div>
                )}
                {last && (
                  <div>
                    <div className="eyebrow" style={{ marginBottom: 8 }}>Last {last.source} · {last.at.slice(11, 19)} · #{last.n}</div>
                    <div className="raw">{last.raw.split("\u001d").map((part, i) => <span key={i}>{i > 0 && <span className="gs">⏎</span>}{part}</span>)}</div>
                    {last.scan && last.scan.fields.length > 0 && <div className="parsed" style={{ marginTop: 8 }}>{last.scan.fields.map((f, i) => <span className={`tok${f.errors.length ? " bad" : ""}`} key={i}><b>{f.key}{f.description ? ` ${f.description}` : ""}</b><code>{f.value}</code></span>)}{last.scan.separatorsRecovered > 0 && <span className="tok"><b>FNC1</b>recovered ×{last.scan.separatorsRecovered}</span>}</div>}
                  </div>
                )}
                {expected > 0 && (
                  <div>
                    <div style={{ display: "flex", justifyContent: "space-between", fontSize: 13, marginBottom: 6 }}><span className="muted">Captured</span><span><strong>{rows.length}</strong> of {expected} · {Math.max(0, expected - rows.length)} to go</span></div>
                    <div className={`progress${rows.length >= expected ? " ok" : ""}`}><i style={{ width: `${Math.min(100, pct(rows.length, expected))}%` }} /></div>
                  </div>
                )}
                <label className="checkrow"><input type="checkbox" checked={vendorRequired} onChange={(e) => setVendorRequired(e.target.checked)} /><span>Vendor serial required for this item <span className="hint">· a separate identifier type, paired by scan order</span></span></label>
                {vendorRequired && (
                  <div className="field">
                    <label htmlFor="r-vendor">Vendor serial <span className="hint">· pairs with the next unpaired internal serial</span></label>
                    <ScanInput id="r-vendor" ariaLabel="Vendor serial" placeholder="Scan the vendor label" hint={<StatusBadge tone="ok">auto-pairs</StatusBadge>} onSubmit={(s) => pairVendor(s.value)} />
                  </div>
                )}
                <label className="checkrow"><input type="checkbox" checked={pending > 0} onChange={(e) => setPending(e.target.checked ? Math.max(0, expected - rows.length) : 0)} /><span>Create <strong>{pending || Math.max(0, expected - rows.length)}</strong> pending-identifier units for the unscanned remainder, identified at put-away</span></label>
                {decide.length > 0 && <div className="note warn"><strong>{decide.length} rows need a decision</strong>&nbsp;before commit. Commit records the {valid.length} valid rows and leaves these in the session.</div>}
                {result && <div className="note ok"><strong>Recorded.</strong>&nbsp;{result.commissioned} commissioned, {result.pending} pending identifier, {result.refused} refused, {result.duplicates} duplicates. Refused rows show their reason in the table.</div>}
                <ErrorNote error={error} />
              </div>
            </div>
          </section>

          <section className="card" aria-labelledby="h-captured">
            <div className="card-header"><h2 id="h-captured">Captured in this session <span className="meta">{rows.length} scans · {valid.length} valid</span></h2></div>
            <div className="findbar compact">
              {([["all", "All", rows.length], ["valid", "Valid", valid.length], ["decide", "Decide", decide.length]] as const).map(([id, label, n]) => (
                <button type="button" key={id} className={`chip${facet === id ? " on" : ""}`} aria-pressed={facet === id} onClick={() => { setFacet(id); setPage(1); }}>{label} {n}</button>
              ))}
              <span className="grow" />
              <span className="status">Newest first</span>
            </div>
            <DataGrid dense columns={columns} page={grid} rowId={(r) => String(r.n)} onPage={setPage} emptyText="Nothing captured yet. Scan a label or paste a list." viewportHeight={560} onRowClick={rowClick} />
          </section>
        </div>
      </main>
      <Rail label="Receipt">
        <Section title="Receipt">
          <Kv rows={[["Document", docId ? `${docType} ${docId.slice(0, 8)}` : "—"], ["Origin", origin], ["Expected", expected || "—"], ["Captured", rows.length], ["Valid", <span className="ok-text">{valid.length}</span>], ["To decide", decide.length ? <span className="bad-text">{decide.length}</span> : "0"], ["Pending id.", pending]]} />
        </Section>
        <Section title="What will be recorded">
          <div className="job"><div><div className="t">unit.commissioned</div><div className="s">internal{vendorRequired ? " + vendor" : ""} serial · origin {origin}</div></div><StatusBadge tone="neutral">{valid.length}</StatusBadge></div>
          <div className="job"><div><div className="t">unit.created_pending_identifier</div><div className="s">identified at put-away</div></div><StatusBadge tone="neutral">{pending}</StatusBadge></div>
          <div className="job"><div><div className="t">custody opened</div><div className="s">{locationKind}{locationId ? ` · ${locationId.slice(0, 8)}` : ""}</div></div><StatusBadge tone="neutral">{valid.length + pending}</StatusBadge></div>
          <div className="job"><div><div className="t">Unit.Commissioned</div><div className="s">published through the outbox</div></div><StatusBadge tone="neutral">{valid.length}</StatusBadge></div>
        </Section>
        <Section title="Rules applied">
          <p className="muted">Set-based: one bulk copy to staging, one validated insert, one projection merge. A serial already open on another unit is never overwritten; it is refused with its holder named. Vendor serials collide only with vendor serials.</p>
        </Section>
        <div className="section figures" style={{ marginTop: 20 }}><Figure label="Scans" value={rows.length} /><Figure label="Valid" value={valid.length} tone="ok" />{decide.length ? <Figure label="Decide" value={decide.length} tone="bad" /> : <Figure label="Decide" value={0} />}<Figure label="Pending" value={pending} /></div>
      </Rail>
    </>
  );
}
