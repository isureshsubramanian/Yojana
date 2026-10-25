import { useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { DataGrid, type GridColumn } from "../components/DataGrid";
import { useGridQuery } from "../components/useGridQuery";
import { StatusBadge } from "../components/StatusBadge";
import type { PageResult } from "../components/types";
import { useSession } from "../session/session";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { openDrawer } from "../shell/drawerStore";
import { Rail } from "../shell/Drawer";
import { ErrorNote, Kv, Section } from "../units/common";
import { useReferenceNames } from "../corrections/ReferenceFields";
import { certificateListPageSchema } from "./api";
import { formLabel, humanise, statusTone } from "./format";
import type { CertificateListRow } from "./types";
import "./certificates.css";

const PAGE_SIZE = 50;

export function Component() {
  const [search, setSearch] = useSearchParams();
  const session = useSession();
  const [active, setActive] = useState<string | null>(null);
  const kind = search.get("kind") ?? "";
  const status = search.get("status") ?? "";
  const serial = search.get("serial") ?? "";
  const filters = useMemo(() => { const value = new URLSearchParams(); if (kind) value.set("kind", kind); if (status) value.set("status", status); if (serial) value.set("unitSerial", serial); return value; }, [kind, status, serial]);
  const query = useGridQuery("/api/certificates/", certificateListPageSchema, { gridId: "certificates", userId: session.user.id, defaultSort: { key: "issued", dir: "desc" }, defaultSize: PAGE_SIZE, params: filters, sortKeys: ["tracking_number", "issued", "status"] });
  const result = query.data;
  const names = useReferenceNames({ items: result?.items.map((row) => row.itemId) ?? [] });

  const set = (changes: Record<string, string>) => {
    const next = new URLSearchParams(search); next.delete("page");
    for (const [key, value] of Object.entries(changes)) value ? next.set(key, value) : next.delete(key);
    setSearch(next);
  };
  const columns = useMemo<GridColumn<CertificateListRow>[]>(() => [
    { key: "tracking", label: "Certificate", width: 22, mono: true, sort: { key: "tracking_number", defaultDir: "asc" }, render: (row) => <Link to={`/certificates/${row.certificateId}`}>{row.trackingNumber}</Link> },
    { key: "unit", label: "Unit", width: 22, render: (row) => <><span className="mono">{row.primarySerial}</span><span className="sub">{names.itemName(row.itemId) ?? "Item name unavailable"}</span></> },
    { key: "form", label: "Form", width: 19, render: (row) => <>{formLabel(row.formType, row.documentKind)}<span className="sub">version {row.version}</span></> },
    { key: "status", label: "Status", width: 14, sort: { key: "status", defaultDir: "asc" }, render: (row) => <StatusBadge tone={statusTone(row.status)}>{humanise(row.status)}</StatusBadge> },
    { key: "issued", label: "Issued", width: 23, sort: { key: "issued", defaultDir: "desc" }, render: (row) => <>{row.issuedAtDisplay}<span className="sub">{provenanceLabel(row)}</span></> },
  ], [names]);
  const grid: PageResult<CertificateListRow> = result ? {
    rows: result.items, total: result.total, page: query.page, pageSize: result.take, continuation: null,
  } : { rows: [], total: 0, page: query.page, pageSize: query.pageSize, continuation: null };
  const open = (row: CertificateListRow) => {
    setActive(row.certificateId);
    openDrawer({ eyebrow: `${humanise(row.documentKind)} · version ${row.version}`, title: row.trackingNumber, mono: true }, <>
      <Section title="Certificate"><Kv rows={[["Unit", `${row.primarySerial} · ${names.itemName(row.itemId) ?? "Item name unavailable"}`], ["Form", formLabel(row.formType, row.documentKind)], ["Status", humanise(row.status)], ["Issued", row.issuedAtDisplay], ["Provenance", provenanceLabel(row)]]} /></Section>
      <div className="act"><Link className="btn primary block" to={`/certificates/${row.certificateId}`}>Open certificate</Link><Link className="btn block" to={`/units/${row.unitId}`}>Open unit</Link></div>
    </>);
  };

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Certificates" }]} />
      <PageHeader title="Certificates" sub="Immutable release records and warranty certificates, with separate version chains."
        actions={<><a className="btn" href={`/api/certificates/export?${new URLSearchParams({ ...Object.fromEntries(filters), sort: query.sort.key, dir: query.sort.dir })}`} download>Export CSV</a><Link className="btn" to="/templates">Template catalogue</Link><Link className="btn primary" to="/certificates/issue">Issue certificates</Link></>} />
      <div className="stack">
        <section className="card" aria-label="Certificate filters"><div className="card-body toolbar">
          <input className="input certificate-serial-filter" aria-label="Unit serial" placeholder="Unit serial" value={serial} onChange={(event) => set({ serial: event.target.value })} />
          <label className="chip-field"><span className="chip-k">Document</span><select className="chip-select" aria-label="Document kind" value={kind} onChange={(event) => set({ kind: event.target.value })}><option value="">All</option><option value="release_record">Release record</option><option value="warranty_certificate">Warranty certificate</option></select></label>
          <label className="chip-field"><span className="chip-k">Status</span><select className="chip-select" aria-label="Status" value={status} onChange={(event) => set({ status: event.target.value })}><option value="">All</option><option value="issued">Issued</option><option value="superseded">Superseded</option><option value="draft">Draft</option></select></label>
        </div><ErrorNote error={query.error} /></section>
        <section className="card" aria-labelledby="certificate-list-heading"><div className="card-header"><h2 id="certificate-list-heading">Certificate records</h2><span className="meta">{result ? `${result.total.toLocaleString()} versions` : "Loading"}</span></div>
          <DataGrid columns={columns} page={grid} rowId={(row) => row.certificateId} onPage={query.onPage} loading={query.loading}
            gridId="certificates" userId={session.user.id} sort={query.sort} defaultSort={{ key: "issued", dir: "desc" }} onSort={query.onSort} pageSize={query.pageSize} defaultPageSize={PAGE_SIZE} onPageSize={query.onPageSize}
            onRowClick={open} activeRowId={active} emptyText="No certificates match these filters." viewportHeight={560} />
        </section>
      </div>
    </main>
    <Rail label="Certificate records"><Section title="Two chains"><p className="muted">Build and identity facts version the release record. Ownership and entitlement facts version the warranty certificate. Neither chain changes the other.</p></Section></Rail>
  </>;
}

function provenanceLabel(row: CertificateListRow): string {
  return row.issuedBy ? `Issued by ${row.issuedBy}` : humanise(row.provenance);
}
