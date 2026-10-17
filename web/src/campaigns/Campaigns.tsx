import { useEffect, useState } from "react";
import { Link, useNavigate } from "react-router";
import { DataGrid, StatusBadge, type GridColumn } from "../components";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { fmtDate } from "../units/format";
import { campaignApi } from "./api";
import { campaignCoverSummary, campaignSchedule, campaignTone, title } from "./format";
import type { CampaignListRow, Page } from "./types";

const PAGE = 50;
const TODAY = new Date().toISOString().slice(0, 10);

export function Component() {
  const navigate = useNavigate();
  const [status, setStatus] = useState("");
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Page<CampaignListRow> | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    const controller = new AbortController(); setLoading(true); setError(null);
    campaignApi.list(status || null, (page - 1) * PAGE, PAGE, controller.signal)
      .then((result) => { setData(result); setLoading(false); })
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) { setError(reason); setLoading(false); } });
    return () => controller.abort();
  }, [status, page]);
  const columns: GridColumn<CampaignListRow>[] = [
    { key: "code", label: "Code · name", width: 25, mono: true, render: (row) => <>{row.code}<span className="sub" style={{ fontFamily: "var(--font-body)" }}>{row.name}</span></> },
    { key: "source", label: "Cover · reason", width: 17, render: (row) => <>{title(row.coverageSource)} · {title(row.reasonCode)}<span className="sub">{campaignCoverSummary(row.partsPct, row.labourPct, row.travelPct)}</span></> },
    { key: "status", label: "Status", width: 12, render: (row) => { const schedule = campaignSchedule(row.runsUntil, TODAY); return <><StatusBadge tone={schedule.ended ? "neutral" : campaignTone(row.status)}>{schedule.ended ? "Ended" : title(row.status)}</StatusBadge><span className="sub">{schedule.label}</span></>; } },
    { key: "units", label: "Units selected", width: 13, numeric: true, render: (row) => row.selectedUnits.toLocaleString() },
    { key: "date", label: "Selected", width: 12, render: (row) => fmtDate(row.selectedAt) },
    { key: "reserve", label: "Reserve tag", width: 21, mono: true, render: (row) => row.reserveTag },
  ];
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Campaigns" }]} />
    <div className="page-header"><div><div className="eyebrow">Claims · recall and goodwill</div><h1>Campaigns</h1><p className="sub">Criteria-led unit selections with their own reserve and snapshotted recipients</p></div><div className="actions"><Link className="btn primary" to="/claims/campaigns/new">New campaign</Link></div></div>
    <section className="card" style={{ marginBottom: "var(--gap)" }}><div className="card-body"><div className="toolbar"><label className="field" style={{ maxWidth: 220 }}><span className="lbl">Status</span><select className="select" value={status} onChange={(event) => { setStatus(event.target.value); setPage(1); }}><option value="">All statuses</option><option value="draft">Draft</option><option value="selected">Selected</option><option value="active">Active</option><option value="closed">Closed</option></select></label></div></div></section>
    <section className="card" aria-labelledby="campaign-list"><div className="card-header"><h2 id="campaign-list">Campaigns <span className="meta">{data?.total.toLocaleString() ?? "—"} total</span></h2><span className="meta">Updated most recently first</span></div>
      <DataGrid columns={columns} page={{ rows: data?.items ?? [], total: data?.total ?? 0, page, pageSize: PAGE, continuation: null }} rowId={(row) => row.campaignId} onPage={setPage} onRowClick={(row) => navigate(`/claims/campaigns/${row.campaignId}`)} loading={loading} emptyText="No campaigns match this status." />
    </section>
    <ErrorNote error={error} />
  </main>;
}
