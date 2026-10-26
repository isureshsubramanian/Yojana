import { useMemo, useState } from "react";
import { DataGrid, KpiStrip, StatusBadge, useGridQuery, type GridColumn } from "../components";
import { useSession } from "../session/session";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { Rail } from "../shell/Drawer";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, useActor } from "../units/common";
import { documentLabel, fmtDateTime, humanise, shortId, splitSerials } from "../units/format";
import { entitlementApi } from "./api";
import { daysOld } from "./format";
import { warrantyLineExceptionPageSchema, type WarrantyLineException } from "./types";

const PAGE = 50;

export function Component() {
  const session = useSession();
  const actor = useActor();
  const [status, setStatus] = useState("open");
  const [active, setActive] = useState<string | null>(null);
  const [version, setVersion] = useState(0);
  const params = useMemo(() => { const value = new URLSearchParams(); if (status) value.set("status", status); return value; }, [status]);
  const query = useGridQuery("/api/warranty-lines/exceptions", warrantyLineExceptionPageSchema, { gridId: "warranty-line-exceptions", userId: session.user.id, defaultSort: { key: "raised", dir: "asc" }, defaultSize: PAGE, params, reloadKey: version, sortKeys: ["raised", "trigger", "status"] });
  const data = query.data;
  const rows = data?.items ?? [];
  const oldest = rows.reduce((age, row) => Math.max(age, daysOld(row.raisedAt)), 0);
  const columns: GridColumn<WarrantyLineException>[] = [
    { key: "document", label: "Document · line", width: 18, mono: true, render: (row) => <>{shortId(row.document.id, 12)}<span className="sub" style={{ fontFamily: "var(--font-body)" }}>{humanise(row.document.type)}{row.document.line != null ? ` · line ${row.document.line}` : ""}</span></> },
    { key: "item", label: "Warranty item", width: 15, mono: true, render: (row) => shortId(row.itemId, 12) },
    { key: "failure", label: "What failed", width: 29, render: (row) => <>{humanise(row.missingInput)}<span className="sub">{row.detail ?? "The target could not be resolved."}</span></> },
    { key: "trigger", label: "Original trigger", width: 16, sort: { key: "trigger", defaultDir: "asc" }, render: (row) => fmtDateTime(row.triggerAt) },
    { key: "age", label: "Age", width: 8, numeric: true, sort: { key: "raised", defaultDir: "asc" }, render: (row) => `${daysOld(row.raisedAt)} d` },
    { key: "status", label: "Status", width: 14, sort: { key: "status", defaultDir: "asc" }, render: (row) => <StatusBadge tone={row.status === "open" ? "warn" : row.status === "resolved" ? "ok" : "info"}>{humanise(row.status)}</StatusBadge> },
  ];
  const open = (row: WarrantyLineException) => {
    setActive(row.exceptionId);
    openDrawer({ eyebrow: `${humanise(row.document.type)}${row.document.line != null ? ` line ${row.document.line}` : ""} · ${shortId(row.itemId)}`, title: shortId(row.document.id, 12), mono: true }, <ResolveExceptionForm row={row} actor={actor} onDone={() => setVersion((value) => value + 1)} />);
  };
  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Entitlements", to: "/entitlements/gaps" }, { label: "Warranty-line exceptions" }]} />
      <div className="page-header"><div><div className="eyebrow">Entitlements · exception queue · posted sales invoices</div><h1>Warranty-line exceptions</h1><p className="sub">Invoice lines for warranty-service items whose target could not be resolved · entitlements are created deferred, dated from the original trigger</p></div><div className="actions"><button type="button" className="btn primary" disabled={!rows[0]} onClick={() => rows[0] && open(rows[0])}>Resolve targets…</button></div></div>
      <ErrorNote error={query.error} />
      <div className="stack">
        <KpiStrip label="Queue figures" kpis={[{ label: status === "open" ? "Open lines" : humanise(status), value: (data?.total ?? 0).toLocaleString(), detail: "invoice and drop-ship warranty lines", trend: null }, { label: "Oldest on page", value: `${oldest} d`, detail: rows[0] ? documentLabel(rows.reduce((old, row) => row.raisedAt < old.raisedAt ? row : old).document) : "queue empty", trend: null }, { label: "Awaiting ASN", value: rows.filter((row) => row.missingInput === "serials_pending_asn").length.toLocaleString(), detail: "resolved when vendor serials arrive", trend: null }, { label: "Needs sales input", value: rows.filter((row) => ["target_not_serialised", "referenced_document_not_posted"].includes(row.missingInput)).length.toLocaleString(), detail: "on this page", trend: null }]} />
        <section className="card" aria-labelledby="exception-list"><div className="card-header"><h2 id="exception-list">{status === "open" ? "Open exceptions" : "Warranty-line exceptions"} <span className="meta">{data?.total.toLocaleString() ?? "…"} lines</span></h2><div className="segmented plain" role="group" aria-label="Status"><button type="button" aria-pressed={status === "open"} onClick={() => setStatus("open")}>Open <span className="n">{status === "open" ? data?.total ?? 0 : ""}</span></button><button type="button" aria-pressed={status === "resolved"} onClick={() => setStatus("resolved")}>Resolved</button><button type="button" aria-pressed={status === ""} onClick={() => setStatus("")}>All</button></div></div><DataGrid columns={columns} page={{ rows, total: data?.total ?? 0, page: query.page, pageSize: query.pageSize, continuation: null }} rowId={(row) => row.exceptionId} onPage={query.onPage} onRowClick={open} activeRowId={active} loading={query.loading} emptyText="No warranty-line exceptions have this status. Deferred coverage will keep its original trigger when a new exception is resolved." viewportHeight={620} gridId="warranty-line-exceptions" userId={session.user.id} sort={query.sort} defaultSort={{ key: "raised", dir: "asc" }} onSort={query.onSort} pageSize={query.pageSize} defaultPageSize={PAGE} onPageSize={query.onPageSize} /></section>
      </div>
    </main>
    <Rail label="Exception queue"><Section title="Resolution rule"><p className="muted">Supplying the serials closes the exception and creates coverage from the original policy trigger. The resolution date never shortens the customer's term.</p></Section><Section title="Queue"><Kv rows={[["Status filter", status || "all"], ["Lines", data?.total.toLocaleString() ?? "—"], ["Oldest on page", `${oldest} days`]]} /></Section></Rail>
  </>;
}

function ResolveExceptionForm({ row, actor, onDone }: { row: WarrantyLineException; actor: string; onDone: () => void }) {
  const [serials, setSerials] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const resolve = async () => {
    setBusy(true); setError(null);
    try { const result = await entitlementApi.resolveException(row.exceptionId, splitSerials(serials), actor); updateDrawer({ eyebrow: "Resolved · warranty-line exception", title: shortId(result.document.id, 12), mono: true }, <><h3>Done</h3><Kv rows={[["Status", humanise(result.status)], ["Original trigger", fmtDateTime(result.triggerAt)], ["Resolved", fmtDateTime(result.resolvedAt)], ["Serials supplied", splitSerials(serials).length.toLocaleString()]]} /></>); onDone(); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  return <><h3>Invoice line</h3><Kv rows={[["Document", documentLabel(row.document)], ["Item", shortId(row.itemId, 12)], ["Failure", humanise(row.missingInput)], ["Detail", row.detail ?? "—"], ["Original trigger", fmtDateTime(row.triggerAt)], ["Raised", fmtDateTime(row.raisedAt)]]} /><h3 style={{ marginTop: 16 }}>Resolve targets</h3><label className="lbl" htmlFor={`exception-${row.exceptionId}`}>Serial list</label><textarea id={`exception-${row.exceptionId}`} className="textarea serial" value={serials} onChange={(event) => setSerials(event.target.value)} placeholder="Paste or scan one serial per line" /><ErrorNote error={error} /><div className="act"><button type="button" className="btn primary block" disabled={!splitSerials(serials).length || busy} aria-busy={busy} onClick={resolve}>Attach {splitSerials(serials).length || "the"} serials</button></div><div className="note" style={{ marginTop: 12 }}>Entitlements are dated from {fmtDateTime(row.triggerAt)}, never from the date this exception is resolved.</div></>;
}
