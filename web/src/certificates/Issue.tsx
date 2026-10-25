import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { newCommandId, type SelectionSource } from "../api/units";
import { DataGrid, FindBar, KpiStrip, PresenceChips, StatusBadge, useDocumentPresence, useGridState, type GridColumn } from "../components";
import { DocumentField, PartyField, type DocumentValue } from "../corrections/ReferenceFields";
import { useSession } from "../session/session";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { openDrawer } from "../shell/drawerStore";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, UnitLink } from "../units/common";
import { humanise, splitSerials } from "../units/format";
import { certificateIssueApi, downloadArchive } from "./api";
import type { CertificateRunResult, CertificateSelectionPreview, CertificateSelectionUnit } from "./types";
import { certificateEventLabel } from "./format";
import "./certificates.css";

const PAGE = 50;
const SOURCES = [["production_order", "Work order"], ["sales_order", "Sales order"], ["delivery", "Delivery"], ["serial_list", "Serial list"]] as const;
const FORMS = [["coc", "Certificate of conformity"], ["generic_release", "Generic release record"], ["dhr_extract", "Production-history extract"]] as const;

export function Component() {
  const session = useSession();
  const [sourceKind, setSourceKind] = useState<(typeof SOURCES)[number][0]>("sales_order");
  const [document, setDocument] = useState<DocumentValue | null>(null);
  const [serialText, setSerialText] = useState("");
  const [formType, setFormType] = useState("coc");
  const [issuer, setIssuer] = useState<string | null>(null);
  const [language, setLanguage] = useState("en");
  const [selectionId, setSelectionId] = useState("");
  const [preview, setPreview] = useState<CertificateSelectionPreview | null>(null);
  const grid = useGridState("certificate-issue-preview", session.user.id, { key: "outcome", dir: "asc" }, PAGE);
  const [find, setFind] = useState("");
  const [outcome, setOutcome] = useState<string | null>(null);
  const [includeBundle, setIncludeBundle] = useState(true);
  const [reason, setReason] = useState("");
  const [commandId, setCommandId] = useState(newCommandId());
  const [result, setResult] = useState<CertificateRunResult | null>(null);
  const [active, setActive] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const presence = useDocumentPresence("certificate_issue", selectionId, busy);

  const request = useMemo(() => selectionId && issuer ? { selectionId, documentKind: "release_record", formType, issuerEntityId: issuer, language, includeBundle, skip: (grid.page - 1) * grid.pageSize, take: grid.pageSize, find: find || null, outcome, sort: grid.sort.key, direction: grid.sort.dir } : null, [selectionId, issuer, formType, language, includeBundle, grid.page, grid.pageSize, grid.sort, find, outcome]);
  useEffect(() => {
    if (!request) { setPreview(null); return; }
    const controller = new AbortController(); setBusy(true); setError(null);
    certificateIssueApi.preview(request, controller.signal).then((value) => { setPreview(value); setBusy(false); })
      .catch((value) => { if (!(value instanceof DOMException && value.name === "AbortError")) { setError(value); setBusy(false); } });
    return () => controller.abort();
  }, [request]);

  const source = (): SelectionSource | null => sourceKind === "serial_list"
    ? splitSerials(serialText).length ? { kind: "pasted_list", serials: splitSerials(serialText) } : null
    : document ? { kind: "document", document: { type: sourceKind, id: document.id } } : null;
  const resolve = async () => {
    const selected = source(); if (!selected) return;
    setBusy(true); setError(null); setResult(null);
    try { const answer = await certificateIssueApi.resolve(selected); setSelectionId(answer.selectionId); grid.reset(); setCommandId(newCommandId()); }
    catch (value) { setError(value); } finally { setBusy(false); }
  };
  const commit = async () => {
    if (!selectionId || !issuer || !reason.trim()) return;
    setBusy(true); setError(null);
    try {
      const answer = await certificateIssueApi.issue({ commandId, selectionId, documentKind: "release_record", formType, issuerEntityId: issuer, language, reprintReason: reason.trim(), includeBundle });
      setResult(answer); downloadArchive(answer);
    } catch (value) { setError(value); } finally { setBusy(false); }
  };

  const k = preview?.kpis;
  const columns: GridColumn<CertificateSelectionUnit>[] = [
    { key: "serial", label: "Serial", width: 28, mono: true, sort: { key: "serial", defaultDir: "asc" }, render: (row) => <UnitLink unitId={row.unitId}>{row.serial ?? "Unlabelled unit"}</UnitLink> },
    { key: "item", label: "Item", width: 28, sort: { key: "item", defaultDir: "asc" }, render: (row) => <span>{row.itemCode ?? row.itemName ?? "Unknown item"}{row.revision && <span className="sub">rev {row.revision}</span>}</span> },
    { key: "certificate", label: "Certificate", width: 25, muted: true, render: (row) => row.trackingNumber ? <span className="mono">{row.trackingNumber} · v{row.certificateVersion}</span> : "none" },
    { key: "outcome", label: "Will do", width: 19, sort: { key: "outcome", defaultDir: "asc" }, render: (row) => <StatusBadge tone={row.outcome === "issue_version_1" ? "info" : row.outcome === "reprint_copy" ? "neutral" : "warn"}>{row.outcome === "issue_version_1" ? "Issue v1" : row.outcome === "reprint_copy" ? "Copy" : humanise(row.reason)}</StatusBadge> },
  ];
  const open = (row: CertificateSelectionUnit) => { setActive(row.unitId); openDrawer({ eyebrow: "Certificate preview", title: row.serial ?? "Unlabelled unit", mono: true }, <><h3>{row.outcome === "not_issuable" ? "Not issuable" : "Certificate output"}</h3><Kv rows={[["Form", FORMS.find(([value]) => value === formType)?.[1] ?? humanise(formType)], ["Will be", row.outcome === "issue_version_1" ? "version 1 · first release" : row.outcome === "reprint_copy" ? `version ${row.certificateVersion} · Copy` : "left out"], ["Item", row.itemName ? `${row.itemCode} · ${row.itemName}` : row.itemCode ?? "Unknown"], ["Reason", row.detail ?? "Ready"], ["Unblock by", row.unblockingStep ?? "—"]]} />{row.outcome === "not_issuable" && <div className="act"><Link className="btn primary block" to={`/units/${row.unitId}`}>Open unit</Link></div>}</>); };
  const notIssuable = preview?.units.filter((row) => row.outcome === "not_issuable") ?? [];
  const displayUser = session.user.displayName || session.user.email;

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Certificates", to: "/certificates" }, { label: "Issue and reprint" }]} />
      <div className="page-header"><div><div className="eyebrow">Selection set · {preview ? `${preview.kpis.inSelection.toLocaleString()} units` : "resolve a source to begin"}</div><h1>Issue certificates for a selection</h1><p className="sub">Units without an issued certificate get version 1; current certificates are reprinted as Copy. Units that are not released, have a MISSING required component, or lack an active template stay out and remain visible.</p></div><div className="actions"><PresenceChips users={presence} /><button type="button" className="btn primary" disabled={busy || !k?.outputDocuments || !reason.trim()} onClick={() => void commit()}><Icon name="print" />{busy ? "Working…" : `Issue ${k?.outputDocuments ?? 0} and download`}</button></div></div>
      <div className="stack">
        <section className="card" aria-label="Selection"><div className="card-body"><div className="certificate-selection-grid">
          <div className="field"><label htmlFor="certificate-select-by">Select by</label><select id="certificate-select-by" className="select" value={sourceKind} onChange={(event) => { setSourceKind(event.target.value as typeof sourceKind); setDocument(null); setSelectionId(""); }}>{SOURCES.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></div>
          {sourceKind === "serial_list" ? <div className="field"><label htmlFor="certificate-serials">Serial list</label><textarea id="certificate-serials" className="textarea serial" value={serialText} onChange={(event) => setSerialText(event.target.value)} placeholder="Paste one serial per line" /></div> : <DocumentField id="certificate-document" label={SOURCES.find(([value]) => value === sourceKind)?.[1] ?? "Document"} types={[sourceKind]} value={document} onChange={setDocument} />}
          <div className="field"><label htmlFor="certificate-form">Form</label><select id="certificate-form" className="select" value={formType} onChange={(event) => { setFormType(event.target.value); setCommandId(newCommandId()); }}>{FORMS.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></div>
          <PartyField id="certificate-issuer" label="Issuer" value={issuer} onChange={setIssuer} hint="Legally responsible issuing entity" />
          <div className="field"><label htmlFor="certificate-language">Language</label><select id="certificate-language" className="select" value={language} onChange={(event) => setLanguage(event.target.value)}><option value="en">English</option><option value="de">Deutsch</option><option value="fr">Français</option></select></div>
        </div><div className="act"><button type="button" className="btn" disabled={busy || !source()} onClick={() => void resolve()}>Resolve and preview</button></div><ErrorNote error={error} />{result && <div className="note ok"><strong>Run recorded{result.replayed ? " · replay" : ""}.</strong>&nbsp;{result.documents.length} documents downloaded; {result.skipped.length} skipped units are on the cover.</div>}</div></section>
        <KpiStrip kpis={[{ label: "In selection", value: String(k?.inSelection ?? 0), detail: "resolved once from the source" }, { label: "New version 1", value: String(k?.newVersion1 ?? 0), detail: "released · no certificate yet" }, { label: "Reprint as Copy", value: String(k?.reprintAsCopy ?? 0), detail: "current immutable version" }, { label: "Not issuable", value: String(k?.notIssuable ?? 0), detail: "left out · reason retained" }, { label: "Output", value: `${k?.outputDocuments ?? 0} files`, detail: includeBundle ? "+ bundle in serial order" : "per unit only" }]} />
        <div className="cols-3-2 top"><section className="card" aria-labelledby="certificate-units"><div className="card-header"><h2 id="certificate-units">Units</h2></div><FindBar query={find} onQuery={(value) => { setFind(value); grid.reset(); }} facets={[{ id: "not_issuable", label: "Not issuable", count: k?.notIssuable ?? 0 }, { id: "reprint_copy", label: "Copy", count: k?.reprintAsCopy ?? 0 }]} activeFacet={outcome} onFacet={(value) => { setOutcome(value); grid.reset(); }} ariaLabel="Find within certificate selection" compact status={preview ? `${preview.filteredTotal.toLocaleString()} match` : undefined} /><DataGrid columns={columns} page={{ rows: preview?.units ?? [], total: preview?.filteredTotal ?? 0, page: grid.page, pageSize: grid.pageSize, continuation: null }} rowId={(row) => row.unitId} onPage={grid.onPage} onRowClick={open} activeRowId={active} loading={busy} emptyText="Resolve a work order, sales order, delivery or serial list." viewportHeight={520} gridId="certificate-issue-preview" userId={session.user.id} sort={grid.sort} defaultSort={{ key: "outcome", dir: "asc" }} onSort={grid.onSort} pageSize={grid.pageSize} defaultPageSize={PAGE} onPageSize={grid.onPageSize} /></section>
          <div className="stack"><section className="card" aria-labelledby="certificate-not"><div className="card-header"><h2 id="certificate-not">Not issuable</h2><span className="meta">fix these, then run again</span></div><div className="card-body"><ul className="certificate-not-issuable">{notIssuable.length ? notIssuable.map((row) => <li key={row.unitId}><span><span className="serial">{row.serial ?? "Unlabelled unit"}</span><span className="s">{row.detail}</span></span><Link className="btn sm" to={`/units/${row.unitId}`}>Open unit</Link></li>) : <li><span className="muted">No not-issuable units on this page.</span></li>}</ul></div></section>
            <section className="card" aria-labelledby="certificate-output"><div className="card-header"><h2 id="certificate-output">Output</h2></div><div className="card-body"><div className="certificate-output-options" role="radiogroup" aria-label="Output"><label className={includeBundle ? "on" : undefined}><input type="radio" checked={includeBundle} onChange={() => setIncludeBundle(true)} /><span>Per unit and bundle<span className="s">HTML files plus one bundle in serial order</span></span></label><label className={!includeBundle ? "on" : undefined}><input type="radio" checked={!includeBundle} onChange={() => setIncludeBundle(false)} /><span>Per unit only<span className="s">HTML files in one archive</span></span></label></div><div className="field" style={{ marginTop: 12 }}><label htmlFor="certificate-reason">Reprint reason</label><input id="certificate-reason" className="input" value={reason} onChange={(event) => { setReason(event.target.value); setCommandId(newCommandId()); }} placeholder="Delivery pack or customer request" /></div><Kv rows={[["Copies", "marked Copy · one reprint log per unit"], ["Signed by", `${displayUser} · released as conforming`]]} /></div></section>
            <section className="card" aria-labelledby="certificate-recorded"><div className="card-header"><h2 id="certificate-recorded">Will be recorded</h2></div><table className="table dense"><tbody><tr><td>{certificateEventLabel("Certificate.Issued", 1)}</td><td>{k?.newVersion1 ?? 0}</td></tr><tr><td>{certificateEventLabel("Certificate.Reprinted")}</td><td>{k?.reprintAsCopy ?? 0} · no new version</td></tr><tr><td>Skipped on cover</td><td>{k?.notIssuable ?? 0}</td></tr></tbody></table></section></div>
        </div>
      </div>
    </main>
    <Rail label="Certificate run"><Section title="Run contract"><Kv rows={[["Selection", selectionId ? "resolved and stored" : "not resolved"], ["Actor", displayUser], ["Signature meaning", "released as conforming"], ["Ordering", "serial ascending"], ["Transaction", "one command receipt"]]} /></Section><Section title="Renderer"><p className="muted">The archive currently contains plain HTML through the certificate renderer port. A licensed PDF renderer can replace it without changing this screen or command shape.</p></Section></Rail>
  </>;
}
