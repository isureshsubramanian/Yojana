import { useCallback, useEffect, useMemo, useState } from "react";
import { ActionDialog, DataGrid, KpiStrip, ScanInput, StatusBadge, type GridColumn } from "../components";
import { useReferenceNames } from "../corrections/ReferenceFields";
import { Rail } from "../shell/Drawer";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, UnitLink } from "../units/common";
import { fmtDate, fmtDateTime } from "../units/format";
import { replacementLinksApi } from "./api";
import { dueText, filterCount, filterTitle, humanise, money, replacementStatus, replacementTone } from "./format";
import type { AwaitingFilter, ReplacementLinkPage, ReplacementLinkRow } from "./types";
import "./replacements.css";

const PAGE_SIZE = 50;
type Dialog = { kind: "extend" | "bill" | "lift"; row: ReplacementLinkRow } | null;

export function Component() {
  const [filter, setFilter] = useState<AwaitingFilter>("overdue");
  const [find, setFind] = useState("");
  const [pageNumber, setPageNumber] = useState(1);
  const [page, setPage] = useState<ReplacementLinkPage | null>(null);
  const [active, setActive] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [dialog, setDialog] = useState<Dialog>(null);
  const [reason, setReason] = useState("");
  const [due, setDue] = useState("");
  const [basis, setBasis] = useState("list");
  const [amount, setAmount] = useState("");
  const [busy, setBusy] = useState(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoading(true); setError(null);
    try { setPage(await replacementLinksApi.list(filter, find, (pageNumber - 1) * PAGE_SIZE, PAGE_SIZE, signal)); }
    catch (reason) { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); }
    finally { setLoading(false); }
  }, [filter, find, pageNumber]);
  useEffect(() => { const controller = new AbortController(); void load(controller.signal); return () => controller.abort(); }, [load]);

  const rows = page?.rows ?? [];
  const names = useReferenceNames({ items: rows.flatMap((row) => [row.originalItemId, row.replacementItemId]), parties: rows.map((row) => row.counterpartyPartyRef) });
  const setView = (next: AwaitingFilter) => { setFilter(next); setPageNumber(1); setActive(null); };
  const showAction = (kind: NonNullable<Dialog>["kind"], row: ReplacementLinkRow) => {
    setDialog({ kind, row }); setReason(""); setDue(row.returnDue ?? ""); setBasis(row.nonReturnPriceBasis ?? "list");
    setAmount(row.nonReturnAmount === null ? "" : String(row.nonReturnAmount));
  };
  const drawer = (row: ReplacementLinkRow) => <ReplacementDrawer row={row} customer={names.party(row.counterpartyPartyRef)} onAction={showAction} />;
  const open = (row: ReplacementLinkRow) => { setActive(row.replacementLinkId); openDrawer({ eyebrow: "Advance replacement", title: row.replacementNumber, mono: true }, drawer(row)); };
  const act = async () => {
    if (!dialog || !reason.trim()) return;
    setBusy(true); setError(null);
    try {
      if (dialog.kind === "extend") await replacementLinksApi.extendDue(dialog.row.replacementLinkId, due, reason.trim());
      else if (dialog.kind === "bill") await replacementLinksApi.bill(dialog.row.replacementLinkId, basis, Number(amount), new Date().toISOString().slice(0, 10), reason.trim());
      else if (dialog.row.counterpartyPartyRef) await replacementLinksApi.liftHold(dialog.row.counterpartyPartyRef, reason.trim());
      setDialog(null); await load();
      updateDrawer({ eyebrow: "Advance replacement", title: dialog.row.replacementNumber, mono: true }, <p className="muted">Action recorded. Select the row again for current facts.</p>);
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };

  const columns = useMemo<GridColumn<ReplacementLinkRow>[]>(() => [
    { key: "replacement", label: "Replacement", width: 14, render: (row) => <span className="primary"><span className="t serial">{row.replacementNumber}</span><span className="sub">{row.sourceNumber}</span></span> },
    { key: "original", label: "Unit to come back", width: 18, render: (row) => <><UnitLink unitId={row.originalUnitId}>{row.originalSerial ?? "Serial unavailable"}</UnitLink><span className="sub">{names.item(row.originalItemId)}</span></> },
    { key: "replacementUnit", label: "Sent in its place", width: 18, render: (row) => <><UnitLink unitId={row.replacementUnitId}>{row.replacementSerial ?? "Serial unavailable"}</UnitLink><span className="sub">{row.shippedAt ? `shipped ${fmtDate(row.shippedAt)}` : names.item(row.replacementItemId)}</span></> },
    { key: "customer", label: "Customer", width: 20, render: (row) => names.party(row.counterpartyPartyRef) },
    { key: "due", label: "Return due", width: 12, numeric: true, render: (row) => <span className={`replacement-due${row.daysOverdue !== null ? row.daysOverdue > 10 ? " bad-text" : " warn-text" : ""}`}>{dueText(row)}</span> },
    { key: "price", label: "If not returned", width: 10, numeric: true, render: (row) => <>{money(row.nonReturnAmount, row.currency)}<span className="sub">{humanise(row.nonReturnPriceBasis)}</span></> },
    { key: "status", label: "Status", width: 12, render: (row) => <StatusBadge tone={replacementTone(row)}>{replacementStatus(row)}</StatusBadge> },
  ], [names]);

  const gridPage = { rows, total: page?.total ?? 0, page: pageNumber, pageSize: PAGE_SIZE, continuation: null };
  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Replacements" }, { label: "Awaiting return" }]} />
      <div className="page-header"><div><h1>Advance replacements awaiting return</h1><p className="sub">Replacements shipped before the failed unit came back · each link closes when that physical unit is received · the nightly check flags overdue returns</p></div></div>
      <ErrorNote error={error} />
      <div className="stack">
        <KpiStrip label="Awaiting return figures" kpis={[
          { label: "Awaiting return", value: page?.kpis.awaiting.toLocaleString() ?? "—", detail: "open advance-replacement links" },
          { label: "Overdue", value: page?.kpis.overdue.toLocaleString() ?? "—", detail: "flagged by the nightly check", trend: page?.kpis.overdue ? "down" : null },
          { label: "Due in 7 days", value: page?.kpis.dueInSevenDays.toLocaleString() ?? "—", detail: "not yet overdue" },
          { label: "Customers on hold", value: page?.kpis.customersOnHold.toLocaleString() ?? "—", detail: "no further advance replacements" },
        ]} />
        <section className="card" aria-label="Replacement filters"><div className="card-body replacement-toolbar">
          <ScanInput value={find} onChange={(value) => { setFind(value.trim()); setPageNumber(1); }} onSubmit={(scan) => { setFind(scan.value); setPageNumber(1); }} ariaLabel="Find an awaiting replacement" placeholder="Serial, replacement document or RMA" />
          <div className="segmented plain" role="group" aria-label="Due status">{(["overdue", "due_soon", "all"] as const).map((value) => <button type="button" key={value} aria-pressed={filter === value} onClick={() => setView(value)}>{filterTitle(value)}<span className="n">{filterCount(value, page?.kpis ?? null)?.toLocaleString() ?? "—"}</span></button>)}</div>
        </div></section>
        <section className="card" aria-labelledby="awaiting-list"><div className="card-header"><h2 id="awaiting-list">{filterTitle(filter)} <span className="meta">{page?.total.toLocaleString() ?? "—"} links</span></h2><span className="meta nowrap">{filter === "overdue" ? "Most overdue first" : "Earliest due first"}</span></div>
          <DataGrid columns={columns} page={gridPage} rowId={(row) => row.replacementLinkId} onPage={setPageNumber} loading={loading} viewportHeight={520} emptyText="No advance replacements match this view." onRowClick={open} activeRowId={active} describedBy="awaiting-list" footerNote="Nightly check publishes Replacement.ReturnOverdue once per link" />
        </section>
      </div>
    </main>
    <Rail label="Advance replacements"><Section title="How closure works"><p className="muted">Receiving the original physical unit closes its link regardless of document order. Billing closes it as billed and leaves the original unit with the customer.</p></Section></Rail>
    {dialog && <ReplacementDialog dialog={dialog} reason={reason} setReason={setReason} due={due} setDue={setDue} basis={basis} setBasis={setBasis} amount={amount} setAmount={setAmount} busy={busy} onCancel={() => setDialog(null)} onConfirm={() => void act()} />}
  </>;
}

function ReplacementDrawer({ row, customer, onAction }: { row: ReplacementLinkRow; customer: string; onAction: (kind: NonNullable<Dialog>["kind"], row: ReplacementLinkRow) => void }) {
  return <><Section title="Link"><Kv rows={[["Customer", customer], ["Reason", humanise(row.replacementReason)], ["Return due", fmtDate(row.returnDue)], ["Overdue since", row.overduePublishedAt ? fmtDateTime(row.overduePublishedAt) : "—"], ["Non-return price", `${humanise(row.nonReturnPriceBasis)} · ${money(row.nonReturnAmount, row.currency)}`], ["Customer hold", row.partyOnHold ? "Set · no new advance replacements" : "Not set"]]} /></Section><Section title="Act"><p className="muted">Every action records the signed-in actor. Extending, billing and lifting a hold require a reason.</p><div className="act"><button type="button" className="btn block" onClick={() => onAction("extend", row)}>Extend due date…</button><button type="button" className="btn block" onClick={() => onAction("bill", row)}>Bill non-return…</button>{row.partyOnHold && row.counterpartyPartyRef && <button type="button" className="btn block" onClick={() => onAction("lift", row)}>Lift customer hold…</button>}</div></Section></>;
}

function ReplacementDialog({ dialog, reason, setReason, due, setDue, basis, setBasis, amount, setAmount, busy, onCancel, onConfirm }: {
  dialog: NonNullable<Dialog>; reason: string; setReason: (value: string) => void; due: string; setDue: (value: string) => void;
  basis: string; setBasis: (value: string) => void; amount: string; setAmount: (value: string) => void; busy: boolean; onCancel: () => void; onConfirm: () => void;
}) {
  const extending = dialog.kind === "extend"; const billing = dialog.kind === "bill";
  const valid = reason.trim() && (!extending || !!due && due > (dialog.row.returnDue ?? "")) && (!billing || Number(amount) > 0);
  const title = extending ? "Extend return due date" : billing ? "Bill non-return" : "Lift customer hold";
  return <ActionDialog title={title} onCancel={onCancel} actions={<button type="button" className="btn primary" disabled={busy || !valid} onClick={onConfirm}>{busy ? "Recording…" : title}</button>}>
    <p className="muted replacement-dialog-note"><span className="serial">{dialog.row.replacementNumber}</span> · {dialog.row.originalSerial ?? "serial unavailable"}</p>
    <div className="replacement-dialog-grid">
      {extending && <div className="field span"><label htmlFor="replacement-new-due">New return due</label><input id="replacement-new-due" className="input" type="date" min={dialog.row.returnDue ?? undefined} value={due} onChange={(event) => setDue(event.target.value)} /><span className="help">Must be later than {fmtDate(dialog.row.returnDue)}.</span></div>}
      {billing && <><div className="field"><label htmlFor="replacement-price-basis">Price basis</label><select id="replacement-price-basis" className="select" value={basis} onChange={(event) => setBasis(event.target.value)}><option value="list">List</option><option value="fmv">Fair market value</option><option value="cost">Cost</option></select></div><div className="field"><label htmlFor="replacement-amount">Amount · {dialog.row.currency}</label><input id="replacement-amount" className="input" type="number" min="0.01" step="0.01" value={amount} onChange={(event) => setAmount(event.target.value)} /></div></>}
      <div className="field span"><label htmlFor="replacement-action-reason">Reason</label><textarea id="replacement-action-reason" className="textarea" autoFocus value={reason} onChange={(event) => setReason(event.target.value)} placeholder="Why this action is being taken" /></div>
    </div>
  </ActionDialog>;
}
