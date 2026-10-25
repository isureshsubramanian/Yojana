import { useCallback, useEffect, useMemo, useState } from "react";
import { useParams } from "react-router";
import { ActionDialog, DataGrid, StatusBadge, type GridColumn, type PageResult } from "../components";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { PageHeader } from "../shell/PageHeader";
import { ErrorNote, Kv, Section } from "../units/common";
import { developerApi, type DeliveryDetail, type DeliveryFilters, type DeliveryPage, type WebhookDeliveryRow, type WebhookSubscription } from "./developers/developerApi";
import { answer, deliveryStatus, prettyJson, statusFilter, when } from "./developers/developerModel";
import "./developers/developer.css";

const PAGE_SIZE = 50;
type Segment = "all" | "delivered" | "retrying" | "parked" | "discarded";

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  const id = useParams().id ?? "";
  const [subscription, setSubscription] = useState<WebhookSubscription | null>(null);
  const [result, setResult] = useState<DeliveryPage | null>(null);
  const [eventTypes, setEventTypes] = useState<string[]>([]);
  const [segment, setSegment] = useState<Segment>("all");
  const [eventType, setEventType] = useState("");
  const [from, setFrom] = useState(""); const [to, setTo] = useState("");
  const [page, setPage] = useState(1); const [cursors, setCursors] = useState<(string | undefined)[]>([undefined]);
  const [loading, setLoading] = useState(true); const [error, setError] = useState<unknown>(null);
  const [rangeOpen, setRangeOpen] = useState(false);

  const filter = useMemo<DeliveryFilters>(() => ({ ...(statusFilter(segment) ? { status: statusFilter(segment)! } : {}), ...(eventType ? { eventType } : {}), ...(from ? { from: startOfDay(from) } : {}), ...(to ? { to: nextDay(to) } : {}) }), [eventType, from, segment, to]);
  const load = useCallback(async (target = page, cursor = cursors[target - 1], requestFilter = filter) => {
    if (!id) return;
    setLoading(true); setError(null);
    try {
      const [sub, next, catalogue] = await Promise.all([developerApi.webhook(id), developerApi.deliveries(id, requestFilter, cursor), eventTypes.length ? Promise.resolve(null) : developerApi.catalogue()]);
      setSubscription(sub); setResult(next); setPage(target); if (catalogue) setEventTypes(catalogue.events.filter(event => event.type !== "endpoint.verify").map(event => event.type));
      if (next.nextCursor) setCursors(current => { const copy = current.slice(0, target); copy[target] = next.nextCursor!; return copy; });
    } catch (reason) { setError(reason); }
    finally { setLoading(false); }
  }, [cursors, eventTypes.length, filter, id, page]);
  useEffect(() => { if (permitted) { setCursors([undefined]); void load(1, undefined, filter); } }, [permitted, segment, eventType, from, to]); // eslint-disable-line react-hooks/exhaustive-deps

  const show = useCallback((row: WebhookDeliveryRow) => {
    const head = { eyebrow: `Delivery · ${row.eventType}`, title: row.recordReference ?? row.eventId, mono: true };
    openDrawer(head, <DeliveryDrawer subscriptionId={id} row={row} onChanged={() => { void load(); updateDrawer(head, <DeliveryDrawer subscriptionId={id} row={row} onChanged={() => void load()} />); }} />);
  }, [id, load]);
  const columns = useMemo<GridColumn<WebhookDeliveryRow>[]>(() => [
    { key: "sent", label: "Sent", width: 13, render: row => new Date(row.createdAt).toLocaleString([], { dateStyle: "short", timeStyle: "short" }) },
    { key: "event", label: "Event", width: 19, mono: true, render: row => row.eventType },
    { key: "record", label: "Record", width: 22, mono: true, render: row => row.recordReference ?? "Reference unavailable" },
    { key: "attempts", label: "Attempts", width: 8, numeric: true, render: row => row.attemptCount },
    { key: "answer", label: "Answer", width: 16, render: answer },
    { key: "time", label: "Time", width: 8, numeric: true, render: row => row.lastDurationMs === null ? "—" : row.lastDurationMs >= 1000 ? `${(row.lastDurationMs / 1000).toFixed(1)} s` : `${row.lastDurationMs} ms` },
    { key: "status", label: "Status", width: 14, render: row => { const status = deliveryStatus(row); return <StatusBadge tone={status.tone}>{status.label}</StatusBadge>; } },
  ], []);
  const rows = result?.items ?? [];
  const grid: PageResult<WebhookDeliveryRow> = { rows, page, pageSize: PAGE_SIZE, total: result?.total ?? 0, continuation: result?.nextCursor ?? null, totalIsExact: true, hasMore: !!result?.nextCursor };
  const segments: Array<[Segment, string, number]> = [["all", "All", result?.counts.all ?? 0], ["delivered", "Delivered", result?.counts.succeeded ?? 0], ["retrying", "Retrying", result?.counts.pending ?? 0], ["parked", "Parked", result?.counts.parked ?? 0], ["discarded", "Discarded", result?.counts.discarded ?? 0]];
  return <main className="content" id="main" tabIndex={-1}>
    <PageHeader eyebrow={`Webhooks · ${subscription?.url ?? "endpoint"}`} title="Delivery log" sub={`Every message sent to this endpoint, with each attempt and what came back. Kept according to the tenant's delivery-log retention setting.`}
      actions={permitted && <><button type="button" className="btn" onClick={() => setRangeOpen(true)}><Icon name="range" />Redeliver a period</button><button type="button" className="btn" disabled={subscription?.status !== "active"} onClick={() => void developerApi.testWebhook(id, subscription?.eventTypes[0]).then(() => void load()).catch(setError)}><Icon name="mail" />Send test event</button></>} />
    {!permitted ? <div className="note bad"><strong>Tenant administrator access is required.</strong></div> : <div className="stack">
      <ErrorNote error={error} onReload={() => void load()} />
      <div className="toolbar developer-delivery-filters"><div className="segmented" role="group" aria-label="Delivery status">{segments.map(([value, label, count]) => <button type="button" key={value} aria-pressed={segment === value} onClick={() => setSegment(value)}>{label}<span className="n">{count.toLocaleString()}</span></button>)}</div>
        <label className="field compact"><span className="sr-only">Event type</span><select className="select" aria-label="Filter by event type" value={eventType} onChange={event => setEventType(event.target.value)}><option value="">Event type: any</option>{eventTypes.map(type => <option value={type} key={type}>{type}</option>)}</select></label>
        <label className="field compact"><span className="sr-only">From date</span><input className="input" type="date" aria-label="Delivery date from" value={from} onChange={event => setFrom(event.target.value)} /></label><span aria-hidden="true">–</span><label className="field compact"><span className="sr-only">To date</span><input className="input" type="date" aria-label="Delivery date to" value={to} onChange={event => setTo(event.target.value)} /></label>
      </div>
      <section className="card" aria-labelledby="delivery-list"><div className="card-header"><h2 id="delivery-list">Deliveries</h2><span className="meta">newest first</span></div><DataGrid columns={columns} page={grid} rowId={row => row.id} onPage={target => { if (target < page || result?.nextCursor) void load(target, cursors[target - 1]); }} onRowClick={show} loading={loading} emptyText="No deliveries match these filters." viewportHeight={580} /></section>
      {rangeOpen && <RedeliverRange subscriptionId={id} onClose={() => setRangeOpen(false)} onDone={() => { setRangeOpen(false); void load(); }} />}
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
