import { useEffect, useMemo, useState } from "react";
import { Link, useNavigate } from "react-router";
import { ActionDialog, DataGrid, FindBar, KpiStrip, StatusBadge, type GridColumn } from "../components";
import type { Kpi, PageResult, Tone } from "../components/types";
import { usePeople } from "../people/people";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { openDrawer } from "../shell/drawerStore";
import { ErrorNote, Kv, Section, UnitLink } from "../units/common";
import { labelsApi } from "./api";
import { humanise } from "./editorModel";
import { dayBounds, summaryKpis } from "./printJobModel";
import type { LabelBatchPrintOutcome, LabelPolicy, LabelPrintJob, LabelTemplateCatalogueRow } from "./types";
import "./labels.css";

const PAGE_SIZE = 50;
const PRINT_ROLES = new Set(["label_administrator", "technician", "inventory_controller", "service_desk", "service_supervisor", "service_manager", "production_supervisor", "warehouse_lead", "shipping_supervisor"]);

export function Component() {
  const session = useSession();
  const canPrint = session.roles.some((role) => PRINT_ROLES.has(role));
  const administrator = session.roles.includes("tenant_administrator");
  const navigate = useNavigate();
  const [jobs, setJobs] = useState<LabelPrintJob[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [find, setFind] = useState("");
  const [status, setStatus] = useState<"all" | "rejected" | "waiting" | "reprints">("all");
  const [printer, setPrinter] = useState("");
  const [printers, setPrinters] = useState<string[]>([]);
  const [fromDay, setFromDay] = useState(today());
  const [throughDay, setThroughDay] = useState(today());
  const [active, setActive] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [dialog, setDialog] = useState(false);
  const [policy, setPolicy] = useState<LabelPolicy>({ legalReviewPhrases: [], printExpiryDates: false, defaultPrinterRef: "default" });
  const [templates, setTemplates] = useState<LabelTemplateCatalogueRow[]>([]);
  const [kpis, setKpis] = useState<Kpi[]>(initialKpis());
  const people = usePeople(jobs.map((job) => job.requestedBy));
  const range = useMemo(() => dayBounds(fromDay, throughDay), [fromDay, throughDay]);

  const load = async (signal?: AbortSignal) => {
    setLoading(true); setError(null);
    try {
      const statusParam = status === "rejected" ? "rejected" : status === "waiting" ? "waiting" : undefined;
      const result = await labelsApi.jobs({ status: statusParam, kind: status === "reprints" ? "reprint" : undefined,
        find: find || undefined, printer: printer || undefined, from: range.from, to: range.to,
        skip: (page - 1) * PAGE_SIZE, take: PAGE_SIZE }, signal);
      setJobs(result.rows); setTotal(result.total);
    } catch (reason) { if (!aborted(reason)) setError(reason); } finally { setLoading(false); }
  };

  useEffect(() => { const controller = new AbortController(); void load(controller.signal); return () => controller.abort(); }, [page, find, status, printer, range.from, range.to]);
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

  const gridPage: PageResult<LabelPrintJob> = {
    rows: jobs,
    total,
    page,
    pageSize: PAGE_SIZE,
    continuation: null,
  };

  const refresh = async () => { await load(); setKpis(summaryKpis(await labelsApi.summary(range.from, range.to))); };
  const showJob = (job: LabelPrintJob) => {
    setActive(job.jobId);
    openDrawer({ eyebrow: "Print job", title: job.serial ?? "Serial unavailable", mono: true }, <JobDrawer job={job} canPrint={canPrint} onOpenUnit={() => { if (job.unitId) navigate(`/units/${job.unitId}`); }} onReprinted={refresh} />);
  };
  const columns: GridColumn<LabelPrintJob>[] = [
    { key: "job", label: "Job", width: 13, mono: true, render: (job) => `…${job.jobId.slice(-8)}` },
    { key: "serial", label: "Serial", width: 17, mono: true, render: (job) => <>{job.serial ?? "Serial unavailable"}{job.rejectionReason && <span className="sub">{job.rejectionReason}</span>}</> },
    { key: "template", label: "Template", width: 22, render: (job) => job.templateName ? `${job.templateName} v${job.templateVersion ?? "—"}` : <span className="muted">No applicable template</span> },
    { key: "why", label: "Why", width: 15, render: (job) => job.originalJobId ? "Reprint" : job.sourceEventId ? "Event request" : "Manual" },
    { key: "printer", label: "Printer", width: 13, render: (job) => job.printerRef ?? "—" },
    { key: "by", label: "By", width: 11, render: (job) => people(job.requestedBy) },
    { key: "status", label: "Status", width: 9, render: (job) => <StatusBadge tone={statusTone(job.status)}>{humanise(job.originalJobId ? "reprinted" : job.status)}</StatusBadge> },
  ];

  return <main className="content print-jobs" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Labels" }, { label: "Print jobs" }]} />
    <div className="page-header"><div><div className="eyebrow">All printers · newest first</div><h1>Print jobs</h1><p className="sub">Every attempt stores its exact rendered payload and hash. Reprints resend that payload; they never render current unit data again.</p></div><div className="actions">
      <Link className="btn" to="/templates">Templates</Link>
      {administrator && <Link className="btn" to="/settings/labels">Label settings</Link>}
      {canPrint && <button type="button" className="btn primary" onClick={() => setDialog(true)}><Icon name="print" />Print labels…</button>}
    </div></div>
    <ErrorNote error={error} />
    <div className="stack"><KpiStrip kpis={kpis} label="Label print activity today" />
      <section className="card" aria-label="Print job filters"><FindBar query={find} onQuery={(value) => { setFind(value); setPage(1); }} facets={[]} activeFacet={null} onFacet={() => {}} compact ariaLabel="Find a print job" placeholder="Serial, job or template" />
        <div className="label-job-filters"><div className="segmented plain" role="group" aria-label="Status"><button type="button" aria-pressed={status === "all"} onClick={() => { setStatus("all"); setPage(1); }}>All</button><button type="button" aria-pressed={status === "rejected"} onClick={() => { setStatus("rejected"); setPage(1); }}>Rejected</button><button type="button" aria-pressed={status === "waiting"} onClick={() => { setStatus("waiting"); setPage(1); }}>Waiting</button><button type="button" aria-pressed={status === "reprints"} onClick={() => { setStatus("reprints"); setPage(1); }}>Reprints</button></div><label className="field label-printer-filter"><span className="lbl">Printer</span><select className="select" value={printer} onChange={(event) => { setPrinter(event.target.value); setPage(1); }}><option value="">All printers</option>{printers.map((value) => <option key={value}>{value}</option>)}</select></label><label className="field"><span className="lbl">From</span><input className="input" type="date" value={fromDay} onChange={(event) => { const value = event.target.value; setFromDay(value); if (throughDay < value) setThroughDay(value); setPage(1); }} /></label><label className="field"><span className="lbl">Through</span><input className="input" type="date" min={fromDay} value={throughDay} onChange={(event) => { setThroughDay(event.target.value); setPage(1); }} /></label></div>
      </section>
      <section className="card" aria-labelledby="print-job-grid"><div className="card-header"><h2 id="print-job-grid">Jobs</h2><span className="meta">newest first · click a row for the stored payload</span></div><DataGrid columns={columns} page={gridPage} rowId={(job) => job.jobId} onPage={(next) => { setPage(next); setActive(null); }} onRowClick={showJob} activeRowId={active} loading={loading} emptyText="No print jobs match these filters." footerNote="All filters are applied before server paging" /></section>
    </div>
    {dialog && <PrintDialog policy={policy} templates={templates} onCancel={() => setDialog(false)} onPrinted={async (close) => { await refresh(); if (close) setDialog(false); }} />}
  </main>;
}

function JobDrawer({ job, canPrint, onOpenUnit, onReprinted }: { job: LabelPrintJob; canPrint: boolean; onOpenUnit: () => void; onReprinted: () => Promise<void> }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const person = usePeople([job.requestedBy]);
  const reprint = async () => {
    setBusy(true); setError(null);
    try { await labelsApi.reprint(job.jobId, job.printerRef); await onReprinted(); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  return <>
    {job.rejectionReason && <Section title="Rejection"><div className="note bad"><strong>Rejected · nothing sent.</strong>&nbsp;{job.rejectionReason}</div></Section>}
    <Section title="Job"><Kv rows={[["Template", job.templateName ? `${job.templateName} v${job.templateVersion ?? "—"}` : "No applicable template"], ["Requested", `${person(job.requestedBy)} · ${fmt(job.requestedAt)}`], ["Printer", job.printerRef], ["Payload hash", job.payloadHash ? <code className="label-hash">{job.payloadHash}</code> : "none · not rendered"], ["Status", humanise(job.originalJobId ? "reprinted" : job.status)]]} /></Section>
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
  return <div className="stack"><div className={rejected ? "note warn" : "note ok"}><strong>Print run complete.</strong>&nbsp;{outcomes.length - rejected} submitted · {rejected} rejected.</div><div className="table-wrap"><table className="table"><thead><tr><th>Serial</th><th>Outcome</th><th>Detail</th></tr></thead><tbody>{outcomes.map((outcome, index) => <tr key={`${outcome.serial}-${index}`}><td className="mono">{outcome.serial}</td><td><StatusBadge tone={statusTone(outcome.status)}>{humanise(outcome.status)}</StatusBadge></td><td>{outcome.rejectionReason ?? (outcome.jobId ? `Job …${outcome.jobId.slice(-8)}` : "Submitted")}</td></tr>)}</tbody></table></div></div>;
}

function today(): string { return localDate(new Date()); }
function localDate(value: Date): string { return `${value.getFullYear()}-${String(value.getMonth() + 1).padStart(2, "0")}-${String(value.getDate()).padStart(2, "0")}`; }
function initialKpis(): Kpi[] { return ["Printed", "Waiting", "Rejected", "Reprints", "From events"].map((label) => ({ label, value: "…", detail: "Loading" })); }
function statusTone(status: string): Tone { return status === "confirmed" ? "ok" : status === "rejected" ? "bad" : status === "sent" || status === "rendered" ? "warn" : "info"; }
function fmt(value: string): string { return new Date(value).toLocaleString(); }
function aborted(reason: unknown): boolean { return reason instanceof DOMException && reason.name === "AbortError"; }
