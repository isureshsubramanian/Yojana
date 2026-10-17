import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useLocation, useParams } from "react-router";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { PartyField, useReferenceNames } from "../corrections/ReferenceFields";
import { usePeople } from "../people/people";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { ErrorNote, Kv, Section } from "../units/common";
import { fmtDate, humanise } from "../units/format";
import { campaignApi } from "./api";
import { campaignSchedule, repairPresentation, title } from "./format";
import type { Campaign, CampaignMatchEvidence, CampaignUnitRow, CampaignUnitsSummary, Page, SelectCampaignResult } from "./types";
import "./campaigns.css";

const PAGE = 50;
const TODAY = new Date().toISOString().slice(0, 10);

export function Component() {
  const { campaignId = "" } = useParams();
  const location = useLocation();
  const selected = (location.state as { selected?: SelectCampaignResult } | null)?.selected;
  const [campaign, setCampaign] = useState<Campaign | null>(null);
  const [summary, setSummary] = useState<CampaignUnitsSummary | null>(null);
  const [data, setData] = useState<Page<CampaignUnitRow> | null>(null);
  const [serial, setSerial] = useState("");
  const [owner, setOwner] = useState<string | null>(null);
  const [custody, setCustody] = useState("");
  const [differs, setDiffers] = useState("");
  const [repair, setRepair] = useState("");
  const [page, setPage] = useState(1);
  const [active, setActive] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState(selected ? `${selected.added.toLocaleString()} new units added; ${selected.selected.toLocaleString()} selected in total.` : "");
  const [error, setError] = useState<unknown>(null);
  const filters = useMemo(() => ({ serial: serial || null, owner, custody: custody || null, ownerDiffersInvoice: differs === "" ? null : differs === "yes", repair: repair || null, skip: (page - 1) * PAGE, take: PAGE }), [serial, owner, custody, differs, repair, page]);

  const reloadSummary = useCallback((signal?: AbortSignal) => Promise.all([campaignApi.detail(campaignId, signal), campaignApi.unitsSummary(campaignId, signal)]).then(([nextCampaign, nextSummary]) => { setCampaign(nextCampaign); setSummary(nextSummary); }), [campaignId]);
  useEffect(() => {
    const controller = new AbortController(); setError(null);
    reloadSummary(controller.signal).catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    return () => controller.abort();
  }, [reloadSummary]);
  useEffect(() => {
    const controller = new AbortController(); setLoading(true); setError(null);
    campaignApi.units(campaignId, filters, controller.signal).then((result) => { setData(result); setLoading(false); })
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) { setError(reason); setLoading(false); } });
    return () => controller.abort();
  }, [campaignId, filters]);

  const names = useReferenceNames({ parties: data?.items.flatMap((row) => [row.ownerPartyRef, row.custodianPartyRef, row.invoicePartyRef]) ?? [] });
  const people = usePeople(data?.items.map((row) => row.selectedBy) ?? []);
  const reselect = async () => {
    setBusy(true); setError(null); setNotice("");
    try { const result = await campaignApi.select(campaignId); setNotice(`${result.added.toLocaleString()} new units added; ${result.selected.toLocaleString()} selected in total.`); await reloadSummary(); setPage(1); setData(await campaignApi.units(campaignId, { ...filters, skip: 0 })); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const openUnit = (row: CampaignUnitRow) => {
    setActive(row.unitId);
    const head = { eyebrow: "Campaign unit", title: row.serial ?? "Unit", mono: true };
    openDrawer(head, <UnitDrawer row={row} names={names} person={people} evidence={null} />);
    campaignApi.match(campaignId, row.unitId).then((evidence) => updateDrawer(head, <UnitDrawer row={row} names={names} person={people} evidence={evidence} />)).catch(() => undefined);
  };
  const columns: GridColumn<CampaignUnitRow>[] = [
    { key: "serial", label: "Serial", width: 14, mono: true, render: (row) => row.serial ?? "—" },
    { key: "owner", label: "Owner · recipient", width: 22, render: (row) => <span className="primary">{names.party(row.ownerPartyRef)}<span className="sub">{row.ownerSince ? `since ${fmtDate(row.ownerSince)}` : humanise(row.ownerPartyType)}</span></span> },
    { key: "custodian", label: "Custodian · recipient", width: 20, render: (row) => row.custodianPartyRef === row.ownerPartyRef || !row.custodianPartyRef ? <>{row.custodianPartyRef ? "same as owner" : title(row.custodyAtSelection ?? "")}<span className="sub">{row.custodySince ? `since ${fmtDate(row.custodySince)}` : ""}</span></> : <>{names.party(row.custodianPartyRef)}<span className="sub">{title(row.custodyAtSelection ?? row.custodianPartyType ?? "")} · not the owner</span></> },
    { key: "invoice", label: "Invoiced to", width: 20, render: (row) => row.invoicePartyRef === row.ownerPartyRef ? <span className="muted">same as owner</span> : row.invoicePartyRef ? <>{names.party(row.invoicePartyRef)}<span className="sub">reference only</span></> : <span className="muted">—</span> },
    { key: "selected", label: "Selected", width: 12, render: (row) => fmtDate(row.selectedAt) },
    { key: "repair", label: "Repair", width: 12, render: (row) => { const value = repairPresentation(row.claimStatus, row.custodyKind); return <><StatusBadge tone={value.tone}>{value.label}</StatusBadge>{row.claimNumber && <span className="sub">{row.claimNumber}</span>}</>; } },
  ];
  const kpis = summary ? [
    { label: "Units selected", value: summary.unitsSelected.toLocaleString(), detail: `selected ${fmtDate(summary.selectedAt)} · ${summary.addedSinceFirstSelection.toLocaleString()} added since` },
    { label: "Recipients", value: summary.recipients.toLocaleString(), detail: `owners ${summary.owners.toLocaleString()} · custodians who are not the owner ${summary.custodiansNotOwner.toLocaleString()}` },
    { label: "Owner differs from invoice", value: summary.ownerDiffersFromInvoice.toLocaleString(), detail: "resold or sold through a distributor" },
    { label: "Repaired", value: summary.repaired.toLocaleString(), detail: `${campaign?.coverageSource ?? "campaign"} claims settled or closed` },
    { label: "In our hands", value: summary.inOurHands.toLocaleString(), detail: "warehouse, 3PL or in repair · fix before shipping" },
  ] : [];
  const schedule = campaignSchedule(campaign?.runsUntil ?? null, TODAY);

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Campaigns", to: "/claims/campaigns" }, { label: campaign?.code ?? "Campaign", to: `/claims/campaigns/${campaignId}` }, { label: "Units" }]} />
      <div className="page-header"><div><div className="eyebrow">Campaign {campaign?.code ?? "…"} · {campaign?.coverageSource ?? "…"} · {campaign?.status ?? "…"} · reserve {campaign?.reserveTag ?? "…"}</div><h1>Selected units and who to notify</h1><p className="sub">Recipients are the owner and custodian captured when each unit was selected · the invoice customer is reference only and is never notified for that reason</p></div><div className="actions"><Link className="btn" to={`/claims/campaigns/${campaignId}`}>Criteria</Link><a className="btn" href={campaignApi.exportUrl(campaignId)}><Icon name="down" />Export recipients</a><button type="button" className="btn primary" disabled={busy || schedule.ended || campaign?.status === "active" || campaign?.status === "closed"} onClick={() => void reselect()}><Icon name="check" />{schedule.ended ? "Selection ended" : "Re-select new matches"}</button></div></div>
      <div className="stack">
        {summary && <KpiStrip kpis={kpis} label="Campaign unit figures" />}
        {schedule.ended && <div className="note"><strong>Re-selection closed.</strong>&nbsp;This campaign ended on {campaign?.runsUntil}; existing selections remain in its history.</div>}
        {notice && <div className="note"><strong>Selection complete.</strong>&nbsp;{notice}</div>}
        <section className="card" aria-labelledby="campaign-unit-filters"><div className="card-body"><div className="toolbar"><div className="scanbox plain"><Icon name="scan" /><input className="input" type="search" placeholder="Find serial" aria-label="Find a campaign unit by serial" value={serial} onChange={(event) => { setSerial(event.target.value); setPage(1); }} /></div><div style={{ minWidth: 240 }}><PartyField id="campaign-owner" label="Owner" value={owner} onChange={(value) => { setOwner(value); setPage(1); }} optional /></div><Filter label="Custody" value={custody} onChange={(value) => { setCustody(value); setPage(1); }} options={[["", "All"], ["warehouse", "Warehouse"], ["at_3pl", "3PL"], ["in_repair", "In repair"], ["customer_site", "Customer site"]]} /><Filter label="Owner differs from invoice" value={differs} onChange={(value) => { setDiffers(value); setPage(1); }} options={[["", "Any"], ["yes", "Yes"], ["no", "No"]]} /><Filter label="Repair" value={repair} onChange={(value) => { setRepair(value); setPage(1); }} options={[["", "Any"], ["with_claim", "Claim exists"], ["without_claim", "No claim"], ["repaired", "Repaired"]]} /></div></div></section>
        <section className="card" aria-labelledby="campaign-units"><div className="card-header"><h2 id="campaign-units">Units <span className="meta">{data?.total.toLocaleString() ?? "—"} units · {summary?.recipients.toLocaleString() ?? "—"} recipients</span></h2><span className="meta">Sorted by serial</span></div><DataGrid columns={columns} page={{ rows: data?.items ?? [], total: data?.total ?? 0, page, pageSize: PAGE, continuation: null }} rowId={(row) => row.unitId} onPage={setPage} onRowClick={openUnit} activeRowId={active} loading={loading} emptyText="No selected units match these filters." /></section>
        <ErrorNote error={error} />
      </div>
    </main>
    <Rail label="Campaign units"><Section title="Recipient rule"><p className="muted">The owner and custodian are immutable selection-time snapshots. Re-selection adds newly matching units and never rewrites earlier recipients.</p></Section></Rail>
  </>;
}

function Filter({ label, value, onChange, options }: { label: string; value: string; onChange: (value: string) => void; options: [string, string][] }) { return <label className="field"><span className="lbl">{label}</span><select className={`chip-select${value ? " on" : ""}`} value={value} onChange={(event) => onChange(event.target.value)}>{options.map(([id, text]) => <option value={id} key={id}>{text}</option>)}</select></label>; }
function UnitDrawer({ row, names, person, evidence }: { row: CampaignUnitRow; names: ReturnType<typeof useReferenceNames>; person: ReturnType<typeof usePeople>; evidence: CampaignMatchEvidence[] | null }) { return <><Section title="Recipients at selection"><Kv rows={[["Owner", `${names.party(row.ownerPartyRef)}${row.ownerSince ? ` · since ${fmtDate(row.ownerSince)}` : ""}`], ["Custodian", row.custodianPartyRef === row.ownerPartyRef ? "same party" : names.party(row.custodianPartyRef)], ["Invoiced to", row.invoicePartyRef ? `${names.party(row.invoicePartyRef)} · reference only` : "—"], ["Selected by", person(row.selectedBy)], ["Matched on", evidence ? evidence.map((item) => `${item.label}: ${item.value}`).join(" · ") : "Loading…"]]} /></Section><Section title="Ownership changed since selection?"><p className="muted">Recipients are a snapshot. The unit page always shows today’s owner; a re-selection does not rewrite earlier recipients.</p></Section></>; }
