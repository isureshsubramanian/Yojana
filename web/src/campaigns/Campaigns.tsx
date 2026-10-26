import { useMemo, useState } from "react";
import { Link, useNavigate } from "react-router";
import { DataGrid, StatusBadge, useGridQuery, type GridColumn } from "../components";
import { useSession } from "../session/session";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { fmtDate } from "../units/format";
import { campaignCoverSummary, campaignSchedule, campaignTone, title } from "./format";
import { campaignListRowSchema, pageSchema, type CampaignListRow } from "./types";

const PAGE = 50;
const TODAY = new Date().toISOString().slice(0, 10);

export function Component() {
  const session = useSession();
  const navigate = useNavigate();
  const [status, setStatus] = useState("");
  const filters = useMemo(() => { const value = new URLSearchParams(); if (status) value.set("status", status); return value; }, [status]);
  const query = useGridQuery("/api/campaigns/", pageSchema(campaignListRowSchema), { gridId: "campaigns", userId: session.user.id, defaultSort: { key: "updated", dir: "desc" }, defaultSize: PAGE, params: filters, sortKeys: ["code", "updated", "status"] });
  const data = query.data;
  const columns: GridColumn<CampaignListRow>[] = [
    { key: "code", label: "Code · name", width: 25, mono: true, sort: { key: "code", defaultDir: "asc" }, render: (row) => <>{row.code}<span className="sub" style={{ fontFamily: "var(--font-body)" }}>{row.name}</span></> },
    { key: "source", label: "Cover · reason", width: 17, render: (row) => <>{title(row.coverageSource)} · {title(row.reasonCode)}<span className="sub">{campaignCoverSummary(row.partsPct, row.labourPct, row.travelPct)}</span></> },
    { key: "status", label: "Status", width: 12, sort: { key: "status", defaultDir: "asc" }, render: (row) => { const schedule = campaignSchedule(row.runsUntil, TODAY); return <><StatusBadge tone={schedule.ended ? "neutral" : campaignTone(row.status)}>{schedule.ended ? "Ended" : title(row.status)}</StatusBadge><span className="sub">{schedule.label}</span></>; } },
    { key: "units", label: "Units selected", width: 13, numeric: true, render: (row) => row.selectedUnits.toLocaleString() },
    { key: "date", label: "Updated", width: 12, sort: { key: "updated", defaultDir: "desc" }, render: (row) => fmtDate(row.updatedAt) },
    { key: "reserve", label: "Reserve tag", width: 21, mono: true, render: (row) => row.reserveTag },
  ];
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Campaigns" }]} />
    <div className="page-header"><div><div className="eyebrow">Claims · recall and goodwill</div><h1>Campaigns</h1><p className="sub">Criteria-led unit selections with their own reserve and snapshotted recipients</p></div><div className="actions"><Link className="btn primary" to="/claims/campaigns/new">New campaign</Link></div></div>
    <section className="card" style={{ marginBottom: "var(--gap)" }}><div className="card-body"><div className="toolbar"><label className="field" style={{ maxWidth: 220 }}><span className="lbl">Status</span><select className="select" value={status} onChange={(event) => setStatus(event.target.value)}><option value="">All statuses</option><option value="draft">Draft</option><option value="selected">Selected</option><option value="active">Active</option><option value="closed">Closed</option></select></label></div></div></section>
    <section className="card" aria-labelledby="campaign-list"><div className="card-header"><h2 id="campaign-list">Campaigns <span className="meta">{data?.total.toLocaleString() ?? "—"} total</span></h2><span className="meta">Updated most recently first</span></div>
      <DataGrid columns={columns} page={{ rows: data?.items ?? [], total: data?.total ?? 0, page: query.page, pageSize: query.pageSize, continuation: null }} rowId={(row) => row.campaignId} onPage={query.onPage} onRowClick={(row) => navigate(`/claims/campaigns/${row.campaignId}`)} loading={query.loading} emptyText="No campaigns match this status." gridId="campaigns" userId={session.user.id} sort={query.sort} defaultSort={{ key: "updated", dir: "desc" }} onSort={query.onSort} pageSize={query.pageSize} defaultPageSize={PAGE} onPageSize={query.onPageSize} />
    </section>
    <ErrorNote error={query.error} />
  </main>;
}
