import { useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router";
import type { BulkExecutionResult, BulkOperationArguments, BulkPreviewResult, SelectionSource, UnitOutcome } from "../api/units";
import { DEFAULT_LEGAL_ENTITY, newCommandId, now, ranges, type SerialSchemeRow } from "../api/units";
import { DocumentField, ItemField, LegalEntityField, PartyField, useReferenceNames } from "../corrections/ReferenceFields";
import { DataGrid, Shortcut, StatusBadge, useModEnter, type GridColumn } from "../components";
import { openDrawer } from "../shell/drawerStore";
import { Rail } from "../shell/Drawer";
import { useHandoff, useLegacyParam } from "../shell/handoff";
import { Breadcrumbs } from "../shell/PageHeader";
import { usePeople } from "../people/people";
import { ErrorNote, Kv, Section, useActor } from "../units/common";
import { humanise, outcomeTone, shortId, splitSerials } from "../units/format";
import { entitlementApi } from "./api";
import type { BulkApprovalRequest, CoverageFamily, PolicySummary } from "./types";

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const PAGE = 50;
const KINDS = [["document", "Document"], ["customer", "Customer"], ["item", "Item"], ["date_range", "Date range"], ["pasted_list", "Pasted list"], ["serial_range", "Serial range"]] as const;
const OPERATIONS = [
  ["entitlement.create", "Create"], ["entitlement.amend", "Amend"], ["entitlement.attach_extended", "Attach extended or add-on"],
  ["entitlement.supersede", "Supersede"], ["entitlement.void", "Void"], ["entitlement.close", "Close"], ["entitlement.backfill", "Backfill"],
] as const;
/** Documents whose units a selection can address, and documents a bulk change can cite as its source. Codes are the recorded event document types (DocumentPrefix in Ports/DefaultReferencePorts.cs). */
const SELECTION_DOCUMENTS = ["shipment", "sales_order", "invoice", "production_order", "receipt", "return"] as const;
const SOURCE_DOCUMENTS = ["invoice", "sales_order", "shipment"] as const;
/** AmendmentReason.All in Yojana.Serialisation.Contracts/EntitlementVocabulary.cs: the reasons an amendment policy prices. */
export const BULK_REASONS = [
  ["purchase", "Purchase"], ["goodwill", "Goodwill"], ["correction", "Correction"], ["statutory_extension", "Statutory extension"],
  ["dispute_settlement", "Dispute settlement"], ["seller_error", "Seller error"],
] as const;

export function Component() {
  const actor = useActor();
  const [params] = useSearchParams();
  // ?approval=<requestId>: the approver's view of a stored request (P2-T21). Its arguments are fixed; the preview is judged with the approver's roles.
  const requestId = params.get("approval");
  const [request, setRequest] = useState<BulkApprovalRequest | null>(null);
  const [raised, setRaised] = useState<BulkApprovalRequest | null>(null);
  const person = usePeople([actor, request?.requestedBy, request?.decidedBy]);
  const [decisionNote, setDecisionNote] = useState("");
  const [approveCommandId] = useState(newCommandId);
  useLegacyParam("serials", "serials");
  const initialSerials = useHandoff().serials ?? "";
  const [kind, setKind] = useState((initialSerials ? "pasted_list" : "document"));
  const [operation, setOperation] = useState(params.get("operation") ?? "entitlement.supersede");
  const [documentType, setDocumentType] = useState<string>(SELECTION_DOCUMENTS[0]);
  const [documentId, setDocumentId] = useState("");
  const [partyRef, setPartyRef] = useState("");
  const [itemId, setItemId] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [serials, setSerials] = useState(initialSerials);
  useEffect(() => { if (initialSerials) { setKind("pasted_list"); setSerials(initialSerials); } }, [initialSerials]);
  const [scheme, setScheme] = useState("default");
  const [rangeStart, setRangeStart] = useState("");
  const [rangeEnd, setRangeEnd] = useState("");
  const [family, setFamily] = useState(params.get("family") ?? "base_warranty");
  const [policyCode, setPolicyCode] = useState("");
  const [effectiveDate, setEffectiveDate] = useState(new Date().toISOString().slice(0, 10));
  const [contractDate, setContractDate] = useState(new Date().toISOString().slice(0, 10));
  const [obligorEntityId, setObligorEntityId] = useState(DEFAULT_LEGAL_ENTITY);
  const [newEnd, setNewEnd] = useState("");
  const [termValue, setTermValue] = useState("");
  const [termUnit, setTermUnit] = useState("months");
  const [price, setPrice] = useState("");
  const [currency, setCurrency] = useState("");
  const [evidence, setEvidence] = useState("");
  const [reason, setReason] = useState("purchase");
  const [sourceType, setSourceType] = useState("invoice");
  const [sourceId, setSourceId] = useState("");
  const names = useReferenceNames({ documents: [sourceId ? { type: sourceType, id: sourceId } : null] });
  const [notification, setNotification] = useState("");
  const [financeReview, setFinanceReview] = useState("");
  const [enteredValue, setEnteredValue] = useState("");
  const [note, setNote] = useState("");
  const [families, setFamilies] = useState<CoverageFamily[]>([]);
  const [schemes, setSchemes] = useState<SerialSchemeRow[]>([]);
  const [policies, setPolicies] = useState<PolicySummary[]>([]);
  const [selectionId, setSelectionId] = useState<string | null>(null);
  const [resolvedCount, setResolvedCount] = useState(0);
  const [preview, setPreview] = useState<BulkPreviewResult | null>(null);
  const [find, setFind] = useState("");
  const [outcome, setOutcome] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [executed, setExecuted] = useState<BulkExecutionResult | null>(null);
  const [active, setActive] = useState<string | null>(null);

  useEffect(() => {
    if (!requestId) return;
    const controller = new AbortController();
    entitlementApi.approvalRequest(requestId, controller.signal).then((row) => {
      const x = row.arguments.extra ?? {};
      setRequest(row); setOperation(row.operation); setSelectionId(row.selectionId); setResolvedCount(row.total);
      setFamily(x.family ?? "base_warranty"); setPolicyCode(x.policy_code ?? ""); setEffectiveDate(x.effective_date ?? ""); setContractDate(x.contract_date ?? "");
      setObligorEntityId(x.obligor_entity_id ?? ""); setNewEnd(x.new_end ?? ""); setTermValue(x.new_term_value ?? ""); setTermUnit(x.new_term_unit ?? "months");
      setPrice(x.price ?? ""); setCurrency(x.currency ?? ""); setEvidence(x.evidence_refs ?? ""); setNotification(x.customer_notification_ref ?? "");
      setFinanceReview(x.finance_review_ref ?? ""); setEnteredValue(x.entered_value ?? ""); setReason(row.arguments.reason ?? ""); setNote(row.arguments.note ?? "");
      setSourceType(row.sourceDocument.type); setSourceId(row.sourceDocument.id);
    }).catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    return () => controller.abort();
  }, [requestId]);

  useEffect(() => { Promise.all([entitlementApi.families(), entitlementApi.policies(), entitlementApi.currency(), ranges.schemes()]).then(([f, p, c, k]) => { setFamilies(f); setPolicies(p); setSchemes(k); if (!currency && c.currency) setCurrency(c.currency); }).catch(setError); }, []); // eslint-disable-line react-hooks/exhaustive-deps

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
  const formArgs = useMemo<BulkOperationArguments | null>(() => GUID.test(sourceId) ? { eventTime: now(), actorRef: actor, legalEntityId: DEFAULT_LEGAL_ENTITY, sourceDocument: { type: sourceType, id: sourceId }, reason, note: note || null, extra } : null, [sourceId, actor, sourceType, reason, note, extra]);
  // A stored request executes its own arguments, never the form's.
  const args = request ? request.arguments : formArgs;
  const pending = request?.status === "pending";
  const approvalBlocked = !!preview && preview.block > 0 && !request;
  const sourceReady = (kind === "document" && GUID.test(documentId)) || (kind === "customer" && GUID.test(partyRef)) || (kind === "item" && GUID.test(itemId)) || (kind === "date_range" && !!from && !!to) || (kind === "pasted_list" && splitSerials(serials).length > 0) || (kind === "serial_range" && !!rangeStart && !!rangeEnd);

  const resolve = async () => {
    setBusy(true); setError(null); setPreview(null); setExecuted(null); setRaised(null);
    try { const result = await entitlementApi.resolveSelection(source, actor, newCommandId()); setSelectionId(result.selectionId); setResolvedCount(result.resolved); setPage(1); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };

  useEffect(() => {
    if (!selectionId || !args) return;
    const controller = new AbortController();
    setLoading(true); setError(null);
    entitlementApi.previewSelection(selectionId, operation, args, (page - 1) * PAGE, PAGE, find || null, outcome, controller.signal)
      .then((result) => { setPreview(result); setLoading(false); })
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) { setError(reason); setLoading(false); } });
    return () => controller.abort();
  }, [selectionId, operation, args, page, find, outcome]);

  const execute = async () => {
    if (!selectionId || !args) return;
    setBusy(true); setError(null);
    try { const result = await entitlementApi.executeSelection(selectionId, operation, args, newCommandId()); setExecuted(result); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };

  const raise = async () => {
    if (!selectionId || !args) return;
    setBusy(true); setError(null);
    try { setRaised(await entitlementApi.raiseApproval(selectionId, operation, args, note || null, newCommandId())); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const approve = async () => {
    if (!request) return;
    setBusy(true); setError(null);
    try { setRequest(await entitlementApi.approveRequest(request.requestId, decisionNote || null, approveCommandId)); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const reject = async () => {
    if (!request) return;
    setBusy(true); setError(null);
    try { setRequest(await entitlementApi.rejectRequest(request.requestId, decisionNote)); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  useModEnter(request ? approve : approvalBlocked ? raise : execute, request
    ? !pending || !preview || preview.block > 0 || busy
    : approvalBlocked
      ? !args || busy || !!raised
      : !preview || !args || preview.change === 0 || preview.block > 0 || busy || !!executed);

  const openOutcome = (row: UnitOutcome) => {
    const detail = previewDetail(row.detail);
    setActive(row.unitId);
    openDrawer({ eyebrow: `Selected unit · ${humanise(row.outcome)}`, title: row.serial ?? shortId(row.unitId), mono: true }, <><h3>Outcome</h3><Kv rows={[["Operation", operation], ["Outcome", humanise(row.outcome)], ["Rule", row.reason ?? "—"], ["Detail", detail?.summary ?? row.detail ?? "—"], ["Now", detail?.now ?? "—"], ["After", detail?.after ?? "unchanged"], ["Days", detail?.days == null ? "—" : signed(detail.days)], ["Unit", shortId(row.unitId, 12)]]} /><div className="act"><Link className="btn primary block" to={`/units/${row.unitId}`}>Open unit</Link></div></>);
  };
  const columns: GridColumn<UnitOutcome>[] = [
    { key: "serial", label: "Serial", width: 17, mono: true, render: (row) => row.serial ?? shortId(row.unitId) },
    { key: "now", label: `Now in ${families.find((row) => row.familyCode === family)?.name.toLowerCase() ?? humanise(family).toLowerCase()}`, width: 25, render: (row) => { const value = previewDetail(row.detail); return <>{value?.now ?? (row.outcome === "skip" ? "none live" : "—")}<span className="sub">{value?.nowEnd ? `ends ${value.nowEnd}` : shortId(row.unitId, 12)}</span></>; } },
    { key: "after", label: "After", width: 20, render: (row) => { const value = previewDetail(row.detail); return <>{value?.after ?? "unchanged"}<span className="sub">{value?.afterEnd ? `ends ${value.afterEnd}` : ""}</span></>; } },
    { key: "days", label: "Days", width: 10, numeric: true, render: (row) => { const days = previewDetail(row.detail)?.days; return days == null ? "—" : signed(days); } },
    { key: "outcome", label: "Outcome", width: 28, render: (row) => { const value = previewDetail(row.detail); return <><StatusBadge tone={outcomeTone(row.outcome)}>{row.outcome === "change" ? "Will change" : `${humanise(row.outcome)}${row.reason ? ` · ${humanise(row.reason)}` : ""}`}</StatusBadge>{value?.summary && <span className="sub">{value.summary}</span>}</>; } },
  ];

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Entitlements", to: "/entitlements/gaps" }, { label: "Bulk operation" }]} />
      <div className="page-header"><div><div className="eyebrow">Bulk change · preview, then execute</div><h1>{OPERATIONS.find(([id]) => id === operation)?.[1] ?? "Bulk entitlement operation"}</h1><p className="sub">{selectionId ? `Selection ${shortId(selectionId)} · ${resolvedCount} units` : "Address a selection set"} · one transaction, one source document, one approval</p></div><div className="actions"><Link className="btn ghost" to="/entitlements/gaps">Discard</Link>{request ? <>
        <button className="btn" type="button" disabled={!pending || !decisionNote.trim() || busy} aria-busy={busy} onClick={reject}>Reject</button>
        <button className="btn primary" type="button" disabled={!pending || !preview || preview.block > 0 || busy} aria-busy={busy} onClick={approve}>Approve and execute · {preview?.change ?? 0}<Shortcut keys="mod+enter" /></button>
      </> : <>
        <button className="btn" type="button" disabled={!sourceReady || busy} aria-busy={busy} onClick={resolve}>Resolve selection</button>
        {approvalBlocked ? <button className="btn primary" type="button" disabled={!args || busy || !!raised} aria-busy={busy} onClick={raise}>Request approval · {preview?.block ?? 0}<Shortcut keys="mod+enter" /></button>
          : <button className="btn primary" type="button" disabled={!preview || !args || preview.change === 0 || preview.block > 0 || busy || !!executed} aria-busy={busy} onClick={execute}>Execute · {preview?.change ?? 0}<Shortcut keys="mod+enter" /></button>}
      </>}</div></div>

      <section className="card" style={{ marginBottom: "var(--gap)" }}><div className="card-body" style={{ paddingTop: 10, paddingBottom: 10 }}><div className="segmented plain" role="group" aria-label="Operation" style={{ flexWrap: "wrap" }}>{OPERATIONS.map(([id, label]) => <button type="button" key={id} aria-pressed={operation === id} disabled={!!request} onClick={() => { setOperation(id); setPreview(null); }}>{label}</button>)}</div></div></section>
      <div className="cols-2-3">
        <section className="card" aria-labelledby="bulk-set"><div className="card-header"><h2 id="bulk-set">Selection set and change</h2><span className="meta">Step 1 of 2</span></div><div className="card-body"><div className="form-grid">
          {/* A stored request's selection and arguments are fixed: the approver reads them, never edits them. */}
          <fieldset disabled={!!request} style={{ display: "contents" }}>
          {!request && <><div className="field span"><span className="lbl">Address units by</span><div className="segmented plain" role="group" aria-label="Selection source" style={{ flexWrap: "wrap" }}>{KINDS.map(([id, label]) => <button type="button" key={id} aria-pressed={kind === id} onClick={() => setKind(id)}>{label}</button>)}</div></div>
          {kind === "document" && <div className="span"><DocumentField id="bulk-doc" label="Document" types={SELECTION_DOCUMENTS} value={documentId ? { type: documentType, id: documentId } : null} onChange={(value) => { if (value) setDocumentType(value.type); setDocumentId(value?.id ?? ""); }} hint="every unit the document touched" /></div>}
          {kind === "customer" && <div className="span"><PartyField id="bulk-party" label="Customer" value={partyRef || null} onChange={(value) => setPartyRef(value ?? "")} hint="units the customer owns" /></div>}
          {kind === "item" && <div className="span"><ItemField id="bulk-item" label="Item" value={itemId || null} onChange={(value) => setItemId(value ?? "")} /></div>}
          {kind === "date_range" && <><Field label="From" id="bulk-from" value={from} onChange={setFrom} type="date" /><Field label="To" id="bulk-to" value={to} onChange={setTo} type="date" /></>}
          {kind === "pasted_list" && <div className="field span"><label htmlFor="bulk-serials">Serials <span className="hint">· {splitSerials(serials).length} distinct</span></label><textarea id="bulk-serials" className="textarea serial" value={serials} onChange={(event) => setSerials(event.target.value)} /></div>}
          {kind === "serial_range" && <><div className="field"><label htmlFor="bulk-scheme">Serial scheme</label><select id="bulk-scheme" className="select" value={scheme} onChange={(event) => setScheme(event.target.value)}>{!schemes.some((row) => row.code === scheme) && <option value={scheme}>{humanise(scheme)}</option>}{schemes.map((row) => <option value={row.code} key={row.code}>{humanise(row.code)} · e.g. {row.example}</option>)}</select></div><div /><Field label="From" id="bulk-range-from" value={rangeStart} onChange={setRangeStart} mono /><Field label="To" id="bulk-range-to" value={rangeEnd} onChange={setRangeEnd} mono /></>}</>}
          {request && <div className={`span note ${request.status === "approved" ? "ok" : request.status === "pending" ? "info" : "warn"}`}><strong>{request.status === "pending" ? "Waiting for your decision." : `Request ${request.status}.`}</strong>&nbsp;{request.status === "stale" ? "The units changed after it was raised, so nothing was executed. Preview again and raise a new request." : request.status === "pending" ? `Raised by ${person(request.requestedBy)} · ${request.block} units need approval. The arguments are fixed by the request.` : `${request.decidedBy ? person(request.decidedBy) : ""}${request.decisionNote ? ` · ${request.decisionNote}` : ""}`}</div>}
          {selectionId && <div className="span note ok"><strong>{request ? "Requested selection." : "Resolved."}</strong>&nbsp;{resolvedCount} units are frozen in selection {shortId(selectionId)}.</div>}
          <div className="field"><label htmlFor="bulk-family">Coverage family</label><select id="bulk-family" className="select" value={family} onChange={(event) => setFamily(event.target.value)}>{families.map((row) => <option value={row.familyCode} key={row.familyCode}>{row.name}</option>)}</select></div>
          {(operation === "entitlement.create" || operation === "entitlement.supersede" || operation === "entitlement.attach_extended") && <div className="field"><label htmlFor="bulk-policy">Policy</label><select id="bulk-policy" className="select" value={policyCode} onChange={(event) => setPolicyCode(event.target.value)}><option value="">Latest applicable policy</option>{policies.filter((row) => row.coverageFamily === family).map((row) => <option value={row.policyCode} key={row.policyCode}>{row.name}</option>)}</select></div>}
          {["entitlement.create", "entitlement.attach_extended"].includes(operation) && <><Field label="Contract date" id="bulk-contract" value={contractDate} onChange={setContractDate} type="date" /><LegalEntityField id="bulk-obligor" label="Obligor legal entity" value={obligorEntityId || null} onChange={(value) => setObligorEntityId(value ?? "")} hint="the company that owes the warranty" /></>}
          {["entitlement.supersede", "entitlement.void", "entitlement.close"].includes(operation) && <Field label="Effective date" id="bulk-effective" value={effectiveDate} onChange={setEffectiveDate} type="date" />}
          {operation === "entitlement.amend" && <><Field label="New end date" id="bulk-end" value={newEnd} onChange={setNewEnd} type="date" /><Field label="New term value" id="bulk-term" value={termValue} onChange={setTermValue} type="number" /><div className="field"><label htmlFor="bulk-term-unit">Term unit</label><select id="bulk-term-unit" className="select" value={termUnit} onChange={(event) => setTermUnit(event.target.value)}><option value="months">months</option><option value="days">days</option></select></div><Field label="Entered amendment value" id="bulk-entered-value" value={enteredValue} onChange={setEnteredValue} type="number" /></>}
          {operation === "entitlement.attach_extended" && <><Field label="Price per unit" id="bulk-price" value={price} onChange={setPrice} type="number" /><Field label="Currency" id="bulk-currency" value={currency} onChange={setCurrency} /></>}
          {operation === "entitlement.backfill" && <LegalEntityField id="bulk-backfill-obligor" label="Obligor legal entity" value={obligorEntityId || null} onChange={(value) => setObligorEntityId(value ?? "")} hint="the company that owes the warranty" />}
          {["entitlement.backfill", "entitlement.void"].includes(operation) && <Field label="Evidence references" id="bulk-evidence" value={evidence} onChange={setEvidence} placeholder="Your document or case numbers, comma-separated" span />}
          <div className="field"><label htmlFor="bulk-reason">Reason</label><select id="bulk-reason" className="select" value={reason} onChange={(event) => setReason(event.target.value)}>{!BULK_REASONS.some(([code]) => code === reason) && <option value={reason}>{humanise(reason)}</option>}{BULK_REASONS.map(([code, label]) => <option value={code} key={code}>{label}</option>)}</select></div>
          <div className="span"><DocumentField id="bulk-source" label="Source document" types={SOURCE_DOCUMENTS} value={sourceId ? { type: sourceType, id: sourceId } : null} onChange={(value) => { if (value) setSourceType(value.type); setSourceId(value?.id ?? ""); }} hint="the document that justifies this change · required for preview" /></div>
          <Field label="Customer notification reference" id="bulk-notification" value={notification} onChange={setNotification} placeholder="Letter or e-mail reference" />
          <Field label="Finance review reference" id="bulk-finance" value={financeReview} onChange={setFinanceReview} placeholder="Approval or case number" />
          <div className="field span"><label htmlFor="bulk-note">Note</label><textarea id="bulk-note" className="textarea" value={note} onChange={(event) => setNote(event.target.value)} /></div>
          </fieldset>
          {request && <div className="span field"><label htmlFor="bulk-decision">Decision note <span className="hint">· required to reject</span></label><textarea id="bulk-decision" className="textarea" value={decisionNote} onChange={(event) => setDecisionNote(event.target.value)} disabled={!pending} /></div>}
          {raised && <div className="span note ok"><strong>Approval requested.</strong>&nbsp;Waiting in the <Link to="/approvals">approval inbox</Link> · request {shortId(raised.requestId)}.</div>}
          {executed && <div className="span note ok"><strong>Executed{executed.replayed ? " (replay)" : ""}.</strong>&nbsp;{executed.done} done, {executed.skipped} skipped, {executed.blocked} blocked · report {shortId(executed.validationReportId)}.</div>}
          <ErrorNote error={error} />
        </div></div></section>
        <section className="card" aria-labelledby="bulk-preview"><div className="card-header"><h2 id="bulk-preview">Preview <span className="meta">computed by the command, not the screen</span></h2><span className="meta">Step 2 of 2</span></div>
          <div className="preview-summary"><Figure label="Resolved" value={preview?.total ?? resolvedCount} /><Figure label="Will change" value={preview?.change ?? 0} tone="ok" /><Figure label="Will skip" value={preview?.skip ?? 0} tone="warn" /><Figure label="Blocked" value={preview?.block ?? 0} tone="bad" /></div>
          <div className="findbar compact"><input className="input" aria-label="Find within preview" placeholder="Find serial" value={find} onChange={(event) => { setFind(event.target.value); setPage(1); }} />{[[null, `All ${preview?.total ?? resolvedCount}`], ["change", `Change ${preview?.change ?? 0}`], ["skip", `Skip ${preview?.skip ?? 0}`], ["block", `Block ${preview?.block ?? 0}`]].map(([value, label]) => <button type="button" className={`chip${outcome === value ? " on" : ""}`} key={value ?? "all"} onClick={() => { setOutcome(value ?? null); setPage(1); }}>{label}</button>)}</div>
          {preview?.page.some((row) => row.outcome === "block" && row.reason === "approval_required") && <div className="note warn bulk-approval"><strong>Approval required.</strong>&nbsp;One or more changing units need a higher role. Request approval to send this exact preview to someone who holds it.</div>}
          <DataGrid columns={columns} page={{ rows: preview?.page ?? [], total: preview?.total ?? 0, page, pageSize: PAGE, continuation: null }} rowId={(row) => row.unitId} onPage={setPage} onRowClick={openOutcome} activeRowId={active} loading={loading} emptyText={selectionId ? "No units match this preview filter." : "Resolve a selection to preview every unit."} viewportHeight={520} />
        </section>
      </div>
    </main>
    <Rail label="Execution"><Section title="Routing"><Kv rows={[["Initiator", person(actor)], ["Approver", request ? "you · judged from your roles" : "judged from signed-in roles"], ["Source document", sourceId ? names.document({ type: sourceType, id: sourceId }) : "—"], ["Transaction", "1 · all or nothing"], ["Selection", selectionId ? shortId(selectionId) : "—"]]} /></Section><Section title="Rules applied"><p className="muted">The server rehearses the real command for every unit and rolls it back. Execution uses the same arguments in one transaction. A request for approval freezes this preview on the server; approval re-checks it and executes only if no unit has changed since.</p></Section></Rail>
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
