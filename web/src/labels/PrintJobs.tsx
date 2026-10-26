import { useEffect, useMemo, useState } from "react";
import { Link, useNavigate } from "react-router";
import { ActionDialog, DataGrid, FindBar, KpiStrip, StatusBadge, useGridQuery, type GridColumn } from "../components";
import { useTenantTime, type TenantTimeFormatter } from "../components/tenantTime";
import type { Kpi, Tone } from "../components/types";
import { usePeople } from "../people/people";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { openDrawer } from "../shell/drawerStore";
import { ErrorNote, Kv, Section, UnitLink } from "../units/common";
import { labelsApi } from "./api";
import { humanise } from "./editorModel";
import { printJobRejectionSummary, summaryKpis } from "./printJobModel";
import { labelPrintJobPageSchema, type LabelBatchPrintOutcome, type LabelPolicy, type LabelPrintJob, type LabelTemplateCatalogueRow } from "./types";
import "./labels.css";

const PAGE_SIZE = 50;
const PRINT_ROLES = new Set(["label_administrator", "technician", "inventory_controller", "service_desk", "service_supervisor", "service_manager", "production_supervisor", "warehouse_lead", "shipping_supervisor"]);

export function Component() {
  const tenantTime = useTenantTime();
  const session = useSession();
  const canPrint = session.roles.some((role) => PRINT_ROLES.has(role));
  const administrator = session.roles.includes("tenant_administrator");
  const navigate = useNavigate();
  const [find, setFind] = useState("");
  const [status, setStatus] = useState<"all" | "rejected" | "waiting" | "reprints">("all");
  const [printer, setPrinter] = useState("");
  const [printers, setPrinters] = useState<string[]>([]);
  const [fromDay, setFromDay] = useState(() => tenantPrintJobDays(tenantTime).fromDay);
  const [throughDay, setThroughDay] = useState(() => tenantPrintJobDays(tenantTime).throughDay);
  const [active, setActive] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [dialog, setDialog] = useState(false);
  const [policy, setPolicy] = useState<LabelPolicy>({ legalReviewPhrases: [], printExpiryDates: false, defaultPrinterRef: "default" });
  const [templates, setTemplates] = useState<LabelTemplateCatalogueRow[]>([]);
  const [kpis, setKpis] = useState<Kpi[]>(initialKpis());
  const range = useMemo(() => tenantTime.dayBounds(fromDay, throughDay), [fromDay, tenantTime, throughDay]);
  useEffect(() => { const days = tenantPrintJobDays(tenantTime); setFromDay(days.fromDay); setThroughDay(days.throughDay); }, [tenantTime.zone]);
  const filterParams = useMemo(() => { const value = new URLSearchParams({ from: range.from, to: range.to }); if (status === "rejected" || status === "waiting") value.set("status", status); if (status === "reprints") value.set("kind", "reprint"); if (find) value.set("find", find); if (printer) value.set("printer", printer); return value; }, [find, printer, range.from, range.to, status]);
  const query = useGridQuery("/api/labels/print-jobs", labelPrintJobPageSchema, { gridId: "print-jobs", userId: session.user.id, defaultSort: { key: "requested", dir: "desc" }, defaultSize: PAGE_SIZE, params: filterParams, sortKeys: ["number", "requested", "status"] });
  const jobs = query.data?.rows ?? [];
  const people = usePeople(jobs.map((job) => job.requestedBy));
  useEffect(() => {
    const controller = new AbortController();
    Promise.all([labelsApi.settings(controller.signal), labelsApi.templates(controller.signal)])
      .then(([nextPolicy, nextTemplates]) => { setPolicy(nextPolicy); setTemplates(nextTemplates); })
      .catch((reason) => { if (!aborted(reason)) setError(reason); });
    Promise.all([labelsApi.summary(range.from, range.to, controller.signal), labelsApi.printers(range.from, range.to, controller.signal)])
      .then(([summary, nextPrinters]) => { setKpis(summaryKpis(summary)); setPrinters(nextPrinters); })
      .catch((reason) => { if (!aborted(reason)) setError(reason); });
    return () => controller.abort();
  }, [range.from, range.to]);

  const gridPage = {
    rows: jobs,
    total: query.data?.total ?? 0,
    page: query.page,
    pageSize: query.pageSize,
    continuation: null,
  };

  const refresh = async () => { query.reload(); setKpis(summaryKpis(await labelsApi.summary(range.from, range.to))); };
  const showJob = (job: LabelPrintJob) => {
    setActive(job.jobId);
    openDrawer({ eyebrow: "Print job", title: job.jobNumber, mono: true }, <JobDrawer job={job} canPrint={canPrint} onOpenUnit={() => { if (job.unitId) navigate(`/units/${job.unitId}`); }} onReprinted={refresh} />);
  };
  const columns: GridColumn<LabelPrintJob>[] = [
    { key: "job", label: "Job", width: 13, mono: true, sort: { key: "number", defaultDir: "asc" }, render: (job) => job.jobNumber },
    { key: "serial", label: "Serial", width: 17, render: (job) => <><span className="serial">{job.serial ?? "Serial unavailable"}</span>{(job.rejectionReason || job.emptyRequiredField) && <span className="sub">{printJobRejectionSummary(job.emptyRequiredField, job.rejectionReason)}</span>}</> },
    { key: "template", label: "Template", width: 22, render: (job) => job.templateName ? `${job.templateName} v${job.templateVersion ?? "—"}` : <span className="muted">No applicable template</span> },
    { key: "why", label: "Why", width: 15, render: (job) => job.originalJobId ? "Reprint" : job.sourceEventId ? "Event request" : "Manual" },
    { key: "printer", label: "Printer", width: 13, render: (job) => job.printerRef ?? "—" },
    { key: "by", label: "By", width: 11, render: (job) => people(job.requestedBy) },
    { key: "status", label: "Status", width: 9, sort: { key: "status", defaultDir: "asc" }, render: (job) => <StatusBadge tone={statusTone(job.status)}>{humanise(job.originalJobId ? "reprinted" : job.status)}</StatusBadge> },
    { key: "requested", label: "Requested", width: 12, sort: { key: "requested", defaultDir: "desc" }, render: (job) => tenantTime.dateTime(job.requestedAt) },
  ];

  return <main className="content print-jobs" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Labels" }, { label: "Print jobs" }]} />
    <div className="page-header"><div><div className="eyebrow">All printers · newest first</div><h1>Print jobs</h1><p className="sub">Every attempt stores its exact rendered payload and hash. Reprints resend that payload; they never render current unit data again.</p></div><div className="actions">
      <Link className="btn" to="/templates">Templates</Link>
      {administrator && <Link className="btn" to="/settings/labels">Label settings</Link>}
      {canPrint && <button type="button" className="btn primary" onClick={() => setDialog(true)}><Icon name="print" />Print labels…</button>}
    </div></div>
    <ErrorNote error={error ?? query.error} />
    <div className="stack"><KpiStrip kpis={kpis} label="Label print activity in the selected range" />
      <section className="card" aria-label="Print job filters"><FindBar query={find} onQuery={setFind} facets={[]} activeFacet={null} onFacet={() => {}} compact ariaLabel="Find a print job" placeholder="Serial, job or template" />
        <div className="label-job-filters"><div className="segmented plain" role="group" aria-label="Status"><button type="button" aria-pressed={status === "all"} onClick={() => setStatus("all")}>All</button><button type="button" aria-pressed={status === "rejected"} onClick={() => setStatus("rejected")}>Rejected</button><button type="button" aria-pressed={status === "waiting"} onClick={() => setStatus("waiting")}>Waiting</button><button type="button" aria-pressed={status === "reprints"} onClick={() => setStatus("reprints")}>Reprints</button></div><label className="field label-printer-filter"><span className="lbl">Printer</span><select className="select" value={printer} onChange={(event) => setPrinter(event.target.value)}><option value="">All printers</option>{printers.map((value) => <option key={value}>{value}</option>)}</select></label><label className="field"><span className="lbl">From</span><input className="input" type="date" value={fromDay} onChange={(event) => { const value = event.target.value; setFromDay(value); if (throughDay < value) setThroughDay(value); }} /></label><label className="field"><span className="lbl">Through</span><input className="input" type="date" min={fromDay} value={throughDay} onChange={(event) => setThroughDay(event.target.value)} /></label></div>
      </section>
      <section className="card" aria-labelledby="print-job-grid"><div className="card-header"><h2 id="print-job-grid">Jobs</h2><span className="meta">newest first · click a row for the stored payload</span></div><DataGrid columns={columns} page={gridPage} rowId={(job) => job.jobId} onPage={(next) => { query.onPage(next); setActive(null); }} onRowClick={showJob} activeRowId={active} loading={query.loading} emptyText="No print jobs match these filters." footerNote="All filters are applied before server paging" gridId="print-jobs" userId={session.user.id} sort={query.sort} defaultSort={{ key: "requested", dir: "desc" }} onSort={query.onSort} pageSize={query.pageSize} defaultPageSize={PAGE_SIZE} onPageSize={query.onPageSize} /></section>
    </div>
    {dialog && <PrintDialog policy={policy} templates={templates} onCancel={() => setDialog(false)} onPrinted={async (close) => { await refresh(); if (close) setDialog(false); }} />}
  </main>;
}

function JobDrawer({ job, canPrint, onOpenUnit, onReprinted }: { job: LabelPrintJob; canPrint: boolean; onOpenUnit: () => void; onReprinted: () => Promise<void> }) {
  const tenantTime = useTenantTime();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const person = usePeople([job.requestedBy]);
  const reprint = async () => {
    setBusy(true); setError(null);
    try { await labelsApi.reprint(job.jobId, job.printerRef); await onReprinted(); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  return <>
    {job.rejectionReason && <Section title="Rejection"><div className="note bad"><strong>Rejected · nothing sent.</strong>&nbsp;{printJobRejectionSummary(job.emptyRequiredField, job.rejectionReason)}</div></Section>}
    <Section title="Job"><Kv rows={[["Job number", job.jobNumber], ["Serial", job.serial], ["Template", job.templateName ? `${job.templateName} v${job.templateVersion ?? "—"}` : "No applicable template"], ["Requested", `${person(job.requestedBy)} · ${tenantTime.dateTime(job.requestedAt)}`], ["Printer", job.printerRef], ["Payload hash", job.payloadHash ? <code className="label-hash">{job.payloadHash}</code> : "none · not rendered"], ["Status", humanise(job.originalJobId ? "reprinted" : job.status)]]} /></Section>
    {job.renderedPayload && <Section title="Stored payload"><pre className="code label-drawer-payload">{job.renderedPayload}</pre></Section>}
    <Section title="Actions"><div className="act">{job.unitId && <button type="button" className="btn block" onClick={onOpenUnit}>Open unit</button>}{canPrint && <button type="button" className="btn primary block" disabled={busy || !job.renderedPayload} onClick={() => void reprint()}>{busy ? "Reprinting…" : "Reprint stored payload"}</button>}</div><ErrorNote error={error} /></Section>
  </>;
}

function PrintDialog({ policy, templates, onCancel, onPrinted }: { policy: LabelPolicy; templates: LabelTemplateCatalogueRow[]; onCancel: () => void; onPrinted: (close: boolean) => Promise<void> }) {
  const [serials, setSerials] = useState("");
  const [printer, setPrinter] = useState(policy.defaultPrinterRef);
  const [templateId, setTemplateId] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [outcomes, setOutcomes] = useState<LabelBatchPrintOutcome[]>([]);
  const values = serials.split(/[\n,]+/).map((value) => value.trim()).filter(Boolean);
  const submit = async () => {
    setBusy(true); setError(null); setOutcomes([]);
    try {
      const printed = await labelsApi.printBatch(values, printer, templateId || null);
      setOutcomes(printed.outcomes);
      await onPrinted(false);
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  return <ActionDialog title="Print labels" wide onCancel={onCancel} actions={<button type="button" className="btn primary" disabled={busy || !values.length || !printer.trim()} onClick={() => void submit()}>{busy ? "Printing…" : `Print ${values.length || ""} label${values.length === 1 ? "" : "s"}`}</button>}>
    <label className="field"><span className="lbl">Serial list</span><textarea className="textarea mono" rows={8} value={serials} onChange={(event) => setSerials(event.target.value)} placeholder="One serial per line, or comma separated" /><span className="help">Up to 500 serials. The server resolves the complete list and reports every outcome.</span></label>
    <div className="form-grid"><label className="field"><span className="lbl">Printer</span><input className="input" value={printer} onChange={(event) => setPrinter(event.target.value)} /></label><label className="field"><span className="lbl">Template <span className="hint">· optional</span></span><select className="select" value={templateId} onChange={(event) => setTemplateId(event.target.value)}><option value="">Choose active template by unit</option>{templates.filter((template) => template.activeTemplateVersionId !== null).map((template) => <option key={template.templateId} value={template.activeTemplateVersionId ?? ""}>{template.name} · v{template.activeVersion}</option>)}</select></label></div>
    <ErrorNote error={error} />{outcomes.length > 0 && <BatchOutcomes outcomes={outcomes} />}
  </ActionDialog>;
}

function BatchOutcomes({ outcomes }: { outcomes: LabelBatchPrintOutcome[] }) {
  const rejected = outcomes.filter((outcome) => outcome.status === "rejected").length;
  return <div className="stack"><div className={rejected ? "note warn" : "note ok"}><strong>Print run complete.</strong>&nbsp;{outcomes.length - rejected} submitted · {rejected} rejected.</div><div className="table-wrap"><table className="table"><thead><tr><th>Serial</th><th>Outcome</th><th>Detail</th></tr></thead><tbody>{outcomes.map((outcome, index) => <tr key={`${outcome.serial}-${index}`}><td className="mono">{outcome.serial}</td><td><StatusBadge tone={statusTone(outcome.status)}>{humanise(outcome.status)}</StatusBadge></td><td>{outcome.status === "rejected" ? printJobRejectionSummary(outcome.emptyRequiredField, outcome.rejectionReason) : outcome.jobId ? `Job …${outcome.jobId.slice(-8)}` : "Submitted"}</td></tr>)}</tbody></table></div></div>;
}

function initialKpis(): Kpi[] { return ["Printed", "Waiting", "Rejected", "Reprints", "From events"].map((label) => ({ label, value: "…", detail: "Loading" })); }
function statusTone(status: string): Tone { return status === "confirmed" ? "ok" : status === "rejected" ? "bad" : status === "sent" || status === "rendered" ? "warn" : "info"; }
function aborted(reason: unknown): boolean { return reason instanceof DOMException && reason.name === "AbortError"; }
function tenantPrintJobDays(time: TenantTimeFormatter): { fromDay: string; throughDay: string } {
  const throughDay = time.localInput(new Date().toISOString()).slice(0, 10);
  const from = new Date(`${throughDay}T00:00:00Z`); from.setUTCDate(from.getUTCDate() - 29);
  return { fromDay: from.toISOString().slice(0, 10), throughDay };
}
