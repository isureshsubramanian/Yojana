import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { DataGrid, KpiStrip, StatusBadge, useGridQuery, type GridColumn, type PageResult } from "../components";
import { useReferenceNames } from "../corrections/ReferenceFields";
import { usePeople } from "../people/people";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { openDrawer } from "../shell/drawerStore";
import { PageHeader } from "../shell/PageHeader";
import { ErrorNote, Kv, Section } from "../units/common";
import { blockerPath, erasureStatus, erasureSteps, storeLabel } from "./privacyModel";
import { erasurePage, privacyApi, type ErasureDetail, type ErasureRow } from "./privacyApi";

const PAGE_SIZE = 50;
const PRIVACY_EXPORT_PATH = "/api/privacy/erasures/export";
const PRIVACY_NEW_PATH = "/settings/privacy/new";
const when = (value: string | null) => value ? new Date(value).toLocaleString() : "—";

export function Component() {
  const session = useSession();
  const permitted = session.roles.includes("tenant_administrator");
  const query = useGridQuery("/api/privacy/erasures", erasurePage, {
    gridId: "privacy-erasures", userId: session.user.id, defaultSort: { key: "recorded", dir: "desc" },
    defaultSize: PAGE_SIZE, paging: "cursor", sortKeys: ["recorded", "reference", "received", "status"],
    enabled: permitted, pageFacts: value => ({ continuation: value.nextCursor }),
  });
  const result = query.data;
  const parties = useReferenceNames({ parties: result?.items.filter(row => row.status === "refused" && row.subjectKind === "Party").map(row => row.subjectId) ?? [] });
  const people = usePeople(result?.items.filter(row => row.status === "refused" && row.subjectKind === "User").map(row => row.subjectId) ?? []);

  const subject = useCallback((row: ErasureRow) => {
    if (row.status !== "refused") return row.token;
    const name = row.subjectKind === "Party" ? parties.party(row.subjectId) : people(row.subjectId);
    return name === row.subjectId ? "Name unavailable" : name;
  }, [parties, people]);
  const show = useCallback((row: ErasureRow) => openDrawer({ eyebrow: "Erasure request", title: row.requestReference, mono: true }, <ErasureDrawer row={row} subject={subject(row)} />), [subject]);
  const columns = useMemo<GridColumn<ErasureRow>[]>(() => [
    { key: "reference", label: "Request ref", width: 15, mono: true, sort: { key: "reference", defaultDir: "asc" }, render: row => <span className="primary">{row.requestReference}</span> },
    { key: "person", label: "Person", width: 25, render: row => subject(row) },
    { key: "kind", label: "Kind", width: 10, render: row => row.subjectKind === "Party" ? "Customer" : "User" },
    { key: "by", label: "Requested by", width: 19, render: row => row.requestedBy },
    { key: "received", label: "Received", width: 14, muted: true, sort: { key: "received", defaultDir: "desc" }, render: row => new Date(`${row.requestReceivedOn}T00:00:00`).toLocaleDateString() },
    { key: "status", label: "Status", width: 17, sort: { key: "status", defaultDir: "asc" }, render: row => { const state = erasureStatus(row.status, row.failureRule); return <StatusBadge tone={state.tone}>{state.label}</StatusBadge>; } },
  ], [subject]);
  const rows = result?.items ?? [];
  const grid: PageResult<ErasureRow> = { rows, page: query.page, pageSize: query.pageSize,
    total: (query.page - 1) * query.pageSize + rows.length + (result?.nextCursor ? 1 : 0), continuation: result?.nextCursor ?? null,
    totalIsExact: false, hasMore: !!result?.nextCursor };
  const oldest = result?.summary.oldestOpen;
  const oldestDetail = !oldest ? "no open requests" : oldest.warning === "overdue" ? `overdue · due ${oldest.dueOn}` : oldest.warning === "due_soon" ? `one-month limit near · due ${oldest.dueOn}` : "legal limit one month from receipt";

  return <main className="content" id="main" tabIndex={-1}>
    <PageHeader eyebrow="Settings · Data and knowledge · tenant administrators only" title="Privacy"
      sub="Erase a person's personal data that Yojana holds when they ask. Units, ownership history, warranties, claims and RMAs are kept, but no longer point to the person. Erase the person in your source system as well: Yojana cannot do that for you."
      actions={permitted && <><a className="btn" href={`${PRIVACY_EXPORT_PATH}?${new URLSearchParams({ sort: query.sort.key, dir: query.sort.dir })}`}><Icon name="down" />Export log</a><Link className="btn primary" to={PRIVACY_NEW_PATH}><Icon name="plus" />New erasure</Link></>} />
    {!permitted ? <div className="note bad"><strong>Tenant administrator access is required.</strong></div> : <div className="stack">
      <ErrorNote error={query.error} onReload={query.reload} />
      <KpiStrip label="Erasure request figures" kpis={[
        { label: "Completed", value: result?.summary.completedLastTwelveMonths.toLocaleString() ?? "—", detail: "last 12 months" },
        { label: "In progress", value: result?.summary.inProgress.toLocaleString() ?? "—", detail: "pending or running" },
        { label: "Refused", value: result?.summary.refused.toLocaleString() ?? "—", detail: "saved with the reason" },
        { label: "Oldest open request", value: oldest ? `${oldest.ageDays} ${oldest.ageDays === 1 ? "day" : "days"}` : "—", detail: oldestDetail },
      ]} />
      <section className="card" aria-label="Erasure requests"><div className="card-header"><h2>Erasure requests</h2><span className="meta">newest first</span></div>
        <DataGrid columns={columns} page={grid} rowId={row => row.erasureId} onPage={query.onPage} onRowClick={show} loading={query.loading} emptyText="No erasure requests have been recorded." viewportHeight={560}
          gridId="privacy-erasures" userId={session.user.id} sort={query.sort} defaultSort={{ key: "recorded", dir: "desc" }} onSort={query.onSort}
          pageSize={query.pageSize} defaultPageSize={PAGE_SIZE} onPageSize={query.onPageSize} />
      </section>
    </div>}
  </main>;
}

function ErasureDrawer({ row, subject }: { row: ErasureRow; subject: string }) {
  const [detail, setDetail] = useState<ErasureDetail | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { const controller = new AbortController(); privacyApi.detail(row.erasureId, controller.signal).then(setDetail).catch(reason => { if (!controller.signal.aborted) setError(reason); }); return () => controller.abort(); }, [row.erasureId]);
  if (error) return <ErrorNote error={error} />;
  if (!detail) return <p className="muted">Loading…</p>;
  return <>
    <Section title="Request"><Kv rows={[["Person", subject], ["Requested by", detail.requestedBy], ["Received", new Date(`${detail.requestReceivedOn}T00:00:00`).toLocaleDateString()], ["Recorded", when(detail.requestedAt)], ["Reason", detail.reason]]} /></Section>
    <Section title="Progress"><ul className="checks">{erasureSteps(detail).map(step => <li key={step.label}><span>{step.label}<span className="s">{step.detail}</span></span><StatusBadge tone={step.status === "Done" ? "ok" : step.status === "Running" ? "info" : step.status === "Refused" ? "warn" : "neutral"}>{step.status}</StatusBadge></li>)}</ul></Section>
    {detail.counts.length > 0 && <Section title="Per-store counts"><div className="kv">{detail.counts.map(count => <span className="privacy-count" key={`${count.store}:${count.kind}`}><span><strong>{storeLabel(count.store, count.kind)}</strong><small>{count.action}</small></span><strong>{(count.status === "completed" ? count.erasedCount : count.previewCount).toLocaleString()}</strong></span>)}</div></Section>}
    {detail.blockers.length > 0 && <Section title="Blocking records"><ul className="privacy-blockers">{detail.blockers.map(blocker => { const path = blockerPath(blocker); return <li key={`${blocker.kind}:${blocker.recordId ?? blocker.reference}`}><span><strong>{blocker.reference}</strong><small>{blocker.detail}</small></span>{path && <Link className="btn sm" to={path}>Open</Link>}</li>; })}</ul></Section>}
    <Section title="Kept, and why"><p className="muted">{detail.retentionBasis}</p></Section>
  </>;
}
