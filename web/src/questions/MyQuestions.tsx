import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router";
import { DataGrid, StatusBadge, type GridColumn, type PageResult } from "../components";
import { openDrawer } from "../shell/drawerStore";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { questionsApi } from "./api";
import { humanise, recordKinds, recordPath, statusLabel, statusTone } from "./model";
import type { MyQuestion, MyQuestionPage, RankedCount } from "./types";
import "./questions.css";

const dateTime = (value: string) => new Date(value).toLocaleString([], { dateStyle: "medium", timeStyle: "short" });

export function Component() {
  const [scope, setScope] = useState("all"); const [status, setStatus] = useState("open");
  const [recordKind, setRecordKind] = useState(""); const [age, setAge] = useState("");
  const [input, setInput] = useState(""); const [search, setSearch] = useState("");
  const [result, setResult] = useState<MyQuestionPage | null>(null); const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(true); const [page, setPage] = useState(1); const [cursor, setCursor] = useState<string | undefined>();
  const cursors = useRef<Array<string | undefined>>([undefined]);
  useEffect(() => { const timer = window.setTimeout(() => setSearch(input.trim()), 250); return () => window.clearTimeout(timer); }, [input]);
  const filters = { scope, status, recordKind, age, search };
  const fetchPage = useCallback((signal?: AbortSignal) => questionsApi.mine(filters, cursor, signal), [scope, status, recordKind, age, search, cursor]);
  useEffect(() => { setPage(1); setCursor(undefined); cursors.current = [undefined]; }, [scope, status, recordKind, age, search]);
  useEffect(() => { const c = new AbortController(); setBusy(true); setError(null); void fetchPage(c.signal).then(setResult).catch(reason => { if (!c.signal.aborted) setError(reason); }).finally(() => { if (!c.signal.aborted) setBusy(false); }); return () => c.abort(); }, [fetchPage]);
  const onPage = (target: number) => { if (target < 1 || busy) return; const next = target > page ? result?.nextCursor ?? undefined : cursors.current[target - 1]; if (target > page && !next) return; cursors.current[target - 1] = next; setCursor(next); setPage(target); };
  const open = (row: MyQuestion) => { const path = recordPath(row); openDrawer({ eyebrow: `${humanise(row.recordKind)} · ${humanise(row.status)}`, title: row.title }, <><div className="section"><h3>Record</h3><p>{row.recordName}</p><p className="muted">Asked by {row.askedByName} · directed at {row.directedTo}</p></div><div className="section"><h3>Waiting</h3><p>{row.waitingDays} days · {row.replyCount} replies</p></div>{path && <div className="section"><Link className="btn primary block" to={`${path}?tab=questions`}>Open question on record</Link></div>}</>); };
  const columns: GridColumn<MyQuestion>[] = [
    { key: "question", label: "Question · on record", width: 38, render: row => <span className="primary wrap">{row.title}<span className="sub">{humanise(row.recordKind)} · {row.recordName}</span></span> },
    { key: "people", label: "Asked by · directed at", width: 19, render: row => <>{row.askedByName}<span className="sub">{row.directedTo}</span></> },
    { key: "status", label: "Status", width: 14, render: row => <StatusBadge tone={statusTone(row.status)}>{statusLabel(row.status, row.replyCount)}</StatusBadge> },
    { key: "replies", label: "Replies", width: 7, numeric: true, render: row => row.replyCount },
    { key: "waiting", label: "Waiting", width: 8, numeric: true, render: row => `${row.waitingDays} d` },
    { key: "activity", label: "Last activity", width: 14, render: row => dateTime(row.lastActivityAt) },
  ];
  const grid: PageResult<MyQuestion> = { rows: result?.items ?? [], total: (page - 1) * 12 + (result?.items.length ?? 0), page, pageSize: 12, continuation: result?.nextCursor ?? null, totalIsExact: false, hasMore: !!result?.nextCursor };
  return <main className="content" id="main" tabIndex={-1}><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Questions" }]} /><PageHeader eyebrow="Questions and knowledge" title="My questions" sub="Questions you asked, questions put to you, and questions put to a role you hold. Waiting longest comes first." />
    <nav className="tabs questions-subnav" aria-label="Questions and knowledge"><Link to="/questions" aria-current="page">My questions</Link></nav>
    <ErrorNote error={error} />
    <section className="card" aria-labelledby="my-questions-table"><div className="card-header"><h2 id="my-questions-table">Waiting <span className="meta">server-paged</span></h2><div className="segmented" role="group" aria-label="Whose questions">{([['asked', 'Asked by me'], ['directed_to_me', 'Directed at me'], ['directed_to_roles', 'At my roles'], ['all', 'All']] as const).map(([value, label]) => <button type="button" key={value} aria-pressed={scope === value} onClick={() => setScope(value)}>{label}</button>)}</div></div>
      <div className="questions-findbar"><label className="field questions-search"><span className="lbl">Search this table as you type</span><input className="input" type="search" value={input} onChange={event => setInput(event.target.value)} placeholder="Title, details or replies" /></label><select className={`chip-select${status ? " on" : ""}`} aria-label="Status" value={status} onChange={event => setStatus(event.target.value)}><option value="">Status · All</option><option value="open">Status · Open</option><option value="answered">Status · Answered</option><option value="closed_without_answer">Status · Closed without answer</option></select><select className={`chip-select${recordKind ? " on" : ""}`} aria-label="Record kind" value={recordKind} onChange={event => setRecordKind(event.target.value)}><option value="">Record kind · All</option>{recordKinds.map(kind => <option key={kind} value={kind}>Record kind · {humanise(kind)}</option>)}</select><select className={`chip-select${age ? " on" : ""}`} aria-label="Age" value={age} onChange={event => setAge(event.target.value)}><option value="">Age · All</option><option value="under_1_day">Age · Under 1 day</option><option value="one_to_three_days">Age · 1–3 days</option><option value="four_to_seven_days">Age · 4–7 days</option><option value="over_seven_days">Age · Over 7 days</option></select></div>
      <DataGrid columns={columns} page={grid} rowId={row => row.questionId} onPage={onPage} loading={busy} onRowClick={open} emptyText="No questions match these filters." viewportHeight={520} footerNote="Search and filters run on the server" />
    </section>
    <div className="cols-2-3" style={{ marginTop: "var(--gap)" }}><Ranked title="Waiting by age" items={result?.summary.waitingByAge ?? []} selected={age} onSelect={setAge} /><Ranked title="Waiting by record kind" items={result?.summary.waitingByRecordKind ?? []} selected={recordKind} onSelect={setRecordKind} /></div>
  </main>;
}

function Ranked({ title, items, selected, onSelect }: { title: string; items: RankedCount[]; selected: string; onSelect: (value: string) => void }) {
  const max = Math.max(1, ...items.map(item => item.count));
  return <section className="card"><div className="card-header"><h2>{title} <span className="meta">click to filter</span></h2></div><div className="card-body"><ul className="question-ranked">{items.map(item => <li key={item.key} aria-current={selected === item.key ? "true" : undefined} onClick={() => onSelect(selected === item.key ? "" : item.key)}><span>{item.label}</span><span className="v">{item.count}</span><span className="bar"><i style={{ width: `${item.count / max * 100}%` }} /></span></li>)}</ul></div></section>;
}
