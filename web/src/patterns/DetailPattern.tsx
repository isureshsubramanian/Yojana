import { Link } from "react-router";
import { StatusBadge, TimelineRail } from "../components";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { timeline } from "./fixtures";
import { Rail } from "../shell/Drawer";

/** Detail pattern: print-ready record with the timeline rail on the left and the current-state inspector on the right. */
export function Component() {
  return (
    <>
      <main className="content" id="main" tabIndex={-1}>
        <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Patterns", to: "/patterns" }, { label: "Units", to: "/patterns/list" }, { label: "2506-493445" }]} />
        <div className="page-header">
          <div>
            <div className="eyebrow">Unit · VX-220 Vacuum controller</div>
            <h1 className="mono" style={{ fontFamily: "var(--font-mono)", letterSpacing: 0 }}>2506-493445</h1>
            <p className="sub">In service · Brightwater Analytics GmbH since 2026-09-02 · base warranty to 2028-09-02</p>
          </div>
          <div className="actions">
            <button type="button" className="btn" onClick={() => window.print()}><Icon name="print" />Print</button>
            <button type="button" className="btn">Issue certificate</button>
            <Link className="btn primary" to="/patterns/form"><Icon name="corr" />Something is wrong</Link>
          </div>
        </div>
        <div className="cols-2">
          <section className="card" aria-labelledby="h-tl">
            <div className="card-header"><h2 id="h-tl">Timeline <span className="meta">newest first</span></h2><span className="meta">Business time · recorded time on hover</span></div>
            <div className="card-body" style={{ paddingTop: 4 }}><TimelineRail entries={timeline} /></div>
          </section>
          <div className="stack">
            <section className="card" aria-labelledby="h-id">
              <div className="card-header"><h2 id="h-id">Identifiers</h2><Link to="/patterns/form" className="btn ghost sm">Correct…</Link></div>
              <table className="table dense">
                <colgroup><col style={{ width: "30%" }} /><col style={{ width: "46%" }} /><col style={{ width: "24%" }} /></colgroup>
                <thead><tr><th>Type</th><th>Value</th><th>Valid from</th></tr></thead>
                <tbody>
                  <tr><td className="primary">Serial (internal)</td><td className="serial">2506-493445</td><td className="muted">2025-03-06</td></tr>
                  <tr><td className="primary">GS1 SGTIN</td><td className="serial" title="(01)09506000134352(21)2506493445">(01)09506000134352(21)2506493445</td><td className="muted">2025-03-10</td></tr>
                  <tr><td className="primary">Customer asset tag</td><td className="serial">BWA-LAB2-0088</td><td className="muted">2026-09-02</td></tr>
                </tbody>
              </table>
            </section>
            <section className="card" aria-labelledby="h-ent">
              <div className="card-header"><h2 id="h-ent">Live entitlements</h2><Link to="/patterns/form" className="btn ghost sm">Amend…</Link></div>
              <table className="table dense">
                <colgroup><col style={{ width: "31%" }} /><col style={{ width: "35%" }} /><col style={{ width: "14%" }} /><col style={{ width: "20%" }} /></colgroup>
                <thead><tr><th>Family</th><th>Period</th><th>Source</th><th>Status</th></tr></thead>
                <tbody>
                  <tr><td className="primary">base</td><td>2026-09-02 → 2028-09-02</td><td className="muted">sale</td><td><StatusBadge tone="ok">Live</StatusBadge></td></tr>
                  <tr><td className="primary">refurbished_floor</td><td>2026-09-02 → 2027-09-02</td><td className="muted">policy</td><td><StatusBadge tone="ok">Live</StatusBadge></td></tr>
                  <tr><td className="primary">extended</td><td>2028-09-02 → 2029-09-02</td><td className="muted" title="sold · SO-2026-7781">sold</td><td><StatusBadge tone="info">Future</StatusBadge></td></tr>
                </tbody>
              </table>
            </section>
            <section className="card" aria-labelledby="h-cert">
              <div className="card-header"><h2 id="h-cert">Certificates</h2><button type="button" className="btn ghost sm">Reprint…</button></div>
              <table className="table dense">
                <colgroup><col style={{ width: "32%" }} /><col style={{ width: "12%" }} /><col style={{ width: "26%" }} /><col style={{ width: "30%" }} /></colgroup>
                <thead><tr><th>Document</th><th className="num">Ver.</th><th>Issued</th><th>Hash</th></tr></thead>
                <tbody>
                  <tr><td className="primary">Release record</td><td className="num">v2</td><td className="muted">2026-08-14 · refurb</td><td className="serial muted">9f3a…c41e</td></tr>
                  <tr><td className="primary">Release record</td><td className="num">v1</td><td className="muted">2025-03-10 · build</td><td className="serial muted">41b0…7d9a</td></tr>
                  <tr><td className="primary">Warranty certificate</td><td className="num">v1</td><td className="muted">2026-09-02 · sale</td><td className="serial muted">c7e2…10af</td></tr>
                </tbody>
              </table>
            </section>
          </div>
        </div>
      </main>
      <Rail label="Current state">
        <div className="section"><h3>Current state</h3><div className="kv"><span className="k">Lifecycle</span><span className="v">in_service</span><span className="k">Disposition</span><span className="v">active</span><span className="k">Condition</span><span className="v">grade A</span><span className="k">Custody</span><span className="v">customer site</span><span className="k">Owner</span><span className="v">Brightwater</span><span className="k">Warranty end (max)</span><span className="v">2029-09-02</span><span className="k">Last event seq</span><span className="v">148,201,977</span></div></div>
        <div className="section"><h3>Lineage</h3><div className="kv"><span className="k">Replaced by</span><span className="v serial">2506-498770</span><span className="k">Derived from</span><span className="v muted">—</span><span className="k">Parent assembly</span><span className="v muted">—</span><span className="k">Components</span><span className="v">4 as built</span></div></div>
        <div className="section"><h3>Open work</h3><div className="job"><div><div className="t">RMA-2026-1183</div><div className="s">Closed 2026-08-14</div></div><StatusBadge tone="neutral">Closed</StatusBadge></div><div className="job"><div><div className="t">Return overdue</div><div className="s">Not applicable</div></div><StatusBadge tone="ok">OK</StatusBadge></div></div>
      </Rail>
    </>
  );
}
