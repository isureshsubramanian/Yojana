import { useState } from "react";
import { Link } from "react-router";
import { DataGrid, FindBar, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { DOCUMENT_TOTAL, documentPage, type DocumentSerialRow } from "./fixtures";

function Highlight({ text, prefix }: { text: string; prefix: string }) {
  return prefix && text.startsWith(prefix) ? <><mark>{prefix}</mark>{text.slice(prefix.length)}</> : <>{text}</>;
}

/** Document pattern: a thousand serials with find-within, facets, match highlight and matches as a selection set. */
export function Component() {
  const [query, setQuery] = useState("2506-4934");
  const [facet, setFacet] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const data = documentPage(query, facet, page);
  const columns: GridColumn<DocumentSerialRow>[] = [
    { key: "serial", label: "Serial", width: 15, render: (r) => <Link className="serial" to="/patterns/detail"><Highlight text={r.serial} prefix={query.trim()} /></Link> },
    { key: "line", label: "Line", width: 6, numeric: true, render: (r) => r.line },
    { key: "item", label: "Item", width: 9, render: (r) => r.item },
    { key: "status", label: "Status", width: 16, render: (r) => <StatusBadge tone={r.tone}>{r.status}</StatusBadge> },
    { key: "owner", label: "Owner · custody", width: 28, render: (r) => <>{r.owner}<span className="sub">{r.ownerSub}</span></> },
    { key: "note", label: "Note", width: 22, render: (r) => r.note ?? <span className="muted">—</span> },
  ];
  return (
    <>
      <main className="content" id="main" tabIndex={-1}>
        <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Patterns", to: "/patterns" }, { label: "Documents" }, { label: "DN-2026-018432" }]} />
        <div className="page-header">
          <div>
            <div className="eyebrow">Delivery · from CX01 · posted 2026-09-01</div>
            <h1>DN-2026-018432</h1>
            <p className="sub">Brightwater Analytics GmbH · 1,000 serials on 4 lines · sales order SO-2026-7781 · invoice INV-2026-11207</p>
          </div>
          <div className="actions">
            <button type="button" className="btn"><Icon name="down" />Export serials</button>
            <button type="button" className="btn">Reconcile with scan</button>
            <Link className="btn primary" to="/patterns/form">Amend entitlements…</Link>
          </div>
        </div>
        <div className="stack">
          <KpiStrip label="Document figures" kpis={[
            { label: "Serials on document", value: "1,000", detail: "4 lines · 2 items" },
            { label: "Shipped and owned", value: "996", detail: "ownership opened 2026-09-02", trend: "up" },
            { label: "Exceptions", value: "4", detail: "2 owner mismatch · 2 in RMA", trend: "down" },
            { label: "Entitlements", value: "998", detail: "base · 24 months" },
            { label: "Certificates", value: "1,000", detail: "warranty v1 issued" },
          ]} />
          <section className="card" aria-labelledby="h-serials">
            <div className="card-header"><h2 id="h-serials">Serials <span className="meta">1,000 on this document</span></h2><span className="meta">Find within the document instead of paging through 20 pages</span></div>
            <FindBar
              ariaLabel="Find a serial in this document"
              query={query}
              onQuery={(q) => { setQuery(q); setPage(1); }}
              facets={[{ id: "all", label: "All", count: DOCUMENT_TOTAL }, { id: "exceptions", label: "Exceptions", count: 4 }, { id: "line-4", label: "Line 4", count: 250 }]}
              activeFacet={facet}
              onFacet={(f) => { setFacet(f === "all" ? null : f); setPage(1); }}
              matchCount={query.trim() ? data.matches : undefined}
              status={query.trim() ? <><strong>{data.matches.toLocaleString()}</strong> of {DOCUMENT_TOTAL.toLocaleString()} match “{query.trim()}” · prefix on internal serial, SGTIN and customer serial</> : <>{DOCUMENT_TOTAL.toLocaleString()} serials · 20 pages of 50</>}
              onUseMatches={() => setSelected(new Set(data.rows.map((r) => r.id)))}
            />
            <DataGrid columns={columns} page={data} rowId={(r) => r.id} onPage={setPage} selection={{ selected, onChange: setSelected, label: (r) => r.serial }} isHit={() => query.trim().length > 0} viewportHeight={560}
              footerNote={query.trim() ? "Clear the find box to page all 1,000" : undefined} />
          </section>
        </div>
      </main>
      <aside className="rail" aria-label="Document">
        <div className="section"><h3>Document</h3><div className="kv"><span className="k">Source</span><span className="v">CX01 · DocEntry 184320</span><span className="k">Posted</span><span className="v">2026-09-01 14:12</span><span className="k">Customer</span><span className="v">Brightwater Analytics</span><span className="k">Ship to</span><span className="v">Lab 2, Freiburg</span><span className="k">Carrier</span><span className="v">Harbourline</span><span className="k">POD</span><span className="v">2026-10-02</span></div></div>
        <div className="section"><h3>Lines</h3><div className="job"><div><div className="t">1 · VX-220</div><div className="s">250 serials</div></div><StatusBadge tone="ok">OK</StatusBadge></div><div className="job"><div><div className="t">2 · VX-220</div><div className="s">250 serials</div></div><StatusBadge tone="ok">OK</StatusBadge></div><div className="job"><div><div className="t">3 · PM-41</div><div className="s">250 serials</div></div><StatusBadge tone="ok">OK</StatusBadge></div><div className="job"><div><div className="t">4 · VX-220</div><div className="s">250 serials</div></div><StatusBadge tone="bad">4 exceptions</StatusBadge></div></div>
        <div className="section"><h3>Find tips</h3><p className="muted">Type a serial, a prefix, a range (2506-493445 → 2506-493468) or paste a list; the find runs server-side over this document's 1,000 units and never loads them all into the page.</p></div>
      </aside>
    </>
  );
}
