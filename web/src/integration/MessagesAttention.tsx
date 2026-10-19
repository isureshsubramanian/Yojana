import { useCallback, useEffect, useMemo, useState } from "react";
import { DataGrid, FindBar, KpiStrip, Shortcut, StatusBadge, useModEnter, type GridColumn, type PageResult } from "../components";
import { openDrawer } from "../shell/drawerStore";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { ErrorNote, Kv, Section } from "../units/common";
import { integrationApi } from "./api";
import { IntegrationAccess, IntegrationTabs, useIntegrationTabCounts } from "./Common";
import { actionSeed, age, localDate, payload, reasonLabel, recordTitle, statusLabel } from "./model";
import type { DeadLetterDetail, DeadLetterPage, DeadLetterRow } from "./types";
import "./integration.css";

const emptySummary = { open: 0, unknownItem: 0, serialNotFound: 0, partyNotLinked: 0, couldNotSend: 0, resolvedLast30Days: 0, retriedLast30Days: 0, discardedLast30Days: 0 };
const empty: DeadLetterPage = { items: [], nextCursor: null, total: 0, summary: emptySummary };
const statusValues = [["needs_attention", "Open"], ["retry_pending", "Retried"], ["discarded", "Discarded"]] as const;

function pageOf(result: DeadLetterPage, page: number): PageResult<DeadLetterRow> {
  return { rows: result.items, total: result.total, page, pageSize: 25, continuation: result.nextCursor, totalIsExact: true, hasMore: result.nextCursor !== null };
}
function pretty(text: string) { try { return JSON.stringify(JSON.parse(text), null, 2); } catch { return text; } }
function tone(row: DeadLetterRow) { return row.direction === "outbound" ? "bad" as const : "warn" as const; }

export function Component() {
  const tabCounts = useIntegrationTabCounts();
  const [result, setResult] = useState(empty); const [page, setPage] = useState(1); const [cursors, setCursors] = useState<Array<string | undefined>>([undefined]);
  const [status, setStatus] = useState("needs_attention"); const [find, setFind] = useState(""); const [reason, setReason] = useState("");
  const [recordType, setRecordType] = useState(""); const [connectionId, setConnectionId] = useState("");
  const [detail, setDetail] = useState<DeadLetterDetail | null>(null); const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);
  const [discardReason, setDiscardReason] = useState("");
  const filters = useMemo(() => ({ status, query: find || undefined, reason: reason || undefined, recordType: recordType || undefined, connectionId: connectionId || undefined, take: 25 }), [status, find, reason, recordType, connectionId]);
  const load = useCallback(async (target = 1, cursor?: string) => {
    setBusy(true); setError(null);
    try {
      const next = await integrationApi.deadLetters({ ...filters, cursor }); setResult(next); setPage(target);
      if (next.nextCursor) setCursors((current) => { const copy = current.slice(0, target); copy[target] = next.nextCursor!; return copy; });
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  }, [filters]);
  useEffect(() => { setCursors([undefined]); void load(); }, [load]);

  const open = async (row: DeadLetterRow) => {
    setError(null);
    try {
      const selected = await integrationApi.deadLetter(row.deadLetterItemId); setDetail(selected); setDiscardReason("");
      openDrawer({ eyebrow: "Message history", title: recordTitle(selected) }, <MessageHistory detail={selected} />);
    } catch (reason) { setError(reason); }
  };
  const afterAction = async () => { setDetail(null); setDiscardReason(""); await load(1); };
  const map = async () => {
    if (!detail) return; const seed = actionSeed(detail);
    if (!seed.externalId || !seed.moduleId) { setError(new Error("No suggested item or party is available for this message yet.")); return; }
    setBusy(true); setError(null); try { await integrationApi.mapAndRetry(detail.deadLetterItemId, seed.entityType, seed.externalId, seed.moduleId); await afterAction(); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const provisional = async () => {
    if (!detail) return; const seed = actionSeed(detail);
    if (!seed.externalId) { setError(new Error("The message does not contain an external code to use.")); return; }
    setBusy(true); setError(null); try { await integrationApi.createProvisionalAndRetry(detail.deadLetterItemId, seed.entityType, seed.externalId); await afterAction(); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const discard = async () => {
    if (!detail || !discardReason.trim()) return;
    setBusy(true); setError(null); try { await integrationApi.discard(detail.deadLetterItemId, discardReason.trim()); await afterAction(); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  useModEnter(map, !detail || busy || !actionSeed(detail).moduleId);

  const connections = useMemo(() => Array.from(new Map(result.items.map((row) => [row.connectionId, row.connectionLabel])).entries()).sort((a, b) => a[1].localeCompare(b[1])), [result.items]);
  const recordTypes = useMemo(() => Array.from(new Set(result.items.map((row) => row.recordType))).sort(), [result.items]);
  const columns: GridColumn<DeadLetterRow>[] = [
    { key: "received", label: "Received", width: 11, mono: true, render: (row) => localDate(row.createdAt) },
    { key: "connection", label: "Connection", width: 17, render: (row) => row.connectionLabel },
    { key: "record", label: "Record", width: 22, render: (row) => <><span className="primary">{recordTitle(row)}</span><span className="sub">{row.direction === "inbound" ? "Received from the ERP" : "Sending to the ERP"}</span></> },
    { key: "reason", label: "Reason", width: 32, render: (row) => <><StatusBadge tone={tone(row)}>{reasonLabel(row.reasonCode)}</StatusBadge><span className="integration-failure">{row.failure}</span></> },
    { key: "attempts", label: "Attempts", width: 8, numeric: true, render: (row) => row.attemptCount },
    { key: "age", label: "Age", width: 10, numeric: true, render: (row) => age(row.createdAt) },
  ];
  const kpis = [
    { label: "Unknown item", value: result.summary.unknownItem.toLocaleString(), detail: "Item mapping is missing" },
    { label: "Serial not found", value: result.summary.serialNotFound.toLocaleString(), detail: "Check identity or source data" },
    { label: "Customer or supplier not linked", value: result.summary.partyNotLinked.toLocaleString(), detail: "Party mapping is missing" },
    { label: "Could not send", value: result.summary.couldNotSend.toLocaleString(), detail: "The rest of the module keeps working" },
    { label: "Resolved in the last 30 days", value: result.summary.resolvedLast30Days.toLocaleString(), detail: `${result.summary.retriedLast30Days.toLocaleString()} retried · ${result.summary.discardedLast30Days.toLocaleString()} discarded` },
  ];

  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Integration", to: "/integration" }, { label: "Messages needing attention" }]} />
    <PageHeader eyebrow={`All connections · ${result.summary.open.toLocaleString()} open`} title="Messages needing attention"
      sub="Messages we could not process or send are kept with what the ERP sent and what we tried to record. The module keeps working meanwhile; nothing here is lost or applied twice."
      actions={<button type="button" className="btn primary" disabled={!detail || busy || !actionSeed(detail).moduleId} onClick={() => void map()}>Map and retry <Shortcut keys="mod+enter" /></button>} />
    <IntegrationTabs attention={result.summary.open} manual={tabCounts.manual} />
    <IntegrationAccess><div className="stack integration-stack"><ErrorNote error={error} /><KpiStrip kpis={kpis} />
      <section className="card" aria-labelledby="attention-list"><div className="card-header"><h2 id="attention-list">{statusLabel(status)} messages</h2><div className="segmented plain" role="group" aria-label="State">{statusValues.map(([value, label]) => <button type="button" key={value} aria-pressed={status === value} onClick={() => setStatus(value)}>{label}{value === "needs_attention" && <span className="n">{result.summary.open}</span>}</button>)}</div></div>
        <FindBar query={find} onQuery={setFind} facets={[]} activeFacet={null} onFacet={() => undefined} compact ariaLabel="Find a document, serial, item code or ERP key" placeholder="Document, serial, item code or ERP key" />
        <div className="toolbar integration-filters">
          <select className="select sm" aria-label="Connection" value={connectionId} onChange={(event) => setConnectionId(event.target.value)}><option value="">All connections</option>{connections.map(([id, label]) => <option key={id} value={id}>{label}</option>)}</select>
          <select className="select sm" aria-label="Reason" value={reason} onChange={(event) => setReason(event.target.value)}><option value="">All reasons</option><option value="item_not_mapped">Unknown item</option><option value="serial_not_found">Serial not found</option><option value="party_not_mapped">Customer or supplier not linked</option><option value="max_outbox_age">Could not send</option></select>
          <select className="select sm" aria-label="Record type" value={recordType} onChange={(event) => setRecordType(event.target.value)}><option value="">All record types</option>{recordTypes.map((value) => <option key={value} value={value}>{recordTitle({ recordType: value })}</option>)}</select>
          <span className="grow" /><span className="hint">Sorted by age, oldest first</span>
        </div>
        <DataGrid columns={columns} page={pageOf(result, page)} rowId={(row) => row.deadLetterItemId} onPage={(target) => void load(target, target > page ? result.nextCursor ?? undefined : cursors[target - 1])} loading={busy} onRowClick={(row) => void open(row)} activeRowId={detail?.deadLetterItemId} viewportHeight={396} emptyText="No messages need attention." />
      </section>
      {detail && <MessageDetail detail={detail} discardReason={discardReason} setDiscardReason={setDiscardReason} map={map} provisional={provisional} discard={discard} busy={busy} />}
    </div></IntegrationAccess>
  </main>;
}

function MessageDetail({ detail, discardReason, setDiscardReason, map, provisional, discard, busy }: { detail: DeadLetterDetail; discardReason: string; setDiscardReason: (value: string) => void; map: () => Promise<void>; provisional: () => Promise<void>; discard: () => Promise<void>; busy: boolean }) {
  const seed = actionSeed(detail); const received = payload(detail);
  return <section className="card" aria-labelledby="message-detail"><div className="card-header"><h2 id="message-detail">{recordTitle(detail)} · {detail.connectionLabel}</h2><StatusBadge tone={detail.direction === "outbound" ? "bad" : "warn"}>{reasonLabel(detail.reasonCode)}</StatusBadge></div>
    <div className="integration-panes"><div><h3>As received from the ERP</h3><pre className="integration-code">{pretty(detail.rawPayload)}</pre><p className="hint">Stored unchanged</p></div>
      <div><h3>What we tried to record</h3><pre className="integration-code">{pretty(detail.canonicalAttempt)}</pre><div className="note bad"><strong>Why it stopped.</strong>&nbsp;{detail.failure}</div></div>
      <div><h3>What to do</h3><div className="integration-actions">
        <div className="act on"><strong>Map and retry</strong><span>Treat the ERP code as the suggested record, then process again.</span>{seed.moduleName && <span className="integration-choice">{seed.moduleName}</span>}<button type="button" className="btn primary sm" disabled={busy || !seed.moduleId || !seed.externalId} onClick={() => void map()}>Map and retry</button></div>
        <div className="act"><strong>Create provisional and retry</strong><span>Record a provisional item or party now and leave it for confirmation.</span><button type="button" className="btn sm" disabled={busy || !seed.externalId} onClick={() => void provisional()}>Create provisional</button></div>
        <div className="act"><label className="field"><span className="lbl">Discard reason</span><input className="input" value={discardReason} onChange={(event) => setDiscardReason(event.target.value)} placeholder="Reason (required)" required /></label><span>Nothing is recorded. The reason stays in the message history.</span><button type="button" className="btn sm" disabled={busy || !discardReason.trim()} onClick={() => void discard()}>Discard with reason</button></div>
      </div></div></div>
  </section>;
}

function MessageHistory({ detail }: { detail: DeadLetterDetail }) {
  return <><Section title="Received"><Kv rows={[["Connection", detail.connectionLabel], ["Record", recordTitle(detail)], ["How", detail.direction === "inbound" ? "Received from the ERP" : "Sending to the ERP"], ["State", statusLabel(detail.status)]]} /></Section>
    <Section title="Attempts">{detail.events.length === 0 ? <p className="muted">No attempts recorded.</p> : <ol className="integration-history">{detail.events.map((event) => <li key={event.deadLetterEventId}><strong>{statusLabel(event.action)}</strong><span>{localDate(event.occurredAt)}{event.reason ? ` · ${event.reason}` : ""}</span></li>)}</ol>}</Section>
    <Section title="Safety"><div className="note">A second copy of this same change is recognised and ignored.</div></Section></>;
}
