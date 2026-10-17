import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { DataGrid, StatusBadge, type GridColumn } from "../components";
import { ItemField, LotField, UnitField, useReferenceNames } from "../corrections/ReferenceFields";
import { usePeople } from "../people/people";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { ErrorNote, Kv, Section } from "../units/common";
import { fmtDate, humanise } from "../units/format";
import { campaignApi } from "./api";
import { campaignSchedule, campaignTone, title } from "./format";
import type { Campaign, CampaignCriterion, CampaignCriterionKind, CampaignDraft, CampaignFunnelStep, CampaignMatchEvidence, CampaignMatchRow, CampaignPreview, CampaignReason } from "./types";
import "./campaigns.css";

const PAGE = 50;
const TODAY = new Date().toISOString().slice(0, 10);

export function Component() {
  const { campaignId } = useParams();
  const navigate = useNavigate();
  const isNew = !campaignId;
  const [campaign, setCampaign] = useState<Campaign | null>(null);
  const [draft, setDraft] = useState<CampaignDraft>(() => emptyDraft());
  const [addKind, setAddKind] = useState<CampaignCriterionKind>("identifier_range");
  const [previewPage, setPreviewPage] = useState(1);
  const [preview, setPreview] = useState<CampaignPreview | null>(null);
  const [funnel, setFunnel] = useState<CampaignFunnelStep[]>([]);
  const [active, setActive] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(!isNew);
  const [error, setError] = useState<unknown>(null);

  const load = useCallback((id: string, signal?: AbortSignal) => {
    setLoading(true); setError(null);
    return campaignApi.detail(id, signal).then((row) => { setCampaign(row); setDraft(toDraft(row)); setLoading(false); return row; });
  }, []);
  useEffect(() => {
    if (!campaignId) return;
    const controller = new AbortController();
    load(campaignId, controller.signal).catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) { setError(reason); setLoading(false); } });
    return () => controller.abort();
  }, [campaignId, load]);
  // Once active or closed the selection is fixed: the server refuses a preview, so the editor shows the selected units instead.
  const selectionFixed = campaign?.status === "active" || campaign?.status === "closed";
  useEffect(() => {
    if (!campaignId || loading || selectionFixed) return;
    const controller = new AbortController();
    Promise.all([campaignApi.preview(campaignId, (previewPage - 1) * PAGE, PAGE, controller.signal), campaignApi.funnel(campaignId, controller.signal)])
      .then(([nextPreview, nextFunnel]) => { setPreview(nextPreview); setFunnel(nextFunnel); })
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    return () => controller.abort();
  }, [campaignId, loading, previewPage, campaign?.updatedAt, selectionFixed]);

  const people = usePeople([campaign?.createdBy]);
  const names = useReferenceNames({ items: preview?.page.items.map((row) => row.itemId) ?? [], parties: preview?.page.items.flatMap((row) => [row.ownerPartyRef, row.custodianPartyRef]) ?? [] });
  const setField = <K extends keyof CampaignDraft>(key: K, value: CampaignDraft[K]) => setDraft((current) => ({ ...current, [key]: value }));
  const updateCriterion = (index: number, patch: Partial<CampaignCriterion>) => setDraft((current) => ({ ...current, criteria: current.criteria.map((row, rowIndex) => rowIndex === index ? { ...row, ...patch } : row) }));
  const removeCriterion = (index: number) => setDraft((current) => ({ ...current, criteria: current.criteria.filter((_, rowIndex) => rowIndex !== index) }));
  const addCriterion = () => setDraft((current) => ({ ...current, criteria: [...current.criteria, emptyCriterion(addKind)] }));
  const coverValid = [draft.partsPct, draft.labourPct, draft.travelPct].every((value) => Number.isInteger(value) && value >= 0 && value <= 100);
  const valid = draft.code.trim() && draft.name.trim() && draft.reserveTag.trim() && draft.reasonCode && draft.reasonNote.length <= 500 && draft.criteria.length > 0 && coverValid;
  const selectionEnded = campaignSchedule(draft.runsUntil || null, TODAY).ended;

  const save = useCallback(async () => {
    if (!valid) return;
    setBusy(true); setError(null);
    try {
      if (campaignId) {
        const result = await campaignApi.update(campaignId, draft); setCampaign(result.campaign); setDraft(toDraft(result.campaign));
      } else {
        const result = await campaignApi.create(draft); navigate(`/claims/campaigns/${result.campaign.campaignId}`, { replace: true });
      }
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  }, [campaignId, draft, navigate, valid]);
  const select = useCallback(async () => {
    if (!campaignId || !valid || selectionEnded) return;
    setBusy(true); setError(null);
    try { await campaignApi.update(campaignId, draft); const result = await campaignApi.select(campaignId); await load(campaignId); navigate(`/claims/campaigns/${campaignId}/units`, { state: { selected: result } }); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  }, [campaignId, draft, load, navigate, selectionEnded, valid]);
  useEffect(() => {
    const key = (event: KeyboardEvent) => { if ((event.metaKey || event.ctrlKey) && event.key === "Enter" && campaignId && preview?.page.total) { event.preventDefault(); void select(); } };
    window.addEventListener("keydown", key); return () => window.removeEventListener("keydown", key);
  }, [campaignId, preview?.page.total, select]);

  const openMatch = (row: CampaignMatchRow) => {
    setActive(row.unitId);
    const head = { eyebrow: "Matched unit", title: row.serial ?? "Unit", mono: true };
    openDrawer(head, <Section title="Why it matched"><p className="muted">Loading evidence…</p></Section>);
    if (!campaignId) return;
    campaignApi.match(campaignId, row.unitId).then((evidence) => updateDrawer(head, <MatchEvidence evidence={evidence} />)).catch((reason) => updateDrawer(head, <ErrorNote error={reason} />));
  };
  const columns: GridColumn<CampaignMatchRow>[] = [
    { key: "serial", label: "Serial", width: 17, mono: true, render: (row) => row.serial ?? "—" },
    { key: "item", label: "Item", width: 17, render: (row) => names.item(row.itemId) },
    { key: "child", label: "Child serial matched", width: 17, mono: true, render: (row) => row.matchedChildSerial ?? "—" },
    { key: "firmware", label: "Firmware", width: 13, render: (row) => <>{row.firmwareValue ?? "—"}{row.firmwareSince && <span className="sub">since {fmtDate(row.firmwareSince)}</span>}</> },
    { key: "owner", label: "Owner now", width: 21, render: (row) => names.party(row.ownerPartyRef) },
    { key: "custody", label: "Custody", width: 15, render: (row) => title(row.custodyKind ?? row.custodianPartyType ?? "") },
  ];
  const grouped = useMemo(() => new Set(draft.criteria.map((row) => row.kind)).size, [draft.criteria]);

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Campaigns", to: "/claims/campaigns" }, { label: isNew ? "New" : campaign?.code ?? "Campaign" }]} />
      <div className="page-header"><div><div className="eyebrow">{isNew ? "New campaign" : `Campaign ${campaign?.code ?? "…"} · ${draft.coverageSource} · ${draft.status} · created ${fmtDate(campaign?.createdAt)} by ${people(campaign?.createdBy)}`}</div><h1>{draft.name || "Name this campaign"}</h1><p className="sub">Units are chosen by criteria, never by hand · the preview is the selection · repairs post to this campaign’s reserve</p></div><div className="actions"><button type="button" className="btn" disabled={!valid || busy} onClick={() => void save()}>Save draft</button><button type="button" className="btn primary" disabled={!campaignId || !preview?.page.total || busy || selectionEnded || draft.status === "active" || draft.status === "closed"} onClick={() => void select()}><Icon name="check" />{selectionEnded ? "Selection ended" : `Select ${(preview?.page.total ?? 0).toLocaleString()} units`}<kbd className="key">⌘↵</kbd></button></div></div>
      <div className="cols-3-2 top">
        <div className="stack">
          <section className="card" aria-labelledby="campaign-criteria"><div className="card-header"><h2 id="campaign-criteria">Criteria <span className="meta">all {grouped} kinds must match · any row within a kind</span></h2><div className="campaign-add"><select className="select" aria-label="Criterion kind" value={addKind} onChange={(event) => setAddKind(event.target.value as CampaignCriterionKind)}><option value="identifier_range">Serial range</option><option value="child">Child unit or item</option><option value="firmware">Firmware</option><option value="lot">Lot</option></select><button type="button" className="btn sm" onClick={addCriterion}><Icon name="plus" />Add</button></div></div><div className="card-body">{draft.criteria.map((criterion, index) => <CriterionEditor key={criterion.criterionId} criterion={criterion} onChange={(patch) => updateCriterion(index, patch)} onRemove={() => removeCriterion(index)} />)}{draft.criteria.length === 0 && <p className="muted">Add at least one criterion. Different kinds narrow with AND; repeated rows of one kind widen with OR.</p>}</div></section>
          <section className="card" aria-labelledby="campaign-preview"><div className="card-header"><h2 id="campaign-preview">Preview <span className="meta">{selectionFixed ? `selection fixed · ${draft.status}` : preview ? `${preview.page.total.toLocaleString()} units match` : isNew ? "save to preview" : "loading"}</span></h2>{selectionFixed && campaignId ? <Link className="btn sm" to={`/claims/campaigns/${campaignId}/units`}>Selected units</Link> : <span className="meta">re-run on select</span>}</div>{selectionFixed ? <div className="card-body"><p className="muted">This campaign is {draft.status}, so its selection no longer changes. The units it selected, with their recipients and claims, are on the units list.</p></div> : <DataGrid columns={columns} page={{ rows: preview?.page.items ?? [], total: preview?.page.total ?? 0, page: previewPage, pageSize: PAGE, continuation: null }} rowId={(row) => row.unitId} onPage={setPreviewPage} onRowClick={openMatch} activeRowId={active} loading={loading} dense emptyText={isNew ? "Save the campaign to preview its criteria." : "No units match all criterion kinds."} footerNote="Owner and custodian are read from current episodes" />}</section>
        </div>
        <div className="stack">
          <section className="card" aria-labelledby="campaign-settings">
            <div className="card-header"><h2 id="campaign-settings">Campaign</h2></div>
            <div className="card-body"><div className="form-grid">
              <Field id="campaign-code" label="Code" value={draft.code} onChange={(value) => setField("code", value)} mono />
              <div className="field"><span className="lbl">Coverage source</span><div className="segmented plain" role="group" aria-label="Coverage source"><button type="button" aria-pressed={draft.coverageSource === "recall"} onClick={() => setField("coverageSource", "recall")}>Recall</button><button type="button" aria-pressed={draft.coverageSource === "goodwill"} onClick={() => setField("coverageSource", "goodwill")}>Goodwill</button></div></div>
              <Field id="campaign-name" label="Name" value={draft.name} onChange={(value) => setField("name", value)} span />
              <Field id="campaign-reserve" label="Reserve tag" value={draft.reserveTag} onChange={(value) => setField("reserveTag", value)} mono help="Finance books repairs here" />
              <div className="field"><label htmlFor="campaign-end">Runs until</label><input id="campaign-end" className="input" type="date" value={draft.runsUntil} onChange={(event) => setField("runsUntil", event.target.value)} /><span className="help">Optional · inclusive</span></div>
              <div className="field span campaign-cover"><span className="lbl">Cover</span><div className="campaign-cover-fields"><PercentageField id="campaign-parts" label="Parts" value={draft.partsPct} onChange={(value) => setField("partsPct", value)} /><PercentageField id="campaign-labour" label="Labour" value={draft.labourPct} onChange={(value) => setField("labourPct", value)} /><PercentageField id="campaign-travel" label="Travel" value={draft.travelPct} onChange={(value) => setField("travelPct", value)} /></div><span className="help">Share of each claim line this campaign pays; changes apply to claims decided afterwards.</span>{!coverValid && <span className="form-error" role="alert">Enter a whole percentage from 0 to 100 for each line kind.</span>}</div>
              <div className="field"><label htmlFor="campaign-reason">Reason</label><select id="campaign-reason" className="select" value={draft.reasonCode} onChange={(event) => setField("reasonCode", event.target.value as CampaignReason)}><option value="safety">Safety</option><option value="reliability">Reliability</option><option value="firmware">Firmware</option><option value="regulatory">Regulatory</option><option value="goodwill">Goodwill</option></select></div>
              <div className="field span"><label htmlFor="campaign-reason-note">Reason note · optional</label><textarea id="campaign-reason-note" className="textarea campaign-reason-note" maxLength={500} value={draft.reasonNote} onChange={(event) => setField("reasonNote", event.target.value)} /></div>
            </div></div>
          </section>
          <section className="card campaign-funnel" aria-labelledby="campaign-funnel"><div className="card-header"><h2 id="campaign-funnel">How the criteria narrow</h2></div><div className="card-body">{funnel.map((step, index) => <div className="job" key={step.kind}><div><div className="t">{step.count.toLocaleString()} units after {humanise(step.kind)}</div><div className="s">cumulative AND step</div></div><StatusBadge tone={index === funnel.length - 1 ? "ok" : "neutral"}>{index === funnel.length - 1 ? "Selected" : title(step.kind)}</StatusBadge></div>)}{funnel.length === 0 && (selectionFixed ? <p className="muted">The selection is fixed; the criteria are kept as the record of how units were chosen.</p> : <p className="muted">Save the criteria to see each cumulative AND step.</p>)}</div></section>
          <div className="note"><strong>Re-selecting later</strong>&nbsp;adds units that match since the last selection and never removes one already selected.</div>
          <ErrorNote error={error} />
        </div>
      </div>
    </main>
    <Rail label="Campaign"><Section title="Selection contract"><Kv rows={[["Kinds", grouped], ["Rows", draft.criteria.length], ["Status", <StatusBadge tone={campaignTone(draft.status)}>{title(draft.status)}</StatusBadge>], ["Reserve", draft.reserveTag || "—"]]} /></Section>{campaignId && <Section title="Selected units"><Link className="btn primary block" to={`/claims/campaigns/${campaignId}/units`}>Open unit list</Link></Section>}</Rail>
  </>;
}

function CriterionEditor({ criterion, onChange, onRemove }: { criterion: CampaignCriterion; onChange: (patch: Partial<CampaignCriterion>) => void; onRemove: () => void }) {
  return <div className="campaign-criterion"><span className="kind">{criterion.kind === "identifier_range" ? "Serial range" : criterion.kind === "child" ? "Child unit or item" : title(criterion.kind)}</span><div className="values">
    {criterion.kind === "identifier_range" && <><SmallField label="Scheme" value={criterion.scheme ?? ""} onChange={(value) => onChange({ scheme: value })} /><div /><SmallField label="From" value={criterion.rangeStart ?? ""} onChange={(value) => onChange({ rangeStart: value })} mono /><SmallField label="To" value={criterion.rangeEnd ?? ""} onChange={(value) => onChange({ rangeEnd: value })} mono /></>}
    {criterion.kind === "child" && <><div className="field wide"><label htmlFor={`${criterion.criterionId}-target`}>Match by</label><select id={`${criterion.criterionId}-target`} className="select" value={criterion.childUnitId !== null ? "unit" : "item"} onChange={(event) => onChange(event.target.value === "unit" ? { childUnitId: "", childItemId: null } : { childUnitId: null, childItemId: "" })}><option value="item">Child item</option><option value="unit">Child unit</option></select></div>{criterion.childUnitId !== null ? <UnitField id={`${criterion.criterionId}-unit`} label="Child unit" value={criterion.childUnitId || null} onChange={(value) => onChange({ childUnitId: value ?? "" })} /> : <ItemField id={`${criterion.criterionId}-item`} label="Child item" value={criterion.childItemId} onChange={(value) => onChange({ childItemId: value })} />}<LotField id={`${criterion.criterionId}-vendor-lot`} label="Vendor lot" value={criterion.childLotId} onChange={(value) => onChange({ childLotId: value })} optional /></>}
    {criterion.kind === "firmware" && <><SmallField label="Attribute key" value={criterion.firmwareKey ?? "firmware"} onChange={(value) => onChange({ firmwareKey: value })} mono /><SmallField label="Value" value={criterion.firmwareValue ?? ""} onChange={(value) => onChange({ firmwareValue: value })} mono /><SmallField label="Value on date · optional" value={criterion.effectiveAt?.slice(0, 10) ?? ""} onChange={(value) => onChange({ effectiveAt: value ? `${value}T23:59:59Z` : null })} type="date" /></>}
    {criterion.kind === "lot" && <LotField id={`${criterion.criterionId}-lot`} label="Lot" value={criterion.lotId} onChange={(value) => onChange({ lotId: value })} />}
  </div><button type="button" className="icon-btn" aria-label={`Remove ${title(criterion.kind)} criterion`} onClick={onRemove}><Icon name="x" /></button></div>;
}

function MatchEvidence({ evidence }: { evidence: CampaignMatchEvidence[] }) { return <Section title="Why it matched"><Kv rows={evidence.map((row) => [row.label, row.value])} /></Section>; }
function Field({ id, label, value, onChange, mono, span, help }: { id: string; label: string; value: string; onChange: (value: string) => void; mono?: boolean; span?: boolean; help?: string }) { return <div className={`field${span ? " span" : ""}`}><label htmlFor={id}>{label}</label><input id={id} className={`input${mono ? " serial" : ""}`} value={value} onChange={(event) => onChange(event.target.value)} />{help && <span className="help">{help}</span>}</div>; }
function PercentageField({ id, label, value, onChange }: { id: string; label: string; value: number; onChange: (value: number) => void }) { return <div className="field"><label htmlFor={id}>{label}</label><div className="campaign-percent"><input id={id} className="input num" type="number" min="0" max="100" step="1" value={value} onChange={(event) => onChange(Number(event.target.value))} /><span aria-hidden="true">%</span></div></div>; }
function SmallField({ label, value, onChange, mono, type = "text" }: { label: string; value: string; onChange: (value: string) => void; mono?: boolean; type?: string }) { const id = useMemo(() => crypto.randomUUID(), []); return <div className="field"><label htmlFor={id}>{label}</label><input id={id} className={`input${mono ? " serial" : ""}`} type={type} value={value} onChange={(event) => onChange(event.target.value)} /></div>; }
function emptyCriterion(kind: CampaignCriterionKind): CampaignCriterion { return { criterionId: crypto.randomUUID(), kind, scheme: kind === "identifier_range" ? "internal" : null, rangeStart: kind === "identifier_range" ? "" : null, rangeEnd: kind === "identifier_range" ? "" : null, lotId: kind === "lot" ? "" : null, childUnitId: null, childItemId: kind === "child" ? "" : null, firmwareValue: kind === "firmware" ? "" : null, effectiveAt: null, childLotId: null, firmwareKey: kind === "firmware" ? "firmware" : null }; }
function emptyDraft(): CampaignDraft { return { code: "", name: "", coverageSource: "recall", reserveTag: "", status: "draft", partsPct: 100, labourPct: 100, travelPct: 100, reasonCode: "safety", reasonNote: "", runsUntil: "", criteria: [emptyCriterion("identifier_range")] }; }
function toDraft(campaign: Campaign): CampaignDraft { return { code: campaign.code, name: campaign.name, coverageSource: campaign.coverageSource, reserveTag: campaign.reserveTag, status: campaign.status, partsPct: campaign.partsPct, labourPct: campaign.labourPct, travelPct: campaign.travelPct, reasonCode: campaign.reasonCode, reasonNote: campaign.reasonNote ?? "", runsUntil: campaign.runsUntil ?? "", criteria: campaign.criteria }; }
