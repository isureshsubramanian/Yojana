import { useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { DataGrid, type GridColumn } from "../components/DataGrid";
import { StatusBadge } from "../components/StatusBadge";
import type { PageResult } from "../components/types";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { openDrawer } from "../shell/drawerStore";
import { Rail } from "../shell/Drawer";
import { ErrorNote, Kv, Section } from "../units/common";
import { certificateApi } from "./api";
import { fmtDateTime, formLabel, humanise, statusTone } from "./format";
import type { CertificateListRow, Page } from "./types";
import "./certificates.css";

const PAGE_SIZE = 50;

export function Component() {
  const [search, setSearch] = useSearchParams();
  const [result, setResult] = useState<Page<CertificateListRow> | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(false);
  const [active, setActive] = useState<string | null>(null);
  const page = Math.max(1, Number(search.get("page") ?? "1") || 1);
  const kind = search.get("kind") ?? "";
  const status = search.get("status") ?? "";
  const serial = search.get("serial") ?? "";

  useEffect(() => {
    const controller = new AbortController(); setLoading(true); setError(null);
    const params = new URLSearchParams({ skip: String((page - 1) * PAGE_SIZE), take: String(PAGE_SIZE) });
    if (kind) params.set("kind", kind); if (status) params.set("status", status); if (serial) params.set("unitSerial", serial);
    certificateApi.list(params, controller.signal).then(setResult).catch((reason) => { if (!controller.signal.aborted) setError(reason); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [page, kind, status, serial]);

  const set = (changes: Record<string, string>) => {
    const next = new URLSearchParams(search); next.set("page", "1");
    for (const [key, value] of Object.entries(changes)) value ? next.set(key, value) : next.delete(key);
    setSearch(next);
  };
  const columns = useMemo<GridColumn<CertificateListRow>[]>(() => [
    { key: "tracking", label: "Certificate", width: 22, mono: true, render: (row) => <Link to={`/certificates/${row.certificateId}`}>{row.trackingNumber}</Link> },
    { key: "unit", label: "Unit", width: 22, mono: true, render: (row) => <>{row.primarySerial}<span className="sub">{humanise(row.documentKind)}</span></> },
    { key: "form", label: "Form", width: 19, render: (row) => <>{formLabel(row.formType)}<span className="sub">version {row.version}</span></> },
    { key: "status", label: "Status", width: 14, render: (row) => <StatusBadge tone={statusTone(row.status)}>{humanise(row.status)}</StatusBadge> },
    { key: "issued", label: "Issued", width: 23, render: (row) => <>{fmtDateTime(row.issuedAt)}<span className="sub">{humanise(row.provenance)}</span></> },
  ], []);
  const grid: PageResult<CertificateListRow> = result ? {
    rows: result.items, total: result.total, page, pageSize: result.take, continuation: null,
  } : { rows: [], total: 0, page, pageSize: PAGE_SIZE, continuation: null };
  const open = (row: CertificateListRow) => {
    setActive(row.certificateId);
    openDrawer({ eyebrow: `${humanise(row.documentKind)} · version ${row.version}`, title: row.trackingNumber, mono: true }, <>
      <Section title="Certificate"><Kv rows={[["Unit", row.primarySerial], ["Form", formLabel(row.formType)], ["Status", humanise(row.status)], ["Issued", fmtDateTime(row.issuedAt)], ["Provenance", humanise(row.provenance)]]} /></Section>
      <div className="act"><Link className="btn primary block" to={`/certificates/${row.certificateId}`}>Open certificate</Link><Link className="btn block" to={`/units/${row.unitId}`}>Open unit</Link></div>
    </>);
  };

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Certificates" }]} />
      <PageHeader title="Certificates" sub="Immutable release records and warranty certificates, with separate version chains."
        actions={<Link className="btn" to="/templates">Template catalogue</Link>} />
      <div className="stack">
        <section className="card" aria-label="Certificate filters"><div className="card-body toolbar">
          <input className="input certificate-serial-filter" aria-label="Unit serial" placeholder="Unit serial" value={serial} onChange={(event) => set({ serial: event.target.value })} />
          <select className="chip-select" aria-label="Document kind" value={kind} onChange={(event) => set({ kind: event.target.value })}><option value="">Document · All</option><option value="release_record">Release record</option><option value="warranty_certificate">Warranty certificate</option></select>
          <select className="chip-select" aria-label="Status" value={status} onChange={(event) => set({ status: event.target.value })}><option value="">Status · All</option><option value="issued">Issued</option><option value="superseded">Superseded</option><option value="draft">Draft</option></select>
        </div><ErrorNote error={error} /></section>
        <section className="card" aria-labelledby="certificate-list-heading"><div className="card-header"><h2 id="certificate-list-heading">Certificate records</h2><span className="meta">{result ? `${result.total.toLocaleString()} versions` : "Loading"}</span></div>
          <DataGrid columns={columns} page={grid} rowId={(row) => row.certificateId} onPage={(next) => set({ page: String(next) })} loading={loading}
            onRowClick={open} activeRowId={active} emptyText="No certificates match these filters." viewportHeight={560} />
        </section>
      </div>
    </main>
    <Rail label="Certificate records"><Section title="Two chains"><p className="muted">Build and identity facts version the release record. Ownership and entitlement facts version the warranty certificate. Neither chain changes the other.</p></Section></Rail>
  </>;
}
