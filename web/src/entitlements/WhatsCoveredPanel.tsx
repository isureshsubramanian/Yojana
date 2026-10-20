import { Link } from "react-router";
import { useEffect, useState } from "react";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { openDrawer, closeDrawer } from "../shell/drawerStore";
import { ErrorNote, Kv } from "../units/common";
import { fmtDate } from "../units/format";
import { coverItemsApi, coverLabel, usageLabel, startClaimUrl, policyCoverUrl, type Contribution, type UnitCoverItem, type WhatsCoveredPage } from "./coverItems";

export function WhatsCoveredPanel({ unitId, summary }: { unitId: string; summary: WhatsCoveredPage | null }) {
  const [search, setSearch] = useState("");
  const [internal, setInternal] = useState(false);
  return <div className="stack">
    <div className="card toolbar" style={{ padding: 12 }}><input className="input" aria-label="Find a cause, part or service" placeholder="What about… (spill, fan, battery, theft)" value={search} onChange={(e) => setSearch(e.target.value)} /><div className="segmented" role="group" aria-label="Wording"><button type="button" aria-pressed={!internal} onClick={() => { setInternal(false); closeDrawer(); }}>Customer wording</button><button type="button" aria-pressed={internal} onClick={() => { setInternal(true); closeDrawer(); }}>Internal detail</button></div></div>
    {summary && <><CoverStrip data={summary} /><p className="hint">As of {summary.asOf} · each cover uses its own business time zone.</p></>}
    <div className="cols-2" style={{ alignItems: "start" }}><CoveredTable unitId={unitId} covered search={search} internal={internal} />
    <CoveredTable unitId={unitId} covered={false} search={search} internal={internal} /></div>
    {summary?.notice && <p className="hint">{summary.notice}</p>}
  </div>;
}

function CoveredTable({ unitId, covered, search, internal }: { unitId: string; covered: boolean; search: string; internal: boolean }) {
  const [data, setData] = useState<WhatsCoveredPage | null>(null);
  const [page, setPage] = useState(1);
  const [size, setSize] = useState(7);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { setPage(1); }, [unitId, search]);
  useEffect(() => {
    const controller = new AbortController(); setError(null);
    const timer = setTimeout(() => coverItemsApi.unit(unitId, covered ? "covered" : "excluded", search, (page - 1) * size, size, controller.signal).then(setData).catch((e) => { if (!controller.signal.aborted) setError(e); }), 150);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [unitId, covered, search, page, size]);
  return <>
    <section className="card"><div className="card-header"><h2>{covered ? "Covered" : "Not covered"} <span className="meta">{data?.total ?? "…"} items</span></h2><button type="button" className="btn ghost sm" onClick={() => { setSize(size === 7 ? 200 : 7); setPage(1); }}>{size === 7 ? "Show all" : "Show fewer"}</button></div>
      <ErrorNote error={error} />
      <CoverItemsTable data={data} covered={covered} internal={internal} page={page} size={size} onPage={setPage} />
    </section>
  </>;
}
export function CoverStrip({ data }: { data: WhatsCoveredPage }) {
  return <KpiStrip label="Live covers" kpis={data.covers.map((c) => ({ label: c.familyName, value: c.future ? `from ${fmtDate(c.from)}` : c.usage.length ? usageLabel(c) : `to ${fmtDate(c.until)}`, detail: `${c.policyName} v${c.policyVersion} · ${c.future ? "not yet running · " : ""}until ${fmtDate(c.until)}` }))} />;
}
export function CoverItemsTable({ data, covered, internal = false, page = 1, size = 7, onPage = () => undefined }: { data: WhatsCoveredPage | null; covered: boolean; internal?: boolean; page?: number; size?: number; onPage?: (p: number) => void }) {
  const columns: GridColumn<UnitCoverItem>[] = [
    { key: "what", label: "What", width: 43, render: (r) => <div className="wrap">{r.customerWording}<span className="sub">{r.category ?? "Needs category"}</span>{internal && [...r.grants, ...r.excludes].map((x) => <span className="sub" key={`${x.cover.entitlementId}:${x.code}`}>{x.code}{x.internalNote ? ` · ${x.internalNote}` : ""}</span>)}</div> },
    { key: "cover", label: covered ? "Covered by" : "Excluded by", width: 30, render: (r) => <>{(covered ? r.grants : [...r.excludes, ...r.grants]).map((x) => <div key={`${x.cover.entitlementId}:${x.code}`} className="wrap"><StatusBadge tone={x.cover.future ? "info" : covered ? "ok" : "neutral"}>{x.cover.familyName}</StatusBadge><span className="sub">{x.cover.policyName} v{x.cover.policyVersion}{x.cover.future ? ` · from ${fmtDate(x.cover.from)}` : ""}</span></div>)}</> },
    { key: "until", label: covered ? "Until · limit" : "Can it be covered?", width: 27, render: (r) => covered ? <>{r.grants.map((x) => <div className="wrap" key={`${x.cover.entitlementId}:${x.code}`}>{fmtDate(x.cover.until)}<span className="sub">{usageLabel(x.cover)}{x.cover.future ? ` · from ${fmtDate(x.cover.from)}` : ""}</span></div>)}</> : r.canBeCovered },
  ];
  return <DataGrid columns={columns} rowId={(r) => r.key} page={{ rows: data?.items ?? [], total: data?.total ?? 0, page, pageSize: size, continuation: null }} onPage={onPage} onRowClick={(r) => openCoverItem(r, internal, data?.unitId)} loading={!data} />;
}
export function openCoverItem(item: UnitCoverItem, internal: boolean, unitId?: string) {
  openDrawer({ eyebrow: item.covered ? "Covered item" : "Not covered", title: item.customerWording }, <CoverItemDetail item={item} internal={internal} unitId={unitId} />);
}
export function CoverItemDetail({ item, internal, unitId }: { item: UnitCoverItem; internal: boolean; unitId?: string | undefined }) {
  const rows = (contributions: Contribution[], verb: string) => contributions.map((x) => <div className="job" key={`${x.cover.entitlementId}:${x.code}`}><div><div className="t">{x.cover.familyName} {verb}</div><div className="s">{coverLabel(x.cover)} · {x.customerWording}</div><p>{x.cover.future ? `from ${fmtDate(x.cover.from)} · ` : ""}until {fmtDate(x.cover.until)} · {usageLabel(x.cover)}</p><Kv rows={[["Applies to", x.partsScopeDescription ?? "All parts"], ["As of", fmtDate(x.cover.asOf)]]} />{internal && <p>{x.code} · {x.internalNote ?? "No internal note"}</p>}</div></div>);
  const policies = [...new Map([...item.grants, ...item.excludes].map((c) => [`${c.cover.policyCode}:${c.cover.policyVersion}`, c.cover])).values()];
  return <><h3>{item.customerWording}</h3><p>{item.category ?? "Needs category"}</p>{rows(item.grants, "grants it")}{rows(item.excludes, "excludes it")}<div className="actions">{unitId && <Link className="btn sm" to={startClaimUrl(unitId, item.causeCodes)}>Start a claim for this</Link>}{policies.map((c) => { const url = policyCoverUrl(c); return url ? <Link key={url} className="btn sm" to={url}>Open {c.policyName} v{c.policyVersion}</Link> : null; })}</div></>;
}
