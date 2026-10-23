import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn, type PageResult } from "../components";
import { useTenantTime } from "../components/tenantTime";
import { useSession } from "../session/session";
import { openDrawer } from "../shell/drawerStore";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { ErrorNote, Kv, Section } from "../units/common";
import { securityEventsApi, type SecurityEventRow } from "./securityEventsApi";
import { browserName, securityEventKinds, securityEventLabel, securityEventTone, shiftDay } from "./securityEventsModel";
import { userManagementApi, type TenantUserRow } from "./userManagementApi";

const PAGE_SIZE = 25;
type Period = "today" | "7_days" | "30_days" | "custom";
type EventGridRow = SecurityEventRow & { rowKey: string };

export function Component() {
  const session = useSession();
  const permitted = session.roles.includes("tenant_administrator");
  const tenantTime = useTenantTime();
  const today = tenantTime.localInput(new Date().toISOString()).slice(0, 10);
  const [kind, setKind] = useState("");
  const [period, setPeriod] = useState<Period>("7_days");
  const [customFrom, setCustomFrom] = useState(today);
  const [customThrough, setCustomThrough] = useState(today);
  const [userText, setUserText] = useState("");
  const [userId, setUserId] = useState<string | null>(null);
  const [users, setUsers] = useState<TenantUserRow[]>([]);
  const [rows, setRows] = useState<EventGridRow[]>([]);
  const [next, setNext] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);

  const bounds = useMemo(() => {
    const fromDay = period === "today" ? today : period === "7_days" ? shiftDay(today, -6) : period === "30_days" ? shiftDay(today, -29) : customFrom;
    const throughDay = period === "custom" ? customThrough : today;
    return tenantTime.dayBounds(fromDay, throughDay);
  }, [customFrom, customThrough, period, tenantTime, today]);

  const load = useCallback(async (target: number, cursor?: string, signal?: AbortSignal) => {
    setLoading(true); setError(null);
    try {
      const result = await securityEventsApi.page({
        ...(kind ? { kind } : {}), ...(userId ? { userId } : {}), ...bounds, ...(cursor ? { cursor } : {}), take: PAGE_SIZE,
      }, signal);
      setRows(result.items.map((row, index) => ({ ...row, rowKey: `${target}:${index}:${row.occurredAt}:${row.kind}` })));
      setNext(result.nextCursor); setPage(target);
      if (result.nextCursor) setCursors(current => { const copy = current.slice(0, target); copy[target] = result.nextCursor!; return copy; });
    } catch (reason) {
      if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason);
    } finally { if (!signal?.aborted) setLoading(false); }
  }, [bounds, kind, userId]);

  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    setPage(1); setCursors([undefined]);
    void load(1, undefined, controller.signal);
    return () => controller.abort();
  }, [load, permitted]);

  useEffect(() => {
    if (!permitted || userText.trim().length < 2 || userId) { setUsers([]); return; }
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      void userManagementApi.page({ status: "all", q: userText.trim(), take: 12 }, controller.signal)
        .then(result => setUsers(result.items.filter(row => row.userId)))
        .catch(reason => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setUsers([]); });
    }, 200);
    return () => { window.clearTimeout(timer); controller.abort(); };
  }, [permitted, userId, userText]);

  const selectUser = (value: string) => {
    setUserText(value);
    const match = users.find(row => row.displayName === value);
    setUserId(match?.userId ?? null);
  };

  const show = useCallback((row: EventGridRow) => {
    openDrawer({ eyebrow: "Security event", title: securityEventLabel(row.kind) },
      <Section title="Event"><Kv rows={[
        ["When", tenantTime.dateTime(row.occurredAt)], ["User", row.userName ?? "Unknown user"],
        ["By", row.actorName ?? "—"], ["Address", row.clientAddress ?? "—"],
        ["Device", row.userAgent ?? "—"], ["Detail", row.reason || "—"],
      ]} /></Section>);
  }, [tenantTime]);

  const columns = useMemo<GridColumn<EventGridRow>[]>(() => [
    { key: "when", label: "When", width: 14, render: row => tenantTime.dateTime(row.occurredAt), muted: true },
    { key: "event", label: "Event", width: 18, render: row => <StatusBadge tone={securityEventTone(row.kind)}>{securityEventLabel(row.kind)}</StatusBadge> },
    { key: "user", label: "User", width: 17, render: row => row.userName ?? "Unknown user" },
    { key: "actor", label: "By", width: 15, render: row => row.actorName ?? "—" },
    { key: "from", label: "From", width: 16, render: row => [row.clientAddress, browserName(row.userAgent)].filter(Boolean).join(" · ") },
    { key: "detail", label: "Detail", width: 20, render: row => row.reason || "—" },
  ], [tenantTime]);

  const estimatedTotal = (page - 1) * PAGE_SIZE + rows.length + (next ? 1 : 0);
  const grid: PageResult<EventGridRow> = { rows, page, pageSize: PAGE_SIZE, total: estimatedTotal,
    continuation: next, totalIsExact: false, hasMore: !!next };
  const move = (target: number) => { if (target < page || next) void load(target, cursors[target - 1]); };
  const periodLabel = period === "today" ? "today" : period === "7_days" ? "last 7 days" : period === "30_days" ? "last 30 days" : `${customFrom} to ${customThrough}`;

  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Security events" }]} />
    <PageHeader eyebrow={`${session.tenant?.name ?? "Tenant"} · no passwords, codes or links are stored`} title="Security events"
      sub="Every sign-in, lockout, password reset, second-step and passkey change, and every administrator action on a user." />
    <nav className="tabs subnav" aria-label="Sign-in settings"><Link to="/settings/users">Users</Link><Link to="/settings/sign-in-policy">Sign-in policy</Link><Link to="/settings/security-events" aria-current="page">Security events</Link></nav>
    {!permitted && <div className="note bad"><strong>Tenant administrator access is required.</strong></div>}
    {permitted && <div className="stack">
      <KpiStrip label="Security event figures" kpis={[
        { label: "Sign-ins today", value: "—", detail: "Aggregate not supplied by the event log" },
        { label: "Failed sign-ins today", value: "—", detail: "Aggregate not supplied by the event log" },
        { label: "Lockouts today", value: "—", detail: "Aggregate not supplied by the event log" },
        { label: "Password resets, 7 days", value: "—", detail: "Aggregate not supplied by the event log" },
        { label: "Sign-in method changes, 7 days", value: "—", detail: "Aggregate not supplied by the event log" },
      ]} />
      <ErrorNote error={error} onReload={() => void load(page, cursors[page - 1])} />
      <section className="card" aria-labelledby="security-events-list">
        <div className="card-header"><h2 id="security-events-list">Events <span className="meta">{periodLabel} · page {page.toLocaleString()}</span></h2></div>
        <div className="toolbar" style={{ padding: "10px var(--card-pad)", borderBottom: "1px solid var(--rule)" }}>
          <select className="select" aria-label="Event" value={kind} onChange={event => setKind(event.target.value)}>
            <option value="">Any event</option>{securityEventKinds.map(value => <option value={value} key={value}>{securityEventLabel(value)}</option>)}
          </select>
          <input className="input" type="search" list="security-event-users" placeholder="User name or e-mail" aria-label="User" value={userText}
            onChange={event => selectUser(event.target.value)} style={{ width: 240, flex: "none" }} />
          <datalist id="security-event-users">{users.map(row => <option value={row.displayName} key={row.userId!} />)}</datalist>
          {userId && <button type="button" className="btn sm" onClick={() => { setUserText(""); setUserId(null); }}>Clear user</button>}
          <select className="select" aria-label="Period" value={period} onChange={event => setPeriod(event.target.value as Period)}>
            <option value="7_days">Last 7 days</option><option value="today">Today</option><option value="30_days">Last 30 days</option><option value="custom">Custom…</option>
          </select>
          {period === "custom" && <><label className="field inline"><span className="lbl">From</span><input className="input" type="date" value={customFrom} max={customThrough} onChange={event => { if (event.target.value) setCustomFrom(event.target.value); }} /></label><label className="field inline"><span className="lbl">Through</span><input className="input" type="date" value={customThrough} min={customFrom} onChange={event => { if (event.target.value) setCustomThrough(event.target.value); }} /></label></>}
        </div>
        <DataGrid columns={columns} page={grid} rowId={row => row.rowKey} onPage={move} onRowClick={show}
          loading={loading} viewportHeight={560} footerNote="Newest first · 25 a page" emptyText="No security events match these filters." />
      </section>
    </div>}
  </main>;
}
