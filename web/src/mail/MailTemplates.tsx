import { useEffect, useMemo, useState } from "react";
import { Link, Navigate, useNavigate } from "react-router";
import { DataGrid, type GridColumn } from "../components/DataGrid";
import { KpiStrip } from "../components/KpiStrip";
import { StatusBadge } from "../components/StatusBadge";
import { useTenantTime } from "../components/tenantTime";
import type { PageResult } from "../components/types";
import { useSession } from "../session/session";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { mailApi, type MailKindView, type MailTemplatePage, type MailTemplateRow } from "./api";
import { DESIGNER_ROLES, SENDER_ROLES, holds, languageName, templateState } from "./format";
import { MailTabs } from "./MailTabs";
import "./mail.css";

type Filter = "all" | "published" | "drafts";

export function Component() {
  const session = useSession();
  const navigate = useNavigate();
  const tenantTime = useTenantTime();
  const designer = holds(session.roles, DESIGNER_ROLES);
  const [filter, setFilter] = useState<Filter>("all");
  const [page, setPage] = useState<MailTemplatePage | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    if (!designer) return;
    const controller = new AbortController();
    mailApi.templates(filter === "all" ? undefined : filter === "published" ? "published" : "draft", controller.signal).then(setPage)
      .catch((reason) => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, [designer, filter]);

  const columns = useMemo<GridColumn<MailTemplateRow>[]>(() => [
    { key: "name", label: "Template", width: 28, render: (row) => <>{row.name}<span className="sub">{row.description ?? (row.startedFrom ? `Started from: ${row.startedFrom}` : "")}</span></> },
    { key: "kind", label: "Kind of message", width: 18, render: (row) => row.kindName },
    { key: "languages", label: "Languages", width: 12, render: (row) => <span title={row.languages.map(languageName).join(", ")}>{row.languages.map((code) => code.toUpperCase()).join(" · ")}</span> },
    { key: "version", label: "Version", width: 16, render: (row) => { const state = templateState(row); return <>
      <StatusBadge tone={state.tone}>{state.text}</StatusBadge>
      {row.publishedVersion !== null && row.draftVersion !== null && <span className="sub">v{row.draftVersion} {row.waitingForReview ? "waiting for review" : "draft"}</span>}
    </>; } },
    { key: "default", label: "Default for", width: 10, render: (row) => row.defaultFor.length ? row.defaultFor.map((code) => code.toUpperCase()).join(" · ") : <span className="muted">—</span> },
    { key: "change", label: "Last change", width: 16, render: (row) => `${tenantTime.date(row.lastChangeAt)} · ${row.lastChangeBy}` },
    { key: "sent", label: "Sent, 30 days", width: 10, numeric: true, render: (row) => row.publishedVersion === null && row.sent30Days === 0 ? "—" : row.sent30Days.toLocaleString() },
  ], [tenantTime]);

  if (!designer && holds(session.roles, SENDER_ROLES)) return <Navigate to="/mail/sent" replace />;

  const start = async (kind: MailKindView) => {
    setBusy(true); setError(null);
    try {
      const existing = new Set(page?.rows.map((row) => row.name) ?? []);
      let name = `${kind.starterName} · new`;
      for (let n = 2; existing.has(name); n++) name = `${kind.starterName} · new ${n}`;
      const created = await mailApi.create(kind.kind, name);
      navigate(`/mail/templates/${created.templateId}`);
    } catch (reason) { setError(reason); setBusy(false); }
  };

  const kpis = page?.kpis;
  const grid: PageResult<MailTemplateRow> = { rows: page?.rows ?? [], total: page?.total ?? 0, page: 1, pageSize: Math.max(1, page?.rows.length ?? 1), continuation: null };
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Customer e-mail" }, { label: "Mail templates" }]} />
    <PageHeader eyebrow="Customer e-mail" title="Mail templates"
      sub="What the company sends to customers, one template per kind of message and language. Templates are designed here, published as versions, and filled from the record on the server; a sent message keeps the version it was made from."
      actions={<>{holds(session.roles, SENDER_ROLES) && <Link className="btn" to="/mail/sent">Sent log</Link>}
        {page?.kinds.length === 1 && <button type="button" className="btn primary" disabled={busy} onClick={() => void start(page.kinds[0]!)}>New template…</button>}</>} />
    {!designer && <div className="note bad"><strong>Designing customer e-mail needs the service manager, customer service, certificate administrator or tenant administrator role.</strong></div>}
    {designer && <div className="stack">
      <MailTabs templates={page?.total} sent={kpis?.sent30Days} />
      <ErrorNote error={error} />
      {kpis && <KpiStrip kpis={[
        { label: "Published templates", value: kpis.published.toLocaleString(), detail: `for ${kpis.publishedKinds} ${kpis.publishedKinds === 1 ? "kind" : "kinds"} of message` },
        { label: "Drafts", value: kpis.drafts.toLocaleString(), detail: kpis.waitingForReview ? `${kpis.waitingForReview} waiting for review` : "none waiting for review" },
        { label: "Sent, 30 days", value: kpis.sent30Days.toLocaleString(), detail: kpis.deliveredShare === null ? null : `${kpis.deliveredShare}% delivered`, trend: kpis.deliveredShare !== null && kpis.deliveredShare >= 95 ? "up" : null },
        { label: "Languages", value: kpis.languages.length.toLocaleString(), detail: kpis.languages.map(languageName).join(", ") || null },
      ]} />}
      <section className="card" aria-labelledby="mail-templates-heading">
        <div className="card-header"><h2 id="mail-templates-heading">Templates <span className="meta">{page?.total ?? ""}</span></h2>
          <div className="segmented" role="group" aria-label="Show">
            {(["all", "published", "drafts"] as Filter[]).map((value) => <button type="button" key={value} aria-pressed={filter === value} onClick={() => setFilter(value)}>{value === "all" ? "All" : value === "published" ? "Published" : "Drafts"}</button>)}
          </div>
        </div>
        <DataGrid columns={columns} page={grid} rowId={(row) => row.templateId} onPage={() => undefined} loading={!page && !error}
          onRowClick={(row) => navigate(`/mail/templates/${row.templateId}`)} emptyText="No templates yet. Start from a built-in layout below."
          footer={<span className="muted">Each kind of message has one default template per language; the person sending can pick another published one.</span>} />
      </section>
      {page && <section className="card" aria-labelledby="mail-starters-heading">
        <div className="card-header"><h2 id="mail-starters-heading">Start from a built-in layout</h2><span className="meta">copied into a draft you then change</span></div>
        <div className="card-body"><div className="mail-starters">
          {page.kinds.map((kind) => <button type="button" className="mail-starter" key={kind.kind} disabled={busy} onClick={() => void start(kind)}>
            <strong>{kind.starterName}</strong><span>{kind.starterDescription}</span>
          </button>)}
        </div>
          <p className="hint" style={{ margin: "12px 0 0" }}>More kinds of message (certificate issued, claim outcome, return updates) arrive with their own screens; each brings its field list and built-in layout.</p>
        </div>
      </section>}
    </div>}
  </main>;
}
