import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { DataGrid, type GridColumn } from "../components/DataGrid";
import { StatusBadge } from "../components/StatusBadge";
import type { PageResult } from "../components/types";
import { usePeople } from "../people/people";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { Rail } from "../shell/Drawer";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { ErrorNote, Kv, Section } from "../units/common";
import { certificateApi } from "./api";
import { fmtDate, fmtDateTime, formLabel, humanise, statusTone } from "./format";
import type { CertificateTemplateSummary, LabelTemplateSummary } from "./types";
import "./certificates.css";

export function Component() {
  const [certificates, setCertificates] = useState<CertificateTemplateSummary[]>([]);
  const [labels, setLabels] = useState<LabelTemplateSummary[]>([]);
  const [tab, setTab] = useState<"all" | "certificates" | "labels">("all");
  const [active, setActive] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const people = usePeople([
    ...certificates.map((row) => row.approvedBy), ...labels.map((row) => row.legalReviewedBy), ...labels.map((row) => row.createdBy),
  ]);
  useEffect(() => {
    const controller = new AbortController();
    Promise.all([certificateApi.templates(controller.signal), certificateApi.labels(controller.signal)])
      .then(([certificateRows, labelRows]) => { setCertificates(certificateRows); setLabels(labelRows); })
      .catch((reason) => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, []);

  const certificateColumns = useMemo<GridColumn<CertificateTemplateSummary>[]>(() => [
    { key: "name", label: "Template", width: 30, render: (row) => <>{row.name}<span className="sub">{row.builtIn ? "built-in starter · copy to customise" : row.code}</span></> },
    { key: "document", label: "Document", width: 18, render: (row) => <>{humanise(row.documentKind)}<span className="sub">{formLabel(row.formType)}</span></> },
    { key: "version", label: "Version", width: 10, render: (row) => `v${row.latestVersion}` },
    { key: "effective", label: "Effective", width: 18, render: (row) => row.effectiveFrom ? <>since {fmtDate(row.effectiveFrom)}{row.effectiveTo && <span className="sub">to {fmtDate(row.effectiveTo)}</span>}</> : <span className="muted">not active</span> },
    { key: "approval", label: "Approval", width: 14, render: (row) => <StatusBadge tone={statusTone(row.latestStatus)}>{humanise(row.latestStatus)}</StatusBadge> },
    { key: "issued", label: "Issued with it", width: 10, numeric: true, render: (row) => row.issuedCount.toLocaleString() },
  ], []);
  const labelColumns = useMemo<GridColumn<LabelTemplateSummary>[]>(() => [
    { key: "name", label: "Template", width: 30, render: (row) => <>{row.name}<span className="sub">{humanise(row.appliesTo)} · {row.appliesToValue}</span></> },
    { key: "layout", label: "Applies to", width: 18, render: (row) => humanise(row.appliesToValue) },
    { key: "version", label: "Version", width: 10, render: (row) => row.latestVersion ? `v${row.latestVersion}` : "—" },
    { key: "effective", label: "Effective", width: 18, render: (row) => row.effectiveFrom ? <>since {fmtDate(row.effectiveFrom)}{row.effectiveTo && <span className="sub">to {fmtDate(row.effectiveTo)}</span>}</> : <span className="muted">not active</span> },
    { key: "review", label: "Legal review", width: 14, render: (row) => <StatusBadge tone={row.legalReview && !row.legalReviewedBy ? "warn" : statusTone(row.latestStatus)}>{row.legalReview ? row.legalReviewedBy ? "Signed" : "Needed" : "Not needed"}</StatusBadge> },
    { key: "printed", label: "Printed with it", width: 10, numeric: true, render: (row) => row.printedCount.toLocaleString() },
  ], []);
  const certPage: PageResult<CertificateTemplateSummary> = { rows: certificates, total: certificates.length, page: 1, pageSize: Math.max(1, certificates.length), continuation: null };
  const labelPage: PageResult<LabelTemplateSummary> = { rows: labels, total: labels.length, page: 1, pageSize: Math.max(1, labels.length), continuation: null };

  const openCertificate = (row: CertificateTemplateSummary) => {
    const key = `certificate:${row.templateId}`; setActive(key);
    const head = { eyebrow: `${humanise(row.documentKind)} template`, title: row.name };
    openDrawer(head, <div className="muted">Loading versions…</div>);
    certificateApi.template(row.templateId).then((detail) => updateDrawer(head, <>
      <Section title="Versions"><div className="certificate-version-list">{detail.versions.map((version) => <div className="job" key={version.templateVersionId}><span><span className="t">v{version.version} · {humanise(version.status)}</span><span className="s">{version.effectiveFrom ? fmtDate(version.effectiveFrom) : "not active"}{version.approvedBy ? ` · ${people(version.approvedBy)}` : ""}</span></span><StatusBadge tone={statusTone(version.status)}>{humanise(version.status)}</StatusBadge></div>)}</div></Section>
      <Section title="Inputs"><Kv rows={[["Language", "chosen and recorded per version"], ["Issuer", "chosen at issue and snapshotted"], ["Branding", "asset and hash snapshotted at issue"], ["Issued", row.issuedCount.toLocaleString()]]} /></Section>
      <div className="act"><Link className="btn primary block" to={`/templates/certificates/${row.templateId}`}>{row.builtIn ? "Copy or inspect starter" : "Open designer"}</Link></div>
    </>)).catch(setError);
  };
  const openLabel = (row: LabelTemplateSummary) => {
    const key = `label:${row.templateId}`; setActive(key);
    const head = { eyebrow: "Label template", title: row.name };
    openDrawer(head, <div className="muted">Loading versions…</div>);
    certificateApi.label(row.templateId).then((detail) => updateDrawer(head, <>
      <Section title="Versions"><div className="certificate-version-list">{detail.versions.map((version) => <div className="job" key={version.templateVersionId}><span><span className="t">v{version.version} · {humanise(version.status)}</span><span className="s">{fmtDate(version.effectiveFrom)}{version.legalReviewedBy ? ` · legal ${people(version.legalReviewedBy)}` : ""}</span></span><StatusBadge tone={statusTone(version.status)}>{humanise(version.status)}</StatusBadge></div>)}</div></Section>
      <Section title="Catalogue"><Kv rows={[["Applies to", `${humanise(row.appliesTo)} · ${row.appliesToValue}`], ["Printed", row.printedCount.toLocaleString()], ["Created", fmtDateTime(row.createdAt)]]} /></Section>
      <div className="act"><Link className="btn primary block" to={`/labels/templates/${row.templateId}`}>Open label editor</Link></div>
    </>)).catch(setError);
  };

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Templates" }]} />
      <PageHeader title="Templates" sub="Certificate and label versions are effective-dated; issued output always keeps the version it used."
        actions={<Link className="btn primary" to="/templates/certificates/new">New certificate template</Link>} />
      <div className="stack">
        <div className="tabs" role="tablist" aria-label="Template kind">
          <button type="button" role="tab" aria-selected={tab === "all"} onClick={() => setTab("all")}>All <span className="n">{certificates.length + labels.length}</span></button>
          <button type="button" role="tab" aria-selected={tab === "certificates"} onClick={() => setTab("certificates")}>Certificates <span className="n">{certificates.length}</span></button>
          <button type="button" role="tab" aria-selected={tab === "labels"} onClick={() => setTab("labels")}>Labels <span className="n">{labels.length}</span></button>
        </div>
        <ErrorNote error={error} />
        {tab !== "labels" && <section className="card" aria-labelledby="certificate-templates-heading"><div className="card-header"><h2 id="certificate-templates-heading">Certificates</h2><span className="meta">one engine · form-specific layouts</span></div>
          <DataGrid columns={certificateColumns} page={certPage} rowId={(row) => `certificate:${row.templateId}`} onPage={() => undefined} onRowClick={openCertificate} activeRowId={active} emptyText="No certificate templates." />
        </section>}
        {tab !== "certificates" && <section className="card" aria-labelledby="label-templates-heading"><div className="card-header"><h2 id="label-templates-heading">Labels</h2><span className="meta">read-only here · edit in the label designer</span></div>
          <DataGrid columns={labelColumns} page={labelPage} rowId={(row) => `label:${row.templateId}`} onPage={() => undefined} onRowClick={openLabel} activeRowId={active} emptyText="No label templates." />
        </section>}
      </div>
    </main>
    <Rail label="Templates"><Section title="Version rule"><p className="muted">An active version is immutable. A layout change creates a draft, and activation retires the prior version without changing output already issued.</p></Section></Rail>
  </>;
}
