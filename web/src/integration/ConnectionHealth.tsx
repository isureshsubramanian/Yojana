import { useCallback, useEffect, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { KpiStrip, type GridColumn } from "../components";
import { ErrorNote, Kv } from "../units/common";
import { useSession } from "../session/session";
import { mayOperateIntegration, human, recordLabel } from "./model";
import { adminApi } from "./adminApi";
import { date, Frame, Panel, PagedGrid, RoleAccess, State, useCursorPage } from "./AdminCommon";
import type { Connection, ConnectionEvent, ConnectionPage, HealthDetail, TenantStatus, TenantStatusPage } from "./adminTypes";

export function Component() { return <RoleAccess status><HealthScreen /></RoleAccess>; }
function HealthScreen() {
  const [params, setParams] = useSearchParams(); const operator = mayOperateIntegration(useSession().roles); const readOnly = !operator || params.get("view") === "status";
  return <Frame title="Connection health" sub="Whether each connection is receiving, sending and checking. Notifications are a hint; scheduled checks and the daily comparison keep records complete."
    actions={operator && <button className="btn" onClick={() => setParams(readOnly ? {} : { view: "status" })}>{readOnly ? "Operator health" : "Customer status page"}</button>}>
    {readOnly ? <TenantHealth /> : <OperatorHealth />}
  </Frame>;
}
function TenantHealth() {
  const fetch = useCallback((cursor?: string, signal?: AbortSignal) => adminApi.status(cursor, signal), []);
  const source = useCursorPage<TenantStatus, TenantStatusPage>(fetch); const kpis = source.result?.kpis;
  const columns: GridColumn<TenantStatus>[] = [
    { key: "name", label: "Connection", width: 30, render: x => <span className="primary">{x.connectionName}</span> },
    { key: "status", label: "Status", width: 18, render: x => <State value={x.status} /> },
    { key: "checked", label: "Checked up to", width: 20, render: x => date(x.checkedUpTo) },
    { key: "waiting", label: "Waiting to send", width: 16, numeric: true, render: x => x.waitingToSend.toLocaleString() },
    { key: "attention", label: "Need attention", width: 16, numeric: true, render: x => (x.messagesNeedingAttention + x.openManualTasks).toLocaleString() },
  ];
  return <><div className="note">Read-only tenant status. Refreshed every 30 seconds.</div><Refresh refresh={source.refresh} />{kpis && <KpiStrip kpis={[{ label: "Connections", value: String(kpis.connections), detail: `${kpis.live} live · ${kpis.paused} paused` }, { label: "Received today", value: kpis.receivedToday.toLocaleString() }, { label: "Waiting to send", value: String(kpis.waitingToSend) }, { label: "Need attention", value: String(kpis.openDeadLetters + kpis.openManualTasks) }]} />}<section className="card" aria-label="Tenant connection status"><PagedGrid source={source} columns={columns} rowId={x => x.connectionId} empty="No connections have been registered." /></section></>;
}
function Refresh({ refresh }: { refresh: () => void }) { useEffect(() => { const timer = window.setInterval(refresh, 30_000); return () => clearInterval(timer); }, [refresh]); return <button className="btn sm integration-refresh" onClick={refresh}>Refresh now</button>; }
function OperatorHealth() {
  const fetch = useCallback((cursor?: string, signal?: AbortSignal) => adminApi.connections(cursor, false, signal), []);
  const source = useCursorPage<Connection, ConnectionPage>(fetch); const [selected, setSelected] = useState<Connection | null>(null); const k = source.result?.kpis;
  const columns: GridColumn<Connection>[] = [
    { key: "connection", label: "Connection", width: 24, render: x => <><span className="primary">{x.connectionName}</span><span className="sub">{x.connectorName} · <span className="mono">{x.connectorCode}</span></span></> },
    { key: "status", label: "Status", width: 13, render: x => <State value={x.status} /> },
    { key: "notifications", label: "ERP notifications", width: 14, render: x => human(x.health.notificationState) },
    { key: "checked", label: "Checked up to", width: 15, render: x => date(x.health.checkedUpTo) },
    { key: "waiting", label: "Waiting / attention", width: 12, numeric: true, render: x => <>{x.health.waitingToSend.toLocaleString()} / {x.health.openDeadLetters.toLocaleString()}</> },
    { key: "daily", label: "Last daily check", width: 12, render: x => <>{human(x.health.lastDailyCheckResult ?? "not_run")}<span className="sub">{date(x.health.lastDailyCheckAt)}</span></> },
    { key: "calls", label: "Calls / cap", width: 10, numeric: true, render: x => `${x.health.callsUsed.toLocaleString()} / ${x.health.callsCap?.toLocaleString() ?? "No cap"}` },
  ];
  return <><Refresh refresh={source.refresh} />{k && <KpiStrip kpis={[{ label: "Connections", value: String(k.connections), detail: `${k.live} live · ${k.paused} paused · ${k.relayConnected} with relay` }, { label: "Received today", value: k.receivedToday.toLocaleString(), detail: `${k.receivedLast30Days.toLocaleString()} in the last 30 days` }, { label: "Waiting to send", value: k.waitingToSend.toLocaleString() }, { label: "Messages needing attention", value: k.openDeadLetters.toLocaleString() }]} />}<section className="card" aria-label="By connection"><div className="card-header"><h2>By connection</h2><span className="meta">Select a row for record types and recent events</span></div><PagedGrid source={source} columns={columns} rowId={x => x.connectionId} select={setSelected} selected={selected?.connectionId} empty="No active connections. Register one under Connections." /></section>{selected ? <HealthDetails key={selected.connectionId} id={selected.connectionId} /> : <div className="note">Select a connection to see its record types and recent events.</div>}</>;
}
function HealthDetails({ id }: { id: string }) {
  const [detail, setDetail] = useState<HealthDetail | null>(null); const [error, setError] = useState<unknown>(null);
  useEffect(() => { const controller = new AbortController(); const load = () => { adminApi.health(id, controller.signal).then(value => { setDetail(value); setError(null); }).catch(reason => { if (!controller.signal.aborted) setError(reason); }); }; load(); const timer = window.setInterval(load, 30_000); return () => { controller.abort(); clearInterval(timer); }; }, [id]);
  const fetchEvents = useCallback((cursor?: string, signal?: AbortSignal) => adminApi.events(id, cursor, signal), [id]); const events = useCursorPage<ConnectionEvent>(fetchEvents);
  useEffect(() => { const timer = window.setInterval(events.refresh, 30_000); return () => clearInterval(timer); }, [events.refresh]);
  return <><ErrorNote error={error} />{!detail && !error && <p role="status">Loading connection health…</p>}{detail && <Panel title={`${detail.connectionName} · by record type`}><Kv rows={[["Last received", date(detail.health.lastInboundAt)], ["Last updated", date(detail.health.updatedAt)], ["Site relay", detail.health.relayConnected ? "Connected" : "Not connected"]]} />{detail.health.lastErrorMessage && <div className="note warn">{detail.health.lastErrorMessage}</div>}{detail.handOver && <div className="note" role="status"><strong>Waiting to resume after the final migration load.</strong>&nbsp;{detail.handOver.text}</div>}<div className="integration-table"><table className="table"><thead><tr><th>Record type</th><th>Notifications</th><th>Checked up to</th><th>Behind</th><th className="num">Received / attention</th></tr></thead><tbody>{detail.recordTypes.map(x => <tr key={x.recordType}><td>{recordLabel(x.recordType)}</td><td>{human(x.notificationState)}</td><td>{date(x.checkedUpTo)}<span className="sub">Every {x.intervalSeconds}s · {x.overlapSeconds}s overlap</span></td><td>{x.behindSeconds === null ? "—" : `${Math.round(x.behindSeconds / 60)} min`}</td><td className="num">{x.receivedToday.toLocaleString()} / {x.needAttention}</td></tr>)}</tbody></table>{!detail.recordTypes.length && <p className="muted">No record-type checks recorded yet.</p>}</div><Link to="/integration/messages-attention">Messages needing attention</Link></Panel>}<section className="card" aria-label="Recent events"><div className="card-header"><h2>Recent events</h2></div><PagedGrid source={events} columns={[{ key: "at", label: "When", width: 22, render: x => date(x.occurredAt) }, { key: "event", label: "Activity", width: 53, render: x => x.summary }, { key: "actor", label: "Who", width: 25, render: x => x.actorName }]} rowId={x => String(x.sequence)} empty="No connection events recorded." /></section></>;
}
