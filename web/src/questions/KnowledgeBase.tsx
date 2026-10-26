import { useEffect, useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { DataGrid, StatusBadge, useGridQuery, type GridColumn, type PageResult } from "../components";
import { useSession } from "../session/session";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { knowledgeApi } from "./api";
import { KnowledgeSubnav } from "./KnowledgeSubnav";
import { humanise, recordPath } from "./model";
import { knowledgeEntryPage, type KnowledgeEntry, type KnowledgeEntryListItem } from "./types";
import "./questions.css";

const date = (value: string) => new Date(value).toLocaleDateString([], { dateStyle: "medium" });

export function Component() {
  const route = useParams<{ entryId?: string }>(); const navigate = useNavigate();
  const session = useSession();
  const [input, setInput] = useState(""); const [query, setQuery] = useState("");
  const [role, setRole] = useState(""); const [task, setTask] = useState("");
  const [entry, setEntry] = useState<KnowledgeEntry | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { const timer = window.setTimeout(() => setQuery(input.trim()), 250); return () => window.clearTimeout(timer); }, [input]);
  const filters = useMemo(() => { const value = new URLSearchParams(); if (query) value.set("query", query); if (role) value.set("role", role); if (task) value.set("task", task); return value; }, [query, role, task]);
  const gridQuery = useGridQuery("/api/knowledge/entries", knowledgeEntryPage, {
    gridId: "knowledge-base", userId: session.user.id, defaultSort: query ? { key: "relevance", dir: "desc" } : { key: "published", dir: "desc" },
    defaultSize: 12, params: filters, paging: "cursor", sortKeys: query ? ["relevance"] : ["published"],
    pageFacts: value => ({ continuation: value.nextCursor }),
  });
  const result = gridQuery.data;
  useEffect(() => { if (!route.entryId && result?.items[0]) navigate(`/knowledge/${result.items[0].entryId}`, { replace: true }); }, [navigate, result, route.entryId]);
  useEffect(() => { if (!route.entryId) { setEntry(null); return; } const c = new AbortController(); void knowledgeApi.entry(route.entryId, c.signal).then(setEntry).catch(reason => { if (!c.signal.aborted) setError(reason); }); return () => c.abort(); }, [route.entryId]);
  const rows: PageResult<KnowledgeEntryListItem> = { rows: result?.items ?? [], total: (gridQuery.page - 1) * gridQuery.pageSize + (result?.items.length ?? 0), page: gridQuery.page, pageSize: gridQuery.pageSize, continuation: result?.nextCursor ?? null, totalIsExact: false, hasMore: !!result?.nextCursor };
  const columns: GridColumn<KnowledgeEntryListItem>[] = [
    { key: "entry", label: "Reviewed entry", width: 58, render: row => <span className="primary wrap">{row.title}{row.needsReview && <span className="sub"><StatusBadge tone="warn">May be out of date</StatusBadge></span>}<span className="sub">{row.answer}</span></span> },
    { key: "tags", label: "Role · task", width: 24, render: row => <>{row.tags.filter(tag => tag.kind === "role").map(tag => tag.label).join(", ") || "All roles"}<span className="sub">{row.tags.filter(tag => tag.kind === "task").map(tag => tag.label).join(", ") || "No task tag"}</span></> },
    { key: "review", label: "Reviewed", width: 18, ...(query ? {} : { sort: { key: "published", defaultDir: "desc" as const } }), render: row => <><StatusBadge tone="ok">Reviewed</StatusBadge><span className="sub">{row.reviewerName} · {date(row.publishedAt)}</span></> },
  ];
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Questions", to: "/questions" }, { label: "Knowledge base" }]} />
    <PageHeader eyebrow="Questions and knowledge · reviewed entries" title="Knowledge base" sub="Answers the team has checked and written down once, so the same question is not asked again." />
    <KnowledgeSubnav current="knowledge" /><ErrorNote error={error ?? gridQuery.error} onReload={gridQuery.reload} />
    <div className="knowledge-split">
      <section className="card" aria-labelledby="knowledge-list"><div className="card-header"><h2 id="knowledge-list">Find <span className="meta">reviewed first</span></h2></div>
        <div className="questions-findbar"><label className="field questions-search"><span className="lbl">Search this table as you type</span><input className="input" type="search" value={input} onChange={event => setInput(event.target.value)} placeholder="Problem, answer or steps" /></label></div>
        <div className="knowledge-facets"><span className="eyebrow">Browse by role</span><div className="tagset"><button type="button" className={`chip${!role ? " on" : ""}`} onClick={() => setRole("")}>All</button>{result?.roles.map(facet => <button type="button" className={`chip${role === facet.value ? " on" : ""}`} key={facet.value} onClick={() => setRole(role === facet.value ? "" : facet.value)}>{facet.label} · {facet.count}</button>)}</div><span className="eyebrow">By task</span><div className="tagset"><button type="button" className={`chip${!task ? " on" : ""}`} onClick={() => setTask("")}>All</button>{result?.tasks.map(facet => <button type="button" className={`chip${task === facet.value ? " on" : ""}`} key={facet.value} onClick={() => setTask(task === facet.value ? "" : facet.value)}>{facet.label} · {facet.count}</button>)}</div></div>
        <DataGrid columns={columns} page={rows} rowId={row => row.entryId} onPage={gridQuery.onPage} loading={gridQuery.loading} onRowClick={row => navigate(`/knowledge/${row.entryId}`)} activeRowId={route.entryId} emptyText="No reviewed entries match." viewportHeight={520} footerNote="Search and filters run on the server"
          gridId="knowledge-base" userId={session.user.id} sort={gridQuery.sort} defaultSort={query ? { key: "relevance", dir: "desc" } : { key: "published", dir: "desc" }} onSort={gridQuery.onSort}
          pageSize={gridQuery.pageSize} defaultPageSize={12} onPageSize={gridQuery.onPageSize} />
      </section>
      {entry ? <EntryArticle entry={entry} /> : <section className="card"><div className="empty"><h2>No reviewed entry selected</h2><p className="hint">Choose an entry from the ranked list.</p></div></section>}
    </div>
  </main>;
}

function EntryArticle({ entry }: { entry: KnowledgeEntry }) {
  const steps = entry.steps.split(/\r?\n/).map(value => value.trim()).filter(Boolean);
  return <div className="stack"><article className="card knowledge-entry" aria-labelledby="knowledge-entry-title"><div className="question-head"><div className="eyebrow">Knowledge entry · version {entry.currentVersion} · {entry.caseDisclosureTier} disclosure</div><h2 id="knowledge-entry-title">{entry.title}</h2><div className="question-meta"><StatusBadge tone={entry.needsReview ? "warn" : entry.status === "published" ? "ok" : "neutral"}>{entry.needsReview ? "Needs review" : humanise(entry.status)}</StatusBadge>{entry.publishedByName && <span>Reviewed by {entry.publishedByName}{entry.publishedAt ? ` · ${date(entry.publishedAt)}` : ""}</span>}<span>Written by {entry.authorName}</span><span className="grow" />{(entry.allowedActions.canEdit || entry.allowedActions.canRetire || entry.allowedActions.canConfirmStillCorrect) && <Link className="btn sm" to={`/knowledge/entries/${entry.entryId}`}>{entry.allowedActions.canEdit ? "Edit entry" : "Review entry"}</Link>}</div></div><div className="card-body">{entry.needsReview && <div className="note warn"><strong>This entry may be out of date.</strong> It stays available while a reviewer checks it.<ul>{entry.reviewReasons.map(reason => <li key={reason.reasonId}>{reason.detail}</li>)}</ul></div>}<div className="tagset">{entry.tags.map(tag => <span className="tag" key={`${tag.kind}:${tag.value}`}><span className="k">{humanise(tag.kind)}</span>{tag.label}</span>)}</div><h3>Problem</h3><p className="preserve-lines">{entry.problem}</p><h3>Answer</h3><p className="preserve-lines">{entry.answer}</p><h3>Steps</h3>{steps.length ? <ol className="knowledge-steps">{steps.map((step, index) => <li key={index}>{step}</li>)}</ol> : <p className="muted">No separate steps were recorded.</p>}<h3>Written against</h3>{entry.writtenAgainst.length ? <p className="muted">{entry.writtenAgainst.map(item => `${humanise(item.kind)} · ${item.label}`).join(" · ")}</p> : <p className="muted">Nothing recorded.</p>}</div></article><section className="card"><div className="card-header"><h2>Linked records <span className="meta">what you may see</span></h2></div><div className="card-body"><ul className="knowledge-links">{entry.recordLinks.visible.map(link => { const path = recordPath(link); return <li key={`${link.recordKind}:${link.recordId}`}><span><strong>{link.recordName}</strong><span className="sub">{humanise(link.recordKind)}{link.source ? " · question this entry came from" : ""}</span></span>{path && <Link to={path}>Open</Link>}</li>; })}{entry.recordLinks.hiddenCount > 0 && <li className="muted"><span><strong>{entry.recordLinks.hiddenCount} more {entry.recordLinks.hiddenCount === 1 ? "record" : "records"}</strong><span className="sub">you can’t open {entry.recordLinks.hiddenCount === 1 ? "it" : "them"}</span></span></li>}</ul></div></section></div>;
}
