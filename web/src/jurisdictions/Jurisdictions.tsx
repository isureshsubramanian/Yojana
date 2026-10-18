import { useCallback, useEffect, useMemo, useState } from "react";
import { ApiError } from "../api/client";
import { DataGrid, type GridColumn } from "../components/DataGrid";
import { PresenceAdvisory, PresenceChips, useDocumentPresence } from "../components/Presence";
import { StatusBadge } from "../components/StatusBadge";
import type { PageResult } from "../components/types";
import { usePeople } from "../people/people";
import { useSession } from "../session/session";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { openDrawer } from "../shell/drawerStore";
import { Kv } from "../units/common";
import { jurisdictionApi } from "./api";
import { buildWhatAppliesRequest, displayRuleValue, parseRuleValue } from "./model";
import type { Catalogue, JurisdictionRule, PackPreview, RuleContext, RuleResolution } from "./types";
import "./jurisdictions.css";

const emptyContext: RuleContext = { country: "", subdivision: null, buyerClass: "consumer", itemClass: null, price: null, currency: null, contractDate: "2026-09-01", purchaseDate: null, deliveryDate: null, firstSaleDate: null };
const newRule = { ruleId: null as string | null, expectedVersion: null as string | null, kind: "", scope: "country", countryCode: "", subdivisionCode: "", regionGroupId: "", buyerClass: "any", itemClass: "", priceMin: "", priceMax: "", currency: "", dateTested: "contract", effectiveFrom: "2026-09-01", effectiveTo: "", rawValue: "", language: "en", sourceCitation: "", sourceNote: "", confirmedByCounsel: false, counselConfirmedOn: "" };
type RuleDraft = typeof newRule;

export function Component() {
  const session = useSession();
  const canWrite = session.roles.includes("tenant_administrator");
  const [catalogue, setCatalogue] = useState<Catalogue | null>(null);
  const [country, setCountry] = useState("");
  const [kind, setKind] = useState("");
  const [page, setPage] = useState(0);
  const [active, setActive] = useState<string | null>(null);
  const [draft, setDraft] = useState<RuleDraft | null>(null);
  const [group, setGroup] = useState({ code: "", name: "", members: "" });
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
  const rulePage: PageResult<JurisdictionRule> = { rows: rules.slice(page * pageSize, (page + 1) * pageSize), total: rules.length, page, pageSize, continuation: null };
  const columns: GridColumn<JurisdictionRule>[] = [
    { key: "kind", label: "Rule kind", width: 29, render: (row) => humanise(row.kind) },
    { key: "scope", label: "Scope", width: 18, render: (row) => scopeLabel(row) },
    { key: "buyer", label: "Buyer", width: 12, render: (row) => humanise(row.buyerClass) },
    { key: "value", label: "Value", width: 23, render: (row) => <span className="serial">{oneLine(row.value)}</span> },
    { key: "effective", label: "Effective", width: 18, render: (row) => <>{row.effectiveFrom} → {row.effectiveTo ?? "open"}</> },
  ];

  const openRule = (rule: JurisdictionRule) => {
    setActive(rule.ruleId);
    openDrawer({ eyebrow: `${humanise(rule.kind)} · version ${rule.version}`, title: scopeLabel(rule) }, <>
      <Kv rows={[["Value", oneLine(rule.value)], ["Buyer", humanise(rule.buyerClass)], ["Date tested", humanise(rule.dateTested)], ["Effective", `${rule.effectiveFrom} → ${rule.effectiveTo ?? "open"}`], ["Created by", actor(rule.createdBy)], ["Approved by", actor(rule.approvedBy)]]} />
      {rule.sourceCitation && <p className="help">Source: {rule.sourceCitation}</p>}
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
    try { await jurisdictionApi.saveGroup({ commandId: crypto.randomUUID(), regionGroupId: null, code: group.code, name: group.name, members: group.members.split(/[\s,]+/).filter(Boolean), expectedVersion: null }); setGroup({ code: "", name: "", members: "" }); await load(); }
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
    <section className="card top"><div className="card-header"><h2>Rules</h2><span className="meta">{rules.length} versions</span></div><div className="card-body jurisdiction-toolbar"><Field label="Country"><input className="input" maxLength={2} value={country} onChange={(event) => { setCountry(event.target.value.toUpperCase()); setPage(0); }} /></Field><Field label="Rule kind"><select className="input" value={kind} onChange={(event) => { setKind(event.target.value); setPage(0); }}><option value="">All kinds</option>{catalogue?.kinds.map((entry) => <option key={entry.kind} value={entry.kind}>{humanise(entry.kind)}</option>)}</select></Field><button type="button" className="btn" onClick={() => void load()}><Icon name="filter" />Refresh</button></div><DataGrid columns={columns} page={rulePage} rowId={(row) => row.ruleId} onPage={setPage} onRowClick={openRule} activeRowId={active} loading={loading} emptyText="No jurisdiction rules match." /></section>
    {draft && catalogue && <RuleEditor draft={draft} setDraft={setDraft} catalogue={catalogue} busy={busy} presence={presence} onSave={saveRule} onCancel={() => setDraft(null)} />}
    <div className="jurisdiction-sections top">
      <section className="card"><div className="card-header"><h2>What applies?</h2><span className="meta">server-resolved</span></div><div className="card-body stack"><div className="jurisdiction-form"><Field label="Country"><input className="input" maxLength={2} value={context.country} onChange={(event) => setContext({ ...context, country: event.target.value })} /></Field><Field label="Subdivision"><input className="input" placeholder="DE-BY" value={context.subdivision ?? ""} onChange={(event) => setContext({ ...context, subdivision: event.target.value || null })} /></Field><Field label="Buyer"><select className="input" value={context.buyerClass} onChange={(event) => setContext({ ...context, buyerClass: event.target.value })}><option value="consumer">Consumer</option><option value="business">Business</option><option value="any">Any</option></select></Field><Field label="Item class"><input className="input" value={context.itemClass ?? ""} onChange={(event) => setContext({ ...context, itemClass: event.target.value || null })} /></Field><Field label="Price"><input className="input" type="number" value={context.price ?? ""} onChange={(event) => setContext({ ...context, price: numberOrNull(event.target.value) })} /></Field><Field label="Currency"><input className="input" maxLength={3} value={context.currency ?? ""} onChange={(event) => setContext({ ...context, currency: event.target.value || null })} /></Field><Field label="Contract date"><input className="input" type="date" value={context.contractDate ?? ""} onChange={(event) => setContext({ ...context, contractDate: event.target.value || null })} /></Field><Field label="Purchase date"><input className="input" type="date" value={context.purchaseDate ?? ""} onChange={(event) => setContext({ ...context, purchaseDate: event.target.value || null })} /></Field><Field label="Delivery date"><input className="input" type="date" value={context.deliveryDate ?? ""} onChange={(event) => setContext({ ...context, deliveryDate: event.target.value || null })} /></Field><Field label="First-sale date"><input className="input" type="date" value={context.firstSaleDate ?? ""} onChange={(event) => setContext({ ...context, firstSaleDate: event.target.value || null })} /></Field><Field label="Kind"><select className="input" value={testerKind} onChange={(event) => setTesterKind(event.target.value)}><option value="">All kinds</option>{catalogue?.kinds.map((entry) => <option key={entry.kind} value={entry.kind}>{humanise(entry.kind)}</option>)}</select></Field></div><button type="button" className="btn primary" disabled={busy || context.country.length !== 2} onClick={() => void test()}>Test rules</button>{results.map((result) => <div className="jurisdiction-result" key={result.ruleId}><strong>{humanise(result.kind)}</strong> <StatusBadge tone="info">{result.scopeLabel}</StatusBadge><div className="serial">{oneLine(result.value)}</div><span className="help">Tests {humanise(result.dateTested)} on {result.testedDate} · precedence {result.precedence}</span></div>)}{results.length === 0 && <p className="muted">Enter a context to see the effective rules. No result means the neutral default.</p>}</div></section>
      <div className="stack"><section className="card"><div className="card-header"><h2>Region groups</h2><span className="meta">tenant defined</span></div><div className="card-body stack">{catalogue?.regionGroups.map((entry) => <div key={entry.regionGroupId}><strong>{entry.name}</strong><div className="help">{entry.code} · {entry.members.join(", ")}</div></div>)}{canWrite && <><Field label="Code"><input className="input" value={group.code} onChange={(event) => setGroup({ ...group, code: event.target.value })} /></Field><Field label="Name"><input className="input" value={group.name} onChange={(event) => setGroup({ ...group, name: event.target.value })} /></Field><Field label="Country or subdivision members"><textarea className="input jurisdiction-value" value={group.members} onChange={(event) => setGroup({ ...group, members: event.target.value })} /></Field><button type="button" className="btn" disabled={busy || !group.code || !group.name || !group.members} onClick={() => void saveGroup()}>Add group</button></>}</div></section><section className="card"><div className="card-header"><h2>Starter packs</h2></div><div className="card-body stack">{catalogue?.rulePacks.map((pack) => <div key={pack.rulePackId}><strong>{pack.name}</strong><div className="help">{pack.ruleCount} rules · {pack.imported ? "imported" : "not imported"}</div><button type="button" className="btn" onClick={() => void showPack(pack.rulePackId)}>Preview</button></div>)}</div></section></div>
    </div>
    {preview && <section className="card top"><div className="card-header"><h2>{preview.name}</h2><button type="button" className="icon-btn" aria-label="Close pack preview" onClick={() => setPreview(null)}><Icon name="x" /></button></div><div className="card-body stack"><div className="note warn">{preview.warning}</div><p>{preview.regionGroups.length} region group · {preview.rules.length} rules</p>{preview.rules.map((rule, index) => <div key={`${rule.kind}-${index}`}><strong>{humanise(rule.kind)}</strong><span className="help"> · {rule.scopeLabel} · from {rule.effectiveFrom} · {oneLine(rule.value)}</span></div>)}{canWrite && <button type="button" className="btn primary" disabled={busy} onClick={() => void importPack()}>Import this starter</button>}</div></section>}
    <Rail label="Jurisdiction rule help">{rail}</Rail>
  </main>;
}

function RuleEditor({ draft, setDraft, catalogue, busy, presence, onSave, onCancel }: { draft: RuleDraft; setDraft: (value: RuleDraft) => void; catalogue: Catalogue; busy: boolean; presence: ReturnType<typeof useDocumentPresence>; onSave: () => Promise<void>; onCancel: () => void }) {
  const meta = catalogue.kinds.find((entry) => entry.kind === draft.kind);
  return <section className="card top"><div className="card-header"><div><h2>{draft.ruleId ? "New version" : "New rule"}</h2><span className="meta">Value editor: {humanise(meta?.valueType ?? "choose a kind")}</span></div><PresenceChips users={presence} /></div><div className="card-body stack"><PresenceAdvisory users={presence} editing={draft.ruleId !== null} documentLabel="jurisdiction rule" /><div className="jurisdiction-form"><Field label="Rule kind"><select className="input" value={draft.kind} onChange={(event) => setDraft({ ...draft, kind: event.target.value, rawValue: "" })}>{catalogue.kinds.map((entry) => <option key={entry.kind} value={entry.kind}>{humanise(entry.kind)}</option>)}</select></Field><Field label="Scope"><select className="input" value={draft.scope} onChange={(event) => setDraft({ ...draft, scope: event.target.value })}><option value="any">Any</option><option value="country">Country</option><option value="subdivision">Subdivision</option><option value="region_group">Region group</option></select></Field>{draft.scope === "country" && <Field label="Country"><input className="input" maxLength={2} value={draft.countryCode} onChange={(event) => setDraft({ ...draft, countryCode: event.target.value.toUpperCase() })} /></Field>}{draft.scope === "subdivision" && <Field label="Subdivision"><input className="input" value={draft.subdivisionCode} onChange={(event) => setDraft({ ...draft, subdivisionCode: event.target.value.toUpperCase() })} /></Field>}{draft.scope === "region_group" && <Field label="Region group"><select className="input" value={draft.regionGroupId} onChange={(event) => setDraft({ ...draft, regionGroupId: event.target.value })}><option value="">Choose…</option>{catalogue.regionGroups.map((entry) => <option key={entry.regionGroupId} value={entry.regionGroupId}>{entry.name}</option>)}</select></Field>}<Field label="Buyer class"><select className="input" value={draft.buyerClass} onChange={(event) => setDraft({ ...draft, buyerClass: event.target.value })}><option value="any">Any</option><option value="consumer">Consumer</option><option value="business">Business</option></select></Field><Field label="Item class"><input className="input" value={draft.itemClass} onChange={(event) => setDraft({ ...draft, itemClass: event.target.value })} /></Field><Field label="Date tested"><select className="input" value={draft.dateTested} onChange={(event) => setDraft({ ...draft, dateTested: event.target.value })}><option value="contract">Contract</option><option value="purchase">Purchase</option><option value="delivery">Delivery</option><option value="first_sale">First sale</option></select></Field><Field label="Minimum price"><input className="input" type="number" value={draft.priceMin} onChange={(event) => setDraft({ ...draft, priceMin: event.target.value })} /></Field><Field label="Maximum price"><input className="input" type="number" value={draft.priceMax} onChange={(event) => setDraft({ ...draft, priceMax: event.target.value })} /></Field><Field label="Currency"><input className="input" maxLength={3} value={draft.currency} onChange={(event) => setDraft({ ...draft, currency: event.target.value.toUpperCase() })} /></Field><Field label="Effective from"><input className="input" type="date" value={draft.effectiveFrom} onChange={(event) => setDraft({ ...draft, effectiveFrom: event.target.value })} /></Field><Field label="Effective to"><input className="input" type="date" value={draft.effectiveTo} onChange={(event) => setDraft({ ...draft, effectiveTo: event.target.value })} /></Field>{meta?.valueType === "text_language" && <Field label="Language"><input className="input" value={draft.language} onChange={(event) => setDraft({ ...draft, language: event.target.value })} /></Field>}<Field label={`Value${meta?.unit ? ` (${meta.unit})` : ""}`} className="wide">{meta?.valueType === "boolean" ? <select className="input" value={draft.rawValue} onChange={(event) => setDraft({ ...draft, rawValue: event.target.value })}><option value="">Choose…</option><option value="true">Required / true</option><option value="false">Not required / false</option></select> : ["months", "days", "integer", "money"].includes(meta?.valueType ?? "") ? <input className="input" type="number" value={draft.rawValue} onChange={(event) => setDraft({ ...draft, rawValue: event.target.value })} /> : <textarea className="input jurisdiction-value" value={draft.rawValue} placeholder={meta?.valueType === "phrase_list" || meta?.valueType === "string_list" ? "One value per line" : meta?.valueType === "json" ? "Valid JSON" : "Rule value"} onChange={(event) => setDraft({ ...draft, rawValue: event.target.value })} />}</Field><Field label="Source citation" className="span-2"><input className="input" value={draft.sourceCitation} onChange={(event) => setDraft({ ...draft, sourceCitation: event.target.value })} /></Field><Field label="Source note"><input className="input" value={draft.sourceNote} onChange={(event) => setDraft({ ...draft, sourceNote: event.target.value })} /></Field><Field label="Legal confirmation"><label className="checkrow"><input type="checkbox" checked={draft.confirmedByCounsel} onChange={(event) => setDraft({ ...draft, confirmedByCounsel: event.target.checked })} />Confirmed by counsel</label></Field>{draft.confirmedByCounsel && <Field label="Confirmed on"><input className="input" type="date" value={draft.counselConfirmedOn} onChange={(event) => setDraft({ ...draft, counselConfirmedOn: event.target.value })} /></Field>}</div><div className="jurisdiction-actions"><button type="button" className="btn primary" disabled={busy || !draft.kind || !draft.effectiveFrom || !draft.rawValue} onClick={() => void onSave()}><Icon name="check" />Save version</button><button type="button" className="btn" onClick={onCancel}>Cancel</button></div></div></section>;
}

function Field({ label, className, children }: { label: string; className?: string | undefined; children: React.ReactNode }) { return <div className={`field${className ? ` ${className}` : ""}`}><label>{label}</label>{children}</div>; }
function scopeLabel(rule: JurisdictionRule): string { return rule.subdivisionCode ?? rule.countryCode ?? rule.regionGroupName ?? "Any"; }
function oneLine(value: unknown): string { return displayRuleValue(value).replace(/\s+/g, " ").slice(0, 90); }
function humanise(value: string): string { return value.replaceAll("_", " ").replace(/^./, (letter) => letter.toUpperCase()); }
function numberOrNull(value: string): number | null { return value.trim() === "" ? null : Number(value); }
function languageOf(value: unknown): string { return typeof value === "object" && value !== null && "language" in value ? String((value as { language: unknown }).language) : "en"; }
function ErrorNote({ error }: { error: unknown }) { if (!error) return null; const rule = error instanceof ApiError ? error.rule : null; return <div className="form-error" role="alert">{rule && <strong>{humanise(rule)} · </strong>}{error instanceof Error ? error.message : String(error)}</div>; }

