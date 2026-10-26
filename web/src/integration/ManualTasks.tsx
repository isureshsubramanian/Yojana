import { useEffect, useMemo, useState } from "react";
import { DataGrid, Shortcut, StatusBadge, useGridQuery, useModEnter, type GridColumn, type PageResult } from "../components";
import { ApiError } from "../api/client";
import { openDrawer } from "../shell/drawerStore";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { useSession } from "../session/session";
import { ErrorNote, Kv, Section } from "../units/common";
import { integrationApi } from "./api";
import { IntegrationAccess, IntegrationTabs, useIntegrationTabCounts } from "./Common";
import { age, displayValue, draftValueIsMono, human, localDate, parseDraft, recordLabel } from "./model";
import { manualTaskPage, type ManualDraft, type ManualTask, type ManualTaskPage } from "./types";
import "./integration.css";

const empty: ManualTaskPage = { items: [], nextCursor: null, total: 0, desks: [] };
function pageOf(result: ManualTaskPage, page: number, pageSize: number): PageResult<ManualTask> {
  return { rows: result.items, total: result.total, page, pageSize, continuation: result.nextCursor, totalIsExact: true, hasMore: result.nextCursor !== null };
}
function title(task: ManualTask) { const draft = parseDraft(task); return String(draft.title ?? recordLabel(task.commandType)); }
function subtitle(task: ManualTask) { const draft = parseDraft(task); return String(draft.subtitle ?? draft.reason ?? task.connectionLabel); }

export function Component() {
  const session = useSession();
  const tabCounts = useIntegrationTabCounts();
  const [desk, setDesk] = useState(""); const [selected, setSelected] = useState<ManualTask | null>(null); const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null); const [erpNumber, setErpNumber] = useState(""); const [unverified, setUnverified] = useState(false);
  const [cancelReason, setCancelReason] = useState("");
  const filters = useMemo(() => { const value = new URLSearchParams({ status: "open" }); if (desk) value.set("desk", desk); return value; }, [desk]);
  const query = useGridQuery("/api/integration/manual-tasks", manualTaskPage, {
    gridId: "integration-manual-tasks", userId: session.user.id, defaultSort: { key: "created", dir: "asc" },
    defaultSize: 12, params: filters, paging: "cursor", sortKeys: ["created", "desk"],
    pageFacts: value => ({ continuation: value.nextCursor }),
  });
  const result = query.data ?? empty;
  useEffect(() => {
    if (!query.data || query.data.items.some(row => row.manualOutboundTaskId === selected?.manualOutboundTaskId)) return;
    setSelected(query.data.items[0] ?? null); setErpNumber(""); setUnverified(false); setCancelReason("");
  }, [query.data, selected?.manualOutboundTaskId]);

  const select = async (row: ManualTask, drawer = true) => {
    setError(null); setUnverified(false); setErpNumber(""); setCancelReason("");
    try {
      const detail = await integrationApi.manualTask(row.manualOutboundTaskId); setSelected(detail);
      if (drawer) openDrawer({ eyebrow: "Draft history", title: title(detail) }, <TaskHistory task={detail} />);
    } catch (reason) { setError(reason); }
  };
  const complete = async (allowUnverified = false) => {
    if (!selected || !erpNumber.trim()) return; setBusy(true); setError(null);
    try {
      await integrationApi.completeManualTask(selected.manualOutboundTaskId, erpNumber.trim(), allowUnverified);
      setUnverified(false); setSelected(null); query.onPage(1); query.reload();
    } catch (reason) {
      if (reason instanceof ApiError && reason.rule === "manual_task_external_id_unverified") setUnverified(true);
      else setError(reason);
    } finally { setBusy(false); }
  };
  const cancel = async () => {
    const deadLetterItemId = selected?.deadLetterItemId;
    if (!deadLetterItemId || !cancelReason.trim()) return; setBusy(true); setError(null);
    try { await integrationApi.discard(deadLetterItemId, cancelReason.trim()); setSelected(null); query.onPage(1); query.reload(); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  useModEnter(() => complete(false), !selected || busy || !erpNumber.trim());

  const totalOpen = result.desks.reduce((sum, item) => sum + item.count, 0);
  const counts = useMemo(() => new Map(result.desks.map((row) => [row.desk, row.count])), [result.desks]);
  const columns: GridColumn<ManualTask>[] = [
    { key: "draft", label: "Draft", width: 61, render: (row) => <><span className="primary">{title(row)}</span><span className="sub">{subtitle(row)}</span></> },
    { key: "desk", label: "Desk", width: 24, sort: { key: "desk", defaultDir: "asc" }, render: (row) => result.desks.find((item) => item.desk === row.desk)?.label ?? human(row.desk) },
    { key: "age", label: "Age", width: 15, numeric: true, sort: { key: "created", defaultDir: "asc" }, render: (row) => age(row.createdAt) },
  ];

  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Integration", to: "/integration" }, { label: "ERP entries to make by hand" }]} />
    <PageHeader eyebrow={`${selected?.connectionLabel ?? "All connections"} · ${totalOpen.toLocaleString()} waiting`} title="ERP entries to make by hand"
      sub="Documents the module has decided on but does not post itself. Each draft is filled in for you: copy it into the ERP, then record the ERP document number here. Warranty decisions never wait for this."
      actions={<button type="button" className="btn primary" disabled={!selected || busy || !erpNumber.trim()} onClick={() => void complete(false)}>Record ERP number <Shortcut keys="mod+enter" /></button>} />
    <IntegrationTabs attention={tabCounts.attention} manual={totalOpen} />
    <IntegrationAccess><ErrorNote error={error ?? query.error} /><div className="integration-split">
      <section className="card" aria-labelledby="manual-list"><div className="card-header"><h2 id="manual-list">Waiting</h2><span className="meta">oldest first</span></div>
        <div className="segmented integration-desks" role="group" aria-label="Desk"><button type="button" aria-pressed={!desk} onClick={() => setDesk("")}>All<span className="n">{totalOpen}</span></button>{result.desks.map((item) => <button type="button" key={item.desk} aria-pressed={desk === item.desk} onClick={() => setDesk(item.desk)}>{item.label}<span className="n">{counts.get(item.desk) ?? 0}</span></button>)}</div>
        <DataGrid columns={columns} page={pageOf(result, query.page, query.pageSize)} rowId={(row) => row.manualOutboundTaskId} onPage={query.onPage} loading={busy || query.loading} onRowClick={(row) => void select(row)} activeRowId={selected?.manualOutboundTaskId} viewportHeight={522} emptyText="No ERP entries are waiting."
          gridId="integration-manual-tasks" userId={session.user.id} sort={query.sort} defaultSort={{ key: "created", dir: "asc" }} onSort={query.onSort}
          pageSize={query.pageSize} defaultPageSize={12} pageSizeOptions={[12, 25, 50]} onPageSize={query.onPageSize} footerNote="Sorted by age, oldest first" />
      </section>
      <div className="stack">{selected ? <DraftDetail task={selected} erpNumber={erpNumber} setErpNumber={(value) => { setErpNumber(value); setUnverified(false); }} unverified={unverified} complete={complete} busy={busy} cancelReason={cancelReason} setCancelReason={setCancelReason} cancel={cancel} /> : <section className="card integration-empty"><div className="card-body"><h2>No draft selected</h2><p className="muted">Choose a waiting draft to see its copy-ready fields.</p></div></section>}</div>
    </div></IntegrationAccess>
  </main>;
}

function DraftDetail({ task, erpNumber, setErpNumber, unverified, complete, busy, cancelReason, setCancelReason, cancel }: { task: ManualTask; erpNumber: string; setErpNumber: (value: string) => void; unverified: boolean; complete: (allow: boolean) => Promise<void>; busy: boolean; cancelReason: string; setCancelReason: (value: string) => void; cancel: () => Promise<void> }) {
  const draft = parseDraft(task); const fields = Object.entries(draft.fields ?? {}); const lines = draft.lines ?? [];
  const copyAll = async () => { const text = fields.map(([key, value]) => `${human(key)}: ${displayValue(value)}`).join("\n"); await navigator.clipboard.writeText(text); };
  return <section className="card" aria-labelledby="draft-detail"><div className="card-header"><h2 id="draft-detail">{title(task)}</h2><div className="integration-card-actions"><StatusBadge tone="warn">Waiting for ERP number</StatusBadge><button type="button" className="btn sm" onClick={() => void copyAll()}>Copy all</button></div></div>
    <div className="card-body stack"><div className="note"><strong>Why by hand.</strong>&nbsp;{draft.reason ?? "This connection writes references only, or the ERP refused the automatic entry. The decision here is already complete."}</div>
      <ul className="integration-copylist">{fields.map(([key, value]) => <li key={key}><span className="k">{human(key)}</span><span className={`v${draftValueIsMono(key) ? " mono" : ""}`}>{displayValue(value)}</span><button type="button" className="btn sm" onClick={() => void navigator.clipboard.writeText(displayValue(value))}>Copy</button></li>)}</ul>
    </div>
    {lines.length > 0 && <table className="table dense integration-lines"><thead><tr>{Object.keys(lines[0] ?? {}).map((key) => <th key={key}>{human(key)}</th>)}</tr></thead><tbody>{lines.map((line, index) => <tr key={index}>{Object.keys(lines[0] ?? {}).map((key) => <td key={key} className={draftValueIsMono(key) ? "mono" : undefined}>{displayValue(line[key])}</td>)}</tr>)}</tbody></table>}
    {draft.total !== undefined && <div className="card-footer"><span className="muted">Tax and accounts are set by the ERP as usual</span><span className="grow" /><strong>Total {draft.currency ?? ""} {Number(draft.total).toLocaleString(undefined, { minimumFractionDigits: 2 })}</strong></div>}
    <div className="integration-enter-id"><label className="field"><span className="lbl">ERP document number, once posted</span><input className="input mono" value={erpNumber} onChange={(event) => setErpNumber(event.target.value)} placeholder="for example 30552" required /><span className="help">We look for the pasted reference first, so the same document is never recorded twice.</span></label><button type="button" className="btn primary" disabled={busy || !erpNumber.trim()} onClick={() => void complete(false)}>Record ERP number</button></div>
    {unverified && <div className="integration-warning" role="alert"><div className="note warn"><strong>We could not verify that ERP number.</strong>&nbsp;Check it before continuing. You may still record it because this tenant allows unknown numbers with a warning.</div><button type="button" className="btn primary" disabled={busy} onClick={() => void complete(true)}>Record anyway</button></div>}
    {task.deadLetterItemId && <div className="integration-cancel"><label className="field"><span className="lbl">Cancel reason</span><input className="input" value={cancelReason} onChange={(event) => setCancelReason(event.target.value)} placeholder="Reason (required)" required /></label><button type="button" className="btn" disabled={busy || !cancelReason.trim()} onClick={() => void cancel()}>Cancel with reason</button></div>}
  </section>;
}

function TaskHistory({ task }: { task: ManualTask }) {
  const draft = parseDraft(task);
  return <><Section title="From"><Kv rows={[["Draft", title(task)], ["Connection", task.connectionLabel], ["Desk", human(task.desk)], ["Raised", localDate(task.createdAt)]]} /></Section><Section title="Why"><div className="note">{draft.reason ?? "This entry must be made by hand."}</div></Section></>;
}
