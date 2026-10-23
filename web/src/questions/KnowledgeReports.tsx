import { useEffect, useState } from "react";
import { Link } from "react-router";
import { KpiStrip } from "../components";
import { Icon } from "../shell/Icons";
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
  if (seconds < 60) return "Under 1 m";
  return `${Math.floor(seconds / 60)} m`;
};
const date = (value: string) => new Date(value).toLocaleDateString([], { dateStyle: "medium" });
// Window bounds are tenant-local calendar dates (yyyy-mm-dd); read them as dates, not instants, so no zone shifts the day.
const day = (value: string) => new Date(`${value}T00:00:00Z`).toLocaleDateString([], { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" });
const filterNames: Array<["directedRole" | "recordKind" | "topic", string]> = [["directedRole", "Role"], ["recordKind", "Record kind"], ["topic", "Topic"]];

export function Component() {
  const [filters, setFilters] = useState<KnowledgeReportFilters>({ period: "90_days" });
  const [report, setReport] = useState<KnowledgeReport | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { const controller = new AbortController(); setError(null); void reportsApi.report(filters, controller.signal)
    .then(setReport).catch(reason => { if (!controller.signal.aborted) setError(reason); }); return () => controller.abort(); }, [filters]);
  const select = (key: "directedRole" | "recordKind" | "topic", value: string) => setFilters(current => ({ ...current, [key]: current[key] === value ? undefined : value }));
  const active = filterNames.flatMap(([key, name]) => {
    const value = filters[key];
    if (!value) return [];
    const list = key === "directedRole" ? report?.byDirectedRole : key === "recordKind" ? report?.byRecordKind : report?.byTopic;
    return [[key, name, list?.find(item => item.key === value)?.label ?? value] as const];
  });
  const kpis = [
    { label: "First reply, median", value: duration(report?.firstReply.medianSeconds ?? null), detail: `90th percentile ${duration(report?.firstReply.ninetiethPercentileSeconds ?? null).toLowerCase()}` },
    { label: "Answer accepted, median", value: duration(report?.acceptedAnswer.medianSeconds ?? null), detail: `90th percentile ${duration(report?.acceptedAnswer.ninetiethPercentileSeconds ?? null).toLowerCase()}` },
    { label: "Unanswered now", value: (report?.unanswered ?? 0).toLocaleString(), detail: `${report?.unansweredByAge.find(item => item.key === "over_3_days")?.count ?? 0} older than 3 days` },
    { label: "Pointed to an entry", value: (report?.pointedToAnEntry ?? 0).toLocaleString(), detail: "answered by link, not retyped" },
  ];
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Questions", to: "/questions" }, { label: "Reports" }]} />
    <PageHeader eyebrow={`Questions and knowledge · ${report ? `${day(report.window.from)} – ${day(report.window.through)}` : "…"} · ${(report?.questions ?? 0).toLocaleString()} ${report?.questions === 1 ? "question" : "questions"}`} title="Reports" sub="Where questions come from, how fast they are answered, and which answers should become, or already are, knowledge entries." actions={<><div className="segmented" role="group" aria-label="Period">{periods.map(([value, label]) => <button type="button" key={value} aria-pressed={filters.period === value} onClick={() => setFilters(current => ({ ...current, period: value }))}>{label}</button>)}</div><a className="btn primary" href={reportsApi.exportUrl(filters)}>Export CSV</a></>} />
    <KnowledgeSubnav current="reports" /><ErrorNote error={error} />
    {active.length > 0 && <div className="knowledge-report-filters" aria-label="Active filters"><span className="muted">Filtered by</span>{active.map(([key, name, label]) => <button type="button" className="chip on" key={key} aria-label={`Remove filter ${name}: ${label}`} onClick={() => select(key, filters[key]!)}>{name}: {label}<Icon name="x" /></button>)}<button type="button" className="btn sm ghost" onClick={() => setFilters(current => ({ period: current.period }))}>Clear all</button></div>}
    <KpiStrip kpis={kpis} />
    <div className="knowledge-report-ranks top">
      <Ranked title="By directed role" items={report?.byDirectedRole ?? []} selected={filters.directedRole} onSelect={value => select("directedRole", value)} />
      <Ranked title="By record kind" items={report?.byRecordKind ?? []} selected={filters.recordKind} onSelect={value => select("recordKind", value)} />
      <Ranked title="By topic" items={report?.byTopic ?? []} selected={filters.topic} onSelect={value => select("topic", value)} />
      <Ranked title="Unanswered by age" meta={`${report?.unanswered ?? 0} open`} items={report?.unansweredByAge ?? []} />
    </div>
    <div className="knowledge-report-lower">
      <section className="card"><div className="card-header"><h2>Asked again despite an entry <span className="meta">entry may be hard to find or unclear</span></h2></div><table className="knowledge-report-table"><thead><tr><th>Entry</th><th className="num">Asked again</th><th className="num">Pointed to it</th><th>Last asked</th></tr></thead><tbody>{report?.askedAgainDespiteEntry.map(row => <tr key={row.entryId}><td><Link to={`/knowledge/${row.entryId}`}>{row.title}</Link><span className="sub">Reviewed by {row.reviewedBy}</span></td><td className="num">{row.askedAgain}</td><td className="num">{row.pointedToIt}</td><td>{date(row.lastAskedAt)}</td></tr>)}</tbody></table>{report?.askedAgainDespiteEntry.length === 0 && <div className="card-body muted">No repeated questions matched an existing entry in this period.</div>}</section>
      <Ranked title="Read by new team members" meta={readers(report)} items={(report?.readByNewTeamMembers.entries ?? []).map(item => ({ key: item.entryId, label: item.title, count: item.reads }))} />
    </div>
    {report && <details className="note knowledge-report-help"><summary><strong>How this report works</strong></summary>Periods follow the company's calendar days in {report.window.timeZone}. Medians and 90th percentiles cover the answered questions in the period. {report.visibilityNote} {report.similarityNote} The export has one row per question; the title and question text are blank where you are not allowed to read the record.</details>}
  </main>;
}

function readers(report: KnowledgeReport | null): string {
  const reads = report?.readByNewTeamMembers.reads ?? 0; const people = report?.readByNewTeamMembers.newMembers ?? 0;
  return `${reads.toLocaleString()} ${reads === 1 ? "read" : "reads"} by ${people.toLocaleString()} ${people === 1 ? "person" : "people"}`;
}

function Ranked({ title, meta, items, selected, onSelect }: { title: string; meta?: string; items: KnowledgeReportRank[]; selected?: string | undefined; onSelect?: ((value: string) => void) | undefined }) {
  const max = Math.max(1, ...items.map(item => item.count));
  const row = (item: KnowledgeReportRank) => <><span className="name">{item.label}</span><span className="v">{item.count.toLocaleString()}</span><span className="bar"><i style={{ width: `${item.count / max * 100}%` }} /></span></>;
  return <section className="card"><div className="card-header"><h2>{title}</h2>{(meta ?? onSelect) && <span className="meta">{meta ?? "select to filter"}</span>}</div><div className="card-body"><ul className="question-ranked">{items.map(item => <li key={item.key}>{onSelect
    ? <button type="button" aria-pressed={selected === item.key} onClick={() => onSelect(item.key)}>{row(item)}</button>
    : <div>{row(item)}</div>}</li>)}</ul>{items.length === 0 && <span className="muted">No rows in this period.</span>}</div></section>;
}
