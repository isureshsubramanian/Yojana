import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { useSession } from "../session/session";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { openDrawer } from "../shell/drawerStore";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section } from "../units/common";
import { humanise } from "../units/format";
import { correctionsApi } from "./api";
import { ROOT_CAUSES, bucketRows, latestBucket, rootCauseLabel } from "./format";
import type { CorrectionKpiReport, CorrectionKpiRow, CorrectionKpiTargetPolicy } from "./types";

const BREAKDOWNS = [["root_cause", "Root cause"], ["initiating_role", "Initiating role"], ["initiated_via", "Initiated via"], ["reversed_later", "Reversed later"]] as const;

function isoDate(date: Date): string { return date.toISOString().slice(0, 10); }
function periodFrom(months: number | null): string {
  if (months === null) return "2000-01-01";
  const date = new Date(); date.setUTCDate(1); date.setUTCMonth(date.getUTCMonth() - (months - 1)); return isoDate(date);
}
function monthLabel(bucket: string | null): string { return bucket ? new Date(`${bucket}-01T00:00:00Z`).toLocaleDateString(undefined, { month: "long", year: "numeric", timeZone: "UTC" }) : "Current period"; }
function pct(part: number, whole: number): string { return whole ? `${(part * 100 / whole).toFixed(1)} %` : "0 %"; }

export function Component() {
  const session = useSession();
  const canSetTargets = session.roles.includes("tenant_administrator");
  const [months, setMonths] = useState<number | null>(12);
  const [breakdown, setBreakdown] = useState("root_cause");
  const [report, setReport] = useState<CorrectionKpiReport | null>(null);
  const [roleReport, setRoleReport] = useState<CorrectionKpiReport | null>(null);
  const [viaReport, setViaReport] = useState<CorrectionKpiReport | null>(null);
  const [targets, setTargets] = useState<CorrectionKpiTargetPolicy>({ rootCauses: [] });
  const [active, setActive] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const from = periodFrom(months);
  const to = isoDate(new Date());

  useEffect(() => {
    const controller = new AbortController(); setLoading(true); setError(null);
    Promise.all([
      correctionsApi.kpi(from, to, breakdown, null, controller.signal),
      correctionsApi.kpi(from, to, "initiating_role", null, controller.signal),
      correctionsApi.kpi(from, to, "initiated_via", null, controller.signal),
      correctionsApi.kpiTargets(controller.signal),
    ]).then(([next, roles, via, policy]) => { setReport(next); setRoleReport(roles); setViaReport(via); setTargets(policy); setLoading(false); })
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) { setError(reason); setLoading(false); } });
    return () => controller.abort();
  }, [from, to, breakdown]);

  const bucket = latestBucket(report?.rows ?? []);
  const latest = useMemo(() => {
    const rows = bucketRows(report?.rows ?? [], bucket);
    if (breakdown === "root_cause" && !rows.some((row) => row.dimension === "unclassified")) {
      rows.push({ bucket: bucket ?? to.slice(0, 7), dimension: "unclassified", corrections: 0, unitsCorrected: 0, unitsShipped: rows[0]?.unitsShipped ?? 0, perThousand: 0, reversedLater: 0, targetPerThousand: null });
    }
    return rows;
  }, [report, bucket, breakdown, to]);
  const roleLatest = bucketRows(roleReport?.rows ?? [], latestBucket(roleReport?.rows ?? []));
  const viaLatest = bucketRows(viaReport?.rows ?? [], latestBucket(viaReport?.rows ?? []));
  const total = latest.reduce((sum, row) => sum + row.corrections, 0);
  const shipped = latest.reduce((max, row) => Math.max(max, row.unitsShipped), 0);
  const reversed = latest.reduce((sum, row) => sum + row.reversedLater, 0);
  const viaWorkbench = viaLatest.find((row) => row.dimension === "workbench")?.corrections ?? 0;
  const trendFor = (dimension: string) => (report?.rows.filter((row) => row.dimension === dimension).map((row) => row.perThousand ?? 0) ?? []).slice(-4);

  const openRow = (row: CorrectionKpiRow) => {
    setActive(row.dimension);
    const trend = trendFor(row.dimension);
    openDrawer({ eyebrow: `${breakdown === "root_cause" ? "Root cause" : humanise(breakdown)} · ${monthLabel(bucket)}`, title: breakdown === "root_cause" ? rootCauseLabel(row.dimension) : humanise(row.dimension) }, <>
      <h3>Breakdown</h3>
      <Kv rows={[["Corrections", row.corrections.toLocaleString()], ["Units corrected", row.unitsCorrected.toLocaleString()], ["Units shipped", row.unitsShipped.toLocaleString()], ["Per 1,000", row.perThousand?.toFixed(2) ?? "—"], ["Reversed later", row.dimension === "unclassified" ? "not a correction until re-filed" : row.reversedLater.toLocaleString()], ["Target", row.targetPerThousand?.toFixed(2) ?? "not set"], ["Trend", trend.length ? trend.map((value) => value.toFixed(2)).join(" → ") : "—"]]} />
    </>);
  };

  const columns: GridColumn<CorrectionKpiRow>[] = [
    { key: "dimension", label: breakdown === "root_cause" ? "Root cause" : humanise(breakdown), width: 30, sort: { key: "dimension", value: (row) => breakdown === "root_cause" ? rootCauseLabel(row.dimension) : humanise(row.dimension) }, render: (row) => <span className="primary"><span className="t">{breakdown === "root_cause" ? rootCauseLabel(row.dimension) : humanise(row.dimension)}</span>{breakdown === "root_cause" && <span className="sub">{ROOT_CAUSES[row.dimension]?.sub ?? "correction events"}</span>}</span> },
    { key: "rate", label: "Per 1,000 shipped", width: 22, sort: { key: "rate", defaultDir: "desc", value: (row) => row.perThousand }, render: (row) => <RateBar value={row.perThousand} max={Math.max(1, ...latest.map((item) => item.perThousand ?? 0))} /> },
    { key: "count", label: "Count", width: 9, numeric: true, sort: { key: "count", defaultDir: "desc", value: (row) => row.corrections }, render: (row) => row.corrections.toLocaleString() },
    { key: "reversed", label: "Reversed", width: 9, numeric: true, sort: { key: "reversed", defaultDir: "desc", value: (row) => row.reversedLater }, render: (row) => row.dimension === "unclassified" ? <span className="muted">—</span> : row.reversedLater.toLocaleString() },
    { key: "trend", label: "Recent trend", width: 13, render: (row) => <Trend values={trendFor(row.dimension)} /> },
    { key: "reading", label: "Reading", width: 17, render: (row) => row.dimension === "unclassified" ? <>{report?.unclassifiedAlert?.unclassifiedRequests ?? row.corrections} requests · {report?.unclassifiedAlert?.ratePercent.toFixed(1) ?? "0.0"}%<span className="sub">alert above {report?.unclassifiedAlert?.thresholdPercent.toFixed(1) ?? "—"}%</span></> : row.targetPerThousand == null ? <span className="muted">target not set</span> : (row.perThousand ?? 0) <= row.targetPerThousand ? <span className="ok-text">within target · ≤ {row.targetPerThousand.toFixed(2)}</span> : <span className="bad-text">above target · {row.targetPerThousand.toFixed(2)}</span> },
  ];

  const alert = report?.unclassifiedAlert;
  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Corrections", to: "/corrections" }, { label: "Correction volume" }]} />
      <div className="page-header"><div><h1>Correction volume</h1><p className="sub">Corrections per 1,000 units shipped, by month · computed from correction events, no separate log · targets are set per tenant after the first 90 days{report?.baseline.targetsEffectiveOn ? ` (from ${report.baseline.targetsEffectiveOn})` : ""}</p></div><div className="actions"><div className="segmented plain" role="group" aria-label="Period">{[[3, "3 m"], [12, "12 m"], [null, "All"]].map(([value, label]) => <button key={label} type="button" aria-pressed={months === value} onClick={() => setMonths(value as number | null)}>{label}</button>)}</div><a className="btn primary" href={correctionsApi.kpiExportUrl(from, to, breakdown)} download><Icon name="down" />Export</a></div></div>
      <ErrorNote error={error} />
      {alert?.alert && <div className="note warn correction-alert"><strong>Unclassified-request alert.</strong>&nbsp;{alert.ratePercent.toFixed(1)}% of requests are unclassified, above this tenant’s {alert.thresholdPercent.toFixed(1)}% threshold. Re-filed requests count once under their real root cause.</div>}
      <div className="stack">
        <KpiStrip label="Correction figures" kpis={[
          { label: `Per 1,000 shipped · ${bucket ?? "—"}`, value: shipped ? (total * 1000 / shipped).toFixed(1) : "—", detail: `${shipped.toLocaleString()} units shipped`, trend: null },
          { label: `Corrections · ${bucket ?? "current"}`, value: total.toLocaleString(), detail: `${latest.reduce((sum, row) => sum + row.unitsCorrected, 0).toLocaleString()} units corrected`, trend: null },
          { label: "Reversed later", value: pct(reversed, total), detail: `${reversed.toLocaleString()} of ${total.toLocaleString()}`, trend: null },
          { label: "Via workbench", value: pct(viaWorkbench, viaLatest.reduce((sum, row) => sum + row.corrections, 0)), detail: viaLatest.filter((row) => row.dimension !== "workbench").map((row) => `${humanise(row.dimension)} ${pct(row.corrections, viaLatest.reduce((sum, item) => sum + item.corrections, 0))}`).join(" · ") || "No other entry paths", trend: null },
        ]} />
        <section className="card" aria-labelledby="correction-breakdown"><div className="card-header"><h2 id="correction-breakdown">By {humanise(breakdown)} <span className="meta">{monthLabel(bucket)} · per 1,000 units shipped</span></h2><div className="segmented plain" role="group" aria-label="Breakdown">{BREAKDOWNS.map(([value, label]) => <button key={value} type="button" aria-pressed={breakdown === value} onClick={() => { setBreakdown(value); setActive(null); }}>{label}</button>)}</div></div>
          <DataGrid dense columns={columns} page={{ rows: latest, total: latest.length, page: 1, pageSize: 25, continuation: null }} rowId={(row) => row.dimension} onPage={() => undefined} onRowClick={openRow} activeRowId={active} loading={loading} emptyText="No correction events in this period." viewportHeight={480} footer={`${latest.length.toLocaleString()} rows · ${breakdown === "root_cause" ? "re-filed requests count under their real root cause" : "counts come from correction events"}`} gridId="correction-kpi-breakdown" userId={session.user.id} defaultSort={{ key: "dimension", dir: "asc" }} defaultPageSize={25} />
        </section>
        <section className="card" aria-labelledby="correction-roles"><div className="card-header"><h2 id="correction-roles">Where errors enter <span className="meta">initiating role · {monthLabel(latestBucket(roleReport?.rows ?? []))}</span></h2></div><table className="table dense"><thead><tr><th>Role</th><th className="num">Initiated</th><th className="num">Reversed later</th><th>Reading</th></tr></thead><tbody>{roleLatest.map((row) => <tr key={row.dimension}><td>{humanise(row.dimension)}</td><td className="num">{row.corrections.toLocaleString()}</td><td className="num">{row.reversedLater.toLocaleString()}</td><td>{row.unitsCorrected.toLocaleString()} units corrected</td></tr>)}</tbody></table></section>
      </div>
    </main>
    <Rail label="Correction KPI"><Section title="Baseline"><Kv rows={[["Status", humanise(report?.baseline.status)], ["History", report ? `${report.baseline.historyDays} days` : "—"], ["Remaining", report?.baseline.daysRemaining ?? "—"], ["Started", report?.baseline.startedOn ?? "—"], ["Targets effective", report?.baseline.targetsEffectiveOn ?? "—"]]} /></Section>{canSetTargets && <Section title="Targets per 1,000"><TargetEditor policy={targets} onSaved={setTargets} /></Section>}<Section title="Unclassified requests"><Kv rows={[["Requests", alert?.unclassifiedRequests ?? "—"], ["Rate", alert ? `${alert.ratePercent.toFixed(1)} %` : "—"], ["Alert threshold", alert ? `${alert.thresholdPercent.toFixed(1)} %` : "—"], ["State", alert?.alert ? "above threshold" : "within threshold"]]} /></Section></Rail>
  </>;
}

function RateBar({ value, max }: { value: number | null; max: number }) { const width = value == null ? 0 : Math.max(2, Math.round(value * 100 / max)); return <div className="correction-bar"><div className="progress"><i style={{ width: `${width}%` }} /></div><span className="num">{value?.toFixed(2) ?? "—"}</span></div>; }
function Trend({ values }: { values: number[] }) { const max = Math.max(1, ...values); return <span className="correction-trend" aria-label={values.map((value) => value.toFixed(2)).join(", ")}>{values.map((value, index) => <i key={index} style={{ height: `${Math.max(2, Math.round(value * 22 / max))}px` }} />)}</span>; }

function TargetEditor({ policy, onSaved }: { policy: CorrectionKpiTargetPolicy; onSaved: (policy: CorrectionKpiTargetPolicy) => void }) {
  const [values, setValues] = useState<Record<number, string>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => setValues(Object.fromEntries(policy.rootCauses.map((row) => [row.rootCauseCategory, String(row.maximumPerThousand)]))), [policy]);
  const save = async () => { setBusy(true); setError(null); try { const rootCauses = Object.entries(values).filter(([, value]) => value !== "").map(([root, value]) => ({ rootCauseCategory: Number(root), maximumPerThousand: Number(value) })); onSaved(await correctionsApi.saveKpiTargets({ rootCauses })); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  return <div className="correction-target-grid">{Array.from({ length: 7 }, (_, index) => index + 1).map((root) => <div className="correction-target-row" key={root}><label htmlFor={`target-${root}`}>{root}. {ROOT_CAUSES[String(root)]?.label}</label><input id={`target-${root}`} className="input num" inputMode="decimal" value={values[root] ?? ""} placeholder="not set" onChange={(event) => setValues({ ...values, [root]: event.target.value })} /></div>)}<ErrorNote error={error} /><button type="button" className="btn primary block" onClick={() => void save()} aria-busy={busy}>Save targets</button></div>;
}
