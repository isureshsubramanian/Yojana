import { useCallback, useEffect, useMemo, useState } from "react";
import { useParams } from "react-router";
import { ActionDialog, DataGrid, StatusBadge, useGridQuery, type GridColumn, type PageResult } from "../components";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { PageHeader } from "../shell/PageHeader";
import { ErrorNote, Kv, Section } from "../units/common";
import { deliveryPage, developerApi, type DeliveryDetail, type DeliveryFilters, type WebhookDeliveryRow, type WebhookSubscription } from "./developers/developerApi";
import { answer, deliveryStatus, prettyJson, statusFilter, when } from "./developers/developerModel";
import "./developers/developer.css";

const PAGE_SIZE = 50;
type Segment = "all" | "delivered" | "retrying" | "parked" | "discarded";

export function Component() {
  const session = useSession();
  const permitted = session.roles.includes("tenant_administrator");
  const id = useParams().id ?? "";
  const [subscription, setSubscription] = useState<WebhookSubscription | null>(null);
  const [eventTypes, setEventTypes] = useState<string[]>([]);
  const [segment, setSegment] = useState<Segment>("all");
  const [eventType, setEventType] = useState("");
  const [from, setFrom] = useState(""); const [to, setTo] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [rangeOpen, setRangeOpen] = useState(false);

  const filter = useMemo<DeliveryFilters>(() => ({ ...(statusFilter(segment) ? { status: statusFilter(segment)! } : {}), ...(eventType ? { eventType } : {}), ...(from ? { from: startOfDay(from) } : {}), ...(to ? { to: nextDay(to) } : {}) }), [eventType, from, segment, to]);
  const filterParams = useMemo(() => { const value = new URLSearchParams(); Object.entries(filter).forEach(([key, item]) => { if (item) value.set(key, item); }); return value; }, [filter]);
  const query = useGridQuery(`/api/developer/webhooks/${encodeURIComponent(id)}/deliveries`, deliveryPage, {
    gridId: "developer-deliveries", userId: session.user.id, defaultSort: { key: "sent", dir: "desc" },
    defaultSize: PAGE_SIZE, params: filterParams, paging: "cursor", sortKeys: ["sent", "status"],
    enabled: permitted && !!id, pageFacts: value => ({ continuation: value.nextCursor }),
  });
  const result = query.data;
  useEffect(() => { if (!permitted || !id) return; const controller = new AbortController(); setError(null);
    void Promise.all([developerApi.webhook(id, controller.signal), developerApi.catalogue(controller.signal)])
      .then(([sub, catalogue]) => { setSubscription(sub); setEventTypes(catalogue.events.filter(event => event.type !== "endpoint.verify").map(event => event.type)); })
      .catch(reason => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, [id, permitted]);

  const show = useCallback((row: WebhookDeliveryRow) => {
    const head = { eyebrow: `Delivery · ${row.eventType}`, title: row.recordReference ?? row.eventId, mono: true };
    openDrawer(head, <DeliveryDrawer subscriptionId={id} row={row} onChanged={() => { query.reload(); updateDrawer(head, <DeliveryDrawer subscriptionId={id} row={row} onChanged={query.reload} />); }} />);
  }, [id, query.reload]);
  const columns = useMemo<GridColumn<WebhookDeliveryRow>[]>(() => [
    { key: "sent", label: "Sent", width: 13, sort: { key: "sent", defaultDir: "desc" }, render: row => new Date(row.createdAt).toLocaleString([], { dateStyle: "short", timeStyle: "short" }) },
    { key: "event", label: "Event", width: 19, mono: true, render: row => row.eventType },
    { key: "record", label: "Record", width: 22, mono: true, render: row => row.recordReference ?? "Reference unavailable" },
    { key: "attempts", label: "Attempts", width: 8, numeric: true, render: row => row.attemptCount },
    { key: "answer", label: "Answer", width: 16, render: answer },
    { key: "time", label: "Time", width: 8, numeric: true, render: row => row.lastDurationMs === null ? "—" : row.lastDurationMs >= 1000 ? `${(row.lastDurationMs / 1000).toFixed(1)} s` : `${row.lastDurationMs} ms` },
    { key: "status", label: "Status", width: 14, sort: { key: "status", defaultDir: "asc" }, render: row => { const status = deliveryStatus(row); return <StatusBadge tone={status.tone}>{status.label}</StatusBadge>; } },
  ], []);
  const rows = result?.items ?? [];
  const grid: PageResult<WebhookDeliveryRow> = { rows, page: query.page, pageSize: query.pageSize, total: result?.total ?? 0, continuation: result?.nextCursor ?? null, totalIsExact: true, hasMore: !!result?.nextCursor };
  const segments: Array<[Segment, string, number]> = [["all", "All", result?.counts.all ?? 0], ["delivered", "Delivered", result?.counts.succeeded ?? 0], ["retrying", "Retrying", result?.counts.pending ?? 0], ["parked", "Parked", result?.counts.parked ?? 0], ["discarded", "Discarded", result?.counts.discarded ?? 0]];
  return <main className="content" id="main" tabIndex={-1}>
    <PageHeader eyebrow={`Webhooks · ${subscription?.url ?? "endpoint"}`} title="Delivery log" sub={`Every message sent to this endpoint, with each attempt and what came back. Kept according to the tenant's delivery-log retention setting.`}
      actions={permitted && <><button type="button" className="btn" onClick={() => setRangeOpen(true)}><Icon name="range" />Redeliver a period</button><button type="button" className="btn" disabled={subscription?.status !== "active"} onClick={() => void developerApi.testWebhook(id, subscription?.eventTypes[0]).then(query.reload).catch(setError)}><Icon name="mail" />Send test event</button></>} />
    {!permitted ? <div className="note bad"><strong>Tenant administrator access is required.</strong></div> : <div className="stack">
      <ErrorNote error={error ?? query.error} onReload={query.reload} />
      <div className="toolbar developer-delivery-filters"><div className="segmented" role="group" aria-label="Delivery status">{segments.map(([value, label, count]) => <button type="button" key={value} aria-pressed={segment === value} onClick={() => setSegment(value)}>{label}<span className="n">{count.toLocaleString()}</span></button>)}</div>
        <label className="field compact"><span className="sr-only">Event type</span><select className="select" aria-label="Filter by event type" value={eventType} onChange={event => setEventType(event.target.value)}><option value="">Event type: any</option>{eventTypes.map(type => <option value={type} key={type}>{type}</option>)}</select></label>
        <label className="field compact"><span className="sr-only">From date</span><input className="input" type="date" aria-label="Delivery date from" value={from} onChange={event => setFrom(event.target.value)} /></label><span aria-hidden="true">–</span><label className="field compact"><span className="sr-only">To date</span><input className="input" type="date" aria-label="Delivery date to" value={to} onChange={event => setTo(event.target.value)} /></label>
      </div>
      <section className="card" aria-labelledby="delivery-list"><div className="card-header"><h2 id="delivery-list">Deliveries</h2><span className="meta">newest first</span></div><DataGrid columns={columns} page={grid} rowId={row => row.id} onPage={query.onPage} onRowClick={show} loading={query.loading} emptyText="No deliveries match these filters." viewportHeight={580}
        gridId="developer-deliveries" userId={session.user.id} sort={query.sort} defaultSort={{ key: "sent", dir: "desc" }} onSort={query.onSort}
        pageSize={query.pageSize} defaultPageSize={PAGE_SIZE} onPageSize={query.onPageSize} /></section>
      {rangeOpen && <RedeliverRange subscriptionId={id} onClose={() => setRangeOpen(false)} onDone={() => { setRangeOpen(false); query.reload(); }} />}
    </div>}
  </main>;
}

function DeliveryDrawer({ subscriptionId, row, onChanged }: { subscriptionId: string; row: WebhookDeliveryRow; onChanged: () => void }) {
  const [detail, setDetail] = useState<DeliveryDetail | null>(null); const [error, setError] = useState<unknown>(null); const [busy, setBusy] = useState(false); const [discardOpen, setDiscardOpen] = useState(false);
  useEffect(() => { const controller = new AbortController(); developerApi.delivery(subscriptionId, row.id, controller.signal).then(setDetail).catch(reason => { if (!controller.signal.aborted) setError(reason); }); return () => controller.abort(); }, [row.id, subscriptionId]);
  if (error) return <ErrorNote error={error} />; if (!detail) return <p className="muted">Loading…</p>;
  const current = detail.delivery;
  return <><Section title="Delivery"><Kv rows={[["Event id", <span className="mono developer-break" key="event">{current.eventId}</span>], ["Record", current.recordReference ?? "Reference unavailable"], ["Occurred", when(current.createdAt)], ["Next attempt", current.status === "pending" ? when(current.nextAttemptAt) : "—"]]} /></Section>
    <Section title="Attempts">{detail.attempts.length === 0 ? <p className="muted">No attempts yet.</p> : <ul className="checks">{[...detail.attempts].reverse().map((attempt, index) => <li key={attempt.id}><span>{detail.attempts.length - index} · {new Date(attempt.attemptedAt).toLocaleTimeString()}<span className="s">{attempt.statusCode ? `${attempt.statusCode} · ` : ""}{attempt.responseExcerpt ?? attempt.errorCode ?? attempt.outcome} · {attempt.durationMs} ms</span></span><StatusBadge tone={attempt.outcome === "succeeded" ? "ok" : "warn"}>{attempt.outcome === "succeeded" ? "Delivered" : "Failed"}</StatusBadge></li>)}</ul>}</Section>
    <Section title="Message"><pre className="developer-code">{prettyJson(detail.eventBody)}</pre></Section>
    <div className="act developer-actions"><button type="button" className="btn primary" disabled={busy} onClick={() => { setBusy(true); setError(null); void developerApi.redeliver(subscriptionId, row.id).then(onChanged).catch(setError).finally(() => setBusy(false)); }}><Icon name="cycle" />Redeliver now</button>{current.status === "parked" && <button type="button" className="btn danger" disabled={busy} onClick={() => setDiscardOpen(true)}><Icon name="x" />Discard</button>}</div>
    {discardOpen && <ActionDialog title="Discard this parked delivery?" onCancel={() => setDiscardOpen(false)} actions={<button type="button" className="btn danger" disabled={busy} onClick={() => { setBusy(true); void developerApi.discard(subscriptionId, row.id).then(onChanged).catch(setError).finally(() => { setBusy(false); setDiscardOpen(false); }); }}><Icon name="x" />Discard</button>}><p>The message will not be sent again. Its event remains available until the delivery log expires.</p></ActionDialog>}
  </>;
}

function RedeliverRange({ subscriptionId, onClose, onDone }: { subscriptionId: string; onClose: () => void; onDone: () => void }) {
  const [from, setFrom] = useState(""); const [to, setTo] = useState(""); const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);
  return <ActionDialog title="Redeliver a period" onCancel={onClose} actions={<button type="button" className="btn primary" disabled={busy || !from || !to || from > to} onClick={() => { setBusy(true); setError(null); void developerApi.redeliverRange(subscriptionId, startOfDay(from), nextDay(to)).then(onDone).catch(setError).finally(() => setBusy(false)); }}><Icon name="cycle" />Redeliver period</button>}><div className="stack"><p>Send every original event in this inclusive date range again, keeping the same event ids for de-duplication.</p><ErrorNote error={error} /><div className="form-grid"><label className="field"><span className="lbl">From</span><input className="input" type="date" value={from} onChange={event => setFrom(event.target.value)} /></label><label className="field"><span className="lbl">To</span><input className="input" type="date" value={to} onChange={event => setTo(event.target.value)} /></label></div></div></ActionDialog>;
}

const startOfDay = (date: string) => `${date}T00:00:00.000Z`;
const nextDay = (date: string) => { const value = new Date(`${date}T00:00:00.000Z`); value.setUTCDate(value.getUTCDate() + 1); return value.toISOString(); };
