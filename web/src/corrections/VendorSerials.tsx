import { useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { DEFAULT_LEGAL_ENTITY, newCommandId, now } from "../api/units";
import { DataGrid, KpiStrip, Shortcut, StatusBadge, useModEnter, type GridColumn } from "../components";
import { Rail } from "../shell/Drawer";
import { openDrawer } from "../shell/drawerStore";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, UnitLink } from "../units/common";
import { fmtDateTime, humanise, sentence, shortId } from "../units/format";
import { correctionsApi } from "./api";
import { GUID, identifierLines } from "./format";
import { DocumentField, useReferenceNames, type DocumentValue } from "./ReferenceFields";
import type { AssignIdentifiers, AssignIdentifiersPreview, AssignIdentifiersResult, AwaitingVendorSerialReport, IdentifierAssignmentLineResult } from "./types";

const PAGE = 8;

export function Component() {
  const [params] = useSearchParams();
  const [productionOrderId, setProductionOrderId] = useState(params.get("productionOrder") ?? "");
  const [mode, setMode] = useState("allocation_order");
  const [text, setText] = useState(params.get("serials") ?? "");
  const [reason, setReason] = useState("vendor_list");
  const [evidence, setEvidence] = useState("");
  const [commandId, setCommandId] = useState(newCommandId);
  const [report, setReport] = useState<AwaitingVendorSerialReport | null>(null);
  const [preview, setPreview] = useState<AssignIdentifiersPreview | null>(null);
  const [committed, setCommitted] = useState<AssignIdentifiersResult | null>(null);
  const [filter, setFilter] = useState("rejected");
  const [page, setPage] = useState(1);
  const [active, setActive] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const parsed = useMemo(() => identifierLines(text, mode === "explicit_pairs"), [text, mode]);
  const productionOrder: DocumentValue | null = GUID.test(productionOrderId) ? { type: "production_order", id: productionOrderId } : null;
  const referenceNames = useReferenceNames({ documents: [productionOrder] });
  const productionOrderLabel = productionOrder ? referenceNames.document(productionOrder) : "Production order";

  const loadReport = async (id = productionOrderId) => { if (!GUID.test(id)) return; setBusy(true); setError(null); try { setReport(await correctionsApi.awaitingVendorSerial(id)); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  useEffect(() => { if (GUID.test(productionOrderId)) void loadReport(productionOrderId); }, []); // initial route handoff only
  const command = useMemo<AssignIdentifiers | null>(() => !GUID.test(productionOrderId) || parsed.lines.length === 0 || parsed.errors.length > 0 ? null : { commandId, productionOrder: { type: "production_order", id: productionOrderId }, mode, lines: parsed.lines, reason, eventTime: now(), actorRef: "", legalEntityId: DEFAULT_LEGAL_ENTITY, issuerPartyRef: null, qualifier: null, note: evidence || null }, [productionOrderId, parsed, commandId, mode, reason, evidence]);
  const runPreview = async () => { if (!command) return; setBusy(true); setError(null); setCommitted(null); try { const result = await correctionsApi.previewIdentifiers(command); setPreview(result); setPage(1); setFilter(result.lines.some((row) => row.outcome === "rejected") ? "rejected" : "assign"); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  const commit = async () => { if (!command || !preview || preview.preview.refusal || assigning === 0) return; setBusy(true); setError(null); try { const result = await correctionsApi.assignIdentifiers(command); setCommitted(result); setPreview({ ...preview, lines: result.lines, remainingCount: result.remainingCount, awaitingVendorSerialUnitIds: result.awaitingVendorSerialUnitIds, reversal: result.reversal }); setCommandId(newCommandId()); setReport(await correctionsApi.awaitingVendorSerial(productionOrderId)); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  const sourceRows = preview?.lines ?? [];
  const filtered = filter === "all" ? sourceRows : sourceRows.filter((row) => row.outcome === filter);
  const visible = filtered.slice((page - 1) * PAGE, page * PAGE);
  const rejected = sourceRows.filter((row) => row.outcome === "rejected").length;
  const assigning = sourceRows.filter((row) => row.outcome === "assign" || row.outcome === "assigned").length;
  useModEnter(commit, !command || !preview || !!preview?.preview.refusal || assigning === 0 || busy);

  const openLine = (row: IdentifierAssignmentLineResult) => { const id = String(row.index); setActive(id); const provisional = report?.units.find((unit) => unit.unitId === row.unitId)?.provisionalIdentifier; openDrawer({ eyebrow: `${row.outcome === "rejected" ? "Refused" : "Assignment"} · ${productionOrderLabel}`, title: provisional ?? row.normalisedValue ?? row.rawValue, mono: true }, <><Kv rows={[["Vendor serial", row.normalisedValue ?? row.rawValue], ["Outcome", humanise(row.outcome)], ["Rule", row.reason ? <span className="serial">{row.reason}</span> : "all checks passed"], ["Unit", row.unitId ? shortId(row.unitId, 12) : "not matched"], ["Conflicting unit", row.conflictingUnitId ? shortId(row.conflictingUnitId, 12) : "—"], ["Warnings", row.warnings?.join(" · ") ?? "none"]]} />{row.conflictingUnitId && <div className="act"><UnitLink className="btn block" unitId={row.conflictingUnitId}>Open conflicting unit</UnitLink><Link className="btn block" to={`/corrections/merge?first=${encodeURIComponent(row.unitId ?? "")}&second=${encodeURIComponent(row.conflictingUnitId)}`}>Compare in Merge units</Link></div>}</>); };
  const columns: GridColumn<IdentifierAssignmentLineResult>[] = [
    { key: "unit", label: "Unit · provisional", width: 28, mono: true, render: (row) => report?.units.find((unit) => unit.unitId === row.unitId)?.provisionalIdentifier ?? (row.unitId ? shortId(row.unitId, 12) : "—") },
    { key: "serial", label: "Vendor serial", width: 26, mono: true, render: (row) => row.normalisedValue ?? row.rawValue },
    { key: "outcome", label: "Outcome", width: 46, render: (row) => <><StatusBadge tone={row.outcome === "rejected" ? "bad" : "ok"}>{row.outcome === "rejected" ? `Refused${row.reason ? ` · ${humanise(row.reason)}` : ""}` : row.outcome === "assigned" ? "Assigned" : "Assign"}</StatusBadge>{row.warnings?.length ? <span className="sub">{row.warnings.join(" · ")}</span> : row.reason && <span className="sub">Value is never overwritten</span>}</> },
  ];

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Corrections", to: "/corrections" }, { label: "Assign vendor serials" }]} />
      <div className="page-header"><div><div className="eyebrow">Correction · vendor serial list for a production order · partial lists allowed</div><h1>Assign vendor serials</h1><p className="sub">Production order <span className="serial">{productionOrderLabel}</span> · units carry only a provisional identifier until the vendor’s list arrives</p></div><div className="actions"><Link className="btn ghost" to="/corrections">Back to workbench</Link><button type="button" className="btn" disabled={!command || busy} onClick={() => void runPreview()}>Preview list</button><button type="button" className="btn primary" disabled={!command || !preview || !!preview.preview.refusal || assigning === 0 || busy} onClick={() => void commit()}>Assign {assigning || parsed.lines.length} serials<Shortcut keys="mod+enter" /></button></div></div>
      <ErrorNote error={error} />
      {parsed.errors.length > 0 && <div className="form-error" role="alert">{parsed.errors.join(" ")}</div>}
      <div className="stack">
        <KpiStrip label="Vendor serial figures" kpis={[
          { label: "Awaiting on the order", value: report?.count.toLocaleString() ?? "—", detail: "commissioned with provisional identifiers", trend: null },
          { label: "Already assigned", value: "—", detail: "the awaiting endpoint returns open units only", trend: null },
          { label: "In this list", value: parsed.lines.length.toLocaleString(), detail: preview ? `${assigning} valid · ${rejected} refused` : "preview not run", trend: null },
          { label: "Still awaiting vendor serial", value: (preview?.remainingCount ?? report?.count ?? 0).toLocaleString(), detail: "flagged as awaiting vendor serial", trend: null },
        ]} />
        <div className="cols-2-3">
          <section className="card" aria-labelledby="vendor-list"><div className="card-header"><h2 id="vendor-list">Vendor list</h2><span className="meta">pasted · file import arrives with P7</span></div><div className="card-body"><div className="form-grid">
            <div className="span"><div className="correction-toolbar"><div style={{ flex: "1 1 280px" }}><DocumentField id="vendor-order" label="Production order" types={["production_order"]} value={productionOrder} onChange={(value) => { setProductionOrderId(value?.id ?? ""); setReport(null); setPreview(null); }} /></div><button type="button" className="btn" disabled={!productionOrder || busy} onClick={() => void loadReport()}>Load order</button></div></div>
            <div className="field span"><span className="lbl">Apply by</span><div className="segmented plain" role="group" aria-label="Apply by"><button type="button" aria-pressed={mode === "allocation_order"} onClick={() => { setMode("allocation_order"); setPreview(null); }}>Allocation order</button><button type="button" aria-pressed={mode === "explicit_pairs"} onClick={() => { setMode("explicit_pairs"); setPreview(null); }}>Explicit pairing</button></div></div>
            <div className="field span"><label htmlFor="vendor-lines">{mode === "allocation_order" ? "Serials · one per line" : "Pairs · unit UUID, vendor serial · one per line"}</label><textarea id="vendor-lines" className="textarea correction-paste" value={text} onChange={(event) => { setText(event.target.value); setPreview(null); }} placeholder={mode === "allocation_order" ? "RXV-4410-00191\nRXV-4410-00192" : "00000000-0000-0000-0000-000000000000, RXV-4410-00191"} /></div>
            <div className="field"><label htmlFor="vendor-reason">Reason code</label><select id="vendor-reason" className="select" value={reason} onChange={(event) => { setReason(event.target.value); setPreview(null); }}>{["vendor_list", "vendor_list_supplement", "late_capture"].map((code) => <option key={code} value={code}>{sentence(code)}</option>)}</select></div><div className="field"><label htmlFor="vendor-evidence">Evidence</label><input id="vendor-evidence" className="input" value={evidence} onChange={(event) => { setEvidence(event.target.value); setPreview(null); }} placeholder="Vendor e-mail or ASN reference" /></div>
            <div className="span note ok"><strong>Commit without approver</strong>&nbsp;is evaluated from the signed-in production role and the target-unit facts. No approver identity is sent by the browser.</div>{committed && <div className="span note ok"><strong>{committed.assigned} identifiers assigned.</strong>&nbsp;{committed.remainingCount} units still await a vendor serial · correction {shortId(committed.correctionId, 12)}.</div>}
          </div></div></section>
          <section className="card" aria-labelledby="vendor-preview"><div className="card-header"><h2 id="vendor-preview">Preview <span className="meta">in serial-allocation order</span></h2><div className="correction-toolbar"><div className="segmented plain" role="group" aria-label="Show">{[["rejected", `Refused ${rejected}`], ["assign", `Assigned ${assigning}`], ["all", `All ${sourceRows.length}`]].map(([value, label]) => <button key={value} type="button" aria-pressed={filter === value} onClick={() => { setFilter(value!); setPage(1); }}>{label}</button>)}</div><button type="button" className="btn sm" disabled={!report?.units.length} onClick={() => report && downloadAwaiting(report)}>Download the {report?.count ?? 0}</button></div></div>
            {preview?.preview.refusal && <div className="note bad"><strong>{humanise(preview.preview.refusal.rule)}.</strong>&nbsp;{preview.preview.refusal.message}</div>}
            <DataGrid dense columns={columns} page={{ rows: visible, total: filtered.length, page, pageSize: PAGE, continuation: null }} rowId={(row) => String(row.index)} onPage={setPage} onRowClick={openLine} activeRowId={active} loading={busy} emptyText={preview ? "No rows match this preview filter." : "Paste a list and preview it to see every assignment."} viewportHeight={500} footerNote={rejected ? "Refused values are reported, never applied" : "Every valid value remains provisional until commit"} />
          </section>
        </div>
      </div>
    </main>
    <Rail label="Vendor serial assignment"><Section title="Production order"><Kv rows={[["Document", productionOrderLabel], ["Awaiting", report?.count ?? "—"], ["List lines", parsed.lines.length], ["Mode", humanise(mode)], ["Reason", reason]]} /></Section>{report && <Section title="Awaiting sample">{report.units.slice(0, 6).map((unit) => <div className="job" key={unit.unitId}><div><div className="t serial">{unit.provisionalIdentifier}</div><div className="s">allocated {fmtDateTime(unit.allocatedAt)}</div></div><UnitLink unitId={unit.unitId}>{shortId(unit.unitId)}</UnitLink></div>)}</Section>}</Rail>
  </>;
}

function downloadAwaiting(report: AwaitingVendorSerialReport) { const csv = ["unit_id,item_id,provisional_identifier,allocated_at", ...report.units.map((unit) => [unit.unitId, unit.itemId, unit.provisionalIdentifier, unit.allocatedAt].join(","))].join("\r\n"); const href = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" })); const link = document.createElement("a"); link.href = href; link.download = "awaiting-vendor-serial.csv"; link.click(); URL.revokeObjectURL(href); }
