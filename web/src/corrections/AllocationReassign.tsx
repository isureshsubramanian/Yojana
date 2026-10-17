import { useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { DataGrid, StatusBadge, type GridColumn } from "../components";
import { DEFAULT_LEGAL_ENTITY, newCommandId, now } from "../api/units";
import { Rail } from "../shell/Drawer";
import { openDrawer } from "../shell/drawerStore";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section } from "../units/common";
import { humanise } from "../units/format";
import { correctionsApi } from "./api";
import { GUID, serialSelection } from "./format";
import { DocumentField, useReferenceNames, type DocumentValue } from "./ReferenceFields";
import type { CorrectionAllocationPreview, ReassignAllocation, ReassignAllocationResult } from "./types";

const PAGE = 10;

export function Component() {
  const [params] = useSearchParams();
  const [commandId, setCommandId] = useState(newCommandId);
  const [fromOrder, setFromOrder] = useState(params.get("from") ?? "");
  const [toOrder, setToOrder] = useState(params.get("to") ?? "");
  const [scheme, setScheme] = useState(params.get("scheme") ?? "default");
  const [mode, setMode] = useState("pairwise_exchange");
  const [fromSerials, setFromSerials] = useState(params.get("fromSerials") ?? "");
  const [toSerials, setToSerials] = useState(params.get("toSerials") ?? "");
  const [reason, setReason] = useState("allocation_error");
  const [note, setNote] = useState("");
  const [preview, setPreview] = useState<ReassignAllocationResult | null>(null);
  const [committed, setCommitted] = useState<ReassignAllocationResult | null>(null);
  const [page, setPage] = useState(1);
  const [active, setActive] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const rows = preview?.preview.allocationReassignments ?? [];
  const visible = rows.slice((page - 1) * PAGE, page * PAGE);
  const fromDocument: DocumentValue | null = GUID.test(fromOrder) ? { type: "production_order", id: fromOrder } : null;
  const toDocument: DocumentValue | null = GUID.test(toOrder) ? { type: "production_order", id: toOrder } : null;
  const referenceNames = useReferenceNames({ documents: [fromDocument, toDocument] });
  const fromLabel = fromDocument ? referenceNames.document(fromDocument) : "order A";
  const toLabel = toDocument ? referenceNames.document(toDocument) : "order B";

  const command = useMemo<ReassignAllocation | null>(() => {
    const from = serialSelection(fromSerials, scheme);
    const to = mode === "pairwise_exchange" ? serialSelection(toSerials, scheme) : null;
    if (!GUID.test(fromOrder) || !GUID.test(toOrder) || !from || (mode === "pairwise_exchange" && !to)) return null;
    return { commandId, fromProductionOrder: { type: "production_order", id: fromOrder }, toProductionOrder: { type: "production_order", id: toOrder }, mode, fromSerials: from, toSerials: to, reason, eventTime: now(), actorRef: "", legalEntityId: DEFAULT_LEGAL_ENTITY, note: note || null };
  }, [commandId, fromOrder, toOrder, mode, fromSerials, toSerials, scheme, reason, note]);

  const runPreview = async () => { if (!command) return; setBusy(true); setError(null); setCommitted(null); try { const result = await correctionsApi.previewReassignment(command); setPreview(result); setPage(1); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  const commit = async () => { if (!command || !preview || preview.preview.block > 0 || preview.preview.refusal) return; setBusy(true); setError(null); try { const result = await correctionsApi.reassignAllocation(command); setCommitted(result); setPreview(result); setCommandId(newCommandId()); } catch (reason) { setError(reason); } finally { setBusy(false); } };

  const openPair = (row: CorrectionAllocationPreview) => { const id = String(row.pair); setActive(id); openDrawer({ eyebrow: `Pair ${row.pair} of ${rows.length}`, title: row.fromSerial, mono: true }, <><h3>{row.outcome === "blocked" ? "Refused by the command" : "Ready to move"}</h3><Kv rows={[["Exchange serial", row.exchangeSerial ?? "one-sided move"], ["Rule", row.rule ? <span className="serial">{row.rule}</span> : "all checks passed"], ["Detail", row.detail ?? "reservation is eligible"], ["Unblock by", row.unblockingStep ?? "—"], ["From range", row.fromSerialRangeId], ["To range", row.exchangeSerialRangeId ?? "—"]]} />{row.unblockingStep && <div className="act"><Link className="btn block" to="/corrections">Open in workbench</Link></div>}</>); };
  const columns: GridColumn<CorrectionAllocationPreview>[] = [
    { key: "from", label: `To ${toLabel}`, width: 22, mono: true, render: (row) => row.fromSerial },
    { key: "to", label: mode === "pairwise_exchange" ? `To ${fromLabel}` : "Exchange serial", width: 22, mono: true, render: (row) => row.exchangeSerial ?? <span className="muted">—</span> },
    { key: "reservation", label: "Reservation", width: 22, render: (row) => row.outcome === "blocked" ? <span className="muted">pre-commission</span> : "reserved" },
    { key: "outcome", label: "Outcome", width: 34, render: (row) => <><StatusBadge tone={row.outcome === "blocked" ? "bad" : "ok"}>{row.outcome === "blocked" ? `Blocked${row.rule ? ` · ${humanise(row.rule)}` : ""}` : "Will move"}</StatusBadge>{row.detail && <span className="sub">{row.detail}</span>}</> },
  ];
  const blocked = preview?.preview.block ?? rows.filter((row) => row.outcome === "blocked").length;
  const moving = preview?.preview.change ?? rows.filter((row) => row.outcome !== "blocked").length;

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Corrections", to: "/corrections" }, { label: "Reassign allocation" }]} />
      <div className="page-header"><div><div className="eyebrow">ReassignAllocation · serials allocated to the wrong work order · before commissioning only</div><h1>Reassign allocation</h1><p className="sub">Serial-range reservations move between work orders as a pairwise exchange; a serial already commissioned cannot move here (use the workbench: crossed pair or void).</p></div><div className="actions"><Link className="btn ghost" to="/corrections">Back to workbench</Link><button type="button" className="btn" onClick={() => void runPreview()} disabled={!command || busy}>Preview exchange</button><button type="button" className="btn primary" onClick={() => void commit()} disabled={!command || !preview || blocked > 0 || !!preview.preview.refusal || busy}>Commit exchange<kbd className="key">⌘↵</kbd></button></div></div>
      <ErrorNote error={error} />
      <div className="cols-2-3">
        <section className="card" aria-labelledby="allocation-orders"><div className="card-header"><h2 id="allocation-orders">Work orders</h2><span className="meta">scheme {scheme || "required"}</span></div><div className="card-body">
          <div className="correction-source-fields"><DocumentField id="allocation-from-id" label="From production order" types={["production_order"]} value={fromDocument} onChange={(value) => { setFromOrder(value?.id ?? ""); setPreview(null); }} /><DocumentField id="allocation-to-id" label="To production order" types={["production_order"]} value={toDocument} onChange={(value) => { setToOrder(value?.id ?? ""); setPreview(null); }} /></div>
          <div className="correction-work-orders"><div className="correction-work-order"><div className="k">From</div><div className="serial">{fromLabel}</div><div className="sub muted">reservation source · before commissioning</div></div><div className="correction-exchange" aria-hidden="true">⇄</div><div className="correction-work-order"><div className="k">To</div><div className="serial">{toLabel}</div><div className="sub muted">reservation destination</div></div></div>
          <div className="form-grid" style={{ marginTop: 14 }}><div className="field span"><span className="lbl">Mode</span><div className="segmented plain" role="group" aria-label="Mode"><button type="button" aria-pressed={mode === "pairwise_exchange"} onClick={() => { setMode("pairwise_exchange"); setPreview(null); }}>Pairwise exchange</button><button type="button" aria-pressed={mode === "move"} onClick={() => { setMode("move"); setPreview(null); }}>Move to the other order</button></div></div>
            <div className="field span"><label htmlFor="allocation-from">Serials leaving {fromLabel} · scan, list or range</label><input id="allocation-from" className="input serial" value={fromSerials} onChange={(event) => { setFromSerials(event.target.value); setPreview(null); }} placeholder="2510-007713 → 2510-007740, or one serial per line" /></div>
            {mode === "pairwise_exchange" && <div className="field span"><label htmlFor="allocation-to">Serials leaving {toLabel} in exchange</label><input id="allocation-to" className="input serial" value={toSerials} onChange={(event) => { setToSerials(event.target.value); setPreview(null); }} placeholder="2510-007753 → 2510-007780" /></div>}
            <div className="field"><label htmlFor="allocation-scheme">Serial scheme</label><input id="allocation-scheme" className="input" value={scheme} onChange={(event) => { setScheme(event.target.value); setPreview(null); }} /></div><div className="field"><label htmlFor="allocation-reason">Reason code</label><select id="allocation-reason" className="select" value={reason} onChange={(event) => { setReason(event.target.value); setPreview(null); }}><option value="allocation_error">allocation_error</option><option value="work_order_changed">work_order_changed</option><option value="work_order_cancelled">work_order_cancelled</option></select></div>
            <div className="field span"><label htmlFor="allocation-note">Note</label><input id="allocation-note" className="input" value={note} onChange={(event) => { setNote(event.target.value); setPreview(null); }} placeholder="Why these reservations need to move" /></div>
            <div className="span note ok"><strong>Commit without approver</strong>&nbsp;is evaluated from the signed-in user’s tenant roles. No approver identity is sent by this screen.</div>{committed && <div className="span note ok"><strong>Exchange committed.</strong>&nbsp;Correction {committed.correctionId}{committed.replayed ? " · replayed idempotently" : ""}.</div>}
          </div>
        </div></section>
        <section className="card" aria-labelledby="allocation-preview"><div className="card-header"><h2 id="allocation-preview">Preview <span className="meta">computed by the command</span></h2><button type="button" className="btn ghost sm" disabled={!rows.length} onClick={() => download(rows)}>Download CSV</button></div><div className="preview-summary"><Figure label="Pairs" value={rows.length} /><Figure label="Will move" value={moving} tone="ok" /><Figure label="Blocked" value={blocked} tone="bad" /><Figure label="Range check" value={preview?.preview.refusal ? "refused" : preview ? "valid" : "—"} /></div>
          {preview?.preview.refusal && <div className="note bad"><strong>{humanise(preview.preview.refusal.rule)}.</strong>&nbsp;{preview.preview.refusal.message}{preview.preview.refusal.unblockingStep ? ` · ${preview.preview.refusal.unblockingStep}` : ""}</div>}
          <DataGrid dense columns={columns} page={{ rows: visible, total: rows.length, page, pageSize: PAGE, continuation: null }} rowId={(row) => String(row.pair)} onPage={setPage} onRowClick={openPair} activeRowId={active} loading={busy} emptyText={command ? "Preview this exchange to see every pair." : "Enter both work orders and serial selections to preview."} viewportHeight={460} footerNote={blocked ? "Blocked pairs must be removed or fixed before anything moves" : "Nothing moves until you commit"} />
        </section>
      </div>
    </main>
    <Rail label="Allocation exchange"><Section title="Rules"><p className="muted">Only reservations that have not been commissioned can move. Pairwise exchange preserves order: the first serial in each selection is a pair, then the second, and so on.</p></Section><Section title="Request"><Kv rows={[["Mode", humanise(mode)], ["Scheme", scheme || "—"], ["Pairs", rows.length], ["Will move", moving], ["Blocked", blocked], ["Reason", reason]]} /></Section></Rail>
  </>;
}

function Figure({ label, value, tone }: { label: string; value: string | number; tone?: "ok" | "bad" }) { return <span className="figure"><span className="k">{label}</span><span className={`v${tone ? ` ${tone}-text` : ""}`}>{typeof value === "number" ? value.toLocaleString() : value}</span></span>; }
function download(rows: CorrectionAllocationPreview[]) { const csv = ["pair,from_serial,exchange_serial,outcome,rule,detail", ...rows.map((row) => [row.pair, row.fromSerial, row.exchangeSerial ?? "", row.outcome, row.rule ?? "", row.detail ?? ""].map(csvCell).join(","))].join("\r\n"); const href = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" })); const link = document.createElement("a"); link.href = href; link.download = "allocation-reassignment-preview.csv"; link.click(); URL.revokeObjectURL(href); }
function csvCell(value: string | number): string { const text = String(value); return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text; }
