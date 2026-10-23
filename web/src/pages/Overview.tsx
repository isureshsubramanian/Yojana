import { Link, useLoaderData } from "react-router";
import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { Overview, OverviewCell, OverviewPanel, RailSection } from "../api/types";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { KpiStrip, ScanInput, Shortcut, openSearch } from "../components";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { Rail } from "../shell/Drawer";
import { TrainingWelcomeCard } from "../questions/TrainingWelcomeCard";
import { erpChangeApi } from "../integration/erpChangeApi";
import { erpChangeWorkSummary } from "../integration/erpChangeModel";
import type { ErpChangeMyWorkView } from "../integration/erpChangeTypes";

export function loader({ request }: { request: Request }) {
  return api<Overview>("/api/shell/overview", { signal: request.signal });
}

function greeting(date: Date): string {
  const h = date.getHours();
  return h < 12 ? "Good morning" : h < 18 ? "Good afternoon" : "Good evening";
}

export function Component() {
  const session = useSession();
  const overview = useLoaderData() as Overview;
  const generated = new Date(overview.generatedAt);
  return (
    <>
      <main className="content" id="main" tabIndex={-1}>
        <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Overview" }]} />
        <PageHeader
          title={`${greeting(new Date())}, ${session.user.displayName}`}
          sub={`${session.tenant?.name ?? ""} · figures as of ${generated.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" })}`}
          actions={
            <>
              <Link className="btn" to="/units">Open units</Link>
              <Link className="btn primary" to="/corrections"><Icon name="plus" />New correction<kbd className="key">N</kbd></Link>
            </>
          }
        />
        <div className="stack">
          <TrainingWelcomeCard />
          <ErpChangeHomeNotice />
          <KpiStrip kpis={overview.kpis} />
          {overview.panels.length > 0 && (
            <div className={overview.panels.length > 1 ? "cols-3-2" : "stack"}>
              {overview.panels.map((p) => <Panel key={p.id} panel={p} />)}
            </div>
          )}
          <FindUnit />
        </div>
      </main>
      <Rail label="Inspector">
        {overview.rail.map((s) => <RailBlock key={s.title} section={s} />)}
        <div className="section">
          <h3>Shortcuts</h3>
          <div className="shortcut"><span>Focus scan field</span><span className="keys"><kbd className="key">/</kbd></span></div>
          <div className="shortcut"><span>Search everything</span><span className="keys"><Shortcut keys="mod+k" /></span></div>
          <div className="shortcut"><span>New correction</span><span className="keys"><kbd className="key">N</kbd></span></div>
          <div className="shortcut"><span>Go to units</span><span className="keys"><kbd className="key">G</kbd><kbd className="key">U</kbd></span></div>
        </div>
      </Rail>
    </>
  );
}

function ErpChangeHomeNotice() {
  const session = useSession();
  const eligible = session.roles.some(role => role === "item_master_owner" || role === "inventory_controller");
  const [work, setWork] = useState<ErpChangeMyWorkView | null>(null);
  useEffect(() => {
    if (!eligible) return;
    const controller = new AbortController();
    void erpChangeApi.myWork(controller.signal).then(value => { if (!controller.signal.aborted) setWork(value); })
      .catch(() => { if (!controller.signal.aborted) setWork(null); });
    return () => controller.abort();
  }, [eligible]);
  if (!eligible || !work || work.total === 0) return null;
  return <section className="card" aria-labelledby="erp-change-home-notice"><div className="card-header">
    <h2 id="erp-change-home-notice">ERP change in progress — step {work.currentStep}: {erpChangeWorkSummary(work)}</h2>
    <Link className="btn primary" to={work.screenPath}>Open my ERP change work</Link>
  </div><div className="card-body"><p className="muted">From "{work.sourceName}" to "{work.targetName}".</p></div></section>;
}

function Cell({ cell, numeric }: { cell: OverviewCell; numeric: boolean }) {
  const className = [numeric ? "num" : "", cell.mono ? "serial" : ""].filter(Boolean).join(" ") || undefined;
  return (
    <td className={className}>
      {cell.badge ? <span className={`badge ${cell.badge}`}>{cell.text}</span> : cell.text}
      {cell.sub && <span className="sub">{cell.sub}</span>}
    </td>
  );
}

function Panel({ panel }: { panel: OverviewPanel }) {
  return (
    <section className="card" aria-labelledby={`h-${panel.id}`}>
      <div className="card-header">
        <h2 id={`h-${panel.id}`}>{panel.title}{panel.meta && <span className="meta">{panel.meta}</span>}</h2>
        {panel.link && <Link to={panel.link.href} className="btn ghost sm">{panel.link.label}</Link>}
      </div>
      {panel.rows.length === 0 ? (
        <div className="empty"><p className="hint">{panel.empty ?? "Nothing to show yet."}</p></div>
      ) : (
        <table className="table">
          <colgroup>{panel.columns.map((c) => <col key={c.label} style={{ width: `${c.width}%` }} />)}</colgroup>
          <thead><tr>{panel.columns.map((c) => <th key={c.label} className={c.numeric ? "num" : undefined}>{c.label}</th>)}</tr></thead>
          <tbody>
            {panel.rows.map((row, i) => (
              <tr key={i}>{row.map((cell, j) => <Cell key={j} cell={cell} numeric={panel.columns[j]?.numeric ?? false} />)}</tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

function RailBlock({ section }: { section: RailSection }) {
  const keyValue = section.rows.every((r) => r.badge === null);
  return (
    <div className="section">
      <h3>{section.title}</h3>
      {keyValue ? (
        <div className="kv">
          {section.rows.map((r) => (
            <RailKv key={r.label} label={r.label} value={r.value} />
          ))}
        </div>
      ) : (
        section.rows.map((r) => (
          <div className="job" key={r.label}>
            <div><div className="t">{r.label}</div>{r.detail && <div className="s">{r.detail}</div>}</div>
            {r.badge && <span className={`badge ${r.badge}`}>{r.badgeText ?? r.value ?? ""}</span>}
          </div>
        ))
      )}
    </div>
  );
}

function RailKv({ label, value }: { label: string; value: string | null }) {
  return (
    <>
      <span className="k">{label}</span>
      <span className={value === null ? "v muted" : "v"}>{value ?? "—"}</span>
    </>
  );
}

/** Scan-first entry point: an exact identifier opens the unit; anything else opens global search with the scan as the query. */
function FindUnit() {
  return (
    <section className="card" aria-labelledby="h-scan">
      <div className="card-header"><h2 id="h-scan">Find a unit</h2><span className="meta">Serial, IMEI, SGTIN, asset tag or customer serial</span></div>
      <div className="card-body">
        <ScanInput size="lg" primary ariaLabel="Scan or type an identifier" placeholder="Scan a label or type an identifier" hint={<Shortcut keys="enter" />} onSubmit={(scan) => openSearch(scan.value)} />
        <p className="hint" style={{ marginTop: 10 }}>Keyboard-wedge scanners type and send Enter; FNC1 separators in GS1 strings are recovered. Exact lookups are index seeks.</p>
      </div>
    </section>
  );
}
