import { useEffect, useState } from "react";
import { Link } from "react-router";
import { KpiStrip } from "../components";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { reportsApi } from "./api";
import { KnowledgeSubnav } from "./KnowledgeSubnav";
import type { KnowledgeReport, KnowledgeReportFilters, KnowledgeReportPeriod, KnowledgeReportRank } from "./types";
import "./questions.css";

const periods: Array<[KnowledgeReportPeriod, string]> = [["30_days", "30 days"], ["90_days", "90 days"], ["12_months", "12 months"]];
const duration = (seconds: number | null) => {
  if (seconds === null) return "—";
  if (seconds >= 86400) return `${Math.floor(seconds / 86400)} d ${Math.floor(seconds % 86400 / 3600)} h`;
  if (seconds >= 3600) return `${Math.floor(seconds / 3600)} h ${Math.floor(seconds % 3600 / 60)} m`;
  return `${Math.floor(seconds / 60)} m`;
};
const date = (value: string) => new Date(value).toLocaleDateString([], { dateStyle: "medium" });

export function Component() {
  const [filters, setFilters] = useState<KnowledgeReportFilters>({ period: "90_days" });
  const [report, setReport] = useState<KnowledgeReport | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { const controller = new AbortController(); setError(null); void reportsApi.report(filters, controller.signal)
    .then(setReport).catch(reason => { if (!controller.signal.aborted) setError(reason); }); return () => controller.abort(); }, [filters]);
  const select = (key: "directedRole" | "recordKind" | "topic", value: string) => setFilters(current => ({ ...current, [key]: current[key] === value ? undefined : value }));
  const kpis = [
    { label: "First reply, median", value: duration(report?.firstReply.medianSeconds ?? null), detail: `90th percentile ${duration(report?.firstReply.ninetiethPercentileSeconds ?? null)}` },
    { label: "Answer accepted, median", value: duration(report?.acceptedAnswer.medianSeconds ?? null), detail: `90th percentile ${duration(report?.acceptedAnswer.ninetiethPercentileSeconds ?? null)}` },
    { label: "Unanswered now", value: (report?.unanswered ?? 0).toLocaleString(), detail: `${report?.unansweredByAge.find(item => item.key === "over_3_days")?.count ?? 0} older than 3 days` },
    { label: "Pointed to an entry", value: (report?.pointedToAnEntry ?? 0).toLocaleString(), detail: "answered by link, not retyped" },
  ];
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Questions", to: "/questions" }, { label: "Reports" }]} />
    <PageHeader eyebrow={`Questions and knowledge · ${report?.window.from ?? "…"} to ${report?.window.through ?? "…"} · ${(report?.questions ?? 0).toLocaleString()} questions`} title="Reports" sub="Where questions come from, how fast they are answered, and which answers should become, or already are, knowledge entries." actions={<><div className="segmented" role="group" aria-label="Period">{periods.map(([value, label]) => <button type="button" key={value} aria-pressed={filters.period === value} onClick={() => setFilters(current => ({ ...current, period: value }))}>{label}</button>)}</div><a className="btn primary" href={reportsApi.exportUrl(filters)}>Export CSV</a></>} />
    <KnowledgeSubnav current="reports" /><ErrorNote error={error} /><KpiStrip kpis={kpis} />
    <div className="knowledge-report-ranks top"><Ranked title="By directed role" items={report?.byDirectedRole ?? []} selected={filters.directedRole} onSelect={value => select("directedRole", value)} /><Ranked title="By record kind" items={report?.byRecordKind ?? []} selected={filters.recordKind} onSelect={value => select("recordKind", value)} /><Ranked title="By topic" items={report?.byTopic ?? []} selected={filters.topic} onSelect={value => select("topic", value)} /></div>
    <div className="knowledge-report-lower"><section className="card"><div className="card-header"><h2>Asked again despite an entry <span className="meta">entry may be hard to find or unclear</span></h2></div><table className="knowledge-report-table"><thead><tr><th>Entry</th><th className="num">Asked again</th><th className="num">Pointed to it</th><th>Last asked</th></tr></thead><tbody>{report?.askedAgainDespiteEntry.map(row => <tr key={row.entryId}><td><Link to={`/knowledge/${row.entryId}`}>{row.title}</Link><span className="sub">Reviewed by {row.reviewedBy}</span></td><td className="num">{row.askedAgain}</td><td className="num">{row.pointedToIt}</td><td>{date(row.lastAskedAt)}</td></tr>)}</tbody></table>{report?.askedAgainDespiteEntry.length === 0 && <div className="card-body muted">No repeated questions matched an existing entry in this period.</div>}</section><div className="stack"><Ranked title={`Unanswered by age · ${report?.unanswered ?? 0} open`} items={report?.unansweredByAge ?? []} /><Ranked title={`Read by new team members · ${report?.readByNewTeamMembers.reads ?? 0} reads by ${report?.readByNewTeamMembers.newMembers ?? 0} people`} items={(report?.readByNewTeamMembers.entries ?? []).map(item => ({ key: item.entryId, label: item.title, count: item.reads }))} /></div></div>
    {report && <div className="note knowledge-report-help"><strong>How this report works.</strong> Period boundaries are tenant-local calendar dates in {report.window.timeZone}. Medians and 90th percentiles are calculated set-wise in SQL Server with <code>PERCENTILE_CONT</code>. {report.visibilityNote} {report.similarityNote} The export streams one row per question; title and question text are blank when record-reference visibility does not allow the viewer to read them.</div>}
  </main>;
}

function Ranked({ title, items, selected, onSelect }: { title: string; items: KnowledgeReportRank[]; selected?: string | undefined; onSelect?: ((value: string) => void) | undefined }) {
  const max = Math.max(1, ...items.map(item => item.count));
  return <section className="card"><div className="card-header"><h2>{title} {onSelect && <span className="meta">click to filter</span>}</h2></div><div className="card-body"><ul className="question-ranked">{items.map(item => <li key={item.key} aria-current={selected === item.key ? "true" : undefined} onClick={() => onSelect?.(item.key)}><span>{item.label}</span><span className="v">{item.count}</span><span className="bar"><i style={{ width: `${item.count / max * 100}%` }} /></span></li>)}</ul>{items.length === 0 && <span className="muted">No rows in this period.</span>}</div></section>;
}
