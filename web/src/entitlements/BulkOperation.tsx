import { useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router";
import type { BulkExecutionResult, BulkOperationArguments, BulkPreviewResult, SelectionSource, UnitOutcome } from "../api/units";
import { DEFAULT_LEGAL_ENTITY, newCommandId, now } from "../api/units";
import { DataGrid, StatusBadge, type GridColumn } from "../components";
import { openDrawer } from "../shell/drawerStore";
import { Rail } from "../shell/Drawer";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, useActor } from "../units/common";
import { humanise, outcomeTone, shortId, splitSerials } from "../units/format";
import { entitlementApi } from "./api";
import { approvalId, loadApprovals, removeApproval, saveApproval } from "./approvalStore";
import type { CoverageFamily, PolicySummary, StoredApproval } from "./types";

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const PAGE = 50;
const KINDS = [["document", "Document"], ["customer", "Customer"], ["item", "Item"], ["date_range", "Date range"], ["pasted_list", "Pasted list"], ["serial_range", "Serial range"]] as const;
const OPERATIONS = [
  ["entitlement.create", "Create"], ["entitlement.amend", "Amend"], ["entitlement.attach_extended", "Attach extended or add-on"],
  ["entitlement.supersede", "Supersede"], ["entitlement.void", "Void"], ["entitlement.close", "Close"], ["entitlement.backfill", "Backfill"],
] as const;

export function Component() {
  const actor = useActor();
  const [params] = useSearchParams();
  const stored = useMemo(() => params.get("approval") ? loadApprovals().find((row) => row.id === params.get("approval")) ?? null : null, [params]);
  const initialSerials = params.get("serials") ?? (stored?.source.serials?.join("\n") ?? "");
  const [kind, setKind] = useState(stored?.source.kind ?? (initialSerials ? "pasted_list" : "document"));
  const [operation, setOperation] = useState(stored?.operation ?? params.get("operation") ?? "entitlement.supersede");
  const [documentType, setDocumentType] = useState(stored?.source.document?.type ?? "sales_order");
  const [documentId, setDocumentId] = useState(stored?.source.document?.id ?? "");
  const [partyRef, setPartyRef] = useState(stored?.source.partyRef ?? "");
  const [itemId, setItemId] = useState(stored?.source.itemId ?? "");
  const [from, setFrom] = useState(stored?.source.from?.slice(0, 10) ?? "");
  const [to, setTo] = useState(stored?.source.to?.slice(0, 10) ?? "");
  const [serials, setSerials] = useState(initialSerials);
  const [scheme, setScheme] = useState(stored?.source.scheme ?? "default");
  const [rangeStart, setRangeStart] = useState(stored?.source.rangeStart ?? "");
  const [rangeEnd, setRangeEnd] = useState(stored?.source.rangeEnd ?? "");
  const [family, setFamily] = useState(params.get("family") ?? stored?.arguments.extra?.family ?? "base_warranty");
  const [policyCode, setPolicyCode] = useState(stored?.arguments.extra?.policy_code ?? "");
  const [effectiveDate, setEffectiveDate] = useState(stored?.arguments.extra?.effective_date ?? new Date().toISOString().slice(0, 10));
  const [contractDate, setContractDate] = useState(stored?.arguments.extra?.contract_date ?? new Date().toISOString().slice(0, 10));
  const [obligorEntityId, setObligorEntityId] = useState(stored?.arguments.extra?.obligor_entity_id ?? DEFAULT_LEGAL_ENTITY);
  const [newEnd, setNewEnd] = useState(stored?.arguments.extra?.new_end ?? "");
  const [termValue, setTermValue] = useState(stored?.arguments.extra?.new_term_value ?? "");
  const [termUnit, setTermUnit] = useState(stored?.arguments.extra?.new_term_unit ?? "months");
  const [price, setPrice] = useState(stored?.arguments.extra?.price ?? "");
  const [currency, setCurrency] = useState(stored?.arguments.extra?.currency ?? "");
  const [evidence, setEvidence] = useState(stored?.arguments.extra?.evidence_refs ?? "");
  const [reason, setReason] = useState(stored?.arguments.reason ?? "purchase");
  const [sourceType, setSourceType] = useState(stored?.arguments.sourceDocument.type ?? "invoice");
  const [sourceId, setSourceId] = useState(stored?.arguments.sourceDocument.id ?? "");
  const [notification, setNotification] = useState(stored?.arguments.extra?.customer_notification_ref ?? "");
  const [financeReview, setFinanceReview] = useState(stored?.arguments.extra?.finance_review_ref ?? "");
  const [enteredValue, setEnteredValue] = useState(stored?.arguments.extra?.entered_value ?? "");
  const [note, setNote] = useState(stored?.arguments.note ?? "");
  const [families, setFamilies] = useState<CoverageFamily[]>([]);
  const [policies, setPolicies] = useState<PolicySummary[]>([]);
  const [selectionId, setSelectionId] = useState<string | null>(stored?.selectionId ?? null);
  const [resolvedCount, setResolvedCount] = useState(stored?.total ?? 0);
  const [preview, setPreview] = useState<BulkPreviewResult | null>(null);
  const [find, setFind] = useState("");
  const [outcome, setOutcome] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [executed, setExecuted] = useState<BulkExecutionResult | null>(null);
  const [active, setActive] = useState<string | null>(null);

  useEffect(() => { Promise.all([entitlementApi.families(), entitlementApi.policies(), entitlementApi.currency()]).then(([f, p, c]) => { setFamilies(f); setPolicies(p); if (!currency && c.currency) setCurrency(c.currency); }).catch(setError); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const source = useMemo<SelectionSource>(() => ({
    kind,
    document: kind === "document" && GUID.test(documentId) ? { type: documentType, id: documentId } : null,
    partyRef: kind === "customer" && GUID.test(partyRef) ? partyRef : null,
    itemId: kind === "item" && GUID.test(itemId) ? itemId : null,
    from: kind === "date_range" && from ? new Date(`${from}T00:00:00Z`).toISOString() : null,
    to: kind === "date_range" && to ? new Date(`${to}T23:59:59Z`).toISOString() : null,
    eventType: null,
    serials: kind === "pasted_list" ? splitSerials(serials) : null,
    scheme: kind === "serial_range" ? scheme : null,
    rangeStart: kind === "serial_range" ? rangeStart : null,
    rangeEnd: kind === "serial_range" ? rangeEnd : null,
  }), [kind, documentType, documentId, partyRef, itemId, from, to, serials, scheme, rangeStart, rangeEnd]);

  const extra = useMemo<Record<string, string>>(() => {
    const values: Record<string, string> = { family };
    if (policyCode) values.policy_code = policyCode;
    if (effectiveDate) values.effective_date = effectiveDate;
    if (contractDate) values.contract_date = contractDate;
    if (obligorEntityId) values.obligor_entity_id = obligorEntityId;
    if (newEnd) values.new_end = newEnd;
    if (termValue) { values.new_term_value = termValue; values.new_term_unit = termUnit; }
    if (price) values.price = price;
    if (currency) values.currency = currency;
    if (evidence) values.evidence_refs = evidence;
    if (notification) values.customer_notification_ref = notification;
    if (financeReview) values.finance_review_ref = financeReview;
    if (enteredValue) values.entered_value = enteredValue;
    return values;
  }, [family, policyCode, effectiveDate, contractDate, obligorEntityId, newEnd, termValue, termUnit, price, currency, evidence, notification, financeReview, enteredValue]);
  const args = useMemo<BulkOperationArguments | null>(() => GUID.test(sourceId) ? { eventTime: now(), actorRef: actor, legalEntityId: DEFAULT_LEGAL_ENTITY, sourceDocument: { type: sourceType, id: sourceId }, reason, note: note || null, extra } : null, [sourceId, actor, sourceType, reason, note, extra]);
  const sourceReady = (kind === "document" && GUID.test(documentId)) || (kind === "customer" && GUID.test(partyRef)) || (kind === "item" && GUID.test(itemId)) || (kind === "date_range" && !!from && !!to) || (kind === "pasted_list" && splitSerials(serials).length > 0) || (kind === "serial_range" && !!rangeStart && !!rangeEnd);

  const resolve = async () => {
    setBusy(true); setError(null); setPreview(null); setExecuted(null);
    try { const result = await entitlementApi.resolveSelection(source, actor, newCommandId()); setSelectionId(result.selectionId); setResolvedCount(result.resolved); setPage(1); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };

  useEffect(() => {
    if (!selectionId || !args) return;
    const controller = new AbortController();
    setLoading(true); setError(null);
    entitlementApi.previewSelection(selectionId, operation, args, (page - 1) * PAGE, PAGE, find || null, outcome, controller.signal)
      .then((result) => {
        setPreview(result); setLoading(false);
        const blocked = result.page.filter((row) => row.outcome === "block" && row.reason === "approval_required");
        if (blocked.length) saveApproval({ id: approvalId(selectionId, operation), selectionId, operation, source, arguments: args, blocked, total: result.total, createdAt: new Date().toISOString() });
      })
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) { setError(reason); setLoading(false); } });
    return () => controller.abort();
  }, [selectionId, operation, args, page, find, outcome, source]);

  const execute = async () => {
    if (!selectionId || !args) return;
    setBusy(true); setError(null);
    try { const result = await entitlementApi.executeSelection(selectionId, operation, args, newCommandId()); setExecuted(result); if (stored) removeApproval(stored.id); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };

  const openOutcome = (row: UnitOutcome) => {
    const detail = previewDetail(row.detail);
    setActive(row.unitId);
    openDrawer({ eyebrow: `Selected unit · ${humanise(row.outcome)}`, title: row.serial ?? shortId(row.unitId), mono: true }, <><h3>Outcome</h3><Kv rows={[["Operation", operation], ["Outcome", humanise(row.outcome)], ["Rule", row.reason ?? "—"], ["Detail", detail?.summary ?? row.detail ?? "—"], ["Now", detail?.now ?? "—"], ["After", detail?.after ?? "unchanged"], ["Days", detail?.days == null ? "—" : signed(detail.days)], ["Unit", shortId(row.unitId, 12)]]} /><div className="act"><Link className="btn primary block" to={`/units/${row.unitId}`}>Open unit</Link></div></>);
  };
  const columns: GridColumn<UnitOutcome>[] = [
    { key: "serial", label: "Serial", width: 17, mono: true, render: (row) => row.serial ?? shortId(row.unitId) },
    { key: "now", label: `Now in ${family}`, width: 25, render: (row) => { const value = previewDetail(row.detail); return <>{value?.now ?? (row.outcome === "skip" ? "none live" : "—")}<span className="sub">{value?.nowEnd ? `ends ${value.nowEnd}` : shortId(row.unitId, 12)}</span></>; } },
    { key: "after", label: "After", width: 20, render: (row) => { const value = previewDetail(row.detail); return <>{value?.after ?? "unchanged"}<span className="sub">{value?.afterEnd ? `ends ${value.afterEnd}` : ""}</span></>; } },
    { key: "days", label: "Days", width: 10, numeric: true, render: (row) => { const days = previewDetail(row.detail)?.days; return days == null ? "—" : signed(days); } },
    { key: "outcome", label: "Outcome", width: 28, render: (row) => { const value = previewDetail(row.detail); return <><StatusBadge tone={outcomeTone(row.outcome)}>{row.outcome === "change" ? "Will change" : `${humanise(row.outcome)}${row.reason ? ` · ${humanise(row.reason)}` : ""}`}</StatusBadge>{value?.summary && <span className="sub">{value.summary}</span>}</>; } },
  ];

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Entitlements", to: "/entitlements/gaps" }, { label: "Bulk operation" }]} />
      <div className="page-header"><div><div className="eyebrow">Bulk command · {operation} · preview, then execute</div><h1>{OPERATIONS.find(([id]) => id === operation)?.[1] ?? "Bulk entitlement operation"}</h1><p className="sub">{selectionId ? `Selection ${shortId(selectionId)} · ${resolvedCount} units` : "Address a selection set"} · one transaction, one source document, one approval</p></div><div className="actions"><Link className="btn ghost" to="/entitlements/gaps">Discard</Link><button className="btn" type="button" disabled={!sourceReady || busy} aria-busy={busy} onClick={resolve}>Resolve selection</button><button className="btn primary" type="button" disabled={!preview || !args || preview.change === 0 || preview.block > 0 || busy || !!executed} aria-busy={busy} onClick={execute}>{stored ? "Approve and execute" : "Execute"} · {preview?.change ?? 0}<kbd className="key">⌘↵</kbd></button></div></div>

      <section className="card" style={{ marginBottom: "var(--gap)" }}><div className="card-body" style={{ paddingTop: 10, paddingBottom: 10 }}><div className="segmented plain" role="group" aria-label="Operation" style={{ flexWrap: "wrap" }}>{OPERATIONS.map(([id, label]) => <button type="button" key={id} aria-pressed={operation === id} onClick={() => { setOperation(id); setPreview(null); }}>{label}</button>)}</div></div></section>
      <div className="cols-2-3">
        <section className="card" aria-labelledby="bulk-set"><div className="card-header"><h2 id="bulk-set">Selection set and change</h2><span className="meta">Step 1 of 2</span></div><div className="card-body"><div className="form-grid">
          <div className="field span"><span className="lbl">Address units by</span><div className="segmented plain" role="group" aria-label="Selection source" style={{ flexWrap: "wrap" }}>{KINDS.map(([id, label]) => <button type="button" key={id} aria-pressed={kind === id} onClick={() => setKind(id)}>{label}</button>)}</div></div>
          {kind === "document" && <><Field label="Document type" id="bulk-doc-type" value={documentType} onChange={setDocumentType} /><Field label="Document id" id="bulk-doc-id" value={documentId} onChange={setDocumentId} mono placeholder="UUID" /></>}
          {kind === "customer" && <Field label="Customer party ref" id="bulk-party" value={partyRef} onChange={setPartyRef} mono placeholder="UUID" span />}
          {kind === "item" && <Field label="Item id" id="bulk-item" value={itemId} onChange={setItemId} mono placeholder="UUID" span />}
          {kind === "date_range" && <><Field label="From" id="bulk-from" value={from} onChange={setFrom} type="date" /><Field label="To" id="bulk-to" value={to} onChange={setTo} type="date" /></>}
          {kind === "pasted_list" && <div className="field span"><label htmlFor="bulk-serials">Serials <span className="hint">· {splitSerials(serials).length} distinct</span></label><textarea id="bulk-serials" className="textarea serial" value={serials} onChange={(event) => setSerials(event.target.value)} /></div>}
          {kind === "serial_range" && <><Field label="Scheme" id="bulk-scheme" value={scheme} onChange={setScheme} /><div /><Field label="From" id="bulk-range-from" value={rangeStart} onChange={setRangeStart} mono /><Field label="To" id="bulk-range-to" value={rangeEnd} onChange={setRangeEnd} mono /></>}
          {selectionId && <div className="span note ok"><strong>Resolved.</strong>&nbsp;{resolvedCount} units are frozen in selection {shortId(selectionId)}.</div>}
          <div className="field"><label htmlFor="bulk-family">Coverage family</label><select id="bulk-family" className="select" value={family} onChange={(event) => setFamily(event.target.value)}>{families.map((row) => <option value={row.familyCode} key={row.familyCode}>{row.name} · {row.familyCode}</option>)}</select></div>
          {(operation === "entitlement.create" || operation === "entitlement.supersede" || operation === "entitlement.attach_extended") && <div className="field"><label htmlFor="bulk-policy">Policy</label><select id="bulk-policy" className="select" value={policyCode} onChange={(event) => setPolicyCode(event.target.value)}><option value="">Latest applicable policy</option>{policies.filter((row) => row.coverageFamily === family).map((row) => <option value={row.policyCode} key={row.policyCode}>{row.name} · {row.policyCode}</option>)}</select></div>}
          {["entitlement.create", "entitlement.attach_extended"].includes(operation) && <><Field label="Contract date" id="bulk-contract" value={contractDate} onChange={setContractDate} type="date" /><Field label="Obligor legal entity" id="bulk-obligor" value={obligorEntityId} onChange={setObligorEntityId} mono placeholder="UUID" /></>}
          {["entitlement.supersede", "entitlement.void", "entitlement.close"].includes(operation) && <Field label="Effective date" id="bulk-effective" value={effectiveDate} onChange={setEffectiveDate} type="date" />}
          {operation === "entitlement.amend" && <><Field label="New end date" id="bulk-end" value={newEnd} onChange={setNewEnd} type="date" /><Field label="New term value" id="bulk-term" value={termValue} onChange={setTermValue} type="number" /><div className="field"><label htmlFor="bulk-term-unit">Term unit</label><select id="bulk-term-unit" className="select" value={termUnit} onChange={(event) => setTermUnit(event.target.value)}><option value="months">months</option><option value="days">days</option></select></div><Field label="Entered amendment value" id="bulk-entered-value" value={enteredValue} onChange={setEnteredValue} type="number" /></>}
          {operation === "entitlement.attach_extended" && <><Field label="Price per unit" id="bulk-price" value={price} onChange={setPrice} type="number" /><Field label="Currency" id="bulk-currency" value={currency} onChange={setCurrency} /></>}
          {operation === "entitlement.backfill" && <Field label="Obligor legal entity" id="bulk-backfill-obligor" value={obligorEntityId} onChange={setObligorEntityId} mono placeholder="UUID" />}
          {["entitlement.backfill", "entitlement.void"].includes(operation) && <Field label="Evidence refs" id="bulk-evidence" value={evidence} onChange={setEvidence} placeholder="Comma-separated" span />}
          <Field label="Reason code" id="bulk-reason" value={reason} onChange={setReason} />
          <Field label="Source document type" id="bulk-source-type" value={sourceType} onChange={setSourceType} />
          <Field label="Source document id" id="bulk-source-id" value={sourceId} onChange={setSourceId} mono placeholder="UUID · required for preview" />
          <Field label="Customer notification ref" id="bulk-notification" value={notification} onChange={setNotification} />
          <Field label="Finance review ref" id="bulk-finance" value={financeReview} onChange={setFinanceReview} />
          <div className="field span"><label htmlFor="bulk-note">Note</label><textarea id="bulk-note" className="textarea" value={note} onChange={(event) => setNote(event.target.value)} /></div>
          {executed && <div className="span note ok"><strong>Executed{executed.replayed ? " (replay)" : ""}.</strong>&nbsp;{executed.done} done, {executed.skipped} skipped, {executed.blocked} blocked · report {shortId(executed.validationReportId)}.</div>}
          <ErrorNote error={error} />
        </div></div></section>
        <section className="card" aria-labelledby="bulk-preview"><div className="card-header"><h2 id="bulk-preview">Preview <span className="meta">computed by the command, not the screen</span></h2><span className="meta">Step 2 of 2</span></div>
          <div className="preview-summary"><Figure label="Resolved" value={preview?.total ?? resolvedCount} /><Figure label="Will change" value={preview?.change ?? 0} tone="ok" /><Figure label="Will skip" value={preview?.skip ?? 0} tone="warn" /><Figure label="Blocked" value={preview?.block ?? 0} tone="bad" /></div>
          <div className="findbar compact"><input className="input" aria-label="Find within preview" placeholder="Find serial" value={find} onChange={(event) => { setFind(event.target.value); setPage(1); }} />{[[null, `All ${preview?.total ?? resolvedCount}`], ["change", `Change ${preview?.change ?? 0}`], ["skip", `Skip ${preview?.skip ?? 0}`], ["block", `Block ${preview?.block ?? 0}`]].map(([value, label]) => <button type="button" className={`chip${outcome === value ? " on" : ""}`} key={value ?? "all"} onClick={() => { setOutcome(value ?? null); setPage(1); }}>{label}</button>)}</div>
          {preview?.page.some((row) => row.outcome === "block" && row.reason === "approval_required") && <div className="note warn bulk-approval"><strong>Approval required.</strong>&nbsp;One or more changing units need a higher role. The exact rehearsal and refusal rule have been saved for the approval inbox.</div>}
          <DataGrid columns={columns} page={{ rows: preview?.page ?? [], total: preview?.total ?? 0, page, pageSize: PAGE, continuation: null }} rowId={(row) => row.unitId} onPage={setPage} onRowClick={openOutcome} activeRowId={active} loading={loading} emptyText={selectionId ? "No units match this preview filter." : "Resolve a selection to preview every unit."} viewportHeight={520} />
        </section>
      </div>
    </main>
    <Rail label="Execution"><Section title="Routing"><Kv rows={[["Initiator", actor], ["Approver", stored ? "signed-in manager" : "judged from signed-in roles"], ["Source document", sourceId ? `${sourceType} ${shortId(sourceId)}` : "—"], ["Transaction", "1 · all or nothing"], ["Selection", selectionId ? shortId(selectionId) : "—"]]} /></Section><Section title="Rules applied"><p className="muted">The server rehearses the real command for every unit and rolls it back. Execution uses the same arguments in one transaction. A blocked approval is saved locally so a manager can reopen this exact preview.</p></Section></Rail>
  </>;
}

function Field({ label, id, value, onChange, type = "text", mono, placeholder, span }: { label: string; id: string; value: string; onChange: (value: string) => void; type?: string; mono?: boolean; placeholder?: string; span?: boolean }) {
  return <div className={`field${span ? " span" : ""}`}><label htmlFor={id}>{label}</label><input id={id} className={`input${mono ? " serial" : ""}`} type={type} value={value} onChange={(event) => onChange(event.target.value)} placeholder={placeholder} /></div>;
}

function Figure({ label, value, tone }: { label: string; value: number; tone?: "ok" | "warn" | "bad" }) { return <div className="figure"><span className="k">{label}</span><span className={`v${tone ? ` ${tone}-text` : ""}`}>{value.toLocaleString()}</span></div>; }

interface PreviewDetail { now: string | null; nowEnd: string | null; after: string | null; afterEnd: string | null; days: number | null; summary: string | null }
function previewDetail(value: string | null): PreviewDetail | null { if (!value?.startsWith("{")) return null; try { const raw = JSON.parse(value) as Record<string, unknown>; return { now: text(raw.now), nowEnd: text(raw.now_end ?? raw.nowEnd), after: text(raw.after), afterEnd: text(raw.after_end ?? raw.afterEnd), days: typeof raw.days === "number" ? raw.days : null, summary: text(raw.summary) }; } catch { return null; } }
function text(value: unknown): string | null { return typeof value === "string" ? value : null; }
function signed(value: number): string { return value > 0 ? `+${value.toLocaleString()}` : value.toLocaleString(); }
