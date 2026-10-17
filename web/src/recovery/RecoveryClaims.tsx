import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";
import { Link } from "react-router";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { useReferenceNames } from "../corrections/ReferenceFields";
import { usePeople } from "../people/people";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { closeDrawer, openDrawer } from "../shell/drawerStore";
import { ErrorNote, Kv, Section, UnitLink } from "../units/common";
import { fmtDate } from "../units/format";
import { recoveryApi } from "./api";
import { currencyAmounts, money, noRecoveryExplanation, primaryApprovalRate, recoveryState, termText } from "./format";
import type { RecoveryAnswer, RecoveryClaimPage, RecoveryClaimRow, RecoveryClaimSummary, RecoveryNotRaisedReport } from "./types";

const PAGE_SIZE = 50;

export function Component() {
  const [status, setStatus] = useState("open");
  const [vendor, setVendor] = useState("");
  const [childItem, setChildItem] = useState("");
  const [findText, setFindText] = useState("");
  const [find, setFind] = useState("");
  const [page, setPage] = useState(1);
  const [data, setData] = useState<RecoveryClaimPage | null>(null);
  const [summary, setSummary] = useState<RecoveryClaimSummary | null>(null);
  const [notRaised, setNotRaised] = useState<RecoveryNotRaisedReport | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [revision, setRevision] = useState(0);
  const filters = useMemo(() => ({ status, vendor: vendor || undefined, childItem: childItem || undefined, find: find || undefined, skip: (page - 1) * PAGE_SIZE, take: PAGE_SIZE }), [status, vendor, childItem, find, page]);
  const exportFilters = useMemo(() => ({ status, vendor: vendor || undefined, childItem: childItem || undefined, find: find || undefined }), [status, vendor, childItem, find]);

  useEffect(() => { const timer = window.setTimeout(() => { setFind(findText.trim()); setPage(1); }, 250); return () => window.clearTimeout(timer); }, [findText]);

  useEffect(() => {
    const controller = new AbortController(); setLoading(true); setError(null);
    Promise.all([recoveryApi.page(filters, controller.signal), recoveryApi.summary(controller.signal), recoveryApi.notRaised(30, controller.signal)])
      .then(([nextPage, nextSummary, nextNotRaised]) => { setData(nextPage); setSummary(nextSummary); setNotRaised(nextNotRaised); setLoading(false); })
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) { setError(reason); setLoading(false); } });
    return () => controller.abort();
  }, [filters, revision]);

  const rows = data?.rows ?? [];
  const allItemIds = [...rows.map((row) => row.childItemId), ...(summary?.childItems.map((row) => row.id) ?? []), ...(notRaised?.rows.map((row) => row.itemId) ?? [])];
  const allPartyIds = [...rows.map((row) => row.vendorPartyRef), ...(summary?.vendors.map((row) => row.id) ?? []), ...(notRaised?.rows.map((row) => row.vendorPartyRef) ?? []), summary?.oldestOpen?.vendorPartyRef];
  const names = useReferenceNames({ items: allItemIds, parties: allPartyIds });
  const selected = rows.find((row) => row.recoveryClaimId === selectedId) ?? null;
  const openAmounts = currencyAmounts(summary?.openAmounts ?? [], "requestedAmount");
  const approvedAmounts = currencyAmounts(summary?.approvedThisYear ?? [], "approvedAmount");
  const approvalRate = primaryApprovalRate(summary?.approvedThisYear ?? []);

  const refresh = useCallback(() => { closeDrawer(); setRevision((value) => value + 1); }, []);
  const drawerBody = useCallback((row: RecoveryClaimRow) => <RecoveryDrawer row={row} onSaved={refresh} />, [refresh]);
  const openRow = useCallback((row: RecoveryClaimRow) => {
    setSelectedId(row.recoveryClaimId);
    const state = recoveryState(row);
    openDrawer({ eyebrow: `Recovery claim · ${state.label.toLocaleLowerCase()}`, title: row.recoveryNumber, mono: true }, drawerBody(row));
  }, [drawerBody]);

  const columns: GridColumn<RecoveryClaimRow>[] = [
    { key: "recovery", label: "Recovery", width: 13, mono: true, render: (row) => <>{row.recoveryNumber}<span className="sub" style={{ fontFamily: "var(--font-body)" }}>raised {fmtDate(row.raisedAt)}</span></> },
    { key: "claim", label: "Customer claim", width: 13, mono: true, render: (row) => <Link to={`/claims/${row.customerClaimId}`}>{row.customerClaimNumber}</Link> },
    { key: "child", label: "Child unit", width: 16, render: (row) => <><UnitLink unitId={row.childUnitId}>{row.childSerial}</UnitLink><span className="sub">{names.item(row.childItemId)} · {row.parentSerial ? `in ${row.parentSerial}` : "unit itself"}</span></> },
    { key: "vendor", label: "Vendor", width: 15, render: (row) => names.party(row.vendorPartyRef) },
    { key: "term", label: "Vendor term", width: 14, render: (row) => <>{termText(row.vendorTermMonths, row.vendorTermEnd)}<span className="sub">month {row.claimMonthOfTerm ?? "—"}</span></> },
    { key: "requested", label: "Requested", width: 9, numeric: true, render: (row) => money(row.requestedAmount, row.currency) },
    { key: "approved", label: "Approved", width: 9, numeric: true, muted: true, render: (row) => row.approvedAmount === null ? "—" : money(row.approvedAmount, row.currency) },
    { key: "status", label: "Status", width: 11, render: (row) => { const state = recoveryState(row); return <><StatusBadge tone={state.tone}>{state.label}</StatusBadge><span className="sub">{state.detail}</span></>; } },
  ];

  const title = status === "open" ? "Open recovery claims" : status === "approved" ? "Approved recovery claims" : "Recovery claims";
  const gridPage = { rows, total: data?.total ?? 0, page, pageSize: PAGE_SIZE, continuation: null };

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Claims", to: "/claims" }, { label: "Recovery claims" }]} />
      <div className="page-header"><div><h1>Recovery claims</h1><p className="sub">Raised automatically when an approved customer claim hits a unit or child with a vendor term live on the claim date · requested and approved amounts tracked separately</p></div><div className="actions">
        <a className="btn" href={recoveryApi.exportUrl(exportFilters)} download><Icon name="down" />Export</a>
        <button type="button" className="btn primary" disabled={!selected || selected.status !== "raised"} onClick={() => selected && openRow(selected)}><Icon name="check" />Record vendor answer</button>
      </div></div>
      <ErrorNote error={error} />
      <div className="stack">
        <KpiStrip label="Recovery figures" kpis={[
          { label: "Open with vendors", value: (summary?.openCount ?? 0).toLocaleString(), detail: `${openAmounts.value} requested${openAmounts.others ? ` · ${openAmounts.others}` : ""}`, trend: null },
          { label: `Approved · ${new Date().getFullYear()}`, value: approvedAmounts.value, detail: `${approvalRate === null ? "—" : `${approvalRate} %`} of requested${approvedAmounts.others ? ` · ${approvedAmounts.others}` : ""}`, trend: null },
          { label: "Not raised · term expired", value: (summary?.termExpiredWithoutRecovery ?? 0).toLocaleString(), detail: "vendor term ended before the claim date", trend: null },
          { label: "Oldest open", value: summary?.oldestOpen ? `${summary.oldestOpen.ageDays} d` : "—", detail: summary?.oldestOpen ? `${summary.oldestOpen.recoveryNumber} · ${names.party(summary.oldestOpen.vendorPartyRef)}` : "No open recovery claims", trend: null },
        ]} />

        <section className="card" aria-label="Recovery claim filters"><div className="card-body"><div className="toolbar">
          <div className="scanbox plain"><Icon name="scan" /><input className="input" type="search" value={findText} onChange={(event) => setFindText(event.target.value)} placeholder="Recovery, claim, child serial or vendor" aria-label="Find a recovery claim" /></div>
          <select className={`chip-select${status !== "all" ? " on" : ""}`} aria-label="Status" value={status} onChange={(event) => { setStatus(event.target.value); setPage(1); }}><option value="open">Status · Open</option><option value="approved">Status · Approved</option><option value="all">Status · All</option></select>
          <select className={`chip-select${vendor ? " on" : ""}`} aria-label="Vendor" value={vendor} onChange={(event) => { setVendor(event.target.value); setPage(1); }}><option value="">Vendor · All</option>{summary?.vendors.map((option) => <option key={option.id} value={option.id}>{names.party(option.id)} · {option.count}</option>)}</select>
          <select className={`chip-select${childItem ? " on" : ""}`} aria-label="Child item" value={childItem} onChange={(event) => { setChildItem(event.target.value); setPage(1); }}><option value="">Child item · All</option>{summary?.childItems.map((option) => <option key={option.id} value={option.id}>{names.item(option.id)} · {option.count}</option>)}</select>
        </div></div></section>

        <section className="card" aria-labelledby="recovery-list"><div className="card-header"><h2 id="recovery-list">{title} <span className="meta">{data?.total.toLocaleString() ?? "…"} claims · {summary?.vendors.length.toLocaleString() ?? "…"} vendors</span></h2><span className="meta nowrap">Sorted by age, oldest first</span></div>
          <DataGrid columns={columns} page={gridPage} rowId={(row) => row.recoveryClaimId} onPage={setPage} onRowClick={openRow} activeRowId={selectedId} loading={loading} emptyText="No recovery claims match these filters." viewportHeight={330} />
        </section>

        <section className="card" aria-labelledby="not-raised"><div className="card-header"><h2 id="not-raised">Approved claims that raised no recovery <span className="meta">last {notRaised?.days ?? 30} days · {notRaised?.rows.length.toLocaleString() ?? "…"} claims</span></h2><span className="meta">so a missed recovery is visible, not silent</span></div>
          <table className="table dense"><colgroup><col style={{ width: "16%" }} /><col style={{ width: "22%" }} /><col style={{ width: "22%" }} /><col style={{ width: "40%" }} /></colgroup><tbody>
            {notRaised?.rows.map((row) => <tr key={row.customerClaimId}><td className="serial"><Link to={`/claims/${row.customerClaimId}`}>{row.customerClaimNumber}</Link></td><td><UnitLink unitId={row.unitId}>{row.unitSerial}</UnitLink> · {names.item(row.itemId)}</td><td>{row.vendorPartyRef ? names.party(row.vendorPartyRef) : <span className="muted">no vendor term</span>}</td><td className="muted">{noRecoveryExplanation(row)}</td></tr>)}
            {!loading && !notRaised?.rows.length && <tr><td colSpan={4} className="muted" style={{ textAlign: "center", height: 64 }}>Every approved claim in the last 30 days has an explained recovery outcome.</td></tr>}
          </tbody></table>
        </section>
      </div>
    </main>
    <Rail label="Recovery claims"><Section title="Queue"><Kv rows={[["Open", summary?.openCount.toLocaleString() ?? "—"], ["Selected", selected?.recoveryNumber ?? "—"], ["Not raised · 30 d", notRaised?.rows.length.toLocaleString() ?? "—"]]} /></Section><Section title="Amounts"><p className="muted">Currencies are totalled separately. The first figure is the majority currency by claim count; other currencies remain visible beside it.</p></Section></Rail>
  </>;
}

function RecoveryDrawer({ row, onSaved }: { row: RecoveryClaimRow; onSaved: () => void }) {
  const names = useReferenceNames({ items: [row.childItemId], parties: [row.vendorPartyRef] });
  const person = usePeople([row.approvedBy]);
  return <>
    <Section title="Raised from"><Kv rows={[
      ["Customer claim", <Link className="serial" to={`/claims/${row.customerClaimId}`}>{row.customerClaimNumber} · approved {fmtDate(row.claimApprovedAt)}</Link>],
      ["Child unit", <UnitLink unitId={row.childUnitId}>{row.childSerial} · {names.item(row.childItemId)}</UnitLink>],
      ["Parent unit", row.parentUnitId ? <UnitLink unitId={row.parentUnitId}>{row.parentSerial}</UnitLink> : "unit itself"],
      ["Vendor", names.party(row.vendorPartyRef)],
      ["Inbound entitlement", `${row.vendorTermMonths ?? "—"} m from receipt ${fmtDate(row.vendorTermStart)} · live on the claim date`],
      ["Requested", money(row.requestedAmount, row.currency)],
      ["Approved", row.approvedAmount === null ? "—" : money(row.approvedAmount, row.currency)],
      ["Approved by", row.approvedBy ? person(row.approvedBy) : "—"],
      ["Vendor reference", row.vendorReference ?? "—"],
      ["Vendor note", row.vendorNote ?? "—"],
    ]} /></Section>
    {row.status === "raised" && <Section title="Record vendor answer"><VendorAnswerForm row={row} onSaved={onSaved} /></Section>}
  </>;
}

function VendorAnswerForm({ row, onSaved }: { row: RecoveryClaimRow; onSaved: () => void }) {
  const [approved, setApproved] = useState("");
  const [reference, setReference] = useState("");
  const [note, setNote] = useState("");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const amount = Number(approved);
    if (!Number.isFinite(amount) || amount < 0 || amount > row.requestedAmount) { setError(new Error(`Enter an amount from 0 to ${money(row.requestedAmount, row.currency)}.`)); return; }
    setSaving(true); setError(null);
    const body: RecoveryAnswer = { commandId: crypto.randomUUID(), approvedAmount: amount, eventTime: new Date().toISOString(), vendorReference: reference.trim() || null, vendorNote: note.trim() || null };
    try { await recoveryApi.answer(row.recoveryClaimId, body); onSaved(); } catch (reason) { setError(reason); setSaving(false); }
  };
  return <form onSubmit={(event) => void submit(event)}><div className="form-grid one">
    <div className="field"><label htmlFor="recovery-approved">Approved amount</label><input id="recovery-approved" className="input" type="number" min="0" max={row.requestedAmount} step="0.01" required value={approved} onChange={(event) => setApproved(event.target.value)} placeholder={money(0, row.currency)} /></div>
    <div className="field"><label htmlFor="recovery-reference">Vendor reference</label><input id="recovery-reference" className="input" maxLength={100} value={reference} onChange={(event) => setReference(event.target.value)} placeholder="credit note or RMA no." /></div>
    <div className="field"><label htmlFor="recovery-note">Vendor note</label><textarea id="recovery-note" className="textarea" maxLength={500} value={note} onChange={(event) => setNote(event.target.value)} placeholder="Reason for a partial approval or refusal" /></div>
  </div><ErrorNote error={error} /><p className="hint" style={{ marginTop: 8 }}>Approving publishes RecoveryClaim.Approved; Finance books the vendor debit. Enter zero for a refusal.</p><div className="act"><button type="submit" className="btn primary block" disabled={saving}>{saving ? "Recording…" : "Record answer"}</button></div></form>;
}
