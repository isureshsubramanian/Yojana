import { useCallback, useEffect, useMemo, useState } from "react";
import { DataGrid, type GridColumn } from "../components/DataGrid";
import { PresenceAdvisory, PresenceChips, useDocumentPresence } from "../components/Presence";
import { StatusBadge } from "../components/StatusBadge";
import type { PageResult } from "../components/types";
import { usePeople } from "../people/people";
import { CountrySelect } from "../reference/CountrySelect";
import { SubdivisionInput } from "../reference/SubdivisionInput";
import { countryName } from "../reference/countries";
import { isSubdivisionCode, subdivisionParts } from "../reference/subdivisions";
import { useSession } from "../session/session";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { openDrawer } from "../shell/drawerStore";
import { ErrorNote, Kv } from "../units/common";
import { jurisdictionApi } from "./api";
import { addRegionMember, buildRegionGroupRequest, buildWhatAppliesRequest, displayRuleValue, effectiveRangeLabel, formatRuleValue, pageRules, parseRuleValue, removeRegionMember, ruleKindLabel, scopeDetails, starterName, type RegionGroupDraft } from "./model";
import type { Catalogue, JurisdictionRule, PackPreview, RuleContext, RuleResolution } from "./types";
import "./jurisdictions.css";
import { currencyOptions } from "../reference/currencies";
import { ItemFamilyField } from "../corrections/ReferenceFields";

const emptyContext: RuleContext = { country: "", subdivision: null, buyerClass: "consumer", itemClass: null, price: null, currency: null, contractDate: "2026-09-01", purchaseDate: null, deliveryDate: null, firstSaleDate: null };
const newRule = { ruleId: null as string | null, expectedVersion: null as string | null, kind: "", scope: "country", countryCode: "", subdivisionCode: "", regionGroupId: "", buyerClass: "any", itemClass: "", priceMin: "", priceMax: "", currency: "", dateTested: "contract", effectiveFrom: "2026-09-01", effectiveTo: "", rawValue: "", language: "en", sourceCitation: "", sourceNote: "", confirmedByCounsel: false, counselConfirmedOn: "" };
const emptyGroup: RegionGroupDraft = { code: "", name: "", members: [] };
type RuleDraft = typeof newRule;

export function Component() {
  const session = useSession();
  const canWrite = session.roles.includes("tenant_administrator");
  const [catalogue, setCatalogue] = useState<Catalogue | null>(null);
  const [country, setCountry] = useState("");
  const [kind, setKind] = useState("");
  const [page, setPage] = useState(1);
  const [active, setActive] = useState<string | null>(null);
  const [draft, setDraft] = useState<RuleDraft | null>(null);
  const [group, setGroup] = useState<RegionGroupDraft>(emptyGroup);
  const [groupCountry, setGroupCountry] = useState("");
  const [groupSubdivision, setGroupSubdivision] = useState("");
  const [testerKind, setTesterKind] = useState("");
  const [context, setContext] = useState<RuleContext>(emptyContext);
  const [results, setResults] = useState<RuleResolution[]>([]);
  const [preview, setPreview] = useState<PackPreview | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const presence = useDocumentPresence("jurisdiction_rule", draft?.ruleId ?? "", draft?.ruleId !== null);
  const actor = usePeople(catalogue?.rules.flatMap((rule) => [rule.createdBy, rule.approvedBy]) ?? []);

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoading(true); setError(null);
    try { setCatalogue(await jurisdictionApi.catalogue(country, kind, signal)); }
    catch (reason) { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); }
    finally { setLoading(false); }
  }, [country, kind]);
  useEffect(() => { const controller = new AbortController(); void load(controller.signal); return () => controller.abort(); }, [load]);

  const rules = catalogue?.rules ?? [];
  const pageSize = 25;
  const rulePage: PageResult<JurisdictionRule> = { rows: pageRules(rules, page, pageSize), total: rules.length, page, pageSize, continuation: null };
  const kindMeta = (value: string) => catalogue?.kinds.find((entry) => entry.kind === value);
  const formatted = (ruleKind: string, value: unknown) => { const meta = kindMeta(ruleKind); return formatRuleValue(meta?.valueType, value, meta?.unit); };
  const scope = (rule: JurisdictionRule) => scopeDetails(rule, catalogue?.regionGroups ?? []);
  const columns: GridColumn<JurisdictionRule>[] = [
    { key: "kind", label: "Rule kind", width: 29, render: (row) => ruleKindLabel(row.kind) },
    { key: "scope", label: "Scope", width: 18, render: (row) => <ScopeDisplay details={scope(row)} /> },
    { key: "buyer", label: "Buyer", width: 12, render: (row) => humanise(row.buyerClass) },
    { key: "value", label: "Value", width: 23, render: (row) => <span>{formatted(row.kind, row.value)}</span> },
    { key: "effective", label: "Effective", width: 18, render: (row) => effectiveRangeLabel(row.effectiveFrom, row.effectiveTo) },
  ];

  const openRule = (rule: JurisdictionRule) => {
    setActive(rule.ruleId);
    openDrawer({ eyebrow: `${ruleKindLabel(rule.kind)} · version ${rule.version}`, title: scope(rule).short }, <>
      <Kv rows={[["Scope", <ScopeDisplay details={scope(rule)} />], ["Value", formatted(rule.kind, rule.value)], ["Buyer", humanise(rule.buyerClass)], ["Date tested", humanise(rule.dateTested)], ["Effective", effectiveRangeLabel(rule.effectiveFrom, rule.effectiveTo)], ["Created by", actor(rule.createdBy)], ["Approved by", actor(rule.approvedBy)], ["Source", rule.sourceCitation ?? "—"]]} />
      {canWrite && rule.current && <div className="act"><button type="button" className="btn primary block" onClick={() => edit(rule)}>Edit as new version…</button></div>}
    </>);
  };
  const edit = (rule: JurisdictionRule) => setDraft({ ...newRule, ruleId: rule.ruleId, expectedVersion: rule.versionToken, kind: rule.kind, scope: rule.scope, countryCode: rule.countryCode ?? "", subdivisionCode: rule.subdivisionCode ?? "", regionGroupId: rule.regionGroupId ?? "", buyerClass: rule.buyerClass, itemClass: rule.itemClass ?? "", priceMin: rule.priceMin?.toString() ?? "", priceMax: rule.priceMax?.toString() ?? "", currency: rule.currency ?? "", dateTested: rule.dateTested, effectiveFrom: rule.effectiveFrom, effectiveTo: rule.effectiveTo ?? "", rawValue: displayRuleValue(rule.value), language: languageOf(rule.value), sourceCitation: rule.sourceCitation ?? "", sourceNote: rule.sourceNote ?? "", confirmedByCounsel: rule.confirmedByCounsel, counselConfirmedOn: rule.counselConfirmedOn ?? "" });

  const saveRule = async () => {
    if (!draft || !catalogue) return;
    const meta = catalogue.kinds.find((entry) => entry.kind === draft.kind);
    if (!meta) return;
    setBusy(true); setError(null);
    try {
      await jurisdictionApi.saveRule({ commandId: crypto.randomUUID(), ruleId: draft.ruleId, kind: draft.kind, scope: draft.scope, countryCode: draft.scope === "country" ? draft.countryCode : null, subdivisionCode: draft.scope === "subdivision" ? draft.subdivisionCode : null, regionGroupId: draft.scope === "region_group" ? draft.regionGroupId : null, buyerClass: draft.buyerClass, itemClass: draft.itemClass || null, priceMin: numberOrNull(draft.priceMin), priceMax: numberOrNull(draft.priceMax), currency: draft.currency || null, dateTested: draft.dateTested, effectiveFrom: draft.effectiveFrom, effectiveTo: draft.effectiveTo || null, value: parseRuleValue(meta.valueType, draft.rawValue, draft.language, draft.currency), sourceCitation: draft.sourceCitation || null, sourceNote: draft.sourceNote || null, confirmedByCounsel: draft.confirmedByCounsel, counselConfirmedOn: draft.confirmedByCounsel ? draft.counselConfirmedOn || null : null, expectedVersion: draft.expectedVersion });
      setDraft(null); await load();
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const saveGroup = async () => {
    setBusy(true); setError(null);
    try { await jurisdictionApi.saveGroup(buildRegionGroupRequest(crypto.randomUUID(), group)); setGroup(emptyGroup); setGroupCountry(""); setGroupSubdivision(""); await load(); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const test = async () => {
    setBusy(true); setError(null);
    try { const request = buildWhatAppliesRequest(testerKind, context); setResults((await jurisdictionApi.whatApplies(request.kind ?? "", request.context)).rules); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const showPack = async (id: string) => { setBusy(true); try { setPreview(await jurisdictionApi.packPreview(id)); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  const importPack = async () => { if (!preview) return; setBusy(true); try { await jurisdictionApi.importPack(preview.rulePackId); setPreview(null); await load(); } catch (reason) { setError(reason); } finally { setBusy(false); } };

  const rail = useMemo(() => <>
    <section className="section"><h3>Rule catalogue</h3><p className="muted">Rules are tenant-owned, effective-dated legal configuration. Editing closes the observed version and creates a successor.</p></section>
    <section className="section"><h3>Resolution order</h3><p className="muted">Subdivision, country, region group, then any. Exact buyer, item class and price qualifiers break ties before effective date and version.</p></section>
  </>, []);

  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Jurisdictions" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · {canWrite ? "tenant administrator" : "read only"}</div><h1>Jurisdiction rules</h1><p className="sub">Define the rules for every country, subdivision and tenant region group where you sell. The software supplies rule kinds, never legal values.</p></div>{canWrite && <div className="actions"><button type="button" className="btn primary" onClick={() => setDraft({ ...newRule, kind: catalogue?.kinds[0]?.kind ?? "" })}><Icon name="plus" />New rule</button></div>}</div>
    <ErrorNote error={error} />
    <div className="note warn"><strong>Legal confirmation required.</strong>&nbsp;Starter packs preserve a known operating baseline only. Confirm every imported value with your legal adviser before relying on it.</div>
    <section className="card top"><div className="card-header"><h2>Rules</h2><span className="meta">{rules.length} versions</span></div><div className="card-body jurisdiction-toolbar"><CountrySelect id="jurisdiction-filter-country" label="Country" value={country} onChange={(value) => { setCountry(value); setPage(1); }} /><Field label="Rule kind"><select className="input" value={kind} onChange={(event) => { setKind(event.target.value); setPage(1); }}><option value="">All kinds</option>{catalogue?.kinds.map((entry) => <option key={entry.kind} value={entry.kind}>{ruleKindLabel(entry.kind)}</option>)}</select></Field><button type="button" className="btn" onClick={() => void load()}><Icon name="filter" />Refresh</button></div><DataGrid columns={columns} page={rulePage} rowId={(row) => row.ruleId} onPage={setPage} onRowClick={openRule} activeRowId={active} loading={loading} emptyText="No jurisdiction rules match." /></section>
    {draft && catalogue && <RuleEditor draft={draft} setDraft={setDraft} catalogue={catalogue} busy={busy} presence={presence} onSave={saveRule} onCancel={() => setDraft(null)} />}
    <div className="jurisdiction-sections top">
      <section className="card"><div className="card-header"><h2>What applies?</h2><span className="meta">server-resolved</span></div><div className="card-body stack"><div className="jurisdiction-form"><CountrySelect id="jurisdiction-context-country" label="Country" value={context.country} onChange={(value) => setContext({ ...context, country: value, subdivision: context.subdivision && subdivisionParts(context.subdivision).country === value ? context.subdivision : null })} /><SubdivisionInput id="jurisdiction-context-subdivision" label="Subdivision" value={context.subdivision} onChange={(value) => { const subdivision = value || null; const selectedCountry = subdivisionParts(value).country; setContext({ ...context, country: selectedCountry || context.country, subdivision }); }} /><Field label="Buyer"><select className="input" value={context.buyerClass} onChange={(event) => setContext({ ...context, buyerClass: event.target.value })}><option value="consumer">Consumer</option><option value="business">Business</option><option value="any">Any</option></select></Field><ItemFamilyField id="jurisdiction-context-item-class" label="Item class" optional value={context.itemClass} onChange={(itemClass) => setContext({ ...context, itemClass })} /><Field label="Price"><input className="input" type="number" value={context.price ?? ""} onChange={(event) => setContext({ ...context, price: numberOrNull(event.target.value) })} /></Field><Field label="Currency"><select className="select" value={context.currency ?? ""} onChange={(event) => setContext({ ...context, currency: event.target.value || null })}><option value="">No price given</option>{currencyOptions(context.currency).map((option) => <option key={option.code} value={option.code}>{option.label}</option>)}</select></Field><Field label="Contract date"><input className="input" type="date" value={context.contractDate ?? ""} onChange={(event) => setContext({ ...context, contractDate: event.target.value || null })} /></Field><Field label="Purchase date"><input className="input" type="date" value={context.purchaseDate ?? ""} onChange={(event) => setContext({ ...context, purchaseDate: event.target.value || null })} /></Field><Field label="Delivery date"><input className="input" type="date" value={context.deliveryDate ?? ""} onChange={(event) => setContext({ ...context, deliveryDate: event.target.value || null })} /></Field><Field label="First-sale date"><input className="input" type="date" value={context.firstSaleDate ?? ""} onChange={(event) => setContext({ ...context, firstSaleDate: event.target.value || null })} /></Field><Field label="Kind"><select className="input" value={testerKind} onChange={(event) => setTesterKind(event.target.value)}><option value="">All kinds</option>{catalogue?.kinds.map((entry) => <option key={entry.kind} value={entry.kind}>{humanise(entry.kind)}</option>)}</select></Field></div><button type="button" className="btn primary" disabled={busy || context.country.length !== 2 || Boolean(context.subdivision && !isSubdivisionCode(context.subdivision))} onClick={() => void test()}>Test rules</button>{results.map((result) => <div className="jurisdiction-result" key={result.ruleId}><strong>{humanise(result.kind)}</strong> <StatusBadge tone="info">{result.scopeLabel}</StatusBadge><div>{formatted(result.kind, result.value)}</div><span className="help">Tests {humanise(result.dateTested)} on {result.testedDate} · precedence {result.precedence}</span></div>)}{results.length === 0 && <p className="muted">Enter a context to see the effective rules. No result means the neutral default.</p>}</div></section>
      <div className="stack"><section className="card"><div className="card-header"><h2>Region groups</h2><span className="meta">tenant defined</span></div><div className="card-body stack">{catalogue?.regionGroups.map((entry) => { const display = starterName(entry.name); return <div key={entry.regionGroupId}><strong>{display.name}</strong>{display.starter && <span className="jurisdiction-starter-tag">starter · confirm with your legal adviser</span>}<div className="help">{entry.code} · {entry.members.map(memberLabel).join(", ")}</div></div>; })}{canWrite && <><Field label="Code"><input className="input" value={group.code} onChange={(event) => setGroup({ ...group, code: event.target.value })} /></Field><Field label="Name"><input className="input" value={group.name} onChange={(event) => setGroup({ ...group, name: event.target.value })} /></Field><div className="jurisdiction-member-picker"><CountrySelect id="jurisdiction-group-country" label="Add countries" value={groupCountry} onChange={(value) => { setGroupCountry(value); if (value) { setGroup((current) => ({ ...current, members: addRegionMember(current.members, value) })); setGroupCountry(""); } }} /><div className="jurisdiction-member-chips" aria-label="Selected region-group members">{group.members.map((member) => <button type="button" className="chip on" key={member} aria-label={`Remove ${memberLabel(member)}`} onClick={() => setGroup({ ...group, members: removeRegionMember(group.members, member) })}>{memberLabel(member)}<Icon name="x" /></button>)}</div><div className="jurisdiction-add-subdivision"><SubdivisionInput id="jurisdiction-group-subdivision" label="Add subdivision" value={groupSubdivision} onChange={setGroupSubdivision} /><button type="button" className="btn" disabled={!isSubdivisionCode(groupSubdivision) || group.members.includes(groupSubdivision)} onClick={() => { setGroup({ ...group, members: addRegionMember(group.members, groupSubdivision) }); setGroupSubdivision(""); }}><Icon name="plus" />Add subdivision</button></div></div><button type="button" className="btn" disabled={busy || !group.code || !group.name || group.members.length === 0} onClick={() => void saveGroup()}>Add group</button></>}</div></section><section className="card"><div className="card-header"><h2>Starter packs</h2></div><div className="card-body stack">{catalogue?.rulePacks.map((pack) => { const display = starterName(pack.name); return <div key={pack.rulePackId}><strong>{display.name}</strong>{display.starter && <span className="jurisdiction-starter-tag">starter · confirm with your legal adviser</span>}<div className="help">{pack.ruleCount} rules · {pack.imported ? "imported" : "not imported"}</div><button type="button" className="btn" onClick={() => void showPack(pack.rulePackId)}>Preview</button></div>; })}</div></section></div>
    </div>
    {preview && <section className="card top"><div className="card-header"><h2>{starterName(preview.name).name}</h2>{starterName(preview.name).starter && <span className="jurisdiction-starter-tag">starter · confirm with your legal adviser</span>}<button type="button" className="icon-btn" aria-label="Close pack preview" onClick={() => setPreview(null)}><Icon name="x" /></button></div><div className="card-body stack"><div className="note warn">{preview.warning}</div><p>{preview.regionGroups.length} region groups · {preview.rules.length} rules</p>{preview.rules.map((rule, index) => <div key={`${rule.kind}-${index}`}><strong>{ruleKindLabel(rule.kind)}</strong><span className="help"> · {rule.scopeLabel} · {effectiveRangeLabel(rule.effectiveFrom, null)} · {formatted(rule.kind, rule.value)}</span></div>)}{canWrite && <button type="button" className="btn primary" disabled={busy} onClick={() => void importPack()}>Import this starter</button>}</div></section>}
    <Rail label="Jurisdiction rule help">{rail}</Rail>
  </main>;
}

function RuleEditor({ draft, setDraft, catalogue, busy, presence, onSave, onCancel }: { draft: RuleDraft; setDraft: (value: RuleDraft) => void; catalogue: Catalogue; busy: boolean; presence: ReturnType<typeof useDocumentPresence>; onSave: () => Promise<void>; onCancel: () => void }) {
  const meta = catalogue.kinds.find((entry) => entry.kind === draft.kind);
  const scopeValid = draft.scope === "any" || draft.scope === "country" && Boolean(draft.countryCode)
    || draft.scope === "subdivision" && isSubdivisionCode(draft.subdivisionCode)
    || draft.scope === "region_group" && Boolean(draft.regionGroupId);
  return <section className="card top"><div className="card-header"><div><h2>{draft.ruleId ? "New version" : "New rule"}</h2><span className="meta">Value editor: {humanise(meta?.valueType ?? "choose a kind")}</span></div><PresenceChips users={presence} /></div><div className="card-body stack"><PresenceAdvisory users={presence} editing={draft.ruleId !== null} documentLabel="jurisdiction rule" /><div className="jurisdiction-form"><Field label="Rule kind"><select className="input" value={draft.kind} onChange={(event) => setDraft({ ...draft, kind: event.target.value, rawValue: "" })}>{catalogue.kinds.map((entry) => <option key={entry.kind} value={entry.kind}>{humanise(entry.kind)}</option>)}</select></Field><Field label="Scope"><select className="input" value={draft.scope} onChange={(event) => setDraft({ ...draft, scope: event.target.value })}><option value="any">Any</option><option value="country">Country</option><option value="subdivision">Subdivision</option><option value="region_group">Region group</option></select></Field>{draft.scope === "country" && <CountrySelect id="jurisdiction-rule-country" label="Country" value={draft.countryCode} onChange={(value) => setDraft({ ...draft, countryCode: value })} />}{draft.scope === "subdivision" && <SubdivisionInput id="jurisdiction-rule-subdivision" label="Subdivision" value={draft.subdivisionCode} onChange={(value) => setDraft({ ...draft, subdivisionCode: value })} />}{draft.scope === "region_group" && <Field label="Region group"><select className="input" value={draft.regionGroupId} onChange={(event) => setDraft({ ...draft, regionGroupId: event.target.value })}><option value="">Choose…</option>{catalogue.regionGroups.map((entry) => <option key={entry.regionGroupId} value={entry.regionGroupId}>{entry.name}</option>)}</select></Field>}<Field label="Buyer class"><select className="input" value={draft.buyerClass} onChange={(event) => setDraft({ ...draft, buyerClass: event.target.value })}><option value="any">Any</option><option value="consumer">Consumer</option><option value="business">Business</option></select></Field><ItemFamilyField id="jurisdiction-rule-item-class" label="Item class" optional value={draft.itemClass || null} onChange={(itemClass) => setDraft({ ...draft, itemClass: itemClass ?? "" })} /><Field label="Date tested"><select className="input" value={draft.dateTested} onChange={(event) => setDraft({ ...draft, dateTested: event.target.value })}><option value="contract">Contract</option><option value="purchase">Purchase</option><option value="delivery">Delivery</option><option value="first_sale">First sale</option></select></Field><Field label="Minimum price"><input className="input" type="number" value={draft.priceMin} onChange={(event) => setDraft({ ...draft, priceMin: event.target.value })} /></Field><Field label="Maximum price"><input className="input" type="number" value={draft.priceMax} onChange={(event) => setDraft({ ...draft, priceMax: event.target.value })} /></Field><Field label="Currency"><select className="select" value={draft.currency} onChange={(event) => setDraft({ ...draft, currency: event.target.value })}><option value="">No price band</option>{currencyOptions(draft.currency).map((option) => <option key={option.code} value={option.code}>{option.label}</option>)}</select></Field><Field label="Effective from"><input className="input" type="date" value={draft.effectiveFrom} onChange={(event) => setDraft({ ...draft, effectiveFrom: event.target.value })} /></Field><Field label="Effective to"><input className="input" type="date" value={draft.effectiveTo} onChange={(event) => setDraft({ ...draft, effectiveTo: event.target.value })} /></Field>{meta?.valueType === "text_language" && <Field label="Language"><input className="input" value={draft.language} onChange={(event) => setDraft({ ...draft, language: event.target.value })} /></Field>}<Field label={`Value${meta?.unit ? ` (${meta.unit})` : ""}`} className="wide">{meta?.valueType === "boolean" ? <select className="input" value={draft.rawValue} onChange={(event) => setDraft({ ...draft, rawValue: event.target.value })}><option value="">Choose…</option><option value="true">Required / true</option><option value="false">Not required / false</option></select> : ["months", "days", "integer", "money"].includes(meta?.valueType ?? "") ? <input className="input" type="number" value={draft.rawValue} onChange={(event) => setDraft({ ...draft, rawValue: event.target.value })} /> : <textarea className="input jurisdiction-value" value={draft.rawValue} placeholder={meta?.valueType === "phrase_list" || meta?.valueType === "string_list" ? "One value per line" : meta?.valueType === "json" ? "Valid JSON" : "Rule value"} onChange={(event) => setDraft({ ...draft, rawValue: event.target.value })} />}</Field><Field label="Source citation" className="span-2"><input className="input" value={draft.sourceCitation} onChange={(event) => setDraft({ ...draft, sourceCitation: event.target.value })} /></Field><Field label="Source note"><input className="input" value={draft.sourceNote} onChange={(event) => setDraft({ ...draft, sourceNote: event.target.value })} /></Field><Field label="Legal confirmation"><label className="checkrow"><input type="checkbox" checked={draft.confirmedByCounsel} onChange={(event) => setDraft({ ...draft, confirmedByCounsel: event.target.checked })} />Confirmed by counsel</label></Field>{draft.confirmedByCounsel && <Field label="Confirmed on"><input className="input" type="date" value={draft.counselConfirmedOn} onChange={(event) => setDraft({ ...draft, counselConfirmedOn: event.target.value })} /></Field>}</div><div className="jurisdiction-actions"><button type="button" className="btn primary" disabled={busy || !scopeValid || !draft.kind || !draft.effectiveFrom || !draft.rawValue} onClick={() => void onSave()}><Icon name="check" />Save version</button><button type="button" className="btn" onClick={onCancel}>Cancel</button></div></div></section>;
}

function ScopeDisplay({ details }: { details: ReturnType<typeof scopeDetails> }) {
  return <span className="jurisdiction-scope" title={details.full}><span>{details.name}</span>{details.code && <span className="jurisdiction-scope-code">({details.code})</span>}</span>;
}

function Field({ label, className, children }: { label: string; className?: string | undefined; children: React.ReactNode }) { return <div className={`field${className ? ` ${className}` : ""}`}><label>{label}</label>{children}</div>; }
function humanise(value: string): string {
  if (value === "used_goods_min_term") return ruleKindLabel(value);
  return value.replaceAll("_", " ").replace(/^./, (letter) => letter.toUpperCase());
}
function numberOrNull(value: string): number | null { return value.trim() === "" ? null : Number(value); }
function languageOf(value: unknown): string { return typeof value === "object" && value !== null && "language" in value ? String((value as { language: unknown }).language) : "en"; }
function memberLabel(code: string): string { return `${countryName(code.slice(0, 2))} (${code})`; }

