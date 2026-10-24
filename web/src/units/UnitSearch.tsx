import { useEffect, useMemo, useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router";
import { units, toPage, type UnitSearchResult, type UnitSearchRow } from "../api/units";
import { DataGrid, ScanInput, StatusBadge, type GridColumn } from "../components";
import { openDrawer } from "../shell/drawerStore";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, UnitLink } from "./common";
import { documentLabel, fmtDateTime, humanise, matchSpan, parseDocumentParam, sentence, shortId, statusLabel, statusTone } from "./format";

const PAGE = 50;
const TYPES = ["internal_serial", "manufacturer_serial", "vendor_serial", "customer_serial", "imei", "mac", "udi_di", "sgtin", "giai", "vin", "customer_asset_tag", "channel_order_ref", "provisional"];
const STATUSES = ["pending_identifier", "pre_release", "released", "dormant", "scrapped_pre_release", "scrapped", "void", "decommissioned"];

function Highlight({ value, q }: { value: string; q: string }) {
  const span = matchSpan(value, q);
  if (!span) return <>{value}</>;
  return <>{value.slice(0, span.start)}<mark>{value.slice(span.start, span.start + span.length)}</mark>{value.slice(span.start + span.length)}</>;
}

/** Unit search (P1 screen 1): an exact identifier opens the unit directly, superseded ones included; a prefix lists matches, paged on the server. */
export function Component() {
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const q = params.get("q") ?? "";
  const type = params.get("type") ?? "";
  const status = params.get("status") ?? "";
  const superseded = params.get("superseded") !== "no";
  const document = params.get("document") ?? "";
  const page = Math.max(1, Number(params.get("page") ?? "1"));
  const docRef = useMemo(() => parseDocumentParam(document), [document]);

  const [result, setResult] = useState<UnitSearchResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [resolution, setResolution] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [scan, setScan] = useState(q);
  useEffect(() => setScan(q), [q]);

  const set = (patch: Record<string, string | null>) => {
    const next = new URLSearchParams(params);
    for (const [k, v] of Object.entries(patch)) { if (v === null || v === "") next.delete(k); else next.set(k, v); }
    if (!("page" in patch)) next.delete("page");
    setParams(next);
  };

  useEffect(() => {
    if (!q && !docRef && !type && !status) { setResult(null); return; }
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    units.search({ q: q || undefined, type: type || undefined, status: status || undefined, superseded, document: docRef ? `${docRef.type}:${docRef.id}` : undefined, skip: (page - 1) * PAGE, take: PAGE }, controller.signal)
      .then((r) => { setResult(r); setLoading(false); })
      .catch((e) => { if (!(e instanceof DOMException && e.name === "AbortError")) { setError(e); setLoading(false); } });
    return () => controller.abort();
  }, [q, type, status, superseded, docRef, page]);

  /** Scan-first: the exact lookup runs before any listing; one hit opens the unit. */
  const submit = async (value: string) => {
    setError(null);
    try {
      const lookup = await units.lookup(value, type || undefined);
      setResolution(lookup.resolution);
      if ((lookup.resolution === "exact" || lookup.resolution === "superseded") && lookup.hits.length === 1) {
        navigate(`/units/${lookup.hits[0]!.unitId}`, lookup.resolution === "superseded" ? { state: { resolvedFrom: value } } : undefined);
        return;
      }
      set({ q: value });
    } catch (e) {
      setError(e);
    }
  };

  const columns: GridColumn<UnitSearchRow>[] = [
    { key: "identifier", label: "Identifier", width: 17, render: (r) => <UnitLink unitId={r.unitId}><Highlight value={r.rawValue ?? r.identifier} q={q} /></UnitLink> },
    { key: "type", label: "Type · scheme", width: 15, render: (r) => <>{r.identifierType}<span className="sub">{r.open ? `scheme ${r.scheme}` : `superseded${r.resolvesTo ? ` · resolves to ${r.resolvesTo}` : ""}`}</span></> },
    { key: "item", label: "Item", width: 14, render: (r) => <><span className="primary">{shortId(r.itemId)}</span><span className="sub">{r.modelVersionId ? `model ${shortId(r.modelVersionId)}` : "item id"}</span></> },
    { key: "status", label: "Status", width: 13, render: (r) => r.open ? <StatusBadge tone={statusTone(r.lifecycleStatus)}>{statusLabel(r.lifecycleStatus)}</StatusBadge> : <StatusBadge tone="neutral">Superseded</StatusBadge> },
    { key: "custody", label: "Custody · owner", width: 23, render: (r) => <>{humanise(r.locationKind) || <span className="muted">—</span>}<span className="sub">{r.ownerPartyRef ? `owner ${shortId(r.ownerPartyRef)}` : "no owner recorded"}{r.custodyPartyRef ? ` · with ${shortId(r.custodyPartyRef)}` : ""}</span></> },
    { key: "last", label: "Last event", width: 18, render: (r) => <>{r.lastEventType ?? "—"}<span className="sub">{fmtDateTime(r.lastEventTime)}</span></> },
  ];

  const rowClick = (r: UnitSearchRow) => openDrawer({ eyebrow: `Selected unit · item ${shortId(r.itemId)}`, title: r.rawValue ?? r.identifier, mono: true }, (
    <>
      <h3>Current state</h3>
      <Kv rows={[["Lifecycle", statusLabel(r.lifecycleStatus)], ["Disposition", humanise(r.disposition)], ["Custody", humanise(r.locationKind) || "—"], ["Location", r.locationId ? shortId(r.locationId) : "—"], ["Owner", r.ownerPartyRef ? shortId(r.ownerPartyRef) : "—"], ["Identifier", `${r.identifierType} · ${r.scheme}`], ["Open", r.open ? "yes" : `no · resolves to ${r.resolvesTo ?? "—"}`], ["Last event", `${r.lastEventType ?? "—"} · ${fmtDateTime(r.lastEventTime)}`]]} />
      <div className="act"><Link className="btn primary block" to={`/units/${r.unitId}`}>Open unit</Link><Link className="btn block" to={`/units/${r.unitId}#correct`}>Correct identifier…</Link></div>
    </>
  ));

  const rowId = (r: UnitSearchRow) => `${r.unitId}:${r.identifierType}:${r.identifier}`;
  const [active, setActive] = useState<string | null>(null);
  const selectedSerials = result ? result.page.items.filter((r) => selected.has(rowId(r))).map((r) => r.rawValue ?? r.identifier) : [];
  const grid = result ? toPage(result.page) : { rows: [], total: 0, page: 1, pageSize: PAGE, continuation: null };
  const resultCount = result ? `${result.identifiers.toLocaleString()}${result.totalIsLowerBound ? "+" : ""}` : "";

  return (
    <>
      <main className="content" id="main" tabIndex={-1}>
        <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Units" }]} />
        <PageHeader
          title="Units"
          sub={result ? `${resultCount} identifier results · ${result.units.toLocaleString()} units in this page sample · lookup by any identifier` : "Lookup by any identifier: internal, vendor, customer, IMEI, SGTIN, asset tag"}
          actions={<>
            <Link className="btn" to="/units/ranges">Allocate serials</Link>
            <Link className="btn" to="/units/completeness">As-built completeness</Link>
            <Link className="btn primary" to="/units/receive"><Icon name="plus" />Receive and commission</Link>
          </>}
        />
        <div className="stack">
          <section className="card" aria-label="Lookup">
            <div className="card-body">
              <ScanInput size="lg" primary autoFocus value={scan} onChange={setScan} ariaLabel="Scan or type any identifier" placeholder="Scan or type any identifier" hint={<kbd className="key">/</kbd>} onSubmit={(s) => submit(s.value)} describedBy="lookup-help" />
              <div className="toolbar" style={{ marginTop: 12 }}>
                <label className="chip-field"><span className="chip-k">Identifier</span><select className={`chip-select${type ? " on" : ""}`} aria-label="Identifier type" value={type} onChange={(e) => set({ type: e.target.value })}>
                  <option value="">Any type</option>
                  {TYPES.map((t) => <option key={t} value={t}>{sentence(t)}</option>)}
                </select></label>
                <label className="chip-field"><span className="chip-k">Status</span><select className={`chip-select${status ? " on" : ""}`} aria-label="Lifecycle status" value={status} onChange={(e) => set({ status: e.target.value })}>
                  <option value="">All</option>
                  {STATUSES.map((s) => <option key={s} value={s}>{statusLabel(s)}</option>)}
                </select></label>
                <button type="button" className={`chip${superseded ? " on" : ""}`} aria-pressed={superseded} onClick={() => set({ superseded: superseded ? "no" : null })}>Superseded · {superseded ? "included" : "hidden"}{superseded && <Icon name="x" />}</button>
                {docRef && <button type="button" className="chip on" onClick={() => set({ document: null })}>Document · {documentLabel(docRef)}<Icon name="x" /></button>}
                {q && <button type="button" className="chip on" onClick={() => set({ q: null })}>Prefix · <span className="serial">{q}</span><Icon name="x" /></button>}
                <span className="grow" />
                <span className="hint" id="lookup-help">An exact identifier opens the unit directly, including a superseded one. A prefix lists matches, paged on the server.</span>
              </div>
              <ErrorNote error={error} />
            </div>
          </section>

          <section className="card" aria-labelledby="h-results">
            <div className="card-header">
              <h2 id="h-results">
                {q ? <>Matches for <span className="serial">{q}</span></> : docRef ? <>Units on {documentLabel(docRef)}</> : "Matches"}
                {result && <span className="meta">{resolution === "ambiguous" ? "ambiguous · " : "prefix · "}{resultCount} results · page sample: {result.units.toLocaleString()} units · {result.items} items · {result.identifierTypes} identifier types{result.superseded ? ` · ${result.superseded} superseded` : ""}</span>}
              </h2>
              <span className="meta nowrap">{result ? `Served in ${result.servedInMilliseconds} ms` : "Sorted by identifier"}</span>
            </div>
            <DataGrid columns={columns} page={grid} rowId={rowId} onPage={(p) => set({ page: String(p) })} loading={loading} viewportHeight={560}
              footerNote={result?.totalIsLowerBound ? `${resultCount} results; more available` : undefined}
              emptyText={q || docRef ? "No identifier starts with that." : "Scan or type an identifier to begin."}
              selection={{ selected, onChange: setSelected, label: (r) => r.rawValue ?? r.identifier }}
              onRowClick={(r) => { setActive(rowId(r)); rowClick(r); }} activeRowId={active} describedBy="h-results" />
          </section>
        </div>
      </main>
      <Rail label="This lookup">
        <Section title="This lookup">
          <Kv rows={[["Query", q ? <span className="serial">{q}</span> : "—"], ["Resolution", resolution ?? (q ? "prefix" : "—")], ["Identifier results", result ? resultCount : "—"], ["Units in page sample", result?.units.toLocaleString() ?? "—"], ["Identifier types in sample", result?.identifierTypes ?? "—"], ["Superseded in sample", result?.superseded ?? "—"], ["Served in", result ? `${result.servedInMilliseconds} ms` : "—"]]} />
        </Section>
        <Section title="Selection">
          <p className="muted">{selected.size === 0 ? "Select rows to release them as one selection set, or address a set by document, item, date range or pasted list." : `${selected.size} identifiers selected on this page.`}</p>
          <div className="act">
            <Link className={`btn block${selected.size ? " primary" : ""}`} to="/units/release" state={selectedSerials.length ? { serials: selectedSerials.join("\n") } : null}>Release units…</Link>
          </div>
        </Section>
        <Section title="Why two rows for one string">
          <p className="muted">Uniqueness is scoped to the item: the same string can be an internal serial on one item and a vendor serial on another. The item column disambiguates and each row opens its own unit.</p>
        </Section>
      </Rail>
    </>
  );
}
