import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router";
import { api } from "../api/client";
import { DataGrid, FindBar, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { useReferenceNames } from "../corrections/ReferenceFields";
import { MAX_REFERENCE_NAMES, type PartyReferenceLabel } from "../corrections/referenceStore";
import { campaignApi } from "../campaigns/api";
import type { CampaignListRow } from "../campaigns/types";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, UnitLink } from "../units/common";
import { fmtDate } from "../units/format";
import { claimsApi, type ClaimFilters } from "./api";
import { CLAIM_STATUSES, COVERAGE_SOURCES, ageBucketLabel, claimTone, coverageSourceLabel, csvCell, evidenceCue, humanise, money, moneyTotals } from "./format";
import type { ClaimDetail, ClaimListRow, ClaimPage, ClaimSummary, CurrencyInfo } from "./types";
import "./claims.css";

const PAGE_SIZE = 50;
const INITIAL_FILTERS: ClaimFilters = { status: "open", coverageSource: "", serviceCountry: "", recovery: "", q: "", campaignId: "" };

export function Component() {
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const campaignParam = params.get("campaignId") ?? "";
  const [filters, setFilters] = useState<ClaimFilters>(() => ({ ...INITIAL_FILTERS, campaignId: campaignParam }));
  const [campaigns, setCampaigns] = useState<CampaignListRow[]>([]);
  const [pageNumber, setPageNumber] = useState(1);
  const [page, setPage] = useState<ClaimPage | null>(null);
  const [summary, setSummary] = useState<ClaimSummary | null>(null);
  const [catalogue, setCatalogue] = useState<CurrencyInfo[]>([]);
  const [active, setActive] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [exporting, setExporting] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const load = useCallback((signal?: AbortSignal) => {
    setLoading(true); setError(null);
    return Promise.all([
      claimsApi.list(filters, (pageNumber - 1) * PAGE_SIZE, PAGE_SIZE, signal),
      claimsApi.summary(signal),
      claimsApi.currencyCatalogue(signal),
    ]).then(([nextPage, nextSummary, nextCatalogue]) => { setPage(nextPage); setSummary(nextSummary); setCatalogue(nextCatalogue); })
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); })
      .finally(() => setLoading(false));
  }, [filters, pageNumber]);

  useEffect(() => { const controller = new AbortController(); void load(controller.signal); return () => controller.abort(); }, [load]);
  useEffect(() => { setFilters((current) => current.campaignId === campaignParam ? current : { ...current, campaignId: campaignParam }); setPageNumber(1); }, [campaignParam]);
  useEffect(() => {
    const controller = new AbortController();
    const loadCampaigns = async () => {
      const all: CampaignListRow[] = [];
      let skip = 0;
      while (true) {
        const result = await campaignApi.list(null, skip, 200, controller.signal);
        all.push(...result.items); skip += result.items.length;
        if (!result.items.length || skip >= result.total) break;
      }
      setCampaigns(all);
    };
    void loadCampaigns()
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    return () => controller.abort();
  }, []);
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key.toLowerCase() === "n" && !(event.metaKey || event.ctrlKey || event.altKey) && !(event.target instanceof HTMLElement && event.target.closest("input, textarea, select, [contenteditable]"))) navigate("/claims/new");
    };
    window.addEventListener("keydown", onKey); return () => window.removeEventListener("keydown", onKey);
  }, [navigate]);

  const rows = page?.items ?? [];
  const names = useReferenceNames({ items: rows.map((row) => row.itemId), parties: rows.flatMap((row) => [row.claimantPartyRef, row.settlementPartyRef]) });
  const setFilter = (patch: Partial<ClaimFilters>) => {
    setFilters((current) => ({ ...current, ...patch })); setPageNumber(1);
    if (patch.campaignId !== undefined) {
      const next = new URLSearchParams(params);
      if (patch.campaignId) next.set("campaignId", patch.campaignId); else next.delete("campaignId");
      setParams(next, { replace: true });
    }
  };
  const openClaim = async (row: ClaimListRow) => {
    setActive(row.claimId);
    openDrawer({ eyebrow: "Claim", title: row.claimNumber, mono: true }, <p className="muted">Loading claim…</p>);
    try {
      const detail = await claimsApi.detail(row.claimId);
      updateDrawer({ eyebrow: "Claim", title: row.claimNumber, mono: true }, <ClaimDrawer detail={detail} catalogue={catalogue} />);
    } catch (reason) {
      updateDrawer({ eyebrow: "Claim", title: row.claimNumber, mono: true }, <ErrorNote error={reason} />);
    }
  };
  const exportCurrent = async () => {
    setExporting(true); setError(null);
    try {
      const all: ClaimListRow[] = [];
      let skip = 0;
      while (true) {
        const batch = await claimsApi.list(filters, skip, 500);
        all.push(...batch.items); skip += batch.items.length;
        if (!batch.items.length || skip >= batch.total) break;
      }
      const exportParties: Record<string, PartyReferenceLabel> = {};
      const partyIds = [...new Set(all.flatMap((row) => [row.claimantPartyRef, row.settlementPartyRef]))];
      for (let start = 0; start < partyIds.length; start += MAX_REFERENCE_NAMES) {
        const resolved = await api<{ parties: Record<string, PartyReferenceLabel> }>("/api/serialisation/reference-names", { method: "POST", body: { items: [], parties: partyIds.slice(start, start + MAX_REFERENCE_NAMES), documents: [] } });
        Object.assign(exportParties, resolved.parties);
      }
      const partyName = (id: string) => exportParties[id]?.name ?? exportParties[id]?.code ?? "Name unavailable";
      const header = ["Claim", "Serial", "Claimant", "Settlement party", "Coverage source", "Status", "Service country", "Currency", "Requested", "Approved", "Age days"];
      const lines = [header, ...all.map((row) => [row.claimNumber, row.serial, partyName(row.claimantPartyRef), partyName(row.settlementPartyRef), coverageSourceLabel(row.coverageSource, row.campaignCode), row.status, row.serviceCountry, row.currency, row.requestedAmount, row.approvedAmount, row.ageDays])]
        .map((line) => line.map(csvCell).join(",")).join("\n");
      const url = URL.createObjectURL(new Blob([lines], { type: "text/csv;charset=utf-8" }));
      const anchor = document.createElement("a"); anchor.href = url; anchor.download = "claims.csv"; anchor.click(); URL.revokeObjectURL(url);
    } catch (reason) { setError(reason); } finally { setExporting(false); }
  };

  const columns = useMemo<GridColumn<ClaimListRow>[]>(() => [
    { key: "claim", label: "Claim", width: 12, render: (row) => <span className="primary"><span className="t serial">{row.claimNumber}</span><span className="sub">{row.evidenceRequested ? "evidence requested" : humanise(row.remedy)}</span></span> },
    { key: "serial", label: "Serial", width: 13, render: (row) => <><UnitLink unitId={row.unitId}>{row.serial ?? "No serial recorded"}</UnitLink><span className="sub">{names.item(row.itemId)}</span></> },
    { key: "party", label: "Claimant · settlement", width: 20, render: (row) => <>{names.party(row.claimantPartyRef)}<span className="sub">settles: {row.claimantPartyRef === row.settlementPartyRef ? "same" : names.party(row.settlementPartyRef)}</span></> },
    { key: "source", label: "Source", width: 9, render: (row) => coverageSourceLabel(row.coverageSource, row.campaignCode) },
    { key: "status", label: "Status", width: 15, render: (row) => <><StatusBadge tone={claimTone(row.status)}>{humanise(row.status)}</StatusBadge><span className="sub">{evidenceCue(row.status, row.evidenceRequested, row.denialReason, row.recoveryRaised, row.linesDecided, row.lineCount)}</span></> },
    { key: "requested", label: "Requested", width: 12, numeric: true, render: (row) => money(row.requestedAmount, row.currency, catalogue) },
    { key: "approved", label: "Approved", width: 12, numeric: true, render: (row) => ["submitted", "adjudicating"].includes(row.status) ? <span className="muted">—</span> : money(row.approvedAmount, row.currency, catalogue) },
    { key: "age", label: "Age", width: 7, numeric: true, render: (row) => `${row.ageDays} d` },
  ], [catalogue, names]);

  const gridPage = { rows, total: page?.total ?? 0, page: pageNumber, pageSize: PAGE_SIZE, continuation: null };
  const statusLabel = filters.status === "open" ? "Open claims" : filters.status === "all" ? "All claims" : humanise(filters.status);
  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Claims" }]} />
      <div className="page-header"><div><h1>Claims</h1><p className="sub">Every warranty, recall, goodwill, contract and chargeable claim · adjudicated per line against the policy stack · ageing counted from submission</p></div><div className="actions">
        <Link className="btn" to="/claims/recovery">Recovery claims · {summary?.recoveryOpenCount.toLocaleString() ?? "—"}</Link>
        <button type="button" className="btn" disabled={exporting} aria-busy={exporting} onClick={() => void exportCurrent()}><Icon name="down" />Export</button>
        <Link className="btn primary" to="/claims/new"><Icon name="plus" />New claim<kbd className="key">N</kbd></Link>
      </div></div>
      <ErrorNote error={error} />
      <div className="stack" style={{ marginTop: error ? 16 : 0 }}>
        <KpiStrip label="Claim figures" kpis={[
          { label: "Open claims", value: summary?.open.toLocaleString() ?? "—", detail: summary ? `${summary.submitted} submitted · ${summary.adjudicating} adjudicating · ${summary.approvedNotSettled} approved, not settled · ${summary.deniedNotSettled} denied, not settled` : null },
          { label: `Approved · ${monthName(summary?.asOf)}`, value: summary ? moneyTotals(summary.approvedThisMonth, catalogue) : "—", detail: summary ? `${summary.approvedCountThisMonth} claims · ${summary.partialCountThisMonth} partially approved` : null },
          { label: `Denied · ${monthName(summary?.asOf)}`, value: summary?.deniedCountThisMonth.toLocaleString() ?? "—", detail: summary?.deniedReasonsThisMonth.map((reason) => `${humanise(reason.reason).toLowerCase()} ${reason.count}`).join(" · ") ?? null },
          { label: "Recovery raised", value: summary ? moneyTotals(summary.recoveryOpen, catalogue) : "—", detail: summary ? `${summary.recoveryOpenCount} claims against ${summary.recoveryVendorCount} vendors` : null },
          { label: "Oldest open", value: summary?.oldestOpenAgeDays == null ? "—" : `${summary.oldestOpenAgeDays} d`, detail: summary?.oldestOpenNumber ? `${summary.oldestOpenNumber} · ${summary.oldestOpenEvidenceRequested ? "waiting for evidence" : humanise(summary.oldestOpenStatus).toLowerCase()}` : "No open claims" },
        ]} />
        <section className="card" aria-labelledby="claims-ageing"><div className="card-header"><h2 id="claims-ageing">Ageing of open claims <span className="meta">from submission · {summary?.open.toLocaleString() ?? "—"} open</span></h2><span className="meta">target: adjudicated within {summary?.adjudicationTargetWorkingDays ?? "—"} working days</span></div><div className="card-body"><div className="claims-age">
          {Array.from({ length: 5 }, (_, index) => { const count = summary?.ageBuckets[index] ?? 0; const pct = summary?.open ? Math.round(count / summary.open * 100) : 0; return <div key={ageBucketLabel(index)}><div className="label">{ageBucketLabel(index)}</div><div className={`value${index === 3 && count ? " warn-text" : ""}`}>{count.toLocaleString()}</div><div className={`progress${index < 2 ? " ok" : ""}`}><i style={{ width: `${pct}%` }} /></div></div>; })}
        </div></div></section>
        <section className="card" aria-labelledby="claims-filters"><div className="card-body"><div className="toolbar" id="claims-filters">
          <FindBar compact query={filters.q} onQuery={(q) => setFilter({ q })} facets={[]} activeFacet={null} onFacet={() => undefined} ariaLabel="Find a claim" placeholder="Claim number or any serial" />
          <FilterSelect label="Status" value={filters.status} onChange={(status) => setFilter({ status })} options={["open", "all", ...CLAIM_STATUSES]} />
          <FilterSelect label="Coverage source" value={filters.coverageSource} onChange={(coverageSource) => setFilter({ coverageSource })} options={["", ...(summary?.coverageSources.length ? summary.coverageSources : COVERAGE_SOURCES)]} empty="All" />
          <select className={`chip-select${filters.campaignId ? " on" : ""}`} aria-label="Campaign" value={filters.campaignId} onChange={(event) => setFilter({ campaignId: event.target.value })}><option value="">Campaign · All</option>{campaigns.map((campaign) => <option value={campaign.campaignId} key={campaign.campaignId}>Campaign · {campaign.code} · {campaign.name}</option>)}</select>
          <FilterSelect label="Service country" value={filters.serviceCountry} onChange={(serviceCountry) => setFilter({ serviceCountry })} options={["", ...(summary?.serviceCountries ?? [])]} empty="All" />
          <FilterSelect label="Recovery" value={filters.recovery} onChange={(recovery) => setFilter({ recovery })} options={["", "raised", "none"]} empty="Any" />
        </div></div></section>
        <section className="card" aria-labelledby="claims-list"><div className="card-header"><h2 id="claims-list">{statusLabel} <span className="meta">{page?.total.toLocaleString() ?? "—"} claims · {summary?.coverageSources.length ?? "—"} coverage sources · {summary?.serviceCountries.length ?? "—"} service countries</span></h2><span className="meta nowrap">Sorted by age, oldest first</span></div>
          <DataGrid columns={columns} page={gridPage} rowId={(row) => row.claimId} onPage={setPageNumber} onRowClick={(row) => void openClaim(row)} activeRowId={active} loading={loading} emptyText="No claims match these filters." viewportHeight={560} footerNote={`${summary?.allClaims.toLocaleString() ?? "—"} claims since go-live`} />
        </section>
      </div>
    </main>
    <Rail label="Claims"><Section title="Ageing"><p className="muted">Age is counted from submission. The target is a tenant setting measured in working days; the five buckets use elapsed calendar days for an immediately legible queue.</p></Section></Rail>
  </>;
}

function FilterSelect({ label, value, options, empty, onChange }: { label: string; value: string; options: readonly string[]; empty?: string | undefined; onChange: (value: string) => void }) {
  return <select className={`chip-select${value ? " on" : ""}`} aria-label={label} value={value} onChange={(event) => onChange(event.target.value)}>{[...new Set(options)].map((option) => <option key={option || "all"} value={option}>{label} · {option ? humanise(option) : empty}</option>)}</select>;
}

function ClaimDrawer({ detail, catalogue }: { detail: ClaimDetail; catalogue: CurrencyInfo[] }) {
  const names = useReferenceNames({ items: [detail.itemId, ...(detail.lineRows ?? []).map((line) => line.itemId)], parties: [detail.claimantPartyRef, detail.settlementPartyRef, ...(detail.recoveries ?? []).map((recovery) => recovery.vendorPartyRef)] });
  const next = detail.status === "submitted" || detail.status === "adjudicating" ? "Open the claim to review the policy decision." : detail.status === "approved" || detail.status === "partially_approved" ? "Settle when fulfilment is complete, then close the claim." : detail.status === "denied" ? "Settle any chargeable work or close the denied claim." : detail.status === "settled" ? "Close the claim when no further work remains." : "No further action is due.";
  return <><h3>Claim</h3><Kv rows={[
    ["Unit", <><span className="serial">{detail.serial ?? "No serial recorded"}</span> · {names.item(detail.itemId)}</>],
    ["Claimant", names.party(detail.claimantPartyRef)], ["Settles with", detail.claimantPartyRef === detail.settlementPartyRef ? "Same as claimant" : names.party(detail.settlementPartyRef)],
    ["Service country", detail.serviceCountry], ["Remedy", humanise(detail.remedy)], ["Open work", detail.openWork ?? "—"],
  ]} /><h3 style={{ marginTop: 24 }}>Lines</h3>{(detail.lineRows ?? []).map((line) => <div className="job" key={line.claimLineId}><div><div className="t">{line.description ?? humanise(line.kind)}</div><div className="s">{humanise(line.kind)} · {line.coveragePct} % coverage</div></div><span>{money(line.approvedAmount, detail.currency, catalogue)}</span></div>)}
    {detail.recoveries?.length ? <><h3 style={{ marginTop: 24 }}>Recovery</h3>{detail.recoveries.map((recovery) => <div className="job" key={recovery.recoveryClaimId}><div><div className="t">{recovery.recoveryNumber}</div><div className="s">{names.party(recovery.vendorPartyRef)}</div></div><StatusBadge tone={claimTone(recovery.status)}>{humanise(recovery.status)}</StatusBadge></div>)}</> : null}
    <h3 style={{ marginTop: 24 }}>Next</h3><p className="muted">{next}</p><div className="act"><Link className="btn primary block" to={`/claims/${detail.claimId}`}>Open claim</Link></div></>;
}

function monthName(date: string | null | undefined): string {
  if (!date) return "this month";
  const value = new Date(`${fmtDate(date)}T00:00:00Z`);
  return Number.isNaN(value.getTime()) ? "this month" : new Intl.DateTimeFormat(undefined, { month: "long", timeZone: "UTC" }).format(value);
}
