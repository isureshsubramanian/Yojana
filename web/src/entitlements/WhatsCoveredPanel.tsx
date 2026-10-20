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
  const [data, setData] = useState(summary);
  const [paging, setPaging] = useState({ coveredPage: 1, coveredSize: 7, excludedPage: 1, excludedSize: 7 });
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    // The unit page supplies the initial sections and counts in one read; only changed filters/pages fetch again.
    if (!search && paging.coveredPage === 1 && paging.excludedPage === 1 && paging.coveredSize === 7 && paging.excludedSize === 7) {
      setData(summary); return;
    }
    const controller = new AbortController();
    const timer = setTimeout(() => {
      setError(null);
      coverItemsApi.sections(unitId, search, (paging.coveredPage - 1) * paging.coveredSize, paging.coveredSize,
        (paging.excludedPage - 1) * paging.excludedSize, paging.excludedSize, controller.signal)
        .then(setData).catch((e) => { if (!controller.signal.aborted) setError(e); });
    }, 150);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [unitId, summary, search, paging]);
  const table = (covered: boolean) => {
    const page = covered ? paging.coveredPage : paging.excludedPage;
    const size = covered ? paging.coveredSize : paging.excludedSize;
    const total = covered ? data?.coveredTotal : data?.excludedTotal;
    const onPage = (value: number) => setPaging((p) => ({ ...p, [covered ? "coveredPage" : "excludedPage"]: value }));
    return <section className="card"><div className="card-header"><h2>{covered ? "Covered" : "Not covered"} <span className="meta">{total ?? "…"} items</span></h2><button type="button" className="btn ghost sm" onClick={() => setPaging((p) => ({ ...p, [covered ? "coveredSize" : "excludedSize"]: size === 7 ? 200 : 7, [covered ? "coveredPage" : "excludedPage"]: 1 }))}>{size === 7 ? "Show all" : "Show fewer"}</button></div>
      <CoverItemsTable data={data ? { ...data, items: data.items.filter((x) => x.covered === covered), total: total ?? 0 } : null} covered={covered} internal={internal} page={page} size={size} onPage={onPage} />
    </section>;
  };
  return <div className="stack">
    <div className="card toolbar" style={{ padding: 12 }}><input className="input" aria-label="Find a cause, part or service" placeholder="What about… (spill, fan, battery, theft)" value={search} onChange={(e) => { setSearch(e.target.value); setPaging((p) => ({ ...p, coveredPage: 1, excludedPage: 1 })); }} /><div className="segmented" role="group" aria-label="Wording"><button type="button" aria-pressed={!internal} onClick={() => { setInternal(false); closeDrawer(); }}>Customer wording</button><button type="button" aria-pressed={internal} onClick={() => { setInternal(true); closeDrawer(); }}>Internal detail</button></div></div>
    {data && <><CoverStrip data={data} /><p className="hint">As of {data.asOf} · each cover uses its own business time zone.</p></>}
    <ErrorNote error={error} />
    <div className="cols-2" style={{ alignItems: "start" }}>{table(true)}{table(false)}</div>
    {data?.notice && <p className="hint">{data.notice}</p>}
  </div>;
}

export function CoverStrip({ data }: { data: WhatsCoveredPage }) {
  return <KpiStrip label="Live covers" kpis={data.covers.map((c) => ({ label: c.familyName, value: c.future ? `from ${fmtDate(c.from)}` : c.usage.length ? usageLabel(c) : `to ${fmtDate(c.until)}`, detail: `${c.policyName} v${c.policyVersion} · ${c.future ? "not yet running · " : ""}until ${fmtDate(c.until)}` }))} />;
}
export function CoverItemsTable({ data, covered, internal = false, page = 1, size = 7, onPage = () => undefined }: { data: WhatsCoveredPage | null; covered: boolean; internal?: boolean; page?: number; size?: number; onPage?: (p: number) => void }) {
  const columns: GridColumn<UnitCoverItem>[] = [
    { key: "what", label: "What", width: 43, render: (r) => <div className="wrap">{r.customerWording}<span className="sub">{r.category ?? "Needs category"}</span>{r.causeNames?.length ? <span className="sub">{r.causeNames.join(", ")}</span> : null}{internal && [...r.grants, ...r.excludes].map((x) => <span className="sub" key={`${x.cover.entitlementId}:${x.code}`}>{x.code}{x.internalNote ? ` · ${x.internalNote}` : ""}</span>)}</div> },
    { key: "cover", label: covered ? "Covered by" : "Excluded by", width: 30, render: (r) => <>{(covered ? r.grants : r.excludes).map((x) => <div key={`${x.cover.entitlementId}:${x.code}`} className="wrap"><StatusBadge tone={x.cover.future ? "info" : covered ? "ok" : "neutral"}>{x.cover.familyName}</StatusBadge><span className="sub">{x.cover.policyName} v{x.cover.policyVersion}{x.cover.future ? ` · From ${fmtDate(x.cover.from)}` : ""}</span></div>)}</> },
    { key: "until", label: covered ? "Until · limit" : "Can it be covered?", width: 27, render: (r) => covered ? <>{r.grants.map((x) => <div className="wrap" key={`${x.cover.entitlementId}:${x.code}`}>{fmtDate(x.cover.until)}<span className="sub">{usageLabel(x.cover)}{x.cover.future ? ` · From ${fmtDate(x.cover.from)}` : ""}</span></div>)}</> : r.canBeCovered },
  ];
  return <DataGrid columns={columns} rowId={(r) => r.key} page={{ rows: data?.items ?? [], total: data?.total ?? 0, page, pageSize: size, continuation: null }} onPage={onPage} onRowClick={(r) => openCoverItem(r, internal, data?.unitId)} loading={!data} />;
}
export function openCoverItem(item: UnitCoverItem, internal: boolean, unitId?: string) {
  openDrawer({ eyebrow: item.covered ? "Covered item" : "Not covered", title: item.customerWording }, <CoverItemDetail item={item} internal={internal} unitId={unitId} />);
}
export function CoverItemDetail({ item, internal, unitId }: { item: UnitCoverItem; internal: boolean; unitId?: string | undefined }) {
  const rows = (contributions: Contribution[], verb: string) => contributions.map((x) => <div className="job" key={`${x.cover.entitlementId}:${x.code}`}><div><div className="t">{x.cover.familyName} {verb}</div><div className="s">{coverLabel(x.cover)} · {x.customerWording}</div><p>{x.cover.future ? `from ${fmtDate(x.cover.from)} · ` : ""}until {fmtDate(x.cover.until)} · {usageLabel(x.cover)}</p><Kv rows={[["Applies to", x.partsScopeDescription ?? "All parts"], ["As of", fmtDate(x.cover.asOf)]]} />{internal && <p>{x.code} · {x.internalNote ?? "No internal note"}</p>}</div></div>);
  const policies = [...new Map([...item.grants, ...item.excludes].map((c) => [`${c.cover.policyCode}:${c.cover.policyVersion}`, c.cover])).values()];
  return <><h3>{item.customerWording}</h3><p>{item.category ?? "Needs category"}</p>{item.causeNames?.length ? <p>Causes: {item.causeNames.join(", ")}</p> : null}{rows(item.grants, "grants it")}{rows(item.excludes, "excludes it")}<div className="actions">{unitId && <Link className="btn sm" to={startClaimUrl(unitId, item.causeCodes)}>Start a claim for this</Link>}{policies.map((c) => { const url = policyCoverUrl(c); return url ? <Link key={url} className="btn sm" to={url}>Open {c.policyName} v{c.policyVersion}</Link> : null; })}</div></>;
}
