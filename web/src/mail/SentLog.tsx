import { useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { DataGrid, type GridColumn } from "../components/DataGrid";
import { KpiStrip } from "../components/KpiStrip";
import { StatusBadge } from "../components/StatusBadge";
import { useTenantTime } from "../components/tenantTime";
import type { PageResult } from "../components/types";
import { useSession } from "../session/session";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { ErrorNote, Kv, Section } from "../units/common";
import { mailApi, type SentMailDetail, type SentMailFilter, type SentMailPage, type SentMailRow } from "./api";
import { SENDER_ROLES, deliveryState, holds, languageName } from "./format";
import { MailTabs } from "./MailTabs";
import "./mail.css";

const PAGE_SIZE = 25;

export function Component() {
  const session = useSession();
  const tenantTime = useTenantTime();
  const [params, setParams] = useSearchParams();
  const permitted = holds(session.roles, SENDER_ROLES);
  const unitId = params.get("unitId") ?? undefined;
  const queued = Number(params.get("queued") ?? 0);
  const [status, setStatus] = useState<"" | "retrying" | "failed">("");
  const [kind, setKind] = useState("");
  const [sentBy, setSentBy] = useState("");
  const [typed, setTyped] = useState("");
  const [text, setText] = useState("");
  const [cursors, setCursors] = useState<(string | null)[]>([null]);
  const [page, setPage] = useState<SentMailPage | null>(null);
  const [active, setActive] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { const timer = window.setTimeout(() => setText(typed.trim()), 350); return () => window.clearTimeout(timer); }, [typed]);
  const filter = useMemo<SentMailFilter>(() => ({
    ...(status ? { status } : {}), ...(kind ? { kind } : {}), ...(sentBy ? { sentBy } : {}), ...(unitId ? { unitId } : {}), ...(text ? { q: text } : {}),
  }), [status, kind, sentBy, unitId, text]);
  useEffect(() => setCursors([null]), [filter]);
  const cursor = cursors[cursors.length - 1] ?? null;
  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    mailApi.sent(filter, cursor, controller.signal).then(setPage).catch((reason) => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, [permitted, filter, cursor]);

  const columns = useMemo<GridColumn<SentMailRow>[]>(() => [
    { key: "sent", label: "Sent", width: 11, render: (row) => tenantTime.dateTime(row.queuedAt) },
    { key: "to", label: "To", width: 20, render: (row) => <>{row.toName ?? row.to}<span className="sub">{row.ccCount ? `+${row.ccCount} in Cc · ` : ""}{row.customerName ?? row.to}</span></> },
    { key: "subject", label: "Subject", width: 31, render: (row) => <>{row.subject}<span className="sub">{row.templateName} v{row.templateVersion} · {row.language.toUpperCase()}</span></> },
    { key: "about", label: "About", width: 14, mono: true, render: (row) => row.aboutLabel },
    { key: "by", label: "Sent by", width: 12, render: (row) => row.automatic ? <span className="muted">Sent automatically</span> : row.sentBy },
    { key: "status", label: "Status", width: 12, render: (row) => { const state = deliveryState(row); return <span title={row.lastError ?? undefined}><StatusBadge tone={state.tone}>{state.text}</StatusBadge></span>; } },
  ], [tenantTime]);

  const open = (row: SentMailRow) => {
    setActive(row.messageId);
    const head = { eyebrow: `${row.kindName} · ${deliveryState(row).text}`, title: row.subject };
    openDrawer(head, <p className="muted">Loading the message as sent…</p>);
    mailApi.sentMessage(row.messageId).then((detail) => updateDrawer(head, <SentMessage detail={detail} dateTime={tenantTime.dateTime} />))
      .catch((reason) => updateDrawer(head, <ErrorNote error={reason} />));
  };

  const kpis = page?.kpis;
  const pageNumber = cursors.length;
  const grid: PageResult<SentMailRow> = { rows: page?.rows ?? [], total: page?.total ?? 0, page: pageNumber, pageSize: PAGE_SIZE, continuation: page?.nextCursor ?? null };
  const onPage = (next: number) => {
    if (next > pageNumber && page?.nextCursor) setCursors([...cursors, page.nextCursor]);
    else if (next < pageNumber) setCursors(cursors.slice(0, Math.max(1, next)));
  };
  const clearUnit = () => { const next = new URLSearchParams(params); next.delete("unitId"); next.delete("queued"); setParams(next); };

  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Customer e-mail" }, { label: "Sent log" }]} />
    <PageHeader eyebrow="Customer e-mail · last 30 days" title="Sent log"
      sub="Every e-mail sent to a customer: to whom, from which template version, about which record, and how delivery went. The copy shown is the one that was sent, not today's template."
      actions={permitted ? <a className="btn" href={mailApi.exportUrl(filter)}>Export</a> : undefined} />
    {!permitted && <div className="note bad"><strong>The sent log is for the service desk, service supervisors and managers, customer service and tenant administrators.</strong></div>}
    {permitted && <div className="stack">
      <MailTabs sent={kpis?.total} />
      {queued > 0 && <div className="note ok" role="status"><strong>{queued === 1 ? "Message queued." : `${queued} messages queued.`}</strong>&nbsp;Delivery usually takes under a minute; the status below updates when you reload.</div>}
      {unitId && <div className="note">Showing the messages about one unit. <button type="button" className="btn sm" onClick={clearUnit}>Show all</button> <Link className="btn sm" to={`/units/${unitId}?tab=whats-covered`}>Back to the unit</Link></div>}
      <ErrorNote error={error} />
      {kpis && <KpiStrip kpis={[
        { label: "Sent", value: kpis.sent.toLocaleString(), detail: kpis.deliveredShare === null ? null : `${kpis.deliveredShare}% of ${kpis.total.toLocaleString()}`, trend: kpis.deliveredShare !== null && kpis.deliveredShare >= 95 ? "up" : null },
        { label: "Retrying now", value: kpis.retrying.toLocaleString(), detail: kpis.retrying ? "the mail server was busy; it tries again by itself" : "nothing waiting" },
        { label: "Failed", value: kpis.failed.toLocaleString(), detail: kpis.failed ? "open one to see the mail server's answer" : "none", trend: kpis.failed ? "down" : null },
        { label: "Most sent", value: kpis.mostSentKind ?? "—", detail: kpis.mostSentKind ? `${kpis.mostSentCount.toLocaleString()} messages` : null },
      ]} />}
      <section className="card" aria-labelledby="sent-log-heading">
        <div className="card-header"><h2 id="sent-log-heading">Messages <span className="meta">{page ? `${page.total.toLocaleString()}` : ""}</span></h2>
          <div className="segmented" role="group" aria-label="Delivery">
            <button type="button" aria-pressed={status === ""} onClick={() => setStatus("")}>All</button>
            <button type="button" aria-pressed={status === "retrying"} onClick={() => setStatus("retrying")}>Retrying <span className="n">{kpis?.retrying ?? ""}</span></button>
            <button type="button" aria-pressed={status === "failed"} onClick={() => setStatus("failed")}>Failed <span className="n">{kpis?.failed ?? ""}</span></button>
          </div>
        </div>
        <div className="card-body mail-row">
          <input className="input" style={{ maxWidth: 300 }} type="search" placeholder="Recipient, subject or serial" aria-label="Find messages" value={typed} onChange={(event) => setTyped(event.target.value)} />
          <select className="select" style={{ width: "auto" }} aria-label="Kind of message" value={kind} onChange={(event) => setKind(event.target.value)}>
            <option value="">Any kind</option>{(page?.kinds ?? []).map((x) => <option key={x.kind} value={x.kind}>{x.name}</option>)}
          </select>
          <select className="select" style={{ width: "auto" }} aria-label="Sent by" value={sentBy} onChange={(event) => setSentBy(event.target.value)}>
            <option value="">Anyone</option><option value={session.user.id}>Sent by me</option><option value="automatic">Sent automatically</option>
          </select>
        </div>
        <DataGrid columns={columns} page={grid} rowId={(row) => row.messageId} onPage={onPage} onRowClick={open} activeRowId={active}
          loading={!page && !error} emptyText={text || status || kind || sentBy ? "No message matches." : "No customer e-mail in the last 30 days."} />
      </section>
    </div>}
  </main>;
}

function SentMessage({ detail, dateTime }: { detail: SentMailDetail; dateTime: (value: string | null | undefined) => string }) {
  const row = detail.row;
  return <>
    <Section title="Message"><Kv rows={[
      ["To", row.toName ? `${row.toName} <${row.to}>` : row.to],
      ["Cc", detail.cc.map((x) => x.name ? `${x.name} <${x.address}>` : x.address).join(", ") || "—"],
      ["Customer", row.customerName ?? "—"],
      ["About", row.unitId ? <Link to={`/units/${row.unitId}?tab=whats-covered`}>{row.aboutLabel}</Link> : row.aboutLabel],
      ["Template", `${row.templateName} v${row.templateVersion} · ${languageName(row.language)}`],
      ["Attached", detail.attachmentName ?? "—"],
      ["Personal note", detail.hasNote ? "yes" : "no"],
      ["Sent by", row.sentBy],
    ]} /></Section>
    <Section title="Delivery"><ol className="mail-checks">{detail.timeline.map((entry, index) => <li key={index}><strong>{entry.what}</strong> · {dateTime(entry.at)}{entry.detail ? <span className="muted"> · {entry.detail}</span> : null}</li>)}</ol></Section>
    <Section title="As sent"><iframe className="mail-frame" sandbox="" title="The message as sent" srcDoc={detail.html} /></Section>
    {row.unitId && <div className="act"><Link className="btn block" to={`/units/${row.unitId}/email`}>Send again…</Link></div>}
  </>;
}
